using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// M6 v2: 플레이어 캐릭터 — 네비게이션 이동 방식.
///
/// 조작
/// - 마우스 우클릭: 그 지점까지 도로 경로(A*)를 따라 이동. 이동 중 재클릭 시 경로 갱신
/// - 마우스 좌클릭: 오브젝트 정보 열람 (BuildingInspector 담당)
/// - WASD: 카메라 이동 (MapCamera 담당)
///
/// 규격: 주변 좀비 밀도에 비례한 지속 피해 (닿으면 피해 / 공격 모션 없음)
/// </summary>
public class PlayerCharacter : MonoBehaviour
{
    [Header("Refs")]
    public ZombieFlow flow;
    public RoadNetwork roads;
    public WalkGrid walkGrid;
    public ShopMapRenderer shopData;
    public BuildingLootUI lootUI;

    [Header("Move")]
    public float moveSpeed = 45f;          // m/s
    public float arriveThreshold = 4f;
    [Tooltip("도로에서 목표까지 직선으로 접근하는 최대 거리(m)")]
    public float offRoadReach = 120f;

    [Header("Health")]
    [Tooltip("테스트 중 쓰러지지 않도록 크게. 밸런싱 시 100으로")]
    public float maxHealth = 100000f;

    [Header("Danger")]
    public float dangerRadius = 90f;
    public float damagePerZombiePerSec = 0.020f;
    public float safeThreshold = 3f;

    [Header("Loot")]
    public float lootRange = 60f;
    [Tooltip("도착 시 자동으로 수색창 열기 (기본 꺼짐 — 수색은 직접 열도록)")]
    public bool autoLootOnArrive = false;

    public float Health { get; private set; }
    public float NearbyZombies { get; private set; }
    public bool IsMoving => path != null;

    List<Vector3> path;
    int pathIndex;
    ShopMapRenderer.Building pendingLoot;

    Transform body, ring, pulseRing;
    bool spawned;
    LineRenderer routeLine;
    Camera cam;
    Vector3 rightDownPos;

