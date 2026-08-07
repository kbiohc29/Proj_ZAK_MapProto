using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// v3 변경점 (M3):
/// - 렌더링은 기존과 동일
/// - 추가로 '건물 레지스트리'를 구축: 건물관리번호로 업소를 묶고
///   상호명/업종 소분류/층 정보를 보관 → 클릭 루팅(BuildingLootUI)에서 사용
/// </summary>
public class ShopMapRenderer : MonoBehaviour
{
    [Header("Data")]
    public string fileName = "busan_shops.csv";

    [Header("Point")]
    public float pointSize = 8f;
    public float chunkSize = 2000f;

    [Header("Visibility")]
    [Tooltip("끄면 상가 점을 아예 그리지 않는다 (건물이 모두 있는 상태에선 불필요). 루팅/정보 데이터는 그대로 유지")]
    public bool renderDots = false;

    [Tooltip("뷰 폭이 이 값보다 좁아지면 상가 점을 숨긴다 (근접 줌에선 건물/캐릭터 가독성 우선)")]
    public float hideBelowViewWidth = 2500f;

    // ---- M3: 건물/업소 데이터 ----
    public class ShopRecord
    {
        public string name;       // 상호명
        public string category;   // 업종 소분류명 (없으면 대분류명)
        public string floor;      // 층정보 (빈 값 가능)
    }

    public class Building
    {
        public string key;        // 건물관리번호 (없으면 좌표 키)
        public string title;      // 건물명 → 없으면 도로명주소
        public Vector3 pos;       // 업소 평균 좌표
        public List<ShopRecord> shops = new();
        internal Vector3 posSum;
    }

    public Dictionary<string, Building> Buildings { get; } = new();
    readonly Dictionary<Vector2Int, List<Building>> bGrid = new();
    const float BGridCell = 100f;

    static readonly (string key, Color color)[] Palette =
    {
        ("음식",   new Color(0.95f, 0.45f, 0.30f)),
        ("소매",   new Color(0.30f, 0.65f, 0.95f)),
        ("보건",   new Color(0.90f, 0.20f, 0.35f)),
        ("숙박",   new Color(0.60f, 0.40f, 0.85f)),
        ("교육",   new Color(0.25f, 0.80f, 0.55f)),
        ("학문",   new Color(0.25f, 0.80f, 0.55f)),
        ("예술",   new Color(0.95f, 0.75f, 0.25f)),
        ("스포츠", new Color(0.95f, 0.75f, 0.25f)),
        ("수리",   new Color(0.55f, 0.55f, 0.55f)),
        ("부동산", new Color(0.45f, 0.70f, 0.70f)),
        ("과학",   new Color(0.70f, 0.60f, 0.45f)),
        ("시설",   new Color(0.50f, 0.50f, 0.70f)),
    };
    static readonly Color DefaultColor = new Color(0.8f, 0.8f, 0.8f);

    public int LoadedCount { get; private set; }

    readonly System.Collections.Generic.List<MeshRenderer> chunkRenderers = new();
    Camera cam;
    bool dotsVisible = true;

    void Update()
    {
        if (cam == null) cam = Camera.main;
        if (cam == null || chunkRenderers.Count == 0) return;

        float hFov = Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect) * Mathf.Deg2Rad;
        float viewWidth = 2f * cam.transform.position.y * Mathf.Tan(hFov * 0.5f);

