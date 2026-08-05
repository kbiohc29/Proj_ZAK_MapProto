using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// M7: 가로등. 도로 노드의 일부에 따뜻한 빛 점을 깔고, 밤(_ZakNight)에만 떠오르게 한다.
/// 탑뷰 야경의 주인공은 사실 창문보다 가로등 — 도로망이 빛의 강줄기로 보이는 효과.
/// 세팅: 빈 GameObject에 부착 (RoadNetwork 자동 참조)
/// </summary>
public class StreetLightRenderer : MonoBehaviour
{
    public RoadNetwork roads;

    [Header("Look")]
    [Range(0f, 1f)] public float density = 0.22f; // 도로 노드 중 가로등 비율
    public float dotSize = 5f;                    // 빛 점 크기(m)
    public Color lightColor = new Color(1f, 0.75f, 0.4f);
    public float hdrIntensity = 2.0f;             // 블룸용 HDR 배율
    public float chunkSize = 2000f;

    bool built;
    Material mat;

    void Start()
    {
        if (roads == null) roads = FindFirstObjectByType<RoadNetwork>();
    }

    void Update()
    {
        if (!built)
        {
            if (roads == null || !roads.Ready) return;
            BuildLights();
            built = true;
        }

        // 밤 정도에 맞춰 페이드 (모든 청크가 mat 하나를 공유)
        float night = Shader.GetGlobalFloat("_ZakNight");
        Color c = lightColor * hdrIntensity * night;
        c.a = night;
        mat.color = c;
    }

    void BuildLights()
    {
        mat = new Material(Shader.Find("Sprites/Default"));
        mat.color = Color.clear;

        var chunks = new Dictionary<Vector2Int, (List<Vector3> v, List<int> t)>();
        int count = 0;

        foreach (var kv in roads.NodePos)
        {
            // 노드 ID 해시로 결정론적 선택 (매 실행 동일한 가로등 배치)
            float h = Mathf.Abs(Mathf.Sin(kv.Key * 0.0001f)) % 1f;
            if (h > density) continue;

            Vector3 p = kv.Value;
            var key = new Vector2Int(
                Mathf.FloorToInt(p.x / chunkSize), Mathf.FloorToInt(p.z / chunkSize));
            if (!chunks.TryGetValue(key, out var buf))
            {
                buf = (new List<Vector3>(4096), new List<int>(8192));
                chunks[key] = buf;
            }

            float half = dotSize * 0.5f;
            int s = buf.v.Count;
            // 도로(y=-0.5)보다 살짝 위, 상가 점(y=0)보다 살짝 아래
            buf.v.Add(p + new Vector3(-half, -0.2f, -half));
            buf.v.Add(p + new Vector3(-half, -0.2f,  half));
            buf.v.Add(p + new Vector3( half, -0.2f,  half));
            buf.v.Add(p + new Vector3( half, -0.2f, -half));
            buf.t.Add(s); buf.t.Add(s + 1); buf.t.Add(s + 2);
            buf.t.Add(s); buf.t.Add(s + 2); buf.t.Add(s + 3);
            count++;
        }

        foreach (var kv in chunks)
        {
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(kv.Value.v);
            mesh.SetTriangles(kv.Value.t, 0);
            mesh.RecalculateBounds();

            var go = new GameObject($"Lights_{kv.Key.x}_{kv.Key.y}");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        Debug.Log($"가로등 {count:N0}개 배치");
    }
}
