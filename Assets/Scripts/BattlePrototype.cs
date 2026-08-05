using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// M4: 언더테일식 전투 프로토타입 (지도와 독립된 별도 씬용).
///
/// 규칙
/// - 턴 시작: [공격한다] 또는 [회피에 집중한다] 선택
///   · 공격: 적 HP -25, 스태미너 -20
///   · 회피 집중: 스태미너 +10 회복, 이번 턴 회피 시간 1초 단축
/// - 선택 후: 적의 공격 패턴을 7초간 회피 (WASD/방향키)
/// - 승리 조건 두 가지:
///   · 적 HP 0 → "처치"
///   · 4턴 생존 → "따돌리기 성공" (남은 스태미너 = 가져가는 자원)
/// - 패턴 3종: 방사형 탄막 / 낙하 비 / 좌우 교차 스윕. 턴마다 순환하며 점점 빨라짐
///
/// 세팅: 새 씬에 빈 GameObject 하나 만들고 이 스크립트만 붙이면 끝.
/// 카메라/아레나/플레이어 전부 코드가 만든다. R: 재시작
/// </summary>
public class BattlePrototype : MonoBehaviour
{
    [Header("Balance")]
    public int playerMaxHp = 100;
    public int maxStamina = 100;
    public int enemyMaxHp = 100;
    public int attackDamage = 25;
    public int attackStaminaCost = 20;
    public int evadeStaminaRegen = 10;
    public float dodgeDuration = 7f;
    public int escapeTurns = 4;

    [Header("Feel")]
    public float playerSpeed = 5.5f;
    public float bulletBaseSpeed = 3.2f;
    public float hitInvulnTime = 1.0f;

    // ---- 상태 ----
    enum Phase { Choice, Dodge, Win, Escaped, Dead }
    Phase phase;
    int playerHp, stamina, enemyHp, turn;
    float dodgeTimer, invulnTimer;
    bool evadeFocusThisTurn;
    string message = "";

    // ---- 오브젝트 ----
    Transform player, enemy;
    readonly List<Transform> bullets = new();
    readonly List<Vector3> bulletVel = new();
    readonly List<Transform> pool = new();
    Material playerMat;
    float spawnTimer;

    // 아레나 경계 (XY 평면)
    const float AX = 4.5f, AY = 2.6f;

    void Start()
    {
        var cam = Camera.main;
        cam.orthographic = true;
        cam.orthographicSize = 5f;
        cam.transform.position = new Vector3(0, 0.5f, -10);
        cam.transform.rotation = Quaternion.identity;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.03f, 0.03f, 0.05f);

        MakeArenaFrame();

        player = MakeQuad("Player", new Color(0.95f, 0.25f, 0.35f), 0.32f);
        playerMat = player.GetComponent<MeshRenderer>().sharedMaterial;

        enemy = MakeQuad("Enemy", new Color(0.5f, 0.9f, 0.5f), 1.1f);
        enemy.position = new Vector3(0, 4.0f, 0);

