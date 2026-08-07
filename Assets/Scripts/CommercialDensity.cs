using UnityEngine;

/// <summary>
/// 상가업소 밀도로 '번화가 지도'를 만들어 셰이더에 넘긴다.
/// 밤 조명이 도시 전체에 균일하게 흩어지지 않고 상권에 몰리게 하는 근거 데이터.
///
/// - ShopMapRenderer의 건물별 업소 수를 격자에 누적 → 블러 → 0~1 정규화
/// - 셰이더는 이 텍스처를 샘플해 점등 확률을 조절
///
/// 세팅: 빈 GameObject에 부착 (ShopMapRenderer 자동 참조)
/// </summary>
public class CommercialDensity : MonoBehaviour
{
    [Header("Map")]
    public int resolution = 512;
    [Tooltip("블러 반복 횟수 — 클수록 상권 경계가 부드러워진다")]
    public int blurPasses = 3;
    [Tooltip("이 업소 수를 밀도 1.0으로 본다 (낮출수록 넓은 지역이 번화가로 취급)")]
    public float saturationCount = 40f;

    public bool Ready { get; private set; }

    ShopMapRenderer shopData;
    Texture2D densityTex;

    void Start() => shopData = FindFirstObjectByType<ShopMapRenderer>();

    void Update()
    {
        if (Ready || shopData == null || shopData.Buildings.Count == 0) return;
        Build();
    }

    void Build()
    {
        // 범위 계산
        Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
        foreach (var b in shopData.Buildings.Values)
        {
            min.x = Mathf.Min(min.x, b.pos.x); min.y = Mathf.Min(min.y, b.pos.z);
            max.x = Mathf.Max(max.x, b.pos.x); max.y = Mathf.Max(max.y, b.pos.z);
        }
        Vector2 size = max - min;
        if (size.x < 1f || size.y < 1f) { Ready = true; return; }

        var grid = new float[resolution * resolution];

        // 업소 수를 격자에 누적
        foreach (var b in shopData.Buildings.Values)
        {
            int x = Mathf.Clamp((int)((b.pos.x - min.x) / size.x * (resolution - 1)), 0, resolution - 1);
            int y = Mathf.Clamp((int)((b.pos.z - min.y) / size.y * (resolution - 1)), 0, resolution - 1);
            grid[y * resolution + x] += b.shops.Count;
        }

        for (int i = 0; i < blurPasses; i++) Blur(grid);

        // 텍스처 생성
        densityTex = new Texture2D(resolution, resolution, TextureFormat.RFloat, false)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };

        var pixels = new Color[resolution * resolution];
        for (int i = 0; i < grid.Length; i++)
        {
            float v = Mathf.Clamp01(grid[i] / Mathf.Max(0.01f, saturationCount));
            pixels[i] = new Color(v, v, v, 1f);
        }
        densityTex.SetPixels(pixels);
        densityTex.Apply();

        Shader.SetGlobalTexture("_ZakDensityTex", densityTex);
        Shader.SetGlobalVector("_ZakDensityRect", new Vector4(min.x, min.y, size.x, size.y));

        Ready = true;
        Debug.Log($"번화가 밀도맵 {resolution}x{resolution} 생성 " +
                  $"(범위 {size.x / 1000f:F1}x{size.y / 1000f:F1}km)");
    }

    void Blur(float[] g)
    {
        var src = (float[])g.Clone();
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            float sum = 0f; int n = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= resolution || ny >= resolution) continue;
                sum += src[ny * resolution + nx]; n++;
            }
            g[y * resolution + x] = sum / n;
        }
    }
}
