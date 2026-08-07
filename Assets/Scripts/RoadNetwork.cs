using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 도로: 경로 탐색용 그래프 + 도로다운 외형(등급별 폭 + 케이싱).
/// v2: MiniJson으로 태그를 읽어 highway 등급별 폭을 적용하고,
///     노면 아래에 살짝 넓은 케이싱을 깔아 도로가 배경에서 분리돼 보이게 한다.
/// 데이터: overpass "out geom tags;" 결과 (태그가 없으면 기본 폭 사용)
/// </summary>
public class RoadNetwork : MonoBehaviour
{
    [Header("Data")]
    public string fileName = "busan_roads.json";

    [Header("Visual")]
    [Tooltip("기준 폭(m). 등급별 배율이 곱해진다")]
    public float roadWidth = 9f;
    public float chunkSize = 2000f;
    public Color roadColor = new Color(0.34f, 0.34f, 0.36f);
    public Color casingColor = new Color(0.20f, 0.20f, 0.22f);
    [Tooltip("케이싱이 노면보다 넓은 정도(m)")]
    public float casingExtra = 4f;

    [Header("Night")]
    [Range(0f, 1f)] public float nightDarken = 0.72f;

    // ---- 그래프 ----
    public Dictionary<long, Vector3> NodePos = new();
    public Dictionary<long, List<(long to, float cost)>> Adj = new();
    public bool Ready { get; private set; }

    /// <summary>다리·고가 위의 노드 (육지 판정에서 제외하기 위함)</summary>
    public HashSet<long> BridgeNodes { get; } = new();

    readonly Dictionary<Vector2Int, List<long>> grid = new();
    const float GridCell = 500f;

    readonly List<Material> mats = new();
    readonly List<Color> baseColors = new();

    static float WidthFor(string highway)
    {
        switch (highway)
        {
            case "motorway": case "motorway_link": case "trunk": case "trunk_link": return 2.2f;
            case "primary": case "primary_link": return 1.6f;
            case "secondary": case "secondary_link": return 1.25f;
            case "tertiary": case "tertiary_link": return 1.0f;
            case "residential": case "unclassified": case "service": return 0.65f;
            default: return 0.85f;
        }
    }