        bool shouldShow = viewWidth >= hideBelowViewWidth;
        if (shouldShow == dotsVisible) return;
        dotsVisible = shouldShow;
        foreach (var r in chunkRenderers) if (r != null) r.enabled = shouldShow;
    }

    void Start()
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"CSV 파일이 없습니다: {path}");
            return;
        }

        Encoding enc = DetectEncoding(path);
        if (enc == null)
        {
            Debug.LogError("UTF-8, CP949 모두에서 경도/위도 컬럼을 찾지 못했습니다.");
            return;
        }
        Debug.Log($"인코딩 판별 결과: {enc.WebName}");
        BuildFromCsv(path, enc);
    }

    static Encoding DetectEncoding(string path)
    {
        var candidates = new List<Encoding> { new UTF8Encoding(false) };
        try { candidates.Add(Encoding.GetEncoding(949)); } catch { }
        try { candidates.Add(Encoding.GetEncoding("euc-kr")); } catch { }

        foreach (var enc in candidates)
        {
            string header = ReadFirstLine(path, enc);
            if (header == null) continue;
            var fields = ParseCsvLine(header);
            if (FindColumn(fields, "경도", "lon", "x좌표") >= 0 &&
                FindColumn(fields, "위도", "lat", "y좌표") >= 0)
                return enc;
            Debug.Log($"[{enc.WebName}] 헤더 시도 실패. 읽힌 헤더: {header}");
        }
        return null;
    }

    static string ReadFirstLine(string path, Encoding enc)
    {
        using var sr = new StreamReader(path, enc, detectEncodingFromByteOrderMarks: true);
        return sr.ReadLine();
    }

    /// <summary>키워드 우선순위 순서로 컬럼 검색 (클로드 코드 수정 방식 유지)</summary>
    static int FindColumn(List<string> header, params string[] keys)
    {
        foreach (var k in keys)
        {
            string kl = k.ToLowerInvariant();
            for (int i = 0; i < header.Count; i++)
            {
                string h = header[i].Trim().Replace("\uFEFF", "").ToLowerInvariant();
                if (h.Contains(kl)) return i;
            }
        }
        return -1;
    }

    void BuildFromCsv(string path, Encoding enc)
    {
        var chunks = new Dictionary<Vector2Int, (List<Vector3> v, List<Color> c, List<int> t)>();

        int lonCol = -1, latCol = -1, catCol = -1;
        int nameCol = -1, subCatCol = -1, floorCol = -1, bldgKeyCol = -1, bldgNameCol = -1, addrCol = -1;
        bool headerParsed = false;
        int count = 0, skipped = 0;

        using var sr = new StreamReader(path, enc, detectEncodingFromByteOrderMarks: true);
        string line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            List<string> fields = ParseCsvLine(line);

            if (!headerParsed)
            {
                lonCol      = FindColumn(fields, "경도", "lon", "x좌표");
                latCol      = FindColumn(fields, "위도", "lat", "y좌표");
                catCol      = FindColumn(fields, "대분류명", "업종대분류");
                nameCol     = FindColumn(fields, "상호명");
                subCatCol   = FindColumn(fields, "소분류명", "중분류명");
                floorCol    = FindColumn(fields, "층정보");
                bldgKeyCol  = FindColumn(fields, "건물관리번호");
                bldgNameCol = FindColumn(fields, "건물명");
                addrCol     = FindColumn(fields, "도로명주소");
                headerParsed = true;
                Debug.Log($"컬럼 — 경도:{lonCol} 위도:{latCol} 대분류:{catCol} 상호:{nameCol} " +
                          $"소분류:{subCatCol} 층:{floorCol} 건물키:{bldgKeyCol} 건물명:{bldgNameCol}");
                continue;
            }

            if (fields.Count <= Mathf.Max(lonCol, latCol)) { skipped++; continue; }
            if (!double.TryParse(fields[lonCol], out double lon)) { skipped++; continue; }
            if (!double.TryParse(fields[latCol], out double lat)) { skipped++; continue; }
            if (lon < 124 || lon > 132 || lat < 33 || lat > 39) { skipped++; continue; }

            Vector3 pos = GeoUtil.LonLatToLocal(lon, lat);
            Color col = (catCol >= 0 && catCol < fields.Count)
                ? ColorFor(fields[catCol]) : DefaultColor;

            // ---- 렌더링용 점 ----
            var key = new Vector2Int(
                Mathf.FloorToInt(pos.x / chunkSize), Mathf.FloorToInt(pos.z / chunkSize));
            if (!chunks.TryGetValue(key, out var buf))
            {
                buf = (new List<Vector3>(4096), new List<Color>(4096), new List<int>(8192));
                chunks[key] = buf;
            }
            if (renderDots) AddQuad(buf.v, buf.c, buf.t, pos, pointSize * 0.5f, col);

            // ---- M3: 건물 레지스트리 ----
            RegisterShop(fields, pos, nameCol, subCatCol, catCol, floorCol, bldgKeyCol, bldgNameCol, addrCol);
            count++;
        }

        // 건물 평균좌표 확정 + 공간 격자 등록
        foreach (var b in Buildings.Values)
        {
            b.pos = b.posSum / b.shops.Count;
            var cell = new Vector2Int(
                Mathf.FloorToInt(b.pos.x / BGridCell), Mathf.FloorToInt(b.pos.z / BGridCell));
            if (!bGrid.TryGetValue(cell, out var list)) bGrid[cell] = list = new List<Building>();
            list.Add(b);
        }

        if (renderDots)
            foreach (var kv in chunks) CreateChunkObject(kv.Key, kv.Value);
        LoadedCount = count;
        Debug.Log($"상가업소 {count:N0}개 로드, 건물 {Buildings.Count:N0}동 등록 (스킵 {skipped:N0}행)");
    }

    void RegisterShop(List<string> f, Vector3 pos, int nameCol, int subCatCol, int catCol,
                      int floorCol, int bldgKeyCol, int bldgNameCol, int addrCol)
    {
        string Get(int col) => (col >= 0 && col < f.Count) ? f[col].Trim() : "";

        string bKey = Get(bldgKeyCol);
        if (string.IsNullOrEmpty(bKey))
            bKey = $"@{Mathf.RoundToInt(pos.x / 10f)}_{Mathf.RoundToInt(pos.z / 10f)}"; // 10m 격자 폴백

        if (!Buildings.TryGetValue(bKey, out var b))
        {
            string bName = Get(bldgNameCol);
            if (string.IsNullOrEmpty(bName)) bName = Get(addrCol);
            if (string.IsNullOrEmpty(bName)) bName = "이름 없는 건물";
            Buildings[bKey] = b = new Building { key = bKey, title = bName };
        }

        string cat = Get(subCatCol);
        if (string.IsNullOrEmpty(cat)) cat = Get(catCol);

        b.shops.Add(new ShopRecord { name = Get(nameCol), category = cat, floor = Get(floorCol) });
        b.posSum += pos;
    }

    /// <summary>클릭 지점에서 maxDist(m) 내 가장 가까운 건물. 없으면 null</summary>
    public Building NearestBuilding(Vector3 pos, float maxDist)
    {
        var center = new Vector2Int(
            Mathf.FloorToInt(pos.x / BGridCell), Mathf.FloorToInt(pos.z / BGridCell));
        Building best = null;
        float bestD = maxDist * maxDist;

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (!bGrid.TryGetValue(new Vector2Int(center.x + dx, center.y + dy), out var list))
                continue;
            foreach (var b in list)
            {
                float d = (b.pos - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = b; }
            }
        }
        return best;
    }

    /// <summary>반경 내 상가 클러스터 전부 반환 (큰 건물은 여러 클러스터로 쪼개져 있음)</summary>
    public List<Building> BuildingsInRadius(Vector3 pos, float radius)
    {
        var result = new List<Building>();
        int r = Mathf.CeilToInt(radius / BGridCell);
        var center = new Vector2Int(Mathf.FloorToInt(pos.x / BGridCell), Mathf.FloorToInt(pos.z / BGridCell));
        float r2 = radius * radius;

        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            if (!bGrid.TryGetValue(new Vector2Int(center.x + dx, center.y + dy), out var list)) continue;
            foreach (var b in list)
                if ((b.pos - pos).sqrMagnitude <= r2) result.Add(b);
        }
        return result;
    }

    static Color ColorFor(string categoryName)
    {
        foreach (var (key, color) in Palette)
            if (categoryName.Contains(key)) return color;
        return DefaultColor;
    }

    static void AddQuad(List<Vector3> v, List<Color> c, List<int> t,
                        Vector3 center, float half, Color col)
    {
        int b = v.Count;
        v.Add(center + new Vector3(-half, 0, -half));
        v.Add(center + new Vector3(-half, 0,  half));
        v.Add(center + new Vector3( half, 0,  half));
        v.Add(center + new Vector3( half, 0, -half));
        for (int i = 0; i < 4; i++) c.Add(col);
        t.Add(b); t.Add(b + 1); t.Add(b + 2);
        t.Add(b); t.Add(b + 2); t.Add(b + 3);
    }

    void CreateChunkObject(Vector2Int key, (List<Vector3> v, List<Color> c, List<int> t) buf)
    {
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(buf.v);
        mesh.SetColors(buf.c);
        mesh.SetTriangles(buf.t, 0);
        mesh.RecalculateBounds();

        var go = new GameObject($"Chunk_{key.x}_{key.y}");
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        chunkRenderers.Add(mr);
    }

    static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>(48);
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == '"') inQuotes = true;
                else if (ch == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
        }
        result.Add(sb.ToString());
        return result;
    }
}
