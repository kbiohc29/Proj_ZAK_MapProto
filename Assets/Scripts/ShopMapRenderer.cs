using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 소상공인시장진흥공단 상가(상권)정보 CSV를 읽어
/// 업종 대분류별 색상의 점(쿼드)으로 맵에 뿌린다.
/// - CSV는 StreamingAssets 폴더에 넣고 fileName에 파일명 지정
/// - 헤더에서 '경도', '위도', '상권업종대분류명' 컬럼을 이름으로 찾으므로
///   분기별 스키마 변화에 어느 정도 강함
/// - 점들은 2km 격자 청크 단위로 메시를 합쳐서 드로우콜을 줄임
/// </summary>
public class ShopMapRenderer : MonoBehaviour
{
    [Header("Data")]
    public string fileName = "busan_shops.csv";

    [Header("Point")]
    public float pointSize = 8f;          // 점 한 변(m). 줌아웃 시 잘 안 보이면 키울 것
    public float chunkSize = 2000f;       // 청크 격자 크기(m)

    // 업종 대분류별 색 (2023 개편 분류 기준, 이름 부분일치로 매칭)
    static readonly (string key, Color color)[] Palette =
    {
        ("음식",        new Color(0.95f, 0.45f, 0.30f)),
        ("소매",        new Color(0.30f, 0.65f, 0.95f)),
        ("보건",        new Color(0.90f, 0.20f, 0.35f)), // 병원/약국 등
        ("숙박",        new Color(0.60f, 0.40f, 0.85f)),
        ("교육",        new Color(0.25f, 0.80f, 0.55f)),
        ("예술",        new Color(0.95f, 0.75f, 0.25f)), // 예술·스포츠·여가 (PC방 포함)
        ("수리",        new Color(0.55f, 0.55f, 0.55f)), // 수리·개인서비스
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
        BuildFromCsv(path);
    }

    void BuildFromCsv(string path)
    {
        // 청크 키 → (버텍스, 컬러, 인덱스)
        var chunks = new Dictionary<Vector2Int, (List<Vector3> v, List<Color> c, List<int> t)>();

        int lonCol = -1, latCol = -1, catCol = -1;
        bool headerParsed = false;
        int count = 0;

        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            List<string> fields = ParseCsvLine(line);

            if (!headerParsed)
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    string h = fields[i].Trim();
                    if (h.Contains("경도")) lonCol = i;
                    else if (h.Contains("위도")) latCol = i;
                    else if (h.Contains("대분류명")) catCol = i;
                }
                headerParsed = true;
                if (lonCol < 0 || latCol < 0)
                {
                    Debug.LogError("헤더에서 경도/위도 컬럼을 찾지 못했습니다.");
                    return;
                }
                continue;
            }

            if (fields.Count <= Mathf.Max(lonCol, latCol)) continue;
            if (!double.TryParse(fields[lonCol], out double lon)) continue;
            if (!double.TryParse(fields[latCol], out double lat)) continue;

            Vector3 pos = GeoUtil.LonLatToLocal(lon, lat);
            Color col = DefaultColor;
            if (catCol >= 0 && catCol < fields.Count)
                col = ColorFor(fields[catCol]);

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
        Debug.Log($"상가업소 {count:N0}개 로드, 청크 {chunks.Count}개 생성");
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
        // Sprites/Default: 버텍스 컬러를 지원하는 언릿 셰이더 (Built-in RP 기준)
        // URP 사용 시 "Universal Render Pipeline/Unlit" + 버텍스컬러 셰이더그래프로 교체
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
