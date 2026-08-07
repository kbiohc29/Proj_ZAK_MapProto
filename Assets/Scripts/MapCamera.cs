using UnityEngine;

/// <summary>
/// 통합 지도 카메라 (M6판). 줌 + 팬 + 틸트 + 플레이어 추적을 한 스크립트에서 처리한다.
/// MapCameraTilt는 삭제할 것 — 두 스크립트가 트랜스폼을 나눠 만지면 떨림이 생긴다.
///
/// - 시점: focus(지면의 주목 지점) + 높이 + pitch로 매 프레임 새로 계산 (누적 없음)
/// - 고고도 90도 탑뷰 → 저고도 틸트 자동 전환
/// - 줌: 휠(커서 기준) / Q·E
/// - 팬: 마우스 드래그. WASD는 플레이어가 있으면 플레이어에게 양보
/// - C: 플레이어 추적 토글
/// </summary>
[RequireComponent(typeof(Camera))]
public class MapCamera : MonoBehaviour
{
    [Header("Zoom (뷰 가로폭, m)")]
    public float minViewWidth = 100f;
    public float maxViewWidth = 100000f;
    public float startViewWidth = 2000f;
    public float wheelZoomStep = 0.12f;
    public float keyZoomPerSec = 1.2f;
    public float smoothTime = 0.12f;

    [Header("Pan")]
    public float keyPanScreenFraction = 0.7f;

    [Header("Tilt")]
    public float tiltStartHeight = 1200f;
    public float tiltFullHeight = 250f;
    [Range(45f, 90f)]
    [Tooltip("최대 틸트 각도. 90=탑뷰. 건물 가림 때문에 75~80 권장")]
    public float minPitch = 78f;
    public float tiltSmooth = 5f;

    [Header("Follow")]
    [Tooltip("C키로 켜는 지속 추적. 기본은 꺼짐 — WASD로 자유 관찰")]
    public bool followPlayer = false;
    public float followSmooth = 6f;
    [Tooltip("Space로 캐릭터에게 돌아갈 때의 속도")]
    public float recenterSmooth = 8f;

    Camera cam;
    float targetWidth, currentWidth, widthVelocity;
    Vector3 focus;               // 화면 중앙이 바라보는 지면 좌표
    Vector3 dragOriginWorld, dragStartScreen;
    bool dragging, dragMoved;
    float pitch = 90f;
    bool recentering;
    PlayerCharacter player;

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

