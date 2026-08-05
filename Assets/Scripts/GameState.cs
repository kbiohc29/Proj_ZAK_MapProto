using System.Collections.Generic;

/// <summary>
/// M5: 시스템 간 공유 상태. 스태미너/소음/가방이 루프 전체를 관통한다.
/// (추후 전투 씬 연결 시에도 이 클래스로 상태를 주고받는다)
/// </summary>
public static class GameState
{
    public static int stamina = 100;
    public static int maxStamina = 100;

    /// <summary>수색 소음. 높을수록 감염체 조우 확률 상승. 시간이 지나면 감소.</summary>
    public static float noise = 0f;

    public static Dictionary<string, int> inventory = new();
    public static int signalsResolved = 0;

    public static void Reset()
    {
        stamina = maxStamina;
        noise = 0f;
        inventory.Clear();
        signalsResolved = 0;
    }
}
