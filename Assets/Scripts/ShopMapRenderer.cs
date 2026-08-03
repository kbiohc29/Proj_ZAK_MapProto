using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 소상공인시장진흥공단 상가(상권)정보 CSV를 읽어
/// 업종 대분류별 색상의 점(쿼드)으로 맵에 뿌린다.
///
/// v2 변경점:
/// - 인코딩 자동 판별 (UTF-8 실패 시 CP949로 재시도)
/// - 헤더를 찾지 못하면 실제 헤더 내용을 콘솔에 출력해 원인 파악 가능
/// - 컬럼 매칭 규칙 완화 (경도/위도/lon/lat, 대분류명 변형 대응)
/// </summary>
public class ShopMapRenderer : MonoBehaviour
{
    [Header("Data")]
    public string fileName = "busan_shops.csv";

    [Header("Point")]
    public float pointSize = 8f;          // 점 한 변(m)
    public float chunkSize = 2000f;       // 청크 격자 크기(m)

    // 업종 대분류별 색 (이름 부분일치로 매칭)
    static readonly (string key, Color color)[] Palette =
    {
        ("음식",        new Color(0.95f, 0.45f, 0.30f)),
        ("소매",        new Color(0.30f, 0.65f, 0.95f)),
        ("보건",        new Color(0.90f, 0.20f, 0.35f)),
        ("숙박",        new Color(0.60f, 0.40f, 0.85f)),
        ("교육",        new Color(0.25f, 0.80f, 0.55f)),
        ("학문",        new Color(0.25f, 0.80f, 0.55f)),
        ("예술",        new Color(0.95f, 0.75f, 0.25f)),
        ("스포츠",      new Color(0.95f, 0.75f, 0.25f)),
        ("수리",        new Color(0.55f, 0.55f, 0.55f)),
        ("부동산",      new Color(0.45f, 0.70f, 0.70f)),
        ("과학",        new Color(0.70f, 0.60f, 0.45f)),
        ("시설",        new Color(0.50f, 0.50f, 0.70f)),
    };
    static readonly Color DefaultColor = new Color(0.8f, 0.8f, 0.8f);

    public int LoadedCount { get; private set; }

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
            Debug.LogError("UTF-8, CP949 모두에서 경도/위도 컬럼을 찾지 못했습니다. 위 로그의 헤더 내용을 확인하세요.");
            return;
        }
        Debug.Log($"인코딩 판별 결과: {enc.WebName}");
        BuildFromCsv(path, enc);
    }

    /// <summary>첫 줄을 여러 인코딩으로 읽어보고 경도/위도 컬럼이 잡히는 쪽을 고른다.</summary>
    static Encoding DetectEncoding(string path)
    {
        var candidates = new List<Encoding>();
        candidates.Add(new UTF8Encoding(false));
        try { candidates.Add(Encoding.GetEncoding(949)); }   // CP949 (한국어 완성형)
        catch { /* 플랫폼에 따라 미지원일 수 있음 */ }
        try { candidates.Add(Encoding.GetEncoding("euc-kr")); }
        catch { }

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

    /// <summary>후보 키워드 중 하나라도 포함하는 컬럼의 인덱스. 없으면 -1</summary>
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
                lonCol = FindColumn(fields, "경도", "lon", "x좌표");
                latCol = FindColumn(fields, "위도", "lat", "y좌표");
                catCol = FindColumn(fields, "대분류명", "업종대분류");
                headerParsed = true;
                Debug.Log($"컬럼 인덱스 — 경도:{lonCol} 위도:{latCol} 대분류:{catCol} (총 {fields.Count}컬럼)");
                continue;
            }

            if (fields.Count <= Mathf.Max(lonCol, latCol)) { skipped++; continue; }
            if (!double.TryParse(fields[lonCol], out double lon)) { skipped++; continue; }
            if (!double.TryParse(fields[latCol], out double lat)) { skipped++; continue; }

            // 한국 영역 밖 좌표는 이상치로 간주
            if (lon < 124 || lon > 132 || lat < 33 || lat > 39) { skipped++; continue; }

            Vector3 pos = GeoUtil.LonLatToLocal(lon, lat);
            Color col = (catCol >= 0 && catCol < fields.Count)
                ? ColorFor(fields[catCol]) : DefaultColor;

            var key = new Vector2Int(
                Mathf.FloorToInt(pos.x / chunkSize),
                Mathf.FloorToInt(pos.z / chunkSize));

            if (!chunks.TryGetValue(key, out var buf))
            {
                buf = (new List<Vector3>(4096), new List<Color>(4096), new List<int>(8192));
                chunks[key] = buf;
            }
            AddQuad(buf.v, buf.c, buf.t, pos, pointSize * 0.5f, col);
            count++;
        }

        foreach (var kv in chunks) CreateChunkObject(kv.Key, kv.Value);
        LoadedCount = count;
        Debug.Log($"상가업소 {count:N0}개 로드, 청크 {chunks.Count}개 생성 (스킵 {skipped:N0}행)");
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
    }

    /// <summary>따옴표로 감싼 필드(상호명에 콤마 포함) 처리하는 간단 CSV 파서</summary>
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