        ResetBattle();
    }

    void ResetBattle()
    {
        playerHp = playerMaxHp;
        stamina = maxStamina;
        enemyHp = enemyMaxHp;
        turn = 0;
        message = "감염체와 마주쳤다!";
        ClearBullets();
        player.position = new Vector3(0, -1f, 0);
        phase = Phase.Choice;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R)) { ResetBattle(); return; }

        if (phase == Phase.Dodge)
        {
            MovePlayer();
            SpawnPattern();
            MoveBullets();
            CheckHits();

            dodgeTimer -= Time.deltaTime;
            if (invulnTimer > 0) invulnTimer -= Time.deltaTime;
            playerMat.color = (invulnTimer > 0 && Mathf.PingPong(Time.time * 8f, 1f) > 0.5f)
                ? new Color(1f, 1f, 1f, 0.4f) : new Color(0.95f, 0.25f, 0.35f);

            if (dodgeTimer <= 0f) EndDodgePhase();
        }
    }

    // ---------- 턴 흐름 ----------

    void StartDodgePhase(bool evadeFocus)
    {
        evadeFocusThisTurn = evadeFocus;
        turn++;

        if (!evadeFocus)
        {
            enemyHp = Mathf.Max(0, enemyHp - attackDamage);
            stamina = Mathf.Max(0, stamina - attackStaminaCost);
            message = $"공격! 적에게 {attackDamage} 피해";
            if (enemyHp <= 0) { phase = Phase.Win; message = "감염체를 처치했다"; return; }
        }
        else
        {
            stamina = Mathf.Min(maxStamina, stamina + evadeStaminaRegen);
            message = "숨을 고르며 움직임에 집중한다";
        }

        dodgeTimer = dodgeDuration - (evadeFocus ? 1f : 0f);
        spawnTimer = 0f;
        phase = Phase.Dodge;
    }

    void EndDodgePhase()
    {
        ClearBullets();
        if (turn >= escapeTurns)
        {
            phase = Phase.Escaped;
            message = "감염체를 따돌렸다!";
        }
        else
        {
            phase = Phase.Choice;
            message = $"{turn + 1}번째 턴 — 어떻게 할까";
        }
    }

    // ---------- 플레이어 ----------

    void MovePlayer()
    {
        Vector3 input = new Vector3(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"), 0);
        Vector3 p = player.position + input.normalized * playerSpeed * Time.deltaTime;
        p.x = Mathf.Clamp(p.x, -AX, AX);
        p.y = Mathf.Clamp(p.y, -AY, AY);
        player.position = p;
    }

    void CheckHits()
    {
        if (invulnTimer > 0) return;
        Vector3 pp = player.position;
        foreach (var b in bullets)
        {
            if ((b.position - pp).sqrMagnitude < 0.14f) // 반경 합 근사
            {
                playerHp -= 10;
                invulnTimer = hitInvulnTime;
                if (playerHp <= 0) { phase = Phase.Dead; message = "쓰러졌다..."; ClearBullets(); }
                return;
            }
        }
    }

    // ---------- 탄막 패턴 ----------

    void SpawnPattern()
    {
        float speedMul = 1f + (turn - 1) * 0.15f; // 턴마다 15% 가속
        spawnTimer -= Time.deltaTime;
        if (spawnTimer > 0f) return;

        switch ((turn - 1) % 3)
        {
            case 0: // 방사형: 적 위치에서 원형 탄막
                spawnTimer = 0.9f;
                int n = 12;
                float baseAng = Random.value * Mathf.PI * 2f;
                for (int i = 0; i < n; i++)
                {
                    float a = baseAng + i * Mathf.PI * 2f / n;
                    Fire(enemy.position,
                         new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * bulletBaseSpeed * speedMul);
                }
                break;

            case 1: // 낙하 비: 위에서 무작위 x로 떨어짐
                spawnTimer = 0.12f;
                float x = Random.Range(-AX, AX);
                Fire(new Vector3(x, AY + 1.5f, 0),
                     Vector3.down * bulletBaseSpeed * 1.4f * speedMul);
                break;

            case 2: // 교차 스윕: 좌우에서 수평으로 가로지름
                spawnTimer = 0.35f;
                bool fromLeft = Random.value < 0.5f;
                float y = Random.Range(-AY, AY);
                Fire(new Vector3(fromLeft ? -AX - 1.5f : AX + 1.5f, y, 0),
                     (fromLeft ? Vector3.right : Vector3.left) * bulletBaseSpeed * 1.6f * speedMul);
                break;
        }
    }

    void Fire(Vector3 pos, Vector3 vel)
    {
        Transform b;
        if (pool.Count > 0)
        {
            b = pool[^1];
            pool.RemoveAt(pool.Count - 1);
            b.gameObject.SetActive(true);
        }
        else b = MakeQuad("Bullet", new Color(1f, 0.85f, 0.3f), 0.22f);

        b.position = pos;
        bullets.Add(b);
        bulletVel.Add(vel);
    }

    void MoveBullets()
    {
        for (int i = bullets.Count - 1; i >= 0; i--)
        {
            bullets[i].position += bulletVel[i] * Time.deltaTime;
            Vector3 p = bullets[i].position;
            if (p.x < -AX - 3 || p.x > AX + 3 || p.y < -AY - 3 || p.y > AY + 3)
                Despawn(i);
        }
    }

    void Despawn(int i)
    {
        bullets[i].gameObject.SetActive(false);
        pool.Add(bullets[i]);
        bullets.RemoveAt(i);
        bulletVel.RemoveAt(i);
    }

    void ClearBullets()
    {
        for (int i = bullets.Count - 1; i >= 0; i--) Despawn(i);
    }

    // ---------- 생성 유틸 ----------

    Transform MakeQuad(string name, Color col, float size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.localScale = Vector3.one * size;
        var mat = new Material(Shader.Find("Sprites/Default")) { color = col };
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go.transform;
    }

    void MakeArenaFrame()
    {
        Color frame = new Color(0.85f, 0.85f, 0.9f);
        void Bar(Vector3 pos, Vector3 scale)
        {
            var t = MakeQuad("Frame", frame, 1f);
            t.position = pos; t.localScale = scale;
        }
        float w = AX * 2 + 0.6f, h = AY * 2 + 0.6f, th = 0.08f;
        Bar(new Vector3(0,  AY + 0.3f, 0), new Vector3(w, th, 1));
        Bar(new Vector3(0, -AY - 0.3f, 0), new Vector3(w, th, 1));
        Bar(new Vector3( AX + 0.3f, 0, 0), new Vector3(th, h, 1));
        Bar(new Vector3(-AX - 0.3f, 0, 0), new Vector3(th, h, 1));
    }

    // ---------- UI ----------

    void OnGUI()
    {
        GUIStyle big = new(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        big.normal.textColor = Color.white;
        GUIStyle mid = new(GUI.skin.label) { fontSize = 13 };
        mid.normal.textColor = Color.white;

        GUI.Label(new Rect(16, 12, 500, 26),
            $"HP {playerHp}/{playerMaxHp}   스태미너 {stamina}/{maxStamina}", big);
        GUI.Label(new Rect(16, 38, 500, 22),
            $"감염체 HP {enemyHp}/{enemyMaxHp}   턴 {turn}/{escapeTurns}", mid);
        GUI.Label(new Rect(16, 62, 600, 22), message, mid);

        switch (phase)
        {
            case Phase.Choice:
                bool canAttack = stamina >= attackStaminaCost;
                GUI.enabled = canAttack;
                if (GUI.Button(new Rect(Screen.width / 2 - 230, Screen.height - 90, 220, 56),
                    $"공격한다\n(피해 {attackDamage} / 스태미너 -{attackStaminaCost})"))
                    StartDodgePhase(false);
                GUI.enabled = true;
                if (GUI.Button(new Rect(Screen.width / 2 + 10, Screen.height - 90, 220, 56),
                    $"회피에 집중한다\n(스태미너 +{evadeStaminaRegen} / 회피시간 -1초)"))
                    StartDodgePhase(true);
                break;

            case Phase.Dodge:
                GUI.Label(new Rect(Screen.width / 2 - 40, 12, 200, 26),
                    $"버텨라! {dodgeTimer:F1}s", big);
                break;

            case Phase.Win:
                GUI.Label(new Rect(Screen.width / 2 - 150, Screen.height / 2 - 20, 400, 30),
                    $"승리 — 처치 완료. 남은 스태미너 {stamina} (R: 재시작)", big);
                break;

            case Phase.Escaped:
                GUI.Label(new Rect(Screen.width / 2 - 200, Screen.height / 2 - 20, 500, 30),
                    $"따돌리기 성공 — 스태미너 {stamina}를 아껴서 이탈했다 (R: 재시작)", big);
                break;

            case Phase.Dead:
                GUI.Label(new Rect(Screen.width / 2 - 100, Screen.height / 2 - 20, 300, 30),
                    "패배... (R: 재시작)", big);
                break;
        }
    }
}
