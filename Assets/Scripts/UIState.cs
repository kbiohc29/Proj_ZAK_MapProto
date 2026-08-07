using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// IMGUI(OnGUI)는 마우스 입력을 '소비'하지 않아서, 창을 조작해도
/// Update()에서 Input을 읽는 스크립트들이 그대로 반응한다.
/// 이 클래스가 그 게이트 역할을 한다.
///
/// 사용법
/// - UI 쪽: OnGUI에서 자기 창 영역을 UIState.Register(rect) 로 등록
/// - 월드 입력 쪽: 입력 처리 전에 if (UIState.PointerOverUI) return;
/// </summary>
public static class UIState
{
    static readonly List<Rect> filling = new();   // 이번 프레임에 등록 중
    static readonly List<Rect> active = new();    // 직전 프레임 완성본 (판정에 사용)
    static int lastFrame = -1;

    /// <summary>OnGUI에서 창 영역을 등록한다</summary>
    public static void Register(Rect r)
    {
        if (Time.frameCount != lastFrame)
        {
            active.Clear();
            active.AddRange(filling);
            filling.Clear();
            lastFrame = Time.frameCount;
        }
        filling.Add(r);
    }

    /// <summary>마우스가 UI 위에 있거나, UI 컨트롤이 드래그 중인가</summary>
    public static bool PointerOverUI
    {
        get
        {
            // 슬라이더 등을 잡고 창 밖으로 끌고 나간 경우까지 커버
            if (GUIUtility.hotControl != 0) return true;

            Vector2 m = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            foreach (var r in active) if (r.Contains(m)) return true;
            foreach (var r in filling) if (r.Contains(m)) return true;
            return false;
        }
    }
}
