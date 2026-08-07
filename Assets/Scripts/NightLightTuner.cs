using UnityEngine;

/// <summary>
/// 야경 실시간 튜닝 패널. 플레이 중 F1으로 열고 슬라이더로 조절한다.
/// '값 출력' 버튼을 누르면 현재 설정이 콘솔에 한 줄로 찍히므로,
/// 마음에 드는 조합을 찾으면 그 로그를 그대로 남겨두면 된다.
///
/// 세팅: 빈 GameObject에 부착 (BuildingRenderer와 함께 있어도 됨)
/// </summary>
public class NightLightTuner : MonoBehaviour
{
    [Header("Panel")]
    public bool show = true;
    public KeyCode toggleKey = KeyCode.F1;

    [Header("밝기")]
    [Range(0.2f, 6f)] public float glowNear = 6.00f;      // 근거리(뷰폭 ~8km) 창문 밝기
    [Range(0.2f, 6f)] public float glowFar = 1.28f;       // 원거리(10km+) 점광 밝기
    [Range(0.3f, 3f)] public float dotSize = 1.54f;       // 원거리 점 크기 — 블룸이 잡을 픽셀 확보

    [Header("감쇠 (뷰폭별)")]
    [Range(0.1f, 1.5f)] public float attNear = 0.40f;    // 10km 이하
    [Range(0.1f, 1.5f)] public float attFar = 0.32f;      // 20km 부근

    [Header("다양성")]
    [Range(0f, 3f)] public float colorSaturation = 1.05f; // 색 채도
    [Range(0f, 1f)] public float intensityVariance = 0.72f; // 밝기 랜덤 폭 (바둑판 방지)

    [Header("밀도")]
    [Range(0f, 1f)] public float litRatio = 0.11f;       // 불 켜진 건물 비율
    [Range(0f, 1f)] public float windowLitRatio = 0.64f;  // 건물 내 점등 창문 비율
    [Range(0f, 1f)] public float densityInfluence = 0.80f; // 번화가 집중도

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) show = !show;
        Push();
    }

    void Push()
    {
        Shader.SetGlobalFloat("_ZakGlowNear", glowNear);
        Shader.SetGlobalFloat("_ZakGlowFar", glowFar);
        Shader.SetGlobalFloat("_ZakDotSize", dotSize);
        Shader.SetGlobalFloat("_ZakAttNear", attNear);
        Shader.SetGlobalFloat("_ZakAttFar", attFar);
        Shader.SetGlobalFloat("_ZakColorSat", colorSaturation);
        Shader.SetGlobalFloat("_ZakIntensityVar", intensityVariance);
        Shader.SetGlobalFloat("_ZakLitRatio", litRatio);
        Shader.SetGlobalFloat("_ZakWinRatio", windowLitRatio);
        Shader.SetGlobalFloat("_ZakDensityInfluence", densityInfluence);
    }

    Rect win = new Rect(0, 0, 330, 400);
    bool placed;

    void OnGUI()
    {
        if (!show) return;
        if (!placed) { win.x = Screen.width - 350; win.y = 110; placed = true; }
        win = GUI.Window(7200, win, Draw, "야경 튜닝 (F1)");
        UIState.Register(win);
    }

    void Draw(int id)
    {
        glowNear          = Row("근거리 밝기", glowNear, 0.2f, 6f);
        glowFar           = Row("원거리 밝기", glowFar, 0.2f, 6f);
        dotSize           = Row("원거리 점 크기", dotSize, 0.3f, 3f);
        GUILayout.Space(4);
        attNear           = Row("감쇠 (10km↓)", attNear, 0.1f, 1.5f);
        attFar            = Row("감쇠 (20km)", attFar, 0.1f, 1.5f);
        GUILayout.Space(4);
        colorSaturation   = Row("색 채도", colorSaturation, 0f, 3f);
        intensityVariance = Row("밝기 랜덤 폭", intensityVariance, 0f, 1f);
        GUILayout.Space(4);
        litRatio          = Row("점등 건물 비율", litRatio, 0f, 1f);
        windowLitRatio    = Row("점등 창문 비율", windowLitRatio, 0f, 1f);
        densityInfluence  = Row("번화가 집중도", densityInfluence, 0f, 1f);

        GUILayout.Space(8);
        if (GUILayout.Button("현재 값 콘솔 출력"))
            Debug.Log($"[야경] near={glowNear:F2} far={glowFar:F2} dot={dotSize:F2} " +
                      $"attN={attNear:F2} attF={attFar:F2} sat={colorSaturation:F2} " +
                      $"var={intensityVariance:F2} lit={litRatio:F2} win={windowLitRatio:F2} dens={densityInfluence:F2}");

        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }

    static GUIStyle _lab;
    float Row(string label, float value, float min, float max)
    {
        _lab ??= new GUIStyle(GUI.skin.label) { fontSize = 11 };
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{label}  {value:F2}", _lab, GUILayout.Width(150));
        value = GUILayout.HorizontalSlider(value, min, max);
        GUILayout.EndHorizontal();
        return value;
    }
}
