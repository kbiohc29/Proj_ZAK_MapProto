using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// M6: 좀비 군중 흐름 시뮬레이션.
///
/// 핵심 설계: 개체 단위가 아니라 **도로 세그먼트별 밀도 수치**로 시뮬레이션한다.
/// - 세그먼트당 좀비 '마리 수'를 float로 보관
/// - 매 틱마다 목표(생존자 신호)로 향하는 방향의 이웃 세그먼트로 일정 비율이 흘러감
/// - 화면에는 밀도에 비례한 개수의 점을 뿌려서 물량감을 표현 (렌더링과 시뮬레이션 분리)
/// 덕분에 수만 마리도 성능 문제 없이 굴러간다.
///
/// 세팅: 빈 GameObject에 부착. RoadNetwork 자동 참조.
/// </summary>
public class ZombieFlow : MonoBehaviour
{
    [Header("Refs")]
    public WalkGrid walkGrid;

    [Tooltip("흐름 셀 = WalkGrid 셀 × 이 값 (기본 5칸 = 20m)")]
    public int coarseFactor = 5;

    [Header("Simulation")]
    public int initialHordes = 14;           // 초기 무리 수
    public float hordeSize = 260f;           // 무리당 좀비 수
    public float tickInterval = 0.5f;        // 시뮬 틱 간격(초)
    [Range(0f, 1f)] public float flowRate = 0.35f;  // 틱당 이웃으로 흘러가는 비율
    public float spawnRadius = 4000f;        // 초기 무리 생성 반경(m)

    [Header("Targets")]
    public int survivorCount = 5;            // 좀비가 모여드는 목표 지점 수
    public float retargetInterval = 25f;     // 목표 재평가 주기(초)

    [Header("Visual")]
    public float dotSize = 6f;               // 좀비 점 크기(m)
    public int dotsPerZombie = 1;            // 좀비 몇 마리당 점 하나 (성능 조절용)
    public int maxDots = 24000;              // 화면 점 상한
    public Color zombieColor = new Color(0.55f, 0.15f, 0.18f);

    // ---- 밀도 상태 ----
    // 키: coarse 셀 인덱스. 값: 좀비 수
    readonly Dictionary<int, float> density = new();
    readonly List<int> activeNodes = new();
    readonly Dictionary<int, int> nextHop = new();
    readonly HashSet<int> targetNodes = new();
    public List<Vector3> Targets { get; } = new();

    int cw, ch;
    float cellSizeM;

    int CoarseIndex(int cx, int cy) => cy * cw + cx;
    Vector3 CoarseToWorld(int idx) => walkGrid.CellToWorld(
        (idx % cw) * coarseFactor + coarseFactor / 2,
        (idx / cw) * coarseFactor + coarseFactor / 2);
    bool CellWalkable(int idx) => walkGrid.IsWalkable(
        (idx % cw) * coarseFactor + coarseFactor / 2,
        (idx / cw) * coarseFactor + coarseFactor / 2);

    float tickTimer, retargetTimer;
    bool ready;

    // ---- 렌더링 ----
    Mesh dotMesh;
    Material dotMat;
    GameObject dotGo;
    readonly List<Vector3> vbuf = new();
    readonly List<int> tbuf = new();

    public float TotalZombies { get; private set; }

    void Start()
    {
        if (walkGrid == null) walkGrid = FindFirstObjectByType<WalkGrid>();

        // 방어: 이 오브젝트가 공중/엉뚱한 곳에 생성돼도 자식 메시가 딸려 올라가지 않도록
        transform.position = Vector3.zero;
        transform.rotation = Quaternion.identity;
        transform.localScale = Vector3.one;

        dotGo = new GameObject("ZombieDots");
        dotGo.transform.SetParent(transform, false);
        dotMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        dotGo.AddComponent<MeshFilter>().sharedMesh = dotMesh;
        var mr = dotGo.AddComponent<MeshRenderer>();
        dotMat = new Material(Shader.Find("Sprites/Default")) { color = zombieColor };
        dotMat.renderQueue = 3100; // 도로(기본 투명 큐)보다 위에 그린다
        mr.sharedMaterial = dotMat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }

    void Update()
    {
        if (!ready)
        {
            if (walkGrid == null || !walkGrid.Ready) return;
            Init();
            ready = true;
        }

        tickTimer += Time.deltaTime;
        if (tickTimer >= tickInterval)
        {
            tickTimer -= tickInterval;
            SimulateTick();
            RebuildDots();
        }

        retargetTimer += Time.deltaTime;
        if (retargetTimer >= retargetInterval)
        {
            retargetTimer = 0f;
            PickTargets();
            BuildFlowField();
        }
    }

    // ---------- 초기화 ----------

    void Init()
    {
        cw = Mathf.Max(1, walkGrid.Width / coarseFactor);
        ch = Mathf.Max(1, walkGrid.Height / coarseFactor);
        cellSizeM = walkGrid.cellSize * coarseFactor;

        PickTargets();
        BuildFlowField();

        int placed = 0, guard = 0;
        while (placed < initialHordes && guard++ < 20000)
        {
            int idx = Random.Range(0, cw * ch);
            if (!CellWalkable(idx)) continue;
            if (CoarseToWorld(idx).sqrMagnitude > spawnRadius * spawnRadius) continue;

            density[idx] = density.TryGetValue(idx, out float d) ? d + hordeSize : hordeSize;
            placed++;
        }
        RefreshActive();
        Debug.Log($"좀비 무리 {placed}개 배치 (총 {placed * hordeSize:N0}마리), " +
                  $"흐름격자 {cw}x{ch} ({cellSizeM}m), 목표 {Targets.Count}개");
    }

