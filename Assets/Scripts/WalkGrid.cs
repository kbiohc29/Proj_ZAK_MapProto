using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 보행 격자. 건물 footprint를 래스터화해서 "갈 수 있는 곳 / 막힌 곳"을 만든다.
/// 도로에 얽매이지 않는 이동(플레이어, 좀비)의 기반.
///
/// - 건물 내부 = 막힘, 나머지(도로·공터·인도) = 통행 가능
/// - 격자 A* + 시야선(LOS) 스무딩으로 자연스러운 최단 경로를 만든다
/// - Unity NavMesh를 쓰지 않는 이유: 도시 규모 베이크가 무겁고, 우리는 이미
///   폴리곤 데이터를 갖고 있어서 직접 래스터화가 훨씬 싸고 빠르다
///
/// 세팅: 빈 GameObject에 부착 (BuildingRenderer 자동 참조)
/// </summary>
public class WalkGrid : MonoBehaviour
{
    [Header("Grid")]
    [Tooltip("격자 한 칸 크기(m). 작을수록 정밀하지만 메모리↑")]
    public float cellSize = 4f;
    [Tooltip("건물 주변 여유(칸). 벽에 딱 붙어 지나가지 않게")]
    public int inflate = 1;
    [Tooltip("데이터 범위 바깥 여백(m)")]
    public float margin = 500f;

    [Header("Pathfinding")]
    public int maxExpansions = 120000;

    public bool Ready { get; private set; }
    public Vector3 Origin { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    bool[] blocked;
    BuildingRenderer buildings;

    void Start()
    {
        buildings = FindFirstObjectByType<BuildingRenderer>();
        transform.position = Vector3.zero;
    }

    void Update()
    {
        if (Ready || buildings == null || !buildings.FootprintsReady) return;
        Build();
    }

    void Build()
    {
        var foots = buildings.Footprints;
        if (foots.Count == 0) { Ready = true; return; }

        // 범위 계산
        Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
        foreach (var ring in foots)
            foreach (var p in ring)
            {
                min.x = Mathf.Min(min.x, p.x); min.y = Mathf.Min(min.y, p.z);
                max.x = Mathf.Max(max.x, p.x); max.y = Mathf.Max(max.y, p.z);
            }
        min -= Vector2.one * margin;
        max += Vector2.one * margin;

        Origin = new Vector3(min.x, 0f, min.y);
        Width = Mathf.CeilToInt((max.x - min.x) / cellSize);
        Height = Mathf.CeilToInt((max.y - min.y) / cellSize);
        blocked = new bool[Width * Height];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var ring in foots) Rasterize(ring);
        if (inflate > 0) Inflate(inflate);
        sw.Stop();

        Ready = true;
        Debug.Log($"보행격자 {Width}x{Height} ({cellSize}m) 생성, {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>스캔라인 방식으로 폴리곤 내부를 막힘 처리</summary>
    void Rasterize(Vector3[] ring)
    {
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in ring) { minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }

        int y0 = Mathf.Max(0, WorldToCellY(minZ));
        int y1 = Mathf.Min(Height - 1, WorldToCellY(maxZ));
        var xs = new List<float>(16);

        for (int y = y0; y <= y1; y++)
        {
            float wz = Origin.z + (y + 0.5f) * cellSize;
            xs.Clear();

            for (int i = 0; i < ring.Length; i++)
            {
                Vector3 a = ring[i], b = ring[(i + 1) % ring.Length];
                if ((a.z <= wz && b.z > wz) || (b.z <= wz && a.z > wz))
                {
                    float t = (wz - a.z) / (b.z - a.z);
                    xs.Add(a.x + t * (b.x - a.x));
                }
            }
            if (xs.Count < 2) continue;
            xs.Sort();

            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int x0 = Mathf.Max(0, WorldToCellX(xs[k]));
                int x1 = Mathf.Min(Width - 1, WorldToCellX(xs[k + 1]));
                for (int x = x0; x <= x1; x++) blocked[y * Width + x] = true;
            }
        }
    }

