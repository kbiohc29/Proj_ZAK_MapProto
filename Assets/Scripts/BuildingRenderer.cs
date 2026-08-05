using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// v2 변경점 (M7 비주얼):
/// - ZAK/BuildingUnlit 셰이더 사용 → 소팅 문제 해결, 프로시저럴 밤 창문
/// - 흰색 탈피: 높이 기반 색조(저층 웜톤 → 고층 쿨톤) + 건물별 미세 색 편차
/// - N 키 낮/밤은 2초에 걸쳐 부드럽게 전환 (Shader.SetGlobalFloat)
/// </summary>
public class BuildingRenderer : MonoBehaviour
{
    [Header("Data")]
    public string fileName = "busan_buildings.json";

    [Header("Shape")]
    public float floorHeight = 3.2f;
    public int defaultLevels = 2;
    public float chunkSize = 1000f;

    [Header("Look")]
    public Color lowColor  = new Color(0.82f, 0.78f, 0.72f); // 저층: 웜 베이지
    public Color highColor = new Color(0.72f, 0.75f, 0.80f); // 고층: 쿨 그레이
    public float highColorAtHeight = 60f;                    // 이 높이에서 완전 쿨톤
    public float colorJitter = 0.06f;                        // 건물별 밝기 편차 ±6%
    public Color roofTint = new Color(1.06f, 1.05f, 1.03f);  // 지붕은 살짝 밝게

    [Header("Night")]
    [Range(0f, 1f)]
    [Tooltip("창문 점등률. 0.32=평시 도시, 0.03~0.05=아포칼립스(빛=생존자 신호)")]
    public float litRatio = 0.32f;
    [Range(0f, 1f)]
    [Tooltip("불 켜진 건물 안에서 실제로 빛나는 창문의 비율")]
    public float windowLitRatio = 0.5f;
    public float nightFadeSeconds = 2f;
    public Color dayBackground   = new Color(0.13f, 0.14f, 0.16f);
    public Color nightBackground = new Color(0.015f, 0.02f, 0.05f);

    bool isNight;
    float nightAmt; // 0=낮, 1=밤