    void Start()
    {
        transform.position = Vector3.zero;
        transform.rotation = Quaternion.identity;

        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"도로 파일이 없습니다: {path}");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var root = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
        Build(root);
        sw.Stop();
        Debug.Log($"도로 그래프 완성 — 노드 {NodePos.Count:N0}개, {sw.ElapsedMilliseconds}ms");
        Ready = true;
    }

    void Update()
    {
        float night = Shader.GetGlobalFloat("_ZakNight");
        for (int i = 0; i < mats.Count; i++)
            mats[i].color = Color.Lerp(baseColors[i], baseColors[i] * (1f - nightDarken), night);
    }

    void Build(Dictionary<string, object> root)
    {
        if (root == null || !root.TryGetValue("elements", out var eo) || eo is not List<object> elements)
        {
            Debug.LogError("도로 JSON 형식을 읽지 못했습니다");
            return;
        }

        var casing = new Dictionary<Vector2Int, (List<Vector3> v, List<int> t)>();
        var surface = new Dictionary<Vector2Int, (List<Vector3> v, List<int> t)>();

        foreach (object elObj in elements)
        {
            if (elObj is not Dictionary<string, object> el) continue;
            if (!el.TryGetValue("type", out var ty) || (string)ty != "way") continue;
            if (!el.TryGetValue("nodes", out var no) || no is not List<object> nodes) continue;
            if (!el.TryGetValue("geometry", out var go) || go is not List<object> geom) continue;

            string highway = null;
            bool isBridge = false;
            if (el.TryGetValue("tags", out var to) && to is Dictionary<string, object> tags)
            {
                if (tags.TryGetValue("highway", out var hw)) highway = hw as string;
                if (tags.TryGetValue("bridge", out var br) && (br as string) != "no") isBridge = true;
                if (tags.TryGetValue("man_made", out var mm) && (mm as string) == "bridge") isBridge = true;
            }
            float w = roadWidth * WidthFor(highway);

            int n = Mathf.Min(nodes.Count, geom.Count);

            for (int i = 0; i < n; i++)
            {
                long id = (long)(double)nodes[i];
                if (NodePos.ContainsKey(id)) continue;
                var p = (Dictionary<string, object>)geom[i];
                Vector3 wp = GeoUtil.LonLatToLocal((double)p["lon"], (double)p["lat"]);
                NodePos[id] = wp;
                if (isBridge) BridgeNodes.Add(id);
                var cell = new Vector2Int(Mathf.FloorToInt(wp.x / GridCell), Mathf.FloorToInt(wp.z / GridCell));
                if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new List<long>();
                list.Add(id);
            }

            for (int i = 0; i < n - 1; i++)
            {
                long a = (long)(double)nodes[i], b = (long)(double)nodes[i + 1];
                if (!NodePos.ContainsKey(a) || !NodePos.ContainsKey(b)) continue;
                Vector3 pa = NodePos[a], pb = NodePos[b];
                float cost = Vector3.Distance(pa, pb);
                AddEdge(a, b, cost);
                AddEdge(b, a, cost);

                AddSegment(casing, pa, pb, w + casingExtra);
                AddSegment(surface, pa, pb, w);
            }
        }

        foreach (var kv in casing)
            CreateMesh($"RoadCasing_{kv.Key.x}_{kv.Key.y}", kv.Value.v, kv.Value.t, casingColor, -1.2f, 2960);
        foreach (var kv in surface)
            CreateMesh($"Road_{kv.Key.x}_{kv.Key.y}", kv.Value.v, kv.Value.t, roadColor, -1.0f, 2970);
    }

    void AddEdge(long from, long to, float cost)
    {
        if (!Adj.TryGetValue(from, out var list)) Adj[from] = list = new List<(long, float)>(3);
        list.Add((to, cost));
    }

    void AddSegment(Dictionary<Vector2Int, (List<Vector3> v, List<int> t)> chunks,
                    Vector3 a, Vector3 b, float width)
    {
        Vector3 dir = b - a;
        if (dir.sqrMagnitude < 0.01f) return;
        Vector3 side = Vector3.Cross(dir.normalized, Vector3.up) * (width * 0.5f);
        Vector3 ext = dir.normalized * (width * 0.5f);   // 교차로 이음매 메우기

        Vector3 mid = (a + b) * 0.5f;
        var key = new Vector2Int(Mathf.FloorToInt(mid.x / chunkSize), Mathf.FloorToInt(mid.z / chunkSize));
        if (!chunks.TryGetValue(key, out var buf))
        {
            buf = (new List<Vector3>(8192), new List<int>(16384));
            chunks[key] = buf;
        }

        int s = buf.v.Count;
        buf.v.Add(a - side - ext); buf.v.Add(a + side - ext);
        buf.v.Add(b + side + ext); buf.v.Add(b - side + ext);
        buf.t.Add(s); buf.t.Add(s + 1); buf.t.Add(s + 2);
        buf.t.Add(s); buf.t.Add(s + 2); buf.t.Add(s + 3);
    }

    void CreateMesh(string name, List<Vector3> verts, List<int> tris, Color col, float y, int queue)
    {
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        var shifted = new List<Vector3>(verts.Count);
        foreach (var p in verts) shifted.Add(new Vector3(p.x, y, p.z));
        mesh.SetVertices(shifted);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        var go = new GameObject(name);
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

    /// <summary>월드 좌표에서 가장 가까운 그래프 노드</summary>
    public long NearestNode(Vector3 pos)
    {
        var center = new Vector2Int(Mathf.FloorToInt(pos.x / GridCell), Mathf.FloorToInt(pos.z / GridCell));
        long best = -1;
        float bestD = float.MaxValue;

        for (int ring = 0; ring <= 8; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
            {
                if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != ring) continue;
                if (!grid.TryGetValue(new Vector2Int(center.x + dx, center.y + dy), out var list)) continue;
                foreach (long id in list)
                {
                    float d = (NodePos[id] - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = id; }
                }
            }
            if (best >= 0 && ring >= 1) break;
        }
        return best;
    }
}