    void Start()
    {
        cam = Camera.main;
        if (flow == null) flow = FindFirstObjectByType<ZombieFlow>();
        if (roads == null) roads = FindFirstObjectByType<RoadNetwork>();
        if (walkGrid == null) walkGrid = FindFirstObjectByType<WalkGrid>();
        if (shopData == null) shopData = FindFirstObjectByType<ShopMapRenderer>();
        if (lootUI == null) lootUI = FindFirstObjectByType<BuildingLootUI>();

        Health = maxHealth;

        body      = MakeDisc("PlayerBody",  new Color(1f, 1f, 1f, 1f),          8f,  3300);
        pulseRing = MakeRing("PlayerPulse", new Color(0.2f, 1f, 0.9f, 0.9f),   16f, 3f, 3290);
        ring      = MakeDisc("DangerRing",  new Color(1f, 0.25f, 0.25f, 0.2f), dangerRadius, 3050);

        var lineGo = new GameObject("PlayerRoute");
        routeLine = lineGo.AddComponent<LineRenderer>();
        routeLine.material = new Material(Shader.Find("Sprites/Default"));
        // 캐릭터(흰색/청록)와 구분되도록 경로선은 호박색
        routeLine.startColor = routeLine.endColor = new Color(1f, 0.75f, 0.2f, 0.75f);
        routeLine.material.renderQueue = 3080;
        routeLine.positionCount = 0;
        routeLine.alignment = LineAlignment.TransformZ;
        lineGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    void Update()
    {
        if (!spawned) TrySpawnOnRoad();

        HandleRightClick();
        MoveAlongPath();
        HandleDanger();

        body.position = transform.position + Vector3.up * 4f;
        ring.position = transform.position + Vector3.up * 1.0f;

        // 펄스 링: 크기와 투명도가 맥동해서 눈에 띄게
        float t = Mathf.Repeat(Time.time * 0.9f, 1f);
        pulseRing.position = transform.position + Vector3.up * 4f;
        pulseRing.localScale = Vector3.one * Mathf.Lerp(0.7f, 1.9f, t);
        var pm = pulseRing.GetComponent<MeshRenderer>().sharedMaterial;
        pm.color = new Color(0.2f, 1f, 0.9f, Mathf.Lerp(0.9f, 0f, t));

        if (routeLine.positionCount > 0)
            routeLine.widthMultiplier = Mathf.Clamp(cam.transform.position.y * 0.0012f, 1.2f, 14f);
    }

    /// <summary>도로 데이터가 준비되면 원점에서 가장 가까운 도로 노드 위에 세운다</summary>
    void TrySpawnOnRoad()
    {
        if (roads == null || !roads.Ready) return;
        long n = roads.NearestNode(Vector3.zero);
        transform.position = n >= 0 ? roads.NodePos[n] : Vector3.zero;
        spawned = true;
    }

    // ---------- 이동 지시 ----------

    void HandleRightClick()
    {
        if (UIState.PointerOverUI) return;
        if (Input.GetMouseButtonDown(1)) rightDownPos = Input.mousePosition;
        if (!Input.GetMouseButtonUp(1)) return;
        if ((Input.mousePosition - rightDownPos).magnitude > 6f) return; // 드래그(지도 팬)였음
        if (roads == null || !roads.Ready) return;
        if (lootUI != null && lootUI.IsOpen) return;

        Vector3 dest = ScreenToGround(Input.mousePosition);
        SetDestination(dest);
    }

    public void SetDestination(Vector3 dest)
    {
        // 1순위: 보행 격자 경로 (도로에 얽매이지 않는 자연스러운 최단 경로)
        if (walkGrid != null && walkGrid.Ready)
        {
            var gp = walkGrid.FindPath(transform.position, dest);
            if (gp != null && gp.Count >= 2) { ApplyPath(gp, dest); return; }
        }

        // 2순위: 도로 그래프 (격자 범위 밖 장거리)
        long a = roads.NearestNode(transform.position);
        long b = roads.NearestNode(dest);
        if (a < 0 || b < 0) return;

        var p = Pathfinder.FindPath(roads, a, b);
        if (p == null || p.Count == 0)
        {
            // 경로 없음: 아주 가까우면 직선 이동 허용
            if ((dest - transform.position).magnitude < offRoadReach)
                p = new List<Vector3> { transform.position, dest };
            else return;
        }
        else
        {
            // 도로 노드에서 실제 클릭 지점까지 마지막 한 발 (건물 앞까지)
            if ((dest - p[^1]).magnitude < offRoadReach) p.Add(dest);
        }

        ApplyPath(p, dest);
    }

    void ApplyPath(List<Vector3> p, Vector3 dest)
    {
        path = p;
        pathIndex = 0;

        routeLine.positionCount = path.Count;
        for (int i = 0; i < path.Count; i++)
            routeLine.SetPosition(i, path[i] + Vector3.up * 1.5f);

        pendingLoot = autoLootOnArrive && shopData != null
            ? shopData.NearestBuilding(dest, lootRange) : null;
    }

    void MoveAlongPath()
    {
        if (path == null) return;

        Vector3 target = path[pathIndex];
        Vector3 flat = new Vector3(target.x, transform.position.y, target.z);
        float step = moveSpeed * Time.deltaTime;

        if ((flat - transform.position).magnitude <= Mathf.Max(step, arriveThreshold))
        {
            transform.position = flat;
            pathIndex++;
            if (pathIndex >= path.Count) Arrive();
            return;
        }

        transform.position += (flat - transform.position).normalized * step;
    }

    void Arrive()
    {
        path = null;
        routeLine.positionCount = 0;

        if (pendingLoot != null && lootUI != null)
        {
            lootUI.Open(pendingLoot);
            pendingLoot = null;
        }
    }

    // ---------- 위험 ----------

    void HandleDanger()
    {
        if (flow == null) return;
        NearbyZombies = flow.ZombiesNear(transform.position, dangerRadius);
        if (NearbyZombies <= safeThreshold) return;

        Health = Mathf.Max(0f,
            Health - (NearbyZombies - safeThreshold) * damagePerZombiePerSec * Time.deltaTime);
    }

    // ---------- 유틸 ----------

    Vector3 ScreenToGround(Vector3 screenPos)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        if (Mathf.Abs(ray.direction.y) < 0.0001f) return transform.position;
        float t = -ray.origin.y / ray.direction.y;
        return ray.origin + ray.direction * t;
    }