        targetWidth = currentWidth = startViewWidth;
        focus = Vector3.zero;
        Apply();
    }

    void Start()
    {
        player = FindFirstObjectByType<PlayerCharacter>();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.C)) followPlayer = !followPlayer;
        if (Input.GetKeyDown(KeyCode.Space)) recentering = true;

        HandleWheelZoom();
        HandleKeyZoom();
        HandleDragPan();
        HandleKeyPan();

        currentWidth = Mathf.SmoothDamp(currentWidth, targetWidth, ref widthVelocity, smoothTime);

        // Space 복귀: 캐릭터에게 빠르게(순간이동 아님) 돌아간다
        if (recentering && player != null)
        {
            Vector3 t = player.transform.position;
            focus.x = Mathf.Lerp(focus.x, t.x, Time.deltaTime * recenterSmooth);
            focus.z = Mathf.Lerp(focus.z, t.z, Time.deltaTime * recenterSmooth);
            if ((new Vector2(focus.x - t.x, focus.z - t.z)).magnitude < currentWidth * 0.01f)
                recentering = false;
        }
        // C키 지속 추적 (기본 꺼짐)
        else if (followPlayer && player != null)
        {
            Vector3 t = player.transform.position;
            focus.x = Mathf.Lerp(focus.x, t.x, Time.deltaTime * followSmooth);
            focus.z = Mathf.Lerp(focus.z, t.z, Time.deltaTime * followSmooth);
        }

        Apply();
    }

    // ---------- 입력 ----------

    void HandleWheelZoom()
    {
        if (UIState.PointerOverUI) return;
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Approximately(scroll, 0f)) return;

        Vector3 before = ScreenToGround(Input.mousePosition);

        targetWidth = Mathf.Clamp(
            targetWidth * Mathf.Pow(1f - wheelZoomStep, scroll), minViewWidth, maxViewWidth);
        currentWidth = targetWidth;
        Apply();

        // 커서 기준 줌 (추적 중엔 focus를 건드리지 않음 — 추적이 focus를 소유)
        if (!(followPlayer && player != null))
        {
            Vector3 after = ScreenToGround(Input.mousePosition);
            focus += before - after;
            Apply();
        }
    }

    void HandleKeyZoom()
    {
        float dir = 0f;
        if (Input.GetKey(KeyCode.E)) dir -= 1f;
        if (Input.GetKey(KeyCode.Q)) dir += 1f;
        if (Mathf.Approximately(dir, 0f)) return;

        targetWidth = Mathf.Clamp(
            targetWidth * Mathf.Pow(keyZoomPerSec, dir * Time.deltaTime), minViewWidth, maxViewWidth);
    }

    void HandleDragPan()
    {
        // 좌클릭/휠 드래그로 팬. 좌클릭은 '움직였을 때만' 팬으로 취급하므로
        // 제자리 클릭(=건물 정보 열람)과 공존한다.
        if ((Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(2)) && !UIState.PointerOverUI)
        {
            dragging = true;
            dragStartScreen = Input.mousePosition;
            dragOriginWorld = ScreenToGround(Input.mousePosition);
            dragMoved = false;
        }
        if (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(2)) dragging = false;
        if (!dragging) return;

        if (!dragMoved)
        {
            if ((Input.mousePosition - dragStartScreen).magnitude < 6f) return; // 아직 클릭일 수 있음
            dragMoved = true;
            dragOriginWorld = ScreenToGround(dragStartScreen);
        }

        // 드래그하면 추적 해제 (사용자가 직접 보고 싶어한다는 뜻)
        if (followPlayer && player != null) followPlayer = false;

        Vector3 now = ScreenToGround(Input.mousePosition);
        focus += dragOriginWorld - now;
        Apply();
    }

    void HandleKeyPan()
    {
        // WASD는 항상 지도 관찰용 (캐릭터는 우클릭 네비게이션)
        Vector3 input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        if (input.sqrMagnitude < 0.01f) return;

        // 직접 움직이면 추적/복귀는 즉시 해제 — 자유 관찰이 우선
        recentering = false;
        followPlayer = false;

        focus += input.normalized * (currentWidth * keyPanScreenFraction) * Time.deltaTime;
    }

    // ---------- 시점 계산 ----------

    void Apply()
    {
        // 높이: 뷰 폭 W = 2h·tan(hFov/2)
        float hFovRad = Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect) * Mathf.Deg2Rad;
        float height = currentWidth / (2f * Mathf.Tan(hFovRad * 0.5f));

        // 틸트: 낮을수록 눕는다
        float t = Mathf.Clamp01(Mathf.InverseLerp(tiltStartHeight, tiltFullHeight, height));
        float targetPitch = Mathf.Lerp(90f, minPitch, t);
        pitch = Mathf.Lerp(pitch, targetPitch, Time.deltaTime * tiltSmooth);
        if (Time.frameCount < 3) pitch = targetPitch; // 첫 프레임 튀는 것 방지

        // 매 프레임 focus로부터 새로 계산 → 누적 오차/떨림 없음
        float back = height / Mathf.Tan(pitch * Mathf.Deg2Rad);
        transform.rotation = Quaternion.Euler(pitch, 0f, 0f);
        transform.position = new Vector3(focus.x, height, focus.z - back);
    }

    Vector3 ScreenToGround(Vector3 screenPos)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        if (Mathf.Abs(ray.direction.y) < 0.0001f) return focus;
        float t = -ray.origin.y / ray.direction.y;
        return ray.origin + ray.direction * t;
    }

    // ---------- HUD ----------

    void OnGUI()
    {
        string tier = "";
        foreach (var (width, label) in Tiers)
            if (currentWidth <= width) { tier = label; break; }

        string widthText = currentWidth >= 1000f ? $"{currentWidth / 1000f:F1} km" : $"{currentWidth:F0} m";

        GUI.Label(new Rect(12, 10, 600, 24), $"뷰 폭: {widthText}   각도: {pitch:F0}°", LabelStyle(16));
        GUI.Label(new Rect(12, 34, 600, 24), tier, LabelStyle(14));
        GUI.Label(new Rect(12, 58, 700, 24),
            "WASD/좌드래그: 지도 이동 · Space: 캐릭터 복귀 · 좌클릭: 정보 · 우클릭: 이동 · 휠: 줌", LabelStyle(12));
    }

    static GUIStyle _style;
    static GUIStyle LabelStyle(int size)
    {
        _style ??= new GUIStyle(GUI.skin.label) { normal = { textColor = Color.white } };
        _style.fontSize = size;
        return _style;
    }
}
