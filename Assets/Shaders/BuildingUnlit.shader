Shader "ZAK/BuildingUnlit"
{
    // v6: 실시간 튜닝 가능 버전. 모든 야경 파라미터를 전역 변수로 노출한다.
    // NightLightTuner 컴포넌트에서 플레이 중 슬라이더로 조절.
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
            float _ZakLitRatio;     // 불 켜진 건물 비율
            float _ZakWinRatio;     // 건물 내 점등 창문 비율
            float _ZakGlowNear;     // 근거리 창문 밝기 배율
            float _ZakGlowFar;      // 원거리 점광 밝기 배율
            float _ZakDotSize;      // 원거리 점 크기 배율
            float _ZakColorSat;     // 색 채도 배율
            float _ZakIntensityVar; // 밝기 랜덤 폭 (0=균일, 1=최대)
            float _ZakAttNear;      // 근접 뷰 감쇠 (0.1~1.5)
            float _ZakAttFar;       // 원거리 뷰 감쇠 (0.1~1.5)
            sampler2D _ZakDensityTex;   // 상가 밀도맵 (번화가)
            float4 _ZakDensityRect;     // xy=원점(m), zw=크기(m)
            float _ZakDensityInfluence; // 번화가 집중도 (0=균일, 1=상권에 몰림)

            struct appdata { float4 vertex:POSITION; float3 normal:NORMAL; float4 color:COLOR; };
            struct v2f { float4 pos:SV_POSITION; float4 color:COLOR; float3 worldPos:TEXCOORD0; float3 worldN:TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldN = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float hash21(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            // 조명 색 팔레트. w = 강도 배율
            float4 WindowColor(float h)
            {
                if (h < 0.62) return float4(1.00, 0.78, 0.45, 1.00); // 전구색
                if (h < 0.78) return float4(0.70, 0.83, 1.00, 1.10); // 주백색
                if (h < 0.89) return float4(1.00, 0.48, 0.10, 1.00); // 주황
                if (h < 0.945) return float4(1.00, 0.14, 0.10, 0.95); // 적색
                if (h < 0.98) return float4(0.20, 1.00, 0.32, 0.95); // 녹색
                return float4(0.15, 0.60, 1.00, 1.00);               // 청색
            }

            // 채도 조절
            float3 Saturate3(float3 c, float s)
            {
                float g = dot(c, float3(0.299, 0.587, 0.114));
                return lerp(float3(g,g,g), c, s);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 baseCol = i.color.rgb;
                float bHash = i.color.a;
                float isWall = step(abs(i.worldN.y), 0.5);
                float isRoof = step(0.5, i.worldN.y);
                // 번화가 밀도로 점등 확률 조절 — 상권에 빛이 몰리게
                float2 duv = (i.worldPos.xz - _ZakDensityRect.xy) / max(_ZakDensityRect.zw, 1.0);
                float dens = tex2D(_ZakDensityTex, duv).r;
                if (duv.x < 0 || duv.x > 1 || duv.y < 0 || duv.y > 1) dens = 0;
                float litProb = _ZakLitRatio * lerp(1.0, dens * 3.0, saturate(_ZakDensityInfluence));
                float buildingLit = step(bHash, saturate(litProb * 1.3));

                float camH = _WorldSpaceCameraPos.y;

                // 뷰폭 기반 감쇠 (근접 ↔ 원거리 각각 조절 가능)
                float att = lerp(_ZakAttNear, _ZakAttFar, saturate((camH - 7600.0) / 11400.0));
                att = lerp(att, 1.0, saturate((camH - 19000.0) / 8000.0));

                // ---- 벽 창문 ----
                float u = (abs(i.worldN.x) > abs(i.worldN.z)) ? i.worldPos.z : i.worldPos.x;
                float2 wuv = float2(u / 2.6, i.worldPos.y / 3.2);
                float2 cell = floor(wuv);
                float2 f = frac(wuv);
                float win = step(0.22, f.x) * step(f.x, 0.78) * step(0.30, f.y) * step(f.y, 0.85)
                          * isWall * step(0.5, wuv.y);

                // ---- 낮 디테일 (전부 프로시저럴 — 텍스처/폴리곤 추가 비용 0) ----
                // 층 띠: 슬래브 라인이 보이면 건물이 '층을 가진 구조물'로 읽힌다
                float band = 1.0 - 0.14 * step(frac(i.worldPos.y / 3.2), 0.09) * isWall;
                // 접지 AO: 벽 아랫부분을 어둡게 해 건물이 땅에 붙어 보이게
                float ao = lerp(0.68, 1.0, saturate(i.worldPos.y / 10.0));
                ao = lerp(1.0, ao, isWall);
                // 옥상 질감: 6m 격자로 밝기를 흩어 설비·방수층 느낌
                float rn = hash21(floor(i.worldPos.xz / 6.0) + 0.5);
                float roofTone = lerp(1.0, lerp(0.88, 1.12, rn), isRoof);
                // 옥상 설비: 드물게 어두운 사각 덩어리
                float2 eq = frac(i.worldPos.xz / 12.0) - 0.5;
                float equip = step(hash21(floor(i.worldPos.xz / 12.0) + 7.7), 0.12)
                            * step(max(abs(eq.x), abs(eq.y)), 0.22) * isRoof;

                float3 dayCol = baseCol * band * ao * roofTone * (1.0 - 0.10 * win);
                dayCol *= (1.0 - 0.30 * equip);

                float lit = buildingLit * step(hash21(cell + bHash * 91.7), saturate(_ZakWinRatio));
                float flicker = 0.85 + 0.15 * hash21(cell + 7.7);
                // 카메라까지의 '거리'가 아니라 '높이' 기준 — 화면을 움직여도 밝기가 변하지 않는다
                // (거리 기준이면 같은 건물도 화면 가장자리로 갈수록 멀어져 밝기가 출렁인다)
                float boost = 1.0 + saturate((camH - 1500.0) / 5000.0) * 2.0;

                float4 lc = WindowColor(hash21(float2(bHash * 37.1, 5.3)));
                float3 lcol = Saturate3(lc.rgb, _ZakColorSat);
                float3 winGlow = lcol * lit * flicker * 5.0 * lc.w * boost * att * _ZakGlowNear;

                float2 ruv = i.worldPos.xz / 4.0;
                float2 rf = frac(ruv) - 0.5;
                float roofDot = step(length(rf), 0.16) * step(hash21(floor(ruv)), 0.10) * buildingLit * isRoof;
                float3 roofGlow = lcol * roofDot * 4.0 * lc.w * boost * att * _ZakGlowNear;

                float3 nightNear = baseCol * 0.006 + winGlow * win + roofGlow;

                // ---- 원거리 점광 ----
                float farT = saturate((camH - 3000.0) / 4000.0);
                float cellSz = lerp(6.0, 70.0, farT);
                float2 fuv = i.worldPos.xz / cellSz;
                float2 fc = floor(fuv);

                // 셀 안에서 위치를 흩뜨려 바둑판 패턴 제거
                float2 jit = float2(hash21(fc + 1.73), hash21(fc + 9.11)) - 0.5;
                float2 ff = frac(fuv) - 0.5 - jit * 0.62;

                float fHas = step(hash21(fc + bHash * 31.7), 0.22 * saturate(_ZakWinRatio + 0.3)) * buildingLit;
                float rad = lerp(0.16, 0.34, farT) * _ZakDotSize;
                float fDot = step(length(ff), rad) * fHas;

                // 밝기 랜덤 폭 (균일한 점 밭 방지)
                float ivar = lerp(1.0, 0.20 + 2.2 * hash21(fc + 3.31), saturate(_ZakIntensityVar));

                // 원거리는 셀 단위로 색을 골라 다채롭게
                float4 fc4 = WindowColor(hash21(fc * 0.37 + bHash * 3.1));
                float3 fcol = Saturate3(fc4.rgb, _ZakColorSat);
                float3 farDotGlow = fcol * fDot * (3.5 + farT * 6.5) * fc4.w * ivar * att * _ZakGlowFar;

                float3 nightFar = baseCol * 0.006 + farDotGlow * (1.0 - 0.4 * isRoof);
                float3 nightCol = lerp(nightNear, nightFar, farT);

                return fixed4(lerp(dayCol, nightCol, saturate(_ZakNight)), 1.0);
            }
            ENDCG
        }
    }
}
