using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// M1 테스트: 지도에서 클릭 두 번으로 출발/도착을 찍으면
/// A* 경로를 그리고 유닛(구체)이 도로를 따라 이동한다.
/// - 클릭 vs 드래그 구분: 마우스 다운→업 사이 이동이 6px 미만이면 클릭으로 판정
///   (드래그 팬과 충돌하지 않음)
/// - 세 번째 클릭부터는 새 출발점으로 리셋
/// </summary>
public class RouteTester : MonoBehaviour
{
    public RoadNetwork network;
    public float unitSpeed = 600f;       // m/s. 현실보다 빠르게 (게임 속도감)
    public float lineWidthPerHeight = 0.004f; // 라인 폭 = 카메라 높이 × 이 값
    public float minViewWidthForRoute = 1000f; // 이 줌보다 가까우면(근접 줌) 클릭 무시 (건물 루팅 UI와 역할 분담)

    Camera cam;
    Vector3 mouseDownPos;
    Vector3? startPos;
    List<Vector3> path;
    float pathDist;

    LineRenderer line;
    Transform startMarker, endMarker, unit;

    void Start()
    {
        cam = Camera.main;
        if (network == null) network = FindFirstObjectByType<RoadNetwork>();

        var lineGo = new GameObject("RouteLine");
        line = lineGo.AddComponent<LineRenderer>();
        line.material = new Material(Shader.Find("Sprites/Default"));
        line.startColor = line.endColor = new Color(0.2f, 0.9f, 1f);
        line.positionCount = 0;
        line.alignment = LineAlignment.TransformZ;
        lineGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 지면에 눕히기

        startMarker = MakeMarker("Start", new Color(0.2f, 1f, 0.4f));
        endMarker   = MakeMarker("End",   new Color(1f, 0.3f, 0.3f));
        unit        = MakeMarker("Unit",  Color.white);
        unit.localScale = Vector3.one * 30f;
    }

    Transform MakeMarker(string name, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.localScale = Vector3.one * 45f;
        var mat = new Material(Shader.Find("Sprites/Default")) { color = color };
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        go.SetActive(false);
        return go.transform;
    }

    void Update()
    {
        if (network == null || !network.Ready) return;

        HandleClick();
        MoveUnit();

        // 줌에 따라 라인 폭 보정 (어느 줌에서도 보이게)
        if (line.positionCount > 0)
            line.widthMultiplier = cam.transform.position.y * lineWidthPerHeight;
    }

    void HandleClick()
    {
        if (Input.GetMouseButtonDown(0)) mouseDownPos = Input.mousePosition;
        if (!Input.GetMouseButtonUp(0)) return;
        if ((Input.mousePosition - mouseDownPos).magnitude > 6f) return; // 드래그였음
        if (CurrentViewWidth() < minViewWidthForRoute) return; // 근접 줌에서는 무시

        Vector3 world = ScreenToGround(Input.mousePosition);

        if (startPos == null || path != null)
        {
            // 새 출발점
            startPos = world;
            path = null;
            line.positionCount = 0;
            endMarker.gameObject.SetActive(false);
            unit.gameObject.SetActive(false);
            Place(startMarker, world);
        }
        else
        {
            // 도착점 → 경로 계산
            long a = network.NearestNode(startPos.Value);
            long b = network.NearestNode(world);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            path = Pathfinder.FindPath(network, a, b);
            sw.Stop();

            if (path == null)
            {
                Debug.LogWarning("경로를 찾지 못했습니다 (그래프가 끊긴 지점일 수 있음)");
                startPos = null;
                startMarker.gameObject.SetActive(false);
                return;
            }

            pathDist = 0f;
            for (int i = 1; i < path.Count; i++) pathDist += Vector3.Distance(path[i - 1], path[i]);
            Debug.Log($"경로: {path.Count}개 노드, {pathDist / 1000f:F2}km, 탐색 {sw.ElapsedMilliseconds}ms");

            Place(endMarker, world);
            line.positionCount = path.Count;
            for (int i = 0; i < path.Count; i++)
                line.SetPosition(i, path[i] + Vector3.up * 1f);

            unitProgress = 0f;
            Place(unit, path[0]);
        }
    }

    float unitProgress; // 경로상 이동 거리(m)

    void MoveUnit()
    {
        if (path == null || path.Count < 2) return;

        unitProgress = Mathf.Min(unitProgress + unitSpeed * Time.deltaTime, pathDist);

        float remain = unitProgress;
        for (int i = 1; i < path.Count; i++)
        {
            float seg = Vector3.Distance(path[i - 1], path[i]);
            if (remain <= seg)
            {
                unit.position = Vector3.Lerp(path[i - 1], path[i], seg < 0.001f ? 0 : remain / seg)
                                + Vector3.up * 2f;
                return;
            }
            remain -= seg;
        }
        unit.position = path[^1] + Vector3.up * 2f;
    }

    void Place(Transform t, Vector3 pos)
    {
        t.position = pos + Vector3.up * 2f;
        t.gameObject.SetActive(true);
    }

    Vector3 ScreenToGround(Vector3 screenPos)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        float t = -ray.origin.y / ray.direction.y;
        return ray.origin + ray.direction * t;
    }

    float CurrentViewWidth()
    {
        float hFov = Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect) * Mathf.Deg2Rad;
        return 2f * cam.transform.position.y * Mathf.Tan(hFov * 0.5f);
    }
}
