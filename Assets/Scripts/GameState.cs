using System.Collections.Generic;

/// <summary>
/// 시스템 간 공유 상태. 스태미너/가방이 루프 전체를 관통한다.
/// (추후 전투 씬 연결 시에도 이 클래스로 상태를 주고받는다)
/// </summary>
public static class GameState
{
    public static int stamina = 100;
    public static int maxStamina = 100;

    public static Dictionary<string, int> inventory = new();

    public static void Reset()
    {
        stamina = maxStamina;
        inventory.Clear();
    }
}
