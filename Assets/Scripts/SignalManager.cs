using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// M5: 게임 루프의 심장.
/// 관망(신호 발견) → 파견(도로 이동) → 개입(수색) → 다음 신호
///
/// - 지도 위에 구조 신호(맥동하는 주황 마커) 3개가 활성화됨
/// - 신호를 클릭하면 유닛이 도로를 따라 그곳으로 이동
/// - 도착하면 해당 건물의 수색창이 자동으로 열림
/// - 신호 하나를 처리하면 새 신호가 등장. 5개 처리 = 세션 클리어
///
/// 세팅: 빈 GameObject에 부착. RouteTester는 체크 해제(비활성)할 것 — 클릭이 겹친다.
/// </summary>
public class SignalManager : MonoBehaviour
{
    [Header("Refs (비워두면 자동)")]
    public ShopMapRenderer shopData;
    public RoadNetwork roads;
    public BuildingLootUI lootUI;

    [Header("Session")]
    public int activeSignals = 3;
    public int sessionGoal = 5;
    public float signalMaxDist = 5000f;   // 원점 기준 신호 생성 반경(m)
    public int minShopsForSignal = 5;     // 업소 이만큼 이상인 건물만 신호 후보

    [Header("Unit")]
    public float unitSpeed = 400f;        // m/s

    Camera cam;
    Vector3 mouseDownPos;
    bool initialized;
    string message = "구조 신호를 찾는 중...";

    // 신호
    class Signal
    {
        public ShopMapRenderer.Building building;
        public Transform marker;
    }
    readonly List<Signal> signals = new();
    List<ShopMapRenderer.Building> candidates;

    // 유닛
    Transform unit;
    List<Vector3> path;
    float pathDist, progress;
    Signal target;
    LineRenderer line;