    /// <summary>좀비가 모여들 목표 지점(생존자 신호)을 도로 노드 중에서 선정</summary>
    void PickTargets()
    {
        Targets.Clear();
        targetNodes.Clear();
        int guard = 0;
        while (targetNodes.Count < survivorCount && guard++ < 20000)
        {
            int idx = Random.Range(0, cw * ch);
            if (!CellWalkable(idx) || targetNodes.Contains(idx)) continue;
            Vector3 w = CoarseToWorld(idx);
            if (w.sqrMagnitude > spawnRadius * spawnRadius) continue;
            targetNodes.Add(idx);
            Targets.Add(w);
        }
    }

    /// <summary>
    /// 흐름장(flow field) 생성: 모든 목표에서 동시에 BFS를 퍼뜨려
    /// 각 노드가 '가장 가까운 목표로 가는 다음 노드'를 알게 한다.
    /// A*를 무리마다 돌리는 것보다 훨씬 싸다 (전체 한 번에 O(V+E)).
    /// </summary>
    void BuildFlowField()
    {
        nextHop.Clear();
        var dist = new Dictionary<int, float>();
        var queue = new Queue<int>();
        foreach (int t in targetNodes) { dist[t] = 0f; queue.Enqueue(t); }

        int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dy = { 0, 0, 1, -1, 1, -1, 1, -1 };

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int cx = cur % cw, cy = cur / cw;
            float cd = dist[cur];

            for (int i = 0; i < 8; i++)
            {
                int nx = cx + dx[i], ny = cy + dy[i];
                if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                int nid = CoarseIndex(nx, ny);
                if (!CellWalkable(nid)) continue;

                float nd = cd + (i >= 4 ? 1.414f : 1f) * cellSizeM;
                if (dist.TryGetValue(nid, out float old) && old <= nd) continue;

                dist[nid] = nd;
                nextHop[nid] = cur;
                queue.Enqueue(nid);
            }
        }
    }

    // ---------- 시뮬레이션 ----------

    void SimulateTick()
    {
        var delta = new Dictionary<int, float>();

        foreach (int node in activeNodes)
        {
            if (!density.TryGetValue(node, out float amount) || amount <= 0.01f) continue;
            if (!nextHop.TryGetValue(node, out int next)) continue; // 갈 길 없음 → 정체

            float moving = amount * flowRate;
            delta[node] = delta.TryGetValue(node, out float a) ? a - moving : -moving;
            delta[next] = delta.TryGetValue(next, out float b) ? b + moving : moving;
        }

        foreach (var kv in delta)
        {
            float v = (density.TryGetValue(kv.Key, out float cur) ? cur : 0f) + kv.Value;
            if (v <= 0.01f) density.Remove(kv.Key);
            else density[kv.Key] = v;
        }

        RefreshActive();
    }

    void RefreshActive()
    {
        activeNodes.Clear();
        TotalZombies = 0f;
        foreach (var kv in density)
        {
            activeNodes.Add(kv.Key);
            TotalZombies += kv.Value;
        }
    }

    /// <summary>월드 좌표 주변 radius(m) 안의 좀비 수 — 캐릭터 피격 판정/위험도 조회용</summary>
    public float ZombiesNear(Vector3 pos, float radius)
    {
        if (!ready) return 0f;
        float sum = 0f, r2 = radius * radius;
        foreach (int node in activeNodes)
            if ((CoarseToWorld(node) - pos).sqrMagnitude <= r2) sum += density[node];
        return sum;
    }

    // ---------- 렌더링 ----------

    void RebuildDots()
    {
        vbuf.Clear();
        tbuf.Clear();
        float half = dotSize * 0.5f;
        int dots = 0;

        foreach (int node in activeNodes)
        {
            float amount = density[node];
            int n = Mathf.Min(Mathf.CeilToInt(amount / Mathf.Max(1, dotsPerZombie)), 400);
            Vector3 center = CoarseToWorld(node);
            float spread = cellSizeM * 0.5f;

            for (int i = 0; i < n && dots < maxDots; i++, dots++)
            {
                // 결정론적 지터 (틱마다 튀지 않게 노드 ID + 인덱스 해시)
                float a = Hash(node * 31 + i) * Mathf.PI * 2f;
                float r = Mathf.Sqrt(Hash(node * 57 + i * 7)) * spread;
                Vector3 p = center + new Vector3(Mathf.Cos(a) * r, 2.0f, Mathf.Sin(a) * r);

                int s = vbuf.Count;
                vbuf.Add(p + new Vector3(-half, 0, -half));
                vbuf.Add(p + new Vector3(-half, 0,  half));
                vbuf.Add(p + new Vector3( half, 0,  half));
                vbuf.Add(p + new Vector3( half, 0, -half));
                tbuf.Add(s); tbuf.Add(s + 1); tbuf.Add(s + 2);
                tbuf.Add(s); tbuf.Add(s + 2); tbuf.Add(s + 3);
            }
            if (dots >= maxDots) break;
        }

        dotMesh.Clear();
        dotMesh.SetVertices(vbuf);
        dotMesh.SetTriangles(tbuf, 0);
        dotMesh.RecalculateBounds();
    }

    static float Hash(int n)
    {
        float h = Mathf.Sin(n * 0.0173f) * 43758.5453f;
        return h - Mathf.Floor(h);
    }
}
