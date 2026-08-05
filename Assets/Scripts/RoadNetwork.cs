using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// M1: Overpass(OSM) 도로 데이터를 읽어
/// 1) 도로를 라인 메시로 렌더링하고
/// 2) 경로 탐색용 노드 그래프를 만든다.
///
/// 데이터: overpass-turbo에서 "out geom" 쿼리 결과를 raw JSON으로 저장한 파일.
/// 그래프 연결성은 OSM 노드 ID 공유로 자동 확보된다 (교차로 = 같은 노드 ID).
/// 프로토 단순화: 일방통행(oneway) 무시, 모든 도로 양방향 취급.
/// </summary>
public class RoadNetwork : MonoBehaviour
{
    [Header("Data")]
    public string fileName = "busan_roads.json";

    [Header("Visual")]
    public float roadWidth = 6f;          // 도로 라인 폭(m)
    public float chunkSize = 2000f;
    public Color roadColor = new Color(0.30f, 0.30f, 0.34f);
    public Color nightRoadColor = new Color(0.03f, 0.035f, 0.05f);

    Material sharedRoadMat;

    // ---- 그래프 ----
    public Dictionary<long, Vector3> NodePos = new();
    public Dictionary<long, List<(long to, float cost)>> Adj = new();
    public bool Ready { get; private set; }

    // 최근접 노드 검색용 공간 격자
    readonly Dictionary<Vector2Int, List<long>> grid = new();
    const float GridCell = 500f;

    // ---- Overpass JSON 스키마 (JsonUtility용) ----
    [System.Serializable] class OverpassRoot { public OverpassElement[] elements; }
    [System.Serializable] class OverpassElement
    {
        public string type;
        public long id;
        public long[] nodes;
        public GeomPt[] geometry;
    }
    [System.Serializable] class GeomPt { public double lat; public double lon; }

    void Start()
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"도로 파일이 없습니다: {path}");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var root = JsonUtility.FromJson<OverpassRoot>(File.ReadAllText(path));
        Debug.Log($"JSON 파싱 {sw.ElapsedMilliseconds}ms, way {root.elements?.Length ?? 0}개");

        BuildGraphAndMesh(root);
        sw.Stop();
        Debug.Log($"도로 그래프 완성 — 노드 {NodePos.Count:N0}개, {sw.ElapsedMilliseconds}ms");
        Ready = true;
    }

    void Update()
    {
        if (sharedRoadMat == null) return;
        float night = Shader.GetGlobalFloat("_ZakNight");
        sharedRoadMat.color = Color.Lerp(roadColor, nightRoadColor, night);
    }

    void BuildGraphAndMesh(OverpassRoot root)
    {
        sharedRoadMat = new Material(Shader.Find("Sprites/Default")) { color = roadColor };
        var chunks = new Dictionary<Vector2Int, (List<Vector3> v, List<Color> c, List<int> t)>();

        foreach (var el in root.elements)
        {
            if (el.type != "way" || el.nodes == null || el.geometry == null) continue;
            int n = Mathf.Min(el.nodes.Length, el.geometry.Length);

            for (int i = 0; i < n; i++)
            {
                long id = el.nodes[i];
                if (!NodePos.ContainsKey(id))
                {
                    Vector3 p = GeoUtil.LonLatToLocal(el.geometry[i].lon, el.geometry[i].lat);
                    NodePos[id] = p;
                    var cell = new Vector2Int(
                        Mathf.FloorToInt(p.x / GridCell), Mathf.FloorToInt(p.z / GridCell));
                    if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new List<long>();
                    list.Add(id);
                }
            }

            for (int i = 0; i < n - 1; i++)
            {
                long a = el.nodes[i], b = el.nodes[i + 1];
                Vector3 pa = NodePos[a], pb = NodePos[b];
                float cost = Vector3.Distance(pa, pb);
                AddEdge(a, b, cost);
                AddEdge(b, a, cost); // 프로토: 양방향

                AddSegmentQuad(chunks, pa, pb);
            }
        }

        foreach (var kv in chunks) CreateChunkObject(kv.Key, kv.Value);
    }

    void AddEdge(long from, long to, float cost)
    {
        if (!Adj.TryGetValue(from, out var list)) Adj[from] = list = new List<(long, float)>(3);
        list.Add((to, cost));
    }

    /// <summary>월드 좌표에서 가장 가까운 그래프 노드. 주변 격자 확장 탐색.</summary>
    public long NearestNode(Vector3 pos)
    {
        var center = new Vector2Int(
            Mathf.FloorToInt(pos.x / GridCell), Mathf.FloorToInt(pos.z / GridCell));

        long best = -1;
        float bestD = float.MaxValue;
        for (int ring = 0; ring <= 8; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
            {
                if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != ring) continue; // 링 테두리만
                if (!grid.TryGetValue(new Vector2Int(center.x + dx, center.y + dy), out var list))
                    continue;
                foreach (long id in list)
                {
                    float d = (NodePos[id] - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = id; }
                }
            }
            if (best >= 0 && ring >= 1) break; // 찾은 뒤 한 링 더 보고 종료
        }
        return best;
    }

    // ---- 도로 라인 메시 ----

    void AddSegmentQuad(Dictionary<Vector2Int, (List<Vector3> v, List<Color> c, List<int> t)> chunks,
                        Vector3 a, Vector3 b)
    {
        Vector3 dir = (b - a);
        if (dir.sqrMagnitude < 0.01f) return;
        Vector3 side = Vector3.Cross(dir.normalized, Vector3.up) * (roadWidth * 0.5f);

        Vector3 mid = (a + b) * 0.5f;
        var key = new Vector2Int(
            Mathf.FloorToInt(mid.x / chunkSize), Mathf.FloorToInt(mid.z / chunkSize));
        if (!chunks.TryGetValue(key, out var buf))
        {
            buf = (new List<Vector3>(8192), new List<Color>(8192), new List<int>(16384));
            chunks[key] = buf;
        }

        int s = buf.v.Count;
        // 도로는 점보다 살짝 아래(y=-0.5)에 깔아 z-fighting 방지
        buf.v.Add(a - side + Vector3.down * 0.5f);
        buf.v.Add(a + side + Vector3.down * 0.5f);
        buf.v.Add(b + side + Vector3.down * 0.5f);
        buf.v.Add(b - side + Vector3.down * 0.5f);
        for (int i = 0; i < 4; i++) buf.c.Add(Color.white); // 실제 색은 sharedRoadMat이 담당
        buf.t.Add(s); buf.t.Add(s + 1); buf.t.Add(s + 2);
        buf.t.Add(s); buf.t.Add(s + 2); buf.t.Add(s + 3);
    }

    void CreateChunkObject(Vector2Int key, (List<Vector3> v, List<Color> c, List<int> t) buf)
    {
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(buf.v);
        mesh.SetColors(buf.c);
        mesh.SetTriangles(buf.t, 0);
        mesh.RecalculateBounds();

        var go = new GameObject($"Road_{key.x}_{key.y}");
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = sharedRoadMat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }
}
