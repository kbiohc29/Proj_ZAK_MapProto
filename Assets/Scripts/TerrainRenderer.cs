using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 자연 지형 표현: 물(바다·강·호수), 산림, 공원, 해안선.
/// OSM Overpass JSON 또는 GeoJSON을 읽어 평평한 폴리곤으로 깐다.
///
/// 레이어 높이 (아래에서 위로)
///   -3.0 물 / -2.5 산림·공원 / -0.5 도로 / 0~ 건물
///
/// 세팅: 빈 GameObject에 부착. StreamingAssets에 busan_terrain.json 필요.
/// </summary>
public class TerrainRenderer : MonoBehaviour
{
    [Header("Data")]
    public string fileName = "busan_terrain.json";

    [Header("Colors")]
    public Color waterColor = new Color(0.07f, 0.13f, 0.20f);
    public Color forestColor = new Color(0.10f, 0.16f, 0.12f);
    public Color parkColor = new Color(0.13f, 0.20f, 0.14f);
    public Color coastlineColor = new Color(0.20f, 0.30f, 0.38f);

    [Header("Look")]
    public float coastlineWidth = 14f;
    [Tooltip("선형으로 들어오는 하천의 폭(m)")]
    public float riverWidth = 26f;
    public float colorJitter = 0.12f;

    [Header("Night")]
    [Range(0f, 1f)] public float nightDarken = 0.75f;

    readonly List<Material> mats = new();
    readonly List<Color> baseColors = new();

    /// <summary>SeaRenderer가 육지/바다 경계로 쓰는 해안선</summary>
    public List<Vector3[]> Coastlines { get; } = new();
    public bool TerrainReady { get; private set; }