    void Start()
    {
        cam = Camera.main;
        if (shopData == null) shopData = FindFirstObjectByType<ShopMapRenderer>();
        if (roads == null) roads = FindFirstObjectByType<RoadNetwork>();
        if (lootUI == null) lootUI = FindFirstObjectByType<BuildingLootUI>();

        GameState.Reset();

        unit = MakeMarker("Unit", Color.white, 36f);
        unit.gameObject.SetActive(true);
        unit.position = Vector3.zero + Vector3.up * 2f;

        var lineGo = new GameObject("DispatchLine");
        line = lineGo.AddComponent<LineRenderer>();
        line.material = new Material(Shader.Find("Sprites/Default"));
        line.startColor = line.endColor = new Color(0.3f, 1f, 0.6f);
        line.positionCount = 0;
        line.alignment = LineAlignment.TransformZ;
        lineGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    void Update()
    {
        if (!initialized)
        {
            if (roads == null || !roads.Ready || shopData == null || shopData.Buildings.Count == 0)
                return;
            InitSignals();
            initialized = true;
        }

        PulseMarkers();
        HandleClick();
        MoveUnit();

        if (line.positionCount > 0)
            line.widthMultiplier = cam.transform.position.y * 0.004f;
    }

    // ---------- 신호 ----------

    void InitSignals()
    {
        candidates = shopData.Buildings.Values
            .Where(b => b.shops.Count >= minShopsForSignal
                     && b.pos.sqrMagnitude < signalMaxDist * signalMaxDist)
            .ToList();

        for (int i = 0; i < activeSignals; i++) SpawnSignal();
        message = $"구조 신호 {signals.Count}개 감지 — 신호를 클릭해 파견하라";
    }

    void SpawnSignal()
    {
        if (candidates.Count == 0) return;
        for (int tryN = 0; tryN < 50; tryN++)
        {
            var b = candidates[Random.Range(0, candidates.Count)];
            if (signals.Any(s => s.building == b)) continue;

            var marker = MakeMarker($"Signal_{b.title}", new Color(1f, 0.45f, 0.15f), 60f);
            marker.position = b.pos + Vector3.up * 3f;
            marker.gameObject.SetActive(true);
            signals.Add(new Signal { building = b, marker = marker });
            return;
        }
    }

    void PulseMarkers()
    {
        float s = 1f + Mathf.Sin(Time.time * 4f) * 0.35f;
        foreach (var sig in signals)
            sig.marker.localScale = Vector3.one * 60f * s;
    }

    // ---------- 입력 ----------

    void HandleClick()
    {
        if (Input.GetMouseButtonDown(0)) mouseDownPos = Input.mousePosition;
        if (!Input.GetMouseButtonUp(0)) return;
        if ((Input.mousePosition - mouseDownPos).magnitude > 6f) return;
        if (lootUI != null && lootUI.IsOpen) return; // 수색창 열려있으면 무시

        Vector3 world = ScreenToGround(Input.mousePosition);
        float grabRadius = CurrentViewWidth() * 0.035f; // 화면 폭의 3.5%

        Signal nearest = null;
        float bestD = grabRadius * grabRadius;
        foreach (var s in signals)
        {
            float d = (s.building.pos - world).sqrMagnitude;
            if (d < bestD) { bestD = d; nearest = s; }
        }
        if (nearest != null) Dispatch(nearest);
    }

    // ---------- 파견/이동 ----------

    void Dispatch(Signal sig)
    {
        long a = roads.NearestNode(unit.position);
        long b = roads.NearestNode(sig.building.pos);
        var p = Pathfinder.FindPath(roads, a, b);
        if (p == null || p.Count < 2)
        {
            message = "그곳까지의 경로를 찾을 수 없다...";
            return;
        }

        path = p;
        pathDist = 0f;
        for (int i = 1; i < path.Count; i++) pathDist += Vector3.Distance(path[i - 1], path[i]);
        progress = 0f;
        target = sig;
        message = $"{sig.building.title}(으)로 이동 중 — {pathDist / 1000f:F1}km";

        line.positionCount = path.Count;
        for (int i = 0; i < path.Count; i++)
            line.SetPosition(i, path[i] + Vector3.up * 1f);
    }

    void MoveUnit()
    {
        if (path == null) return;

        progress = Mathf.Min(progress + unitSpeed * Time.deltaTime, pathDist);
        unit.position = PointAlongPath(progress) + Vector3.up * 2f;

        if (progress >= pathDist) Arrive();
    }

    Vector3 PointAlongPath(float dist)
    {
        float remain = dist;
        for (int i = 1; i < path.Count; i++)
        {
            float seg = Vector3.Distance(path[i - 1], path[i]);
            if (remain <= seg)
                return Vector3.Lerp(path[i - 1], path[i], seg < 0.001f ? 0f : remain / seg);
            remain -= seg;
        }
        return path[^1];
    }

    void Arrive()
    {
        var b = target.building;
        message = $"{b.title} 도착 — 수색 개시";

        Destroy(target.marker.gameObject);
        signals.Remove(target);
        path = null;
        line.positionCount = 0;
        target = null;

        GameState.signalsResolved++;
        lootUI?.Open(b);

        if (GameState.signalsResolved < sessionGoal) SpawnSignal();
    }

    // ---------- 유틸 ----------

    Transform MakeMarker(string name, Color color, float size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.localScale = Vector3.one * size;
        var mat = new Material(Shader.Find("Sprites/Default")) { color = color };
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        go.SetActive(false);
        return go.transform;
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

    // ---------- HUD ----------

    void OnGUI()
    {
        GUIStyle big = new(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        big.normal.textColor = Color.white;
        GUIStyle mid = new(GUI.skin.label) { fontSize = 12 };
        mid.normal.textColor = Color.white;

        float x = Screen.width - 330;
        GUI.Label(new Rect(x, 10, 320, 24),
            $"스태미너 {GameState.stamina}/{GameState.maxStamina}   소음 {GameState.noise:F0}", big);
        GUI.Label(new Rect(x, 34, 320, 22),
            $"신호 처리 {GameState.signalsResolved}/{sessionGoal}", mid);
        GUI.Label(new Rect(16, Screen.height - 200, 700, 22), message, mid);

        if (GameState.signalsResolved >= sessionGoal)
        {
            int itemTotal = GameState.inventory.Values.Sum();
            GUI.Label(new Rect(Screen.width / 2 - 250, Screen.height / 2 - 20, 500, 60),
                $"세션 클리어! 신호 {sessionGoal}개 처리 — 물자 {itemTotal}개, 남은 스태미너 {GameState.stamina}", big);
        }
    }
}
