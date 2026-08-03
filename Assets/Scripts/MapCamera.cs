using UnityEngine;

/// <summary>
/// 탑뷰 지도 카메라 (M0 조작 사양 반영판).
/// - 원근 카메라 수직 탑뷰: 건물 extrude 시 화면 가장자리 고층이 자연스럽게 기울어짐
/// - 팬: WASD / 방향키, 또는 마우스 좌·우클릭 드래그 (속도는 현재 줌 폭에 비례)
/// - 줌: 마우스 휠(커서 위치 기준), 또는 Q(줌아웃)/E(줌인, 화면 중앙 기준)
/// - 좌상단에 현재 뷰 폭과 줌 단계(1~6) 표시
/// </summary>
[RequireComponent(typeof(Camera))]
public class MapCamera : MonoBehaviour
{
    [Header("Zoom (뷰 가로폭, m)")]
    public float minViewWidth = 100f;      // 1단계
    public float maxViewWidth = 100000f;   // 6단계
    public float startViewWidth = 2000f;   // 시작: 3단계
    public float wheelZoomStep = 0.12f;    // 휠 1틱당 배율
    public float keyZoomPerSec = 1.2f;     // Q/E 초당 배율
    public float smoothTime = 0.12f;

    [Header("Pan")]
    [Tooltip("초당 이동량 = 현재 뷰 폭 × 이 값. 줌과 무관하게 화면 기준 이동 체감이 일정해짐")]
    public float keyPanScreenFraction = 0.7f;

    Camera cam;
    float targetWidth;
    float currentWidth;
    float widthVelocity;
    Vector3 targetFocus;       // 카메라가 내려다보는 지면 좌표
    Vector3 dragOriginWorld;
    bool dragging;

    // 줌 6단계 (뷰 폭 기준 근사 경계) — docs/design.md 참조
    static readonly (float width, string label)[] Tiers =
    {
        (250f,     "1단계 · 건물 상세 (100m)"),
        (1000f,    "2단계 · 운영 (500m)"),
        (5000f,    "3단계 · 운영 (2km)"),
        (15000f,   "4단계 · 동 단위 (10km)"),
        (50000f,   "5단계 · 구 단위 (20km)"),
        (float.MaxValue, "6단계 · 도시 단위 (100km)"),
    };

    void Awake()
    {
        cam = GetComponent<Camera>();
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 10f;
        cam.farClipPlane = 300000f;
        transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        targetWidth = currentWidth = startViewWidth;
        targetFocus = Vector3.zero;
        ApplyTransform();
    }

    void Update()
    {
        HandleWheelZoom();
        HandleKeyZoom();
        HandleDragPan();
        HandleKeyPan();

        currentWidth = Mathf.SmoothDamp(currentWidth, targetWidth, ref widthVelocity, smoothTime);
        ApplyTransform();
    }

    // ---------- Zoom ----------

    void HandleWheelZoom()
    {
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Approximately(scroll, 0f)) return;

        Vector3 before = ScreenToGround(Input.mousePosition);

        targetWidth = Mathf.Clamp(
            targetWidth * Mathf.Pow(1f - wheelZoomStep, scroll),
            minViewWidth, maxViewWidth);

        // 커서 기준 줌: 줌 후에도 커서가 같은 지면 좌표를 가리키도록 포커스 보정
        currentWidth = targetWidth;
        ApplyTransform();
        Vector3 after = ScreenToGround(Input.mousePosition);
        targetFocus += before - after;
        ApplyTransform();
    }

    void HandleKeyZoom()
    {
        float dir = 0f;
        if (Input.GetKey(KeyCode.E)) dir -= 1f; // 줌인
        if (Input.GetKey(KeyCode.Q)) dir += 1f; // 줌아웃
        if (Mathf.Approximately(dir, 0f)) return;

        targetWidth = Mathf.Clamp(
            targetWidth * Mathf.Pow(keyZoomPerSec, dir * Time.deltaTime),
            minViewWidth, maxViewWidth);
    }

    // ---------- Pan ----------

    void HandleDragPan()
    {
        bool down = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
        bool up   = Input.GetMouseButtonUp(0)   || Input.GetMouseButtonUp(1);

        if (down)
        {
            dragging = true;
            dragOriginWorld = ScreenToGround(Input.mousePosition);
        }
        if (up) dragging = false;

        if (dragging)
        {
            Vector3 nowWorld = ScreenToGround(Input.mousePosition);
            targetFocus += dragOriginWorld - nowWorld;
            ApplyTransform();
        }
    }

    void HandleKeyPan()
    {
        // GetAxisRaw: WASD + 방향키 모두 커버 (기본 Input Manager 기준)
        Vector3 input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        if (input.sqrMagnitude < 0.01f) return;

        // 핵심: 이동 속도가 현재 줌 폭에 비례해야 모든 줌 단계에서 체감이 일정하다
        float speed = currentWidth * keyPanScreenFraction;
        targetFocus += input.normalized * speed * Time.deltaTime;
    }

    // ---------- Core ----------

    void ApplyTransform()
    {
        // 뷰 폭 W = 2 * h * tan(hFov/2)  →  h = W / (2 * tan(hFov/2))
        float hFovRad = Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect)
                        * Mathf.Deg2Rad;
        float height = currentWidth / (2f * Mathf.Tan(hFovRad * 0.5f));
        transform.position = new Vector3(targetFocus.x, height, targetFocus.z);
    }

    Vector3 ScreenToGround(Vector3 screenPos)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        if (Mathf.Approximately(ray.direction.y, 0f)) return targetFocus;
        float t = -ray.origin.y / ray.direction.y;
        return ray.origin + ray.direction * t;
    }

    // ---------- Debug HUD ----------

    void OnGUI()
    {
        string tier = "";
        foreach (var (width, label) in Tiers)
            if (currentWidth <= width) { tier = label; break; }

        string widthText = currentWidth >= 1000f
            ? $"{currentWidth / 1000f:F1} km"
            : $"{currentWidth:F0} m";

        GUI.Label(new Rect(12, 10, 600, 24), $"뷰 폭: {widthText}", LabelStyle(16));
        GUI.Label(new Rect(12, 34, 600, 24), tier, LabelStyle(14));
        GUI.Label(new Rect(12, 58, 700, 24),
            "WASD/드래그: 이동 · 휠: 줌(커서 기준) · Q/E: 줌아웃/줌인", LabelStyle(12));
    }

    static GUIStyle _style;
    static GUIStyle LabelStyle(int size)
    {
        _style ??= new GUIStyle(GUI.skin.label) { normal = { textColor = Color.white } };
        _style.fontSize = size;
        return _style;
    }
}
