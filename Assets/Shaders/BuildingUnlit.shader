Shader "ZAK/BuildingUnlit"
{
    // v3:
    // - _ZakLitRatio(전역): 창문 점등률. 0.32=평시, 0.03=아포칼립스 (BuildingRenderer 인스펙터에서 제어)
    // - 창문 불빛 HDR 대폭 강화 → 블룸이 창문에서 번지도록
    // - 거리 보정: 멀어질수록 불빛 강도를 올려 줌아웃에서도 블룸 유지
    // - 원거리 LOD: 4km+에서는 창문 대신 '불 켜진 건물 전체'가 은은히 발광
    //   → 관망 뷰에서 빛 = 생존자 신호로 읽힘
    // - 옥상 점광은 불 켜진 건물에만 (아포칼립스 점등률이면 자연히 희소해짐)
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _ZakNight;
            float _ZakLitRatio;
            float _ZakWinRatio; // 유인 건물 내 창문 점등 비율

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color  : COLOR;   // rgb=건물색, a=건물별 해시(0~1)
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float4 color    : COLOR;
                float3 worldPos : TEXCOORD0;
                float3 worldN   : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldN = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float hash21(float2 p)
            {
                return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 baseCol = i.color.rgb;
                float bHash = i.color.a;                 // 건물별 랜덤
                float isWall = step(abs(i.worldN.y), 0.5);
                float isRoof = step(0.5, i.worldN.y);

                // 이 건물에 사람이 있는가 (점등률의 1.3배 확률로 '유인 건물' 판정)
                float buildingLit = step(bHash, saturate(_ZakLitRatio * 1.3));

                // ---- 벽 창문 격자 ----
                float u = (abs(i.worldN.x) > abs(i.worldN.z)) ? i.worldPos.z : i.worldPos.x;
                float2 wuv = float2(u / 2.6, i.worldPos.y / 3.2);
                float2 cell = floor(wuv);
                float2 f = frac(wuv);
                float win = step(0.22, f.x) * step(f.x, 0.78)
                          * step(0.30, f.y) * step(f.y, 0.85)
                          * isWall * step(0.5, wuv.y);

                // ---- 낮 ----
                float3 dayCol = baseCol * (1.0 - 0.10 * win);

                // ---- 밤: 창문 (유인 건물의 창 중 _ZakWinRatio 비율만 점등) ----
                float lit = buildingLit * step(hash21(cell + bHash * 91.7), saturate(_ZakWinRatio));
                float flicker = 0.85 + 0.15 * hash21(cell + 7.7);

                // 거리 보정: 완만하게 멀리까지 상승 (면적 축소를 강도로 보상)
                float dist = distance(_WorldSpaceCameraPos, i.worldPos);
                float boost = 1.0 + saturate((dist - 2000.0) / 6000.0) * 2.0;

                float3 winGlow = float3(1.0, 0.78, 0.42) * lit * flicker * 5.0 * boost;

                // ---- 밤: 옥상 점광 (유인 건물 한정) ----
                float2 ruv = i.worldPos.xz / 4.0;
                float2 rcell = floor(ruv);
                float2 rf = frac(ruv) - 0.5;
                float roofDot = step(length(rf), 0.16)
                              * step(hash21(rcell), 0.10)
                              * buildingLit * isRoof;
                float3 roofGlow = float3(1.0, 0.6, 0.3) * roofDot * 4.0 * boost;

                // 근거리 밤: 어둠 속 창문
                float3 nightNear = baseCol * 0.05 + winGlow * win + roofGlow;

                // ---- 원거리 LOD: 표면 발광 대신 '점광' ----
                // LOD 기준을 픽셀 거리가 아닌 '카메라 높이'로: 팬 중에는 격자가 고정되고
                // 줌할 때만 셀 크기가 바뀐다 (점이 화면 따라 미끄러지는 현상 제거)
                float camH = _WorldSpaceCameraPos.y;
                float farT = saturate((camH - 3000.0) / 4000.0);
                float cellSz = lerp(6.0, 70.0, farT);
                float2 fuv = i.worldPos.xz / cellSz;
                float2 fc = floor(fuv);
                float2 ff = frac(fuv) - 0.5;
                float fHas = step(hash21(fc + bHash * 31.7), 0.22 * saturate(_ZakWinRatio + 0.3))
                           * buildingLit;
                // 점 크기: 멀수록 살짝 키워서 블룸이 잡을 픽셀 확보
                float fDot = step(length(ff), lerp(0.16, 0.26, farT)) * fHas;
                // 블룸 강화: 원거리에서 최대 10 HDR
                float3 farDotGlow = float3(1.0, 0.75, 0.42) * fDot * (3.5 + farT * 6.5);

                // 벽 가중: 지붕 한가운데보다 건물 외곽(벽면)의 점이 더 밝게
                float3 nightFar = baseCol * 0.04 + farDotGlow * (1.0 - 0.4 * isRoof);

                float3 nightCol = lerp(nightNear, nightFar, farT);

                float3 col = lerp(dayCol, nightCol, saturate(_ZakNight));
                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }
}
