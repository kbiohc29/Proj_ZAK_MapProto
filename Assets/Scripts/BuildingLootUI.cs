using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 건물 클릭 → 층별 상점 팝업 → 수색해 아이템 획득.
/// 인벤토리/스태미너는 GameState로 이관 (루프 전체와 공유). 수색 비용: 스태미너 -5
/// </summary>
public class BuildingLootUI : MonoBehaviour
{
    public ShopMapRenderer shopData;
    public float clickRadius = 40f;
    public float maxViewWidthForLoot = 1000f;

    public int searchStaminaCost = 5;

    Camera cam;
    Vector3 mouseDownPos;
    ShopMapRenderer.Building selected;
    Vector2 scroll;
    string lastLootMsg = "";

    readonly HashSet<string> looted = new();
    Rect windowRect = new Rect(0, 0, 380, 520);

    void Start()
    {
        cam = Camera.main;
        if (shopData == null) shopData = FindFirstObjectByType<ShopMapRenderer>();
        windowRect.x = Screen.width - windowRect.width - 16;
        windowRect.y = 60;
    }

    /// <summary>외부(SignalManager)에서 건물 수색창을 연다</summary>
    public void Open(ShopMapRenderer.Building b)
    {
        selected = b;
        scroll = Vector2.zero;
        lastLootMsg = "도착했다. 조용히 수색하자...";
    }

    public bool IsOpen => selected != null;

    void Update()
    {
        if (shopData == null) return;

        if (Input.GetMouseButtonDown(0)) mouseDownPos = Input.mousePosition;
        if (!Input.GetMouseButtonUp(0)) return;
        if ((Input.mousePosition - mouseDownPos).magnitude > 6f) return;

        if (selected != null && windowRect.Contains(GuiMouse())) return;
        if (CurrentViewWidth() > maxViewWidthForLoot) return;

        Vector3 world = ScreenToGround(Input.mousePosition);
        var b = shopData.NearestBuilding(world, clickRadius);
        if (b != null) Open(b);
    }

    float CurrentViewWidth()
    {
        float hFov = Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect) * Mathf.Deg2Rad;
        return 2f * cam.transform.position.y * Mathf.Tan(hFov * 0.5f);
    }

    Vector3 ScreenToGround(Vector3 screenPos)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        float t = -ray.origin.y / ray.direction.y;
        return ray.origin + ray.direction * t;
    }

    Vector2 GuiMouse() => new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

    // ---------- 수색 ----------

    void Search(ShopMapRenderer.ShopRecord shop, string lootKey)
    {
        if (GameState.stamina < searchStaminaCost)
        {
            lastLootMsg = "너무 지쳤다... 수색할 힘이 없다";
            return;
        }
        GameState.stamina -= searchStaminaCost;

        var items = ItemTable.Roll(shop.category);
        foreach (var it in items)
            GameState.inventory[it] = GameState.inventory.TryGetValue(it, out int n) ? n + 1 : 1;
        looted.Add(lootKey);
        lastLootMsg = items.Count > 0
            ? $"{shop.name}: {string.Join(", ", items)} 획득"
            : $"{shop.name}: 아무것도 없다...";
    }

    // ---------- UI ----------

    void OnGUI()
    {
        DrawInventory();
        if (selected != null)
            windowRect = GUI.Window(7001, windowRect, DrawBuildingWindow, selected.title);
    }

    void DrawBuildingWindow(int id)
    {
        GUILayout.Label($"업소 {selected.shops.Count}개 · 수색당 스태미너 -{searchStaminaCost}", Small());

        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(370));

        var groups = selected.shops
            .Select((s, i) => (shop: s, idx: i))
            .GroupBy(x => x.shop.floor)
            .OrderBy(g => FloorOrder(g.Key));

        foreach (var g in groups)
        {
            string floorLabel = string.IsNullOrEmpty(g.Key) ? "층 미상" : $"{g.Key}층";
            GUILayout.Space(6);
            GUILayout.Label($"— {floorLabel} —", Bold());

            foreach (var (shop, idx) in g)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{shop.name}\n<size=10>{shop.category}</size>", Rich(),
                                GUILayout.Width(240));

                string lootKey = $"{selected.key}#{idx}";
                if (looted.Contains(lootKey))
                    GUILayout.Label("수색 완료", Small(), GUILayout.Width(80));
                else if (GUILayout.Button("수색", GUILayout.Width(80), GUILayout.Height(34)))
                    Search(shop, lootKey);

                GUILayout.EndHorizontal();
            }
        }
        GUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(lastLootMsg))
            GUILayout.Label(lastLootMsg, Bold());

        if (GUILayout.Button("닫기")) selected = null;
        GUI.DragWindow(new Rect(0, 0, 10000, 24));
    }

    void DrawInventory()
    {
        if (GameState.inventory.Count == 0) return;
        GUILayout.BeginArea(new Rect(12, Screen.height - 170, 300, 160), GUI.skin.box);
        GUILayout.Label("가방", Bold());
        foreach (var kv in GameState.inventory.OrderByDescending(k => k.Value).Take(6))
            GUILayout.Label($"{kv.Key} × {kv.Value}", Small());
        GUILayout.EndArea();
    }

    static int FloorOrder(string f)
    {
        if (string.IsNullOrEmpty(f)) return 999;
        bool basement = f.Contains("지하") || f.StartsWith("B") || f.StartsWith("b") || f.StartsWith("-");
        string digits = new string(f.Where(char.IsDigit).ToArray());
        int n = int.TryParse(digits, out int v) ? v : 0;
        return basement ? -n : n;
    }

    static GUIStyle _small, _bold, _rich;
    static GUIStyle Small() => _small ??= new GUIStyle(GUI.skin.label) { fontSize = 11 };
    static GUIStyle Bold()  => _bold  ??= new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
    static GUIStyle Rich()  => _rich  ??= new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };
}