    /// <summary>지면에 눕는 원형 디스크</summary>
    Transform MakeDisc(string name, Color col, float radius, int queue)
    {
        const int SEG = 48;
        var v = new Vector3[SEG + 1];
        var t = new int[SEG * 3];
        v[0] = Vector3.zero;
        for (int i = 0; i < SEG; i++)
        {
            float a = i * Mathf.PI * 2f / SEG;
            v[i + 1] = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            t[i * 3] = 0; t[i * 3 + 1] = i + 1; t[i * 3 + 2] = (i + 1) % SEG + 1;
        }
        return MakeMeshObject(name, col, v, t, queue);
    }

    /// <summary>지면에 눕는 원형 테두리(도넛)</summary>
    Transform MakeRing(string name, Color col, float radius, float thickness, int queue)
    {
        const int SEG = 48;
        var v = new Vector3[SEG * 2];
        var t = new int[SEG * 6];
        float inner = radius - thickness;
        for (int i = 0; i < SEG; i++)
        {
            float a = i * Mathf.PI * 2f / SEG;
            Vector3 d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            v[i * 2] = d * inner;
            v[i * 2 + 1] = d * radius;

            int n = (i + 1) % SEG;
            t[i * 6]     = i * 2;     t[i * 6 + 1] = i * 2 + 1; t[i * 6 + 2] = n * 2 + 1;
            t[i * 6 + 3] = i * 2;     t[i * 6 + 4] = n * 2 + 1; t[i * 6 + 5] = n * 2;
        }
        return MakeMeshObject(name, col, v, t, queue);
    }

    Transform MakeMeshObject(string name, Color col, Vector3[] verts, int[] tris, int queue)
    {
        var go = new GameObject(name);
        var mesh = new Mesh();
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        var mat = new Material(Shader.Find("Sprites/Default")) { color = col };
        mat.renderQueue = queue;
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return go.transform;
    }

    void OnGUI()
    {
        GUIStyle big = new(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        big.normal.textColor = Health > 30f ? Color.white : new Color(1f, 0.4f, 0.4f);
        GUIStyle mid = new(GUI.skin.label) { fontSize = 12 };
        mid.normal.textColor = Color.white;

        GUI.Label(new Rect(16, Screen.height - 245, 400, 24),
            $"체력 {Health:F0}/{maxHealth:F0}   스태미너 {GameState.stamina}", big);
        GUI.Label(new Rect(16, Screen.height - 222, 480, 22),
            $"주변 좀비 {NearbyZombies:F0}" + (flow != null ? $"   (전체 {flow.TotalZombies:N0})" : "")
            + (IsMoving ? "   · 이동 중" : ""), mid);
        GUI.Label(new Rect(16, Screen.height - 200, 700, 22),
            "좌클릭: 정보 · 우클릭: 캐릭터 이동 · 수색은 정보창에서", mid);

        if (Health <= 0f)
        {
            GUIStyle dead = new(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            dead.normal.textColor = new Color(1f, 0.3f, 0.3f);
            GUI.Label(new Rect(Screen.width / 2 - 100, Screen.height / 2, 400, 40), "쓰러졌다...", dead);
        }
    }
}