    void Start()
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"건물 파일이 없습니다: {path}");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var root = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
        Debug.Log($"건물 JSON 파싱 {sw.ElapsedMilliseconds}ms");

        int built = Build(root);
        sw.Stop();
        Debug.Log($"건물 {built:N0}동 생성, {sw.ElapsedMilliseconds}ms");

        Shader.SetGlobalFloat("_ZakNight", 0f);
    }

    void Update()
    {
        Shader.SetGlobalFloat("_ZakLitRatio", litRatio); // 인스펙터에서 실시간 조절 가능
        Shader.SetGlobalFloat("_ZakWinRatio", windowLitRatio);

        if (Input.GetKeyDown(KeyCode.N)) isNight = !isNight;

        float target = isNight ? 1f : 0f;
        if (!Mathf.Approximately(nightAmt, target))
        {
            nightAmt = Mathf.MoveTowards(nightAmt, target, Time.deltaTime / nightFadeSeconds);
            Shader.SetGlobalFloat("_ZakNight", nightAmt);

            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.Lerp(dayBackground, nightBackground, nightAmt);
            }
        }
    }

    int Build(Dictionary<string, object> root)
    {
        var chunks = new Dictionary<Vector2Int, (List<Vector3> v, List<Color> c, List<int> t)>();
        var ring = new List<Vector3>(64);
        int built = 0;

        if (!(root?["elements"] is List<object> elements)) return 0;

        foreach (object elObj in elements)
        {
            if (elObj is not Dictionary<string, object> el) continue;
            if (!el.TryGetValue("type", out var ty) || (string)ty != "way") continue;
            if (!el.TryGetValue("geometry", out var geomObj) || geomObj is not List<object> geom) continue;
            if (geom.Count < 3) continue;

            ring.Clear();
            foreach (object pObj in geom)
            {
                var p = (Dictionary<string, object>)pObj;
                ring.Add(GeoUtil.LonLatToLocal((double)p["lon"], (double)p["lat"]));
            }
            if (ring.Count > 1 && (ring[0] - ring[^1]).sqrMagnitude < 0.01f)
                ring.RemoveAt(ring.Count - 1);
            if (ring.Count < 3) continue;

            float height = ReadHeight(el);
            if (SignedArea(ring) < 0f) ring.Reverse();

            Vector3 centroid = Vector3.zero;
            foreach (var p in ring) centroid += p;
            centroid /= ring.Count;

            // ---- 건물 색: 높이 기반 + 좌표 해시 편차 ----
            float hT = Mathf.Clamp01(height / highColorAtHeight);
            Color bCol = Color.Lerp(lowColor, highColor, hT);
            float jitter = 1f + (Hash(centroid) - 0.5f) * 2f * colorJitter;
            bCol *= jitter;
            // 알파 채널에 건물별 해시를 실어 셰이더에 전달 (유인 건물 판정용)
            bCol.a = Hash(centroid * 1.37f + Vector3.one * 5.19f);

            var key = new Vector2Int(
                Mathf.FloorToInt(centroid.x / chunkSize),
                Mathf.FloorToInt(centroid.z / chunkSize));
            if (!chunks.TryGetValue(key, out var buf))
            {
                buf = (new List<Vector3>(16384), new List<Color>(16384), new List<int>(32768));
                chunks[key] = buf;
            }

            AddWalls(buf, ring, height, bCol);
            AddRoof(buf, ring, height, bCol);
            built++;
        }

        foreach (var kv in chunks) CreateChunkObject(kv.Key, kv.Value);
        return built;
    }

    static float Hash(Vector3 p)
    {
        float h = Mathf.Sin(p.x * 12.9898f + p.z * 78.233f) * 43758.5453f;
        return h - Mathf.Floor(h);
    }

    float ReadHeight(Dictionary<string, object> el)
    {
        if (el.TryGetValue("tags", out var tagsObj) && tagsObj is Dictionary<string, object> tags)
        {
            if (tags.TryGetValue("building:levels", out var lv) &&
                float.TryParse(lv as string, NumberStyles.Float, CultureInfo.InvariantCulture, out float levels))
                return Mathf.Max(1f, levels) * floorHeight;

            if (tags.TryGetValue("height", out var h) &&
                float.TryParse((h as string)?.Replace("m", "").Trim(),
                               NumberStyles.Float, CultureInfo.InvariantCulture, out float meters))
                return Mathf.Max(floorHeight, meters);
        }
        return defaultLevels * floorHeight;
    }

    /// <summary>XZ 평면 폴리곤의 부호 있는 면적. 양수면 CCW.</summary>
    static float SignedArea(List<Vector3> ring)
    {
        float sum = 0f;
        for (int i = 0; i < ring.Count; i++)
        {
            Vector3 a = ring[i];
            Vector3 b = ring[(i + 1) % ring.Count];
            sum += a.x * b.z - b.x * a.z;
        }
        return sum * 0.5f;
    }

    void AddWalls((List<Vector3> v, List<Color> c, List<int> t) buf, List<Vector3> ring, float height, Color bCol)
    {
        Vector3 lightDir = new Vector3(0.55f, 0f, 0.83f);

        for (int i = 0; i < ring.Count; i++)
        {
            Vector3 a = ring[i];
            Vector3 b = ring[(i + 1) % ring.Count];
            Vector3 edge = b - a;
            if (edge.sqrMagnitude < 0.01f) continue;

            Vector3 normal = Vector3.Cross(Vector3.up, edge).normalized;
            float brightness = 0.62f + 0.30f * (Vector3.Dot(normal, lightDir) * 0.5f + 0.5f);
            Color col = bCol * brightness;
            col.a = bCol.a; // 건물 해시 유지

            int s = buf.v.Count;
            buf.v.Add(a);
            buf.v.Add(b);
            buf.v.Add(b + Vector3.up * height);
            buf.v.Add(a + Vector3.up * height);
            for (int k = 0; k < 4; k++) buf.c.Add(col);
            buf.t.Add(s); buf.t.Add(s + 2); buf.t.Add(s + 1);
            buf.t.Add(s); buf.t.Add(s + 3); buf.t.Add(s + 2);
        }
    }

    void AddRoof((List<Vector3> v, List<Color> c, List<int> t) buf, List<Vector3> ring, float height, Color bCol)
    {
        List<int> tris = EarClip(ring);
        int s = buf.v.Count;
        Color rc = bCol * 1.0f;
        rc.r *= roofTint.r; rc.g *= roofTint.g; rc.b *= roofTint.b;
        rc.a = bCol.a; // 건물 해시 유지

        foreach (var p in ring)
        {
            buf.v.Add(p + Vector3.up * height);
            buf.c.Add(rc);
        }
        for (int i = 0; i < tris.Count; i += 3)
        {
            buf.t.Add(s + tris[i]);
            buf.t.Add(s + tris[i + 2]);
            buf.t.Add(s + tris[i + 1]);
        }
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
                int i0 = idx[(i - 1 + idx.Count) % idx.Count];
                int i1 = idx[i];
                int i2 = idx[(i + 1) % idx.Count];
                Vector3 a = ring[i0], b = ring[i1], c = ring[i2];

                if (Cross2(b - a, c - b) <= 0f) continue;

                bool anyInside = false;
                foreach (int j in idx)
                {
                    if (j == i0 || j == i1 || j == i2) continue;
                    if (PointInTri(ring[j], a, b, c)) { anyInside = true; break; }
                }
                if (anyInside) continue;

                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                idx.RemoveAt(i);
                cut = true;
                break;
            }
            if (!cut) break;
        }

        if (idx.Count == 3)
        {
            tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]);
        }
        else if (idx.Count > 3)
        {
            for (int i = 1; i < idx.Count - 1; i++)
            { tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]); }
        }
        return tris;
    }

    static float Cross2(Vector3 u, Vector3 v) => u.x * v.z - u.z * v.x;

    static bool PointInTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float d1 = Cross2(b - a, p - a);
        float d2 = Cross2(c - b, p - b);
        float d3 = Cross2(a - c, p - c);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    void CreateChunkObject(Vector2Int key, (List<Vector3> v, List<Color> c, List<int> t) buf)
    {
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(buf.v);
        mesh.SetColors(buf.c);
        mesh.SetTriangles(buf.t, 0);
        mesh.RecalculateBounds();

        var go = new GameObject($"Bldg_{key.x}_{key.y}");
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();

        var shader = Shader.Find("ZAK/BuildingUnlit");
        if (shader == null)
        {
            Debug.LogError("ZAK/BuildingUnlit 셰이더를 찾을 수 없습니다. Assets/Shaders/BuildingUnlit.shader 확인");
            shader = Shader.Find("Sprites/Default");
        }
        mr.sharedMaterial = new Material(shader);
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }
}
