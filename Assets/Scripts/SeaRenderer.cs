using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 바다와 육지.
///
/// v2: 격자 근사 대신 **실제 해안선 데이터**를 경계로 쓴다.
///   1) 해안선(natural=coastline)을 격자에 '벽'으로 래스터화
///   2) 도로·건물이 있는 칸은 육지 씨앗
///   3) 지도 바깥에서 바다를 흘려보내되 벽과 육지를 넘지 못하게
///   4) 물이 닿지 못한 칸 = 육지 (산·내륙 포함)
/// 다리는 육지 판정에서 제외한다 (광안대교가 땅이 되는 문제).
///
/// 세팅: 빈 GameObject에 부착 (RoadNetwork / TerrainRenderer 자동 참조)
/// </summary>
public class SeaRenderer : MonoBehaviour
{
    [Header("Refs")]
    public RoadNetwork roads;
    public TerrainRenderer terrain;

    [Header("Land")]
    [Tooltip("격자 크기(m). 작을수록 해안선이 정밀하지만 메모리·시간 증가")]
    public float cellSize = 40f;
    [Tooltip("도로·건물 주변 몇 칸까지 육지로 볼지")]
    public int dilate = 1;
    public bool excludeBridges = true;
    public bool includeBuildings = true;
    [Tooltip("데이터 범위 바깥 여백(m)")]
    public float margin = 3000f;

    [Header("Colors")]
    public Color seaColor = new Color(0.05f, 0.10f, 0.16f);
    public Color landColor = new Color(0.16f, 0.16f, 0.15f);
    public float seaExtent = 200000f;

    [Header("Night")]
    [Range(0f, 1f)] public float nightDarken = 0.65f;

    readonly List<Material> mats = new();
    readonly List<Color> baseColors = new();
    bool built;

    // 격자 상태
    const byte UNKNOWN = 0, LAND = 1, WALL = 2, SEA = 3;
    byte[] cells;
    int W, H;
    Vector3 origin;

    void Start()
    {
        transform.position = Vector3.zero;
        transform.rotation = Quaternion.identity;
        if (roads == null) roads = FindFirstObjectByType<RoadNetwork>();
        if (terrain == null) terrain = FindFirstObjectByType<TerrainRenderer>();
        BuildSeaPlane();
    }

    void Update()
    {
        if (!built && roads != null && roads.Ready && (terrain == null || terrain.TerrainReady))
        {
            BuildLand();
            built = true;
        }

        float night = Shader.GetGlobalFloat("_ZakNight");
        for (int i = 0; i < mats.Count; i++)
            mats[i].color = Color.Lerp(baseColors[i], baseColors[i] * (1f - nightDarken), night);
    }

    void BuildSeaPlane()
    {
        float e = seaExtent;
        Create("Sea", new List<Vector3> {
            new(-e, -6f, -e), new(-e, -6f, e), new(e, -6f, e), new(e, -6f, -e) },
            new List<int> { 0, 1, 2, 0, 2, 3 }, seaColor, 2880);
    }

    void BuildLand()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ---- 1. 범위 ----
        Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
        void Expand(Vector3 p)
        {
            min.x = Mathf.Min(min.x, p.x); min.y = Mathf.Min(min.y, p.z);
            max.x = Mathf.Max(max.x, p.x); max.y = Mathf.Max(max.y, p.z);
        }
        foreach (var p in roads.NodePos.Values) Expand(p);
        if (terrain != null) foreach (var line in terrain.Coastlines) foreach (var p in line) Expand(p);

        min -= Vector2.one * margin;
        max += Vector2.one * margin;
        origin = new Vector3(min.x, 0f, min.y);
        W = Mathf.CeilToInt((max.x - min.x) / cellSize);
        H = Mathf.CeilToInt((max.y - min.y) / cellSize);
        cells = new byte[W * H];

        // ---- 2. 해안선을 벽으로 ----
        int wallCells = 0;
        if (terrain != null)
            foreach (var line in terrain.Coastlines)
                for (int i = 0; i < line.Length - 1; i++)
                    wallCells += RasterLine(line[i], line[i + 1]);

        // ---- 3. 육지 씨앗 (도로 + 건물) ----
        int skipped = 0;
        foreach (var kv in roads.NodePos)
        {
            if (excludeBridges && roads.BridgeNodes.Contains(kv.Key)) { skipped++; continue; }
            Mark(kv.Value, LAND);
        }
        if (includeBuildings)
        {
            var br = FindFirstObjectByType<BuildingRenderer>();
            if (br != null) foreach (var info in br.BuildingInfos) Mark(info.centroid, LAND);
        }

