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
    public Color lowColor  = new Color(0.70f, 0.66f, 0.60f); // 저층: 낡은 콘크리트
    public Color highColor = new Color(0.55f, 0.58f, 0.63f); // 고층: 차가운 회청색
    public float highColorAtHeight = 60f;
    [Tooltip("건물별 밝기 편차 — 도시가 균일한 판으로 안 보이게")]
    public float colorJitter = 0.16f;
    [Tooltip("지붕 밝기 배율. 1보다 작으면 벽보다 어두워 윤곽이 살아난다")]
    public float roofBrightness = 0.82f;
    [Tooltip("지붕별 추가 편차 — 위에서 볼 때 가장 큰 인상을 만든다")]
    public float roofVariation = 0.22f;
    [Tooltip("대형 건물(상업/공장)일수록 회색쪽으로")]
    public float largeBuildingGrey = 0.35f;

    [Header("Oversize (부지/캠퍼스 대응)")]
    [Tooltip("이 면적(㎡)을 넘는 폴리곤은 건물이 아니라 '부지'로 보고 평평하게 깐다. 통행도 가능")]
    public float oversizeAreaThreshold = 12000f;
    public Color siteColor = new Color(0.30f, 0.32f, 0.30f);

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

    /// <summary>WalkGrid 등이 쓰는 건물 외곽선 목록</summary>
    public List<Vector3[]> Footprints { get; } = new();
    public bool FootprintsReady { get; private set; }

    /// <summary>클릭 조회용 건물 정보 (상가 유무와 무관하게 모든 건물)</summary>
    public class BInfo
    {
        public Vector3[] ring;
        public Vector3 centroid;
        public float height;
        public float area;
        public bool isSite;      // 부지(캠퍼스 등)로 판정된 대형 폴리곤
        public int levels => Mathf.Max(1, Mathf.RoundToInt(height / 3.2f));
    }
    public List<BInfo> BuildingInfos { get; } = new();

    readonly Dictionary<Vector2Int, List<int>> pickGrid = new();
    const float PickCell = 120f;

    void IndexBuilding(BInfo info)
    {
        BuildingInfos.Add(info);
        int idx = BuildingInfos.Count - 1;
        var key = new Vector2Int(Mathf.FloorToInt(info.centroid.x / PickCell),
                                 Mathf.FloorToInt(info.centroid.z / PickCell));
        if (!pickGrid.TryGetValue(key, out var list)) pickGrid[key] = list = new List<int>();
        list.Add(idx);
    }

    /// <summary>월드 좌표에 있는 건물을 찾는다. 폴리곤 내부 우선, 없으면 반경 내 최근접</summary>
    public BInfo PickBuilding(Vector3 p, float fallbackRadius = 12f)
    {
        int r = Mathf.CeilToInt(fallbackRadius / PickCell);
        var center = new Vector2Int(Mathf.FloorToInt(p.x / PickCell), Mathf.FloorToInt(p.z / PickCell));

        BInfo nearest = null;
        float bestD = fallbackRadius * fallbackRadius;

        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            if (!pickGrid.TryGetValue(new Vector2Int(center.x + dx, center.y + dy), out var list)) continue;
            foreach (int i in list)
            {
                var info = BuildingInfos[i];
                if (PointInRing(p, info.ring)) return info;   // 내부면 즉시 확정
                float d = (info.centroid - p).sqrMagnitude;
                if (d < bestD) { bestD = d; nearest = info; }
            }
        }
        return nearest;
    }

    public static bool PointInRing(Vector3 p, Vector3[] ring)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            if ((ring[i].z > p.z) != (ring[j].z > p.z) &&
                p.x < (ring[j].x - ring[i].x) * (p.z - ring[i].z) / (ring[j].z - ring[i].z) + ring[i].x)
                inside = !inside;
        }
        return inside;
    }

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

        int built = root.ContainsKey("elements") ? Build(root) : BuildGeoJson(root);
        FootprintsReady = true;
        sw.Stop();
        Debug.Log($"건물 {built:N0}동 생성, {sw.ElapsedMilliseconds}ms");

        Shader.SetGlobalFloat("_ZakNight", 0f);
    }

    void Update()
    {
        // NightLightTuner가 있으면 그쪽이 조명 파라미터를 소유한다
        if (FindFirstObjectByType<NightLightTuner>() == null)
        {
            Shader.SetGlobalFloat("_ZakLitRatio", litRatio);
            Shader.SetGlobalFloat("_ZakWinRatio", windowLitRatio);
        }

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

            bool oversize = Mathf.Abs(SignedArea(ring)) > oversizeAreaThreshold;
            if (oversize) height = 1.0f; // 부지: 평평하게

            Vector3 centroid = Vector3.zero;
            foreach (var p in ring) centroid += p;
            centroid /= ring.Count;

            // ---- 건물 색: 높이 기반 + 좌표 해시 편차 ----
            float hT = Mathf.Clamp01(height / highColorAtHeight);
            Color bCol = Color.Lerp(lowColor, highColor, hT);
            float jitter = 1f + (Hash(centroid) - 0.5f) * 2f * colorJitter;
            bCol *= jitter;
            // 대형 건물은 채도를 낮춰 회색 콘크리트로
            float areaT = Mathf.Clamp01(Mathf.Abs(SignedArea(ring)) / 4000f) * largeBuildingGrey;
            float g = bCol.grayscale;
            bCol = Color.Lerp(bCol, new Color(g, g, g), areaT);
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

            if (oversize) bCol = siteColor;
            AddWalls(buf, ring, height, bCol);
            AddRoof(buf, ring, height, bCol);
            var arr = ring.ToArray();
            if (!oversize) Footprints.Add(arr); // 부지는 통행 가능 → 보행격자에서 제외
            IndexBuilding(new BInfo { ring = arr, centroid = centroid, height = height,
                                      area = Mathf.Abs(SignedArea(ring)), isSite = oversize });
            built++;
        }

        foreach (var kv in chunks) CreateChunkObject(kv.Key, kv.Value);
        return built;
    }

    /// <summary>
    /// GeoJSON FeatureCollection 로더 (국가공간정보포털 GIS건물통합정보 등).
    /// 층수 속성은 "층수" / "GRND_FLR" / "levels" 중 먼저 발견되는 것을 사용.
    /// </summary>
    int BuildGeoJson(Dictionary<string, object> root)
    {
        if (!(root.TryGetValue("features", out var fo) && fo is List<object> features)) return 0;

        var chunks = new Dictionary<Vector2Int, (List<Vector3> v, List<Color> c, List<int> t)>();
        var ring = new List<Vector3>(64);
        int built = 0;

        foreach (object f in features)
        {
            if (f is not Dictionary<string, object> feat) continue;
            if (!(feat.TryGetValue("geometry", out var go) && go is Dictionary<string, object> geom)) continue;
            if (!(geom.TryGetValue("type", out var gt) && geom.TryGetValue("coordinates", out var co))) continue;

            float levels = 2f;
            if (feat.TryGetValue("properties", out var po) && po is Dictionary<string, object> props)
            {
                foreach (string key in new[] { "층수", "GRND_FLR", "levels", "BLD_FLR" })
                    if (props.TryGetValue(key, out var lv) && lv != null &&
                        float.TryParse(lv.ToString(), out float n) && n >= 1f) { levels = n; break; }
            }
            float height = levels * floorHeight;

            var polys = new List<object>();
            if ((string)gt == "Polygon") polys.Add(co);
            else if ((string)gt == "MultiPolygon" && co is List<object> mp) polys.AddRange(mp);

            foreach (object polyObj in polys)
            {
                if (polyObj is not List<object> poly || poly.Count == 0) continue;
                if (poly[0] is not List<object> outer) continue; // 첫 링(외곽)만 사용

                ring.Clear();
                foreach (object ptObj in outer)
                {
                    if (ptObj is not List<object> pt || pt.Count < 2) continue;
                    ring.Add(GeoUtil.LonLatToLocal((double)pt[0], (double)pt[1]));
                }
                if (ring.Count > 1 && (ring[0] - ring[^1]).sqrMagnitude < 0.01f) ring.RemoveAt(ring.Count - 1);
                if (ring.Count < 3) continue;
                if (SignedArea(ring) < 0f) ring.Reverse();

                bool oversize = Mathf.Abs(SignedArea(ring)) > oversizeAreaThreshold;
                if (oversize) height = 1.0f;

                Vector3 centroid = Vector3.zero;
                foreach (var p in ring) centroid += p;
                centroid /= ring.Count;

                float hT = Mathf.Clamp01(height / highColorAtHeight);
                Color bCol = Color.Lerp(lowColor, highColor, hT);
                bCol *= 1f + (Hash(centroid) - 0.5f) * 2f * colorJitter;
                bCol.a = Hash(centroid * 1.37f + Vector3.one * 5.19f);

                var key = new Vector2Int(Mathf.FloorToInt(centroid.x / chunkSize),
                                         Mathf.FloorToInt(centroid.z / chunkSize));
                if (!chunks.TryGetValue(key, out var buf))
                {
                    buf = (new List<Vector3>(16384), new List<Color>(16384), new List<int>(32768));
                    chunks[key] = buf;
                }
                if (oversize) bCol = siteColor;
                AddWalls(buf, ring, height, bCol);
                AddRoof(buf, ring, height, bCol);
                var arr2 = ring.ToArray();
                if (!oversize) Footprints.Add(arr2);
                IndexBuilding(new BInfo { ring = arr2, centroid = centroid, height = height,
                                          area = Mathf.Abs(SignedArea(ring)), isSite = oversize });
                built++;
            }
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
        // 지붕: 벽보다 어둡게 + 건물마다 편차 → 탑뷰에서 도시가 살아 보인다
        float rv = 1f + (Hash(ring[0] * 2.13f + Vector3.one * 3.7f) - 0.5f) * 2f * roofVariation;
        Color rc = bCol * (roofBrightness * rv);
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