    void Inflate(int r)
    {
        var src = (bool[])blocked.Clone();
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (!src[y * Width + x]) continue;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) continue;
                blocked[ny * Width + nx] = true;
            }
        }
    }

    // ---------- 좌표 변환 ----------

    public int WorldToCellX(float wx) => Mathf.FloorToInt((wx - Origin.x) / cellSize);
    public int WorldToCellY(float wz) => Mathf.FloorToInt((wz - Origin.z) / cellSize);
    public Vector3 CellToWorld(int x, int y) =>
        new(Origin.x + (x + 0.5f) * cellSize, 0f, Origin.z + (y + 0.5f) * cellSize);

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>격자 밖은 열린 지형으로 간주해 통행 가능</summary>
    public bool IsWalkable(int x, int y) => !InBounds(x, y) || !blocked[y * Width + x];
    public bool IsWalkableWorld(Vector3 p) => IsWalkable(WorldToCellX(p.x), WorldToCellY(p.z));

    /// <summary>막힌 칸이면 가장 가까운 통행 가능한 칸으로 밀어낸다</summary>
    public bool NearestFree(int x, int y, out int fx, out int fy, int maxRing = 40)
    {
        for (int r = 0; r <= maxRing; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                int nx = x + dx, ny = y + dy;
                if (IsWalkable(nx, ny)) { fx = nx; fy = ny; return true; }
            }
        }
        fx = x; fy = y; return false;
    }

    // ---------- 경로 탐색 ----------

    static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    /// <summary>격자 A*. 성공 시 스무딩된 월드 좌표 경로를 반환</summary>
    public List<Vector3> FindPath(Vector3 from, Vector3 to)
    {
        if (!Ready) return null;

        int sx = WorldToCellX(from.x), sy = WorldToCellY(from.z);
        int gx = WorldToCellX(to.x),   gy = WorldToCellY(to.z);
        if (!IsWalkable(sx, sy)) NearestFree(sx, sy, out sx, out sy);
        if (!IsWalkable(gx, gy)) NearestFree(gx, gy, out gx, out gy);

        // 시야가 트여 있으면 직선으로 끝
        if (HasLineOfSight(CellToWorld(sx, sy), CellToWorld(gx, gy)))
            return new List<Vector3> { from, to };

        var open = new MinHeap();
        var gScore = new Dictionary<int, float>();
        var cameFrom = new Dictionary<int, int>();
        var closed = new HashSet<int>();

        int start = sy * Width + sx, goal = gy * Width + gx;
        gScore[start] = 0f;
        open.Push(Heuristic(sx, sy, gx, gy), start);

        int expansions = 0;
        while (open.Count > 0 && expansions++ < maxExpansions)
        {
            int cur = open.Pop();
            if (cur == goal) return Smooth(Reconstruct(cameFrom, cur), from, to);
            if (!closed.Add(cur)) continue;

            int cx = cur % Width, cy = cur / Width;
            float g = gScore[cur];

            for (int i = 0; i < 8; i++)
            {
                int nx = cx + DX[i], ny = cy + DY[i];
                if (!InBounds(nx, ny) || !IsWalkable(nx, ny)) continue;
                // 대각선은 모서리를 뚫지 않도록
                if (i >= 4 && (!IsWalkable(cx + DX[i], cy) || !IsWalkable(cx, cy + DY[i]))) continue;

                int nid = ny * Width + nx;
                if (closed.Contains(nid)) continue;

                float step = (i >= 4 ? 1.4142f : 1f) * cellSize;
                float ng = g + step;
                if (gScore.TryGetValue(nid, out float old) && old <= ng) continue;

                gScore[nid] = ng;
                cameFrom[nid] = cur;
                open.Push(ng + Heuristic(nx, ny, gx, gy), nid);
            }
        }
        return null;
    }

    float Heuristic(int x, int y, int gx, int gy)
    {
        float dx = Mathf.Abs(x - gx), dy = Mathf.Abs(y - gy);
        return (Mathf.Max(dx, dy) + 0.4142f * Mathf.Min(dx, dy)) * cellSize;
    }

    List<Vector3> Reconstruct(Dictionary<int, int> cameFrom, int cur)
    {
        var path = new List<Vector3> { CellToWorld(cur % Width, cur / Width) };
        while (cameFrom.TryGetValue(cur, out int prev))
        {
            cur = prev;
            path.Add(CellToWorld(cur % Width, cur / Width));
        }
        path.Reverse();
        return path;
    }

    /// <summary>시야선 스무딩(string pulling): 직선으로 갈 수 있는 구간은 중간점을 버린다</summary>
    List<Vector3> Smooth(List<Vector3> raw, Vector3 from, Vector3 to)
    {
        if (raw == null || raw.Count == 0) return null;
        raw[0] = from;
        raw[^1] = to;

        var result = new List<Vector3> { raw[0] };
        int anchor = 0;
        for (int i = 2; i < raw.Count; i++)
        {
            if (HasLineOfSight(raw[anchor], raw[i])) continue;
            result.Add(raw[i - 1]);
            anchor = i - 1;
        }
        result.Add(raw[^1]);
        return result;
    }

    /// <summary>두 점 사이가 막히지 않았는가 (격자 위 Bresenham)</summary>
    public bool HasLineOfSight(Vector3 a, Vector3 b)
    {
        int x0 = WorldToCellX(a.x), y0 = WorldToCellY(a.z);
        int x1 = WorldToCellX(b.x), y1 = WorldToCellY(b.z);

        int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            if (!IsWalkable(x0, y0)) return false;
            if (x0 == x1 && y0 == y1) return true;
            int e2 = err * 2;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx)  { err += dx; y0 += sy; }
        }
    }

    class MinHeap
    {
        readonly List<(float f, int id)> heap = new(2048);
        public int Count => heap.Count;
        public void Push(float f, int id)
        {
            heap.Add((f, id));
            int i = heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (heap[p].f <= heap[i].f) break;
                (heap[p], heap[i]) = (heap[i], heap[p]);
                i = p;
            }
        }
        public int Pop()
        {
            int top = heap[0].id;
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, s = i;
                if (l < heap.Count && heap[l].f < heap[s].f) s = l;
                if (r < heap.Count && heap[r].f < heap[s].f) s = r;
                if (s == i) break;
                (heap[s], heap[i]) = (heap[i], heap[s]);
                i = s;
            }
            return top;
        }
    }
}