/// <summary>업종 → 아이템 풀. 소분류명 키워드 부분일치, 위에서부터 우선 적용.</summary>
public static class ItemTable
{
    static readonly (string[] keys, (string item, int w)[] pool)[] Tables =
    {
        (new[]{"편의점","슈퍼","마트"},
         new[]{("생수",30),("통조림",25),("라면",25),("라이터",10),("건전지",10),("초코바",15)}),
        (new[]{"약국","의원","병원","치과","한의원"},
         new[]{("붕대",30),("소독약",25),("진통제",20),("항생제",8),("메스",5)}),
        (new[]{"정육","수산","청과","반찬"},
         new[]{("신선식품(부패주의)",40),("식칼",8),("얼음팩",15)}),
        (new[]{"철물","공구","건재"},
         new[]{("망치",20),("못 한 줌",25),("덕트테이프",25),("톱",10),("철사",20)}),
        (new[]{"주유소","가스"},
         new[]{("휘발유통",25),("엔진오일",15)}),
        (new[]{"PC방","피시방","노래"},
         new[]{("에너지드링크",30),("과자",30),("전선",15)}),
        (new[]{"카페","커피","제과","베이커리"},
         new[]{("원두",20),("설탕",25),("빵",30)}),
        (new[]{"의류","패션","신발"},
         new[]{("두꺼운 옷",30),("운동화",15)}),
        (new[]{"스포츠","체육"},
         new[]{("야구방망이",12),("보호대",20),("로프",15)}),
        (new[]{"식당","음식","치킨","분식","중국","일식","한식","주점"},
         new[]{("식재료",30),("식용유",20),("소금",20),("주방칼",8)}),
    };

    static readonly (string item, int w)[] DefaultPool =
        { ("잡동사니", 40), ("끈", 15), ("종이상자", 20), ("동전 몇 닢", 15) };

    public static List<string> Roll(string category)
    {
        var pool = DefaultPool;
        if (!string.IsNullOrEmpty(category))
        {
            foreach (var (keys, p) in Tables)
            {
                bool hit = false;
                foreach (var k in keys)
                    if (category.Contains(k)) { hit = true; break; }
                if (hit) { pool = p; break; }
            }
        }

        int count = Random.Range(1, 4);
        var result = new List<string>(count);
        int total = 0;
        foreach (var (_, w) in pool) total += w;

        for (int i = 0; i < count; i++)
        {
            if (Random.value < 0.2f) continue; // 꽝

            int r = Random.Range(0, total);
            foreach (var (item, w) in pool)
            {
                r -= w;
                if (r < 0) { result.Add(item); break; }
            }
        }
        return result;
    }
}
