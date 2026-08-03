using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// M2: OSM 건물 footprint를 층수만큼 extrude해서 심플 3D 건물을 세운다.
/// - 층수: building:levels 태그 → 없으면 height 태그 → 둘 다 없으면 기본 2층
/// - 벽면은 방향에 따라 밝기를 달리해 (가짜 조명) 입체감을 냄. 라이팅 계산 없음
/// - N 키: 낮/밤 토글 (머티리얼 틴트 + 배경색만 바꾸는 최소 구현)
/// - 원근 카메라 수직 탑뷰 덕에 화면 가장자리 고층은 자연스럽게 기울어 보인다
/// </summary>
public class BuildingRenderer : MonoBehaviour
{
    [Header("Data")]
    public string fileName = "busan_buildings.json";

    [Header("Shape")]
    public float floorHeight = 3.2f;      // 층당 높이(m)
    public int defaultLevels = 2;         // 태그 없을 때 기본 층수
    public float chunkSize = 1000f;

    [Header("Look")]
    public Color baseColor = new Color(0.80f, 0.79f, 0.76f);
    public Color roofTint  = new Color(0.90f, 0.89f, 0.87f);

    [Header("Night")]
    public Color dayTint       = Color.white;
    public Color nightTint     = new Color(0.30f, 0.34f, 0.48f);
    public Color dayBackground   = new Color(0.13f, 0.14f, 0.16f);
    public Color nightBackground = new Color(0.015f, 0.02f, 0.05f);

    readonly List<Material> materials = new();
    bool isNight;

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

        ApplyDayNight(); // 초기 배경색 적용
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.N))
        {
            isNight = !isNight;
            ApplyDayNight();
        }
    }

    void ApplyDayNight()
    {
        Color tint = isNight ? nightTint : dayTint;
        foreach (var m in materials) m.color = tint;

        var cam = Camera.main;
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = isNight ? nightBackground : dayBackground;
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

            // ---- 링 좌표 ----
            ring.Clear();
            foreach (object pObj in geom)
            {
                var p = (Dictionary<string, object>)pObj;
                ring.Add(GeoUtil.LonLatToLocal((double)p["lon"], (double)p["lat"]));
            }
            // 닫힌 링이면 마지막 중복점 제거
            if (ring.Count > 1 && (ring[0] - ring[^1]).sqrMagnitude < 0.01f)
                ring.RemoveAt(ring.Count - 1);
            if (ring.Count < 3) continue;

            // ---- 높이 ----
            float height = ReadHeight(el);

            // ---- 감김 방향 통일 (CCW) ----
            if (SignedArea(ring) < 0f) ring.Reverse();

            // ---- 청크 선택 (중심점 기준) ----
            Vector3 centroid = Vector3.zero;
            foreach (var p in ring) centroid += p;
            centroid /= ring.Count;
            var key = new Vector2Int(
                Mathf.FloorToInt(centroid.x / chunkSize),
                Mathf.FloorToInt(centroid.z / chunkSize));
            if (!chunks.TryGetValue(key, out var buf))
            {
                buf = (new List<Vector3>(16384), new List<Color>(16384), new List<int>(32768));
                chunks[key] = buf;
            }

            AddWalls(buf, ring, height);
            AddRoof(buf, ring, height);
            built++;
        }

        foreach (var kv in chunks) CreateChunkObject(kv.Key, kv.Value);
        return built;
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

    // ---- 지오메트리 ----

    void AddWalls((List<Vector3> v, List<Color> c, List<int> t) buf, List<Vector3> ring, float height)
    {
        // 가짜 조명: 벽면 방향과 광원 방향의 내적으로 밝기 결정
        Vector3 lightDir = new Vector3(0.55f, 0f, 0.83f);

        for (int i = 0; i < ring.Count; i++)
        {
            Vector3 a = ring[i];
            Vector3 b = ring[(i + 1) % ring.Count];
            Vector3 edge = b - a;
            if (edge.sqrMagnitude < 0.01f) continue;

            Vector3 normal = Vector3.Cross(Vector3.up, edge).normalized; // CCW 링 기준 바깥쪽
            float brightness = 0.55f + 0.35f * (Vector3.Dot(normal, lightDir) * 0.5f + 0.5f);
            Color col = baseColor * brightness;
            col.a = 1f;

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

    void AddRoof((List<Vector3> v, List<Color> c, List<int> t) buf, List<Vector3> ring, float height)
    {
        List<int> tris = EarClip(ring);
        int s = buf.v.Count;
        foreach (var p in ring)
        {
            buf.v.Add(p + Vector3.up * height);
            buf.c.Add(roofTint);
        }
        // EarClip은 CCW(위에서 볼 때 반시계) 기준 — 위에서 보이려면 뒤집어서 넣는다
        for (int i = 0; i < tris.Count; i += 3)
        {
            buf.t.Add(s + tris[i]);
            buf.t.Add(s + tris[i + 2]);
            buf.t.Add(s + tris[i + 1]);
        }
    }

    /// <summary>단순 다각형 이어클리핑 삼각분할 (CCW 링 기준). 실패 시 팬 분할 폴백.</summary>
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

                if (Cross2(b - a, c - b) <= 0f) continue; // 오목 꼭짓점

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
            if (!cut) break; // 자기교차 등 비정상 폴리곤 → 폴백
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
      /// <summary>XZ 평면 폴리곤의 부호 있는 면적. 양수면 CCW(위에서 볼 때 반시계).</summary>
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
        var mat = new Material(Shader.Find("Sprites/Default"));
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        materials.Add(mat);
    }
}