    void Start()
    {
        transform.position = Vector3.zero;
        transform.rotation = Quaternion.identity;

        string path = System.IO.Path.Combine(Application.streamingAssetsPath, fileName);
        if (!System.IO.File.Exists(path))
        {
            Debug.LogWarning($"지형 파일이 없습니다: {path} (물/산 표현 생략)");
            TerrainReady = true;
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var root = MiniJson.Parse(System.IO.File.ReadAllText(path)) as Dictionary<string, object>;
        int n = Build(root);
        TerrainReady = true;
        sw.Stop();
        Debug.Log($"지형 {n:N0}개 폴리곤 생성, {sw.ElapsedMilliseconds}ms");
    }

    void Update()
    {
        // 밤에는 지형도 함께 어두워진다
        float night = Shader.GetGlobalFloat("_ZakNight");
        for (int i = 0; i < mats.Count; i++)
            mats[i].color = Color.Lerp(baseColors[i], baseColors[i] * (1f - nightDarken), night);
    }

    int Build(Dictionary<string, object> root)
    {
        // 종류별 버퍼
        var buffers = new Dictionary<string, (List<Vector3> v, List<int> t, Color c, float y)>
        {
            ["water"]  = (new List<Vector3>(), new List<int>(), waterColor,  -3.0f),
            ["forest"] = (new List<Vector3>(), new List<int>(), forestColor, -2.5f),
            ["park"]   = (new List<Vector3>(), new List<int>(), parkColor,   -2.4f),
            ["coast"]  = (new List<Vector3>(), new List<int>(), coastlineColor, -2.9f),
            ["river"]  = (new List<Vector3>(), new List<int>(), waterColor,  -2.95f),
        };

        if (!(root != null && root.TryGetValue("elements", out var eo) && eo is List<object> elements))
            return 0;

        var ring = new List<Vector3>(128);
        int count = 0;

        foreach (object elObj in elements)
        {
            if (elObj is not Dictionary<string, object> el) continue;

            string kind = Classify(el);
            if (kind == null) continue;

            // relation(멀티폴리곤): 큰 산림·강은 대부분 relation으로 들어온다
            if (el.TryGetValue("members", out var mo) && mo is List<object> members)
            {
                foreach (object mObj in members)
                {
                    if (mObj is not Dictionary<string, object> m) continue;
                    if (m.TryGetValue("role", out var role) && (role as string) == "inner") continue;
                    if (!m.TryGetValue("geometry", out var mg) || mg is not List<object> mgeom) continue;
                    if (mgeom.Count < 3) continue;

                    ring.Clear();
                    foreach (object pObj in mgeom)
                    {
                        var p = (Dictionary<string, object>)pObj;
                        ring.Add(GeoUtil.LonLatToLocal((double)p["lon"], (double)p["lat"]));
                    }
                    if (ring.Count > 1 && (ring[0] - ring[^1]).sqrMagnitude < 0.01f) ring.RemoveAt(ring.Count - 1);
                    if (ring.Count < 3) continue;

                    var mbuf = buffers[kind];
                    if (kind == "coast") AddPolyline(mbuf.v, mbuf.t, ring, coastlineWidth);
                    else AddPolygon(mbuf.v, mbuf.t, ring);
                    count++;
                }
                continue;
            }

            if (!el.TryGetValue("geometry", out var go) || go is not List<object> geom) continue;
            if (geom.Count < 2) continue;

            ring.Clear();
            foreach (object pObj in geom)
            {
                var p = (Dictionary<string, object>)pObj;
                ring.Add(GeoUtil.LonLatToLocal((double)p["lon"], (double)p["lat"]));
            }

            var buf = buffers[kind];
            if (kind == "coast")
            {
                AddPolyline(buf.v, buf.t, ring, coastlineWidth);
                Coastlines.Add(ring.ToArray());
            }
            else if (kind == "river")
            {
                AddPolyline(buf.v, buf.t, ring, riverWidth);
            }
            else
            {
                if (ring.Count > 1 && (ring[0] - ring[^1]).sqrMagnitude < 0.01f) ring.RemoveAt(ring.Count - 1);
                if (ring.Count < 3) continue;
                AddPolygon(buf.v, buf.t, ring);
            }
            count++;
        }

        foreach (var kv in buffers)
        {
            if (kv.Value.v.Count == 0) continue;
            CreateObject(kv.Key, kv.Value.v, kv.Value.t, kv.Value.c, kv.Value.y);
        }
        return count;
    }

    static string Classify(Dictionary<string, object> el)
    {
        if (!el.TryGetValue("tags", out var to) || to is not Dictionary<string, object> tags) return null;

        string Get(string k) => tags.TryGetValue(k, out var v) ? v as string : null;

        if (Get("natural") == "coastline") return "coast";
        if (Get("waterway") == "river" || Get("waterway") == "stream") return "river";
        if (Get("natural") == "water" || Get("waterway") == "riverbank" ||
            Get("landuse") == "reservoir" || Get("natural") == "bay") return "water";
        if (Get("natural") == "wood" || Get("landuse") == "forest") return "forest";
        if (Get("leisure") == "park" || Get("landuse") == "grass" ||
            Get("landuse") == "meadow" || Get("leisure") == "garden") return "park";
        return null;
    }

    // ---------- 지오메트리 ----------

    void AddPolygon(List<Vector3> v, List<int> t, List<Vector3> ring)
    {
        if (SignedArea(ring) < 0f) ring.Reverse();
        var tris = EarClip(ring);
        int s = v.Count;
        foreach (var p in ring) v.Add(p);
        for (int i = 0; i < tris.Count; i += 3)
        {
            t.Add(s + tris[i]); t.Add(s + tris[i + 2]); t.Add(s + tris[i + 1]);
        }
    }

    void AddPolyline(List<Vector3> v, List<int> t, List<Vector3> line, float width)
    {
        float half = width * 0.5f;
        for (int i = 0; i < line.Count - 1; i++)
        {
            Vector3 a = line[i], b = line[i + 1];
            Vector3 d = b - a;
            if (d.sqrMagnitude < 0.01f) continue;
            Vector3 side = Vector3.Cross(d.normalized, Vector3.up) * half;

            int s = v.Count;
            v.Add(a - side); v.Add(a + side); v.Add(b + side); v.Add(b - side);
            t.Add(s); t.Add(s + 1); t.Add(s + 2);
            t.Add(s); t.Add(s + 2); t.Add(s + 3);
        }
    }

    void CreateObject(string name, List<Vector3> verts, List<int> tris, Color col, float y)
    {
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        var shifted = new List<Vector3>(verts.Count);
        foreach (var p in verts) shifted.Add(new Vector3(p.x, y, p.z));
        mesh.SetVertices(shifted);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        var go = new GameObject($"Terrain_{name}");
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        var mat = new Material(Shader.Find("Sprites/Default")) { color = col };
        mat.renderQueue = 2900 + Mathf.RoundToInt(y);
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;

        mats.Add(mat);
        baseColors.Add(col);
    }

    static float SignedArea(List<Vector3> ring)
    {
        float sum = 0f;
        for (int i = 0; i < ring.Count; i++)
        {
            Vector3 a = ring[i], b = ring[(i + 1) % ring.Count];
            sum += a.x * b.z - b.x * a.z;
        }
        return sum * 0.5f;
    }

    static List<int> EarClip(List<Vector3> ring)
    {
        int n = ring.Count;
        var tris = new List<int>((n - 2) * 3);
        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) idx.Add(i);

        int guard = n * n + 10;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool cut = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int i0 = idx[(i - 1 + idx.Count) % idx.Count], i1 = idx[i], i2 = idx[(i + 1) % idx.Count];
                Vector3 a = ring[i0], b = ring[i1], c = ring[i2];
                if (Cross2(b - a, c - b) <= 0f) continue;

                bool inside = false;
                foreach (int j in idx)
                {
                    if (j == i0 || j == i1 || j == i2) continue;
                    if (PointInTri(ring[j], a, b, c)) { inside = true; break; }
                }
                if (inside) continue;

                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                idx.RemoveAt(i);
                cut = true;
                break;
            }
            if (!cut) break;
        }
        if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
        else if (idx.Count > 3)
            for (int i = 1; i < idx.Count - 1; i++) { tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]); }
        return tris;
    }

    static float Cross2(Vector3 u, Vector3 v) => u.x * v.z - u.z * v.x;

    static bool PointInTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float d1 = Cross2(b - a, p - a), d2 = Cross2(c - b, p - b), d3 = Cross2(a - c, p - c);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