        for (int d = 0; d < dilate; d++) Dilate();

        // ---- 4. 바깥에서 바다 흘려보내기 ----
        var queue = new Queue<int>();
        for (int x = 0; x < W; x++) { Seed(x, 0, queue); Seed(x, H - 1, queue); }
        for (int y = 0; y < H; y++) { Seed(0, y, queue); Seed(W - 1, y, queue); }

        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int cx = c % W, cy = c / W;
            Seed(cx + 1, cy, queue); Seed(cx - 1, cy, queue);
            Seed(cx, cy + 1, queue); Seed(cx, cy - 1, queue);
        }

        // ---- 5. 물이 닿지 못한 칸 = 육지 ----
        BuildLandMesh();
        sw.Stop();
        Debug.Log($"육지 마스크 {W}x{H} ({cellSize}m) — 해안선 벽 {wallCells:N0}칸, " +
                  $"다리 제외 {skipped:N0}노드, {sw.ElapsedMilliseconds}ms");
    }

    void Mark(Vector3 p, byte v)
    {
        int x = Mathf.FloorToInt((p.x - origin.x) / cellSize);
        int y = Mathf.FloorToInt((p.z - origin.z) / cellSize);
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        if (cells[y * W + x] != WALL) cells[y * W + x] = v;
    }

    int RasterLine(Vector3 a, Vector3 b)
    {
        int x0 = Mathf.FloorToInt((a.x - origin.x) / cellSize), y0 = Mathf.FloorToInt((a.z - origin.z) / cellSize);
        int x1 = Mathf.FloorToInt((b.x - origin.x) / cellSize), y1 = Mathf.FloorToInt((b.z - origin.z) / cellSize);
        int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx - dy, n = 0;

        while (true)
        {
            if (x0 >= 0 && y0 >= 0 && x0 < W && y0 < H) { cells[y0 * W + x0] = WALL; n++; }
            if (x0 == x1 && y0 == y1) break;
            int e2 = err * 2;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
        return n;
    }

    void Dilate()
    {
        var src = (byte[])cells.Clone();
        for (int y = 1; y < H - 1; y++)
        for (int x = 1; x < W - 1; x++)
        {
            if (src[y * W + x] != LAND) continue;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int i = (y + dy) * W + (x + dx);
                if (cells[i] == UNKNOWN) cells[i] = LAND;
            }
        }
    }

    void Seed(int x, int y, Queue<int> queue)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        int i = y * W + x;
        if (cells[i] != UNKNOWN) return;   // 육지·벽·이미 바다면 통과 못 함
        cells[i] = SEA;
        queue.Enqueue(i);
    }

    /// <summary>가로 방향 런렝스로 병합해 쿼드 수를 크게 줄인다</summary>
    void BuildLandMesh()
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        for (int y = 0; y < H; y++)
        {
            int runStart = -1;
            for (int x = 0; x <= W; x++)
            {
                bool isLand = x < W && cells[y * W + x] != SEA;
                if (isLand && runStart < 0) runStart = x;
                else if (!isLand && runStart >= 0)
                {
                    float x0 = origin.x + runStart * cellSize, x1 = origin.x + x * cellSize;
                    float z0 = origin.z + y * cellSize, z1 = z0 + cellSize;
                    int s = verts.Count;
                    verts.Add(new Vector3(x0, -5f, z0));
                    verts.Add(new Vector3(x0, -5f, z1));
                    verts.Add(new Vector3(x1, -5f, z1));
                    verts.Add(new Vector3(x1, -5f, z0));
                    tris.Add(s); tris.Add(s + 1); tris.Add(s + 2);
                    tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
                    runStart = -1;
                }
            }
        }
        Create("Land", verts, tris, landColor, 2890);
        Debug.Log($"육지 메시 쿼드 {tris.Count / 6:N0}개");
    }

    void Create(string name, List<Vector3> verts, List<int> tris, Color col, int queue)
    {
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        var go = new GameObject($"Sea_{name}");
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        var mat = new Material(Shader.Find("Sprites/Default")) { color = col, renderQueue = queue };
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;

        mats.Add(mat);
        baseColors.Add(col);
    }
}
