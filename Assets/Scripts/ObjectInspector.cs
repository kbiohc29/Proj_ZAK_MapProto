using System.Linq;
using UnityEngine;

/// <summary>
/// 좌클릭 정보 열람. 상가 유무와 관계없이 **모든 건물**의 정보를 보여준다.
/// - 건물 형상은 BuildingRenderer에서 (층수, 면적, 부지 여부)
/// - 입점 업소는 ShopMapRenderer에서 (있으면 층별 목록)
/// 수색(획득)은 캐릭터가 실제로 도착해야 가능 — 정보와 행동의 분리.
/// </summary>
public class ObjectInspector : MonoBehaviour
{
    public BuildingRenderer buildingData;
    public ShopMapRenderer shopData;
    public PlayerCharacter player;
    public BuildingLootUI lootUI;

    [Tooltip("건물 밖을 눌렀을 때 인정할 여유 거리(m). 작을수록 엄격")]
    public float fallbackRadius = 12f;
    [Tooltip("이 뷰 폭(m)보다 넓게 줌아웃하면 건물 정보를 열지 않는다")]
    public float maxViewWidthForInfo = 2000f;
    public bool debugLog = false;

    Camera cam;
    Vector3 downPos;
    BuildingRenderer.BInfo selected;
    ShopMapRenderer.Building shops;
    Vector2 scroll;
    Rect win = new Rect(0, 0, 340, 380);

    void Start()
    {
        cam = Camera.main;
        if (buildingData == null) buildingData = FindFirstObjectByType<BuildingRenderer>();
        if (shopData == null) shopData = FindFirstObjectByType<ShopMapRenderer>();
        if (player == null) player = FindFirstObjectByType<PlayerCharacter>();
        if (lootUI == null) lootUI = FindFirstObjectByType<BuildingLootUI>();
    }

    void Update()
    {
        if (UIState.PointerOverUI) return;
        if (Input.GetMouseButtonDown(0)) downPos = Input.mousePosition;
        if (!Input.GetMouseButtonUp(0)) return;
        if ((Input.mousePosition - downPos).magnitude > 6f) return;   // 드래그였음
        if (lootUI != null && lootUI.IsOpen) return;

        // 줌아웃 상태에서는 건물 정보를 열지 않는다 (운영 줌 이하에서만)
        if (CurrentViewWidth() > maxViewWidthForInfo) { selected = null; return; }

        Vector3 world = ScreenToGround(Input.mousePosition);
        selected = buildingData != null ? buildingData.PickBuilding(world, fallbackRadius) : null;

        // 건물 폴리곤 안에 있는 상가 클러스터를 모두 병합 (큰 건물은 여러 덩어리로 나뉨)
        shops = null;
        if (selected != null && shopData != null)
        {
            float searchR = Mathf.Max(45f, Mathf.Sqrt(selected.area));
            var clusters = shopData.BuildingsInRadius(selected.centroid, searchR);
            var merged = new ShopMapRenderer.Building { key = "merged", title = "", pos = selected.centroid };
            foreach (var c in clusters)
            {
                if (!BuildingRenderer.PointInRing(c.pos, selected.ring)) continue;
                if (string.IsNullOrEmpty(merged.title)) merged.title = c.title;
                merged.shops.AddRange(c.shops);
            }
            if (merged.shops.Count > 0) shops = merged;
        }

        scroll = Vector2.zero;

        if (debugLog)
            Debug.Log($"[Inspector] {world} r={fallbackRadius:F0} 건물={(selected == null ? "없음" : $"{selected.levels}층")} " +
                      $"업소={(shops == null ? 0 : shops.shops.Count)}");

        if (selected != null)
        {
            Vector2 m = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            win.x = Mathf.Clamp(m.x + 16f, 0f, Mathf.Max(0f, Screen.width - win.width));
            win.y = Mathf.Clamp(m.y - 20f, 0f, Mathf.Max(0f, Screen.height - win.height));
        }
    }

    void OnGUI()
    {
        if (selected == null) return;
        string title = shops != null ? shops.title : (selected.isSite ? "부지 / 시설 구역" : "건물");
        win = GUI.Window(7100, win, Draw, title);
        UIState.Register(win);
    }

    void Draw(int id)
    {
        float dist = player != null ? Vector3.Distance(player.transform.position, selected.centroid) : -1f;

        GUILayout.Label(selected.isSite
            ? $"부지 · 면적 {selected.area:N0}㎡" + (dist >= 0 ? $" · 거리 {dist:F0}m" : "")
            : $"지상 {selected.levels}층 · 면적 {selected.area:N0}㎡" + (dist >= 0 ? $" · 거리 {dist:F0}m" : ""),
            Small());

        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(250));
        if (shops != null && shops.shops.Count > 0)
        {
            foreach (var g in shops.shops.GroupBy(s => s.floor).OrderBy(g => FloorOrder(g.Key)))
            {
                GUILayout.Space(4);
                GUILayout.Label(string.IsNullOrEmpty(g.Key) ? "— 층 미상 —" : $"— {g.Key}층 —", Bold());
                foreach (var s in g)
                    GUILayout.Label($"{s.name}  <size=10>{s.category}</size>", Rich());
            }
        }
        else
        {
            GUILayout.Space(6);
            GUILayout.Label(selected.isSite
                ? "등록된 상업시설 정보가 없는 구역이다."
                : "등록된 상가가 없다. 주거 건물로 보인다.", Small());
        }
        GUILayout.EndScrollView();

        if (player != null && GUILayout.Button("이곳으로 이동", GUILayout.Height(28)))
        {
            player.SetDestination(selected.centroid);
            selected = null;
        }
        if (GUILayout.Button("닫기")) selected = null;
        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }

    float CurrentViewWidth()
    {
        float hFov = Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect) * Mathf.Deg2Rad;
        return 2f * cam.transform.position.y * Mathf.Tan(hFov * 0.5f);
    }

    Vector3 ScreenToGround(Vector3 screenPos)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        if (Mathf.Abs(ray.direction.y) < 0.0001f) return Vector3.zero;
        float t = -ray.origin.y / ray.direction.y;
        return ray.origin + ray.direction * t;
    }

    static int FloorOrder(string f)
    {
        if (string.IsNullOrEmpty(f)) return 999;
        bool basement = f.Contains("지하") || f.StartsWith("B") || f.StartsWith("b") || f.StartsWith("-");
        string digits = new string(f.Where(char.IsDigit).ToArray());
        int n = int.TryParse(digits, out int v) ? v : 0;
        return basement ? -n : n;
    }

    static GUIStyle _s, _b, _r;
    static GUIStyle Small() => _s ??= new GUIStyle(GUI.skin.label) { fontSize = 11 };
    static GUIStyle Bold() => _b ??= new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
    static GUIStyle Rich() => _r ??= new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true };
}
