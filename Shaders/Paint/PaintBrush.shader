// 월드 공간 브러시 셰이더 (PaintSurfaceManager가 CommandBuffer.DrawMesh로 사용).
// 표면 메시를 UV 좌표 위치에 펼쳐서 RenderTexture에 그리므로, 각 픽셀이 메시 위 어느 월드 지점인지 알 수 있다.
//  - Pass 0 (브러시): 브러시 중심과의 '월드 거리'로 칠하기/지우기
//    → 메시 크기/스케일/UV 배치/타일링과 무관하게 항상 같은 실제 크기(미터)의 자국
//    → 칠하기(_Splatter=1)는 울퉁불퉁한 가장자리 + 주변에 튄 방울, 지우기는 둥근 원
//    → 모양은 브러시 위치로만 정해지므로 모든 클라이언트에서 같은 자국이 찍힌다
//  - Pass 1 (면적 측정): 픽셀 하나가 덮는 월드 면적(m²)을 기록 → 칠해진 픽셀 수를 실제 면적으로 환산할 때 사용
//  - Pass 2 (묶음 칠하기): 한 표면에 자국 여러 개(최대 MAX_DABS)를 한 번에 찍는다 → 시작 페인트처럼 많이 찍을 때 메시를 자국마다 다시 그리지 않음
// CommandBuffer 직접 실행이라 URP 파이프라인을 거치지 않으므로 UnityCG.cginc(CGPROGRAM) 기반으로 동작함.
Shader "Hidden/PaintBrush"
{
    Properties
    {
        _BrushPos ("Brush World Position", Vector) = (0, 0, 0, 0)
        _BrushRadius ("Brush Radius (m)", Float) = 0.2
        _BrushValue ("Brush Value (1=칠하기, 0=지우기)", Float) = 1
        _Splatter ("Splatter (1=페인트 자국, 0=둥근 원)", Float) = 0
        _EdgeRoughness ("Edge Roughness", Range(0, 1)) = 0.5
        _DropletAmount ("Droplet Amount", Range(0, 1)) = 0.5
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    // 방울이 퍼지는 최대 거리 (브러시 반경 배수). 이 밖은 계산하지 않는다. PaintSpawnManager의 SPLATTER_REACH와 맞출 것
    #define SPLATTER_REACH 1.8
    // 묶음 칠하기 한 번에 찍는 자국 수. PaintSurfaceManager의 MAX_DABS_PER_DRAW와 맞출 것
    #define MAX_DABS 16
    // 자국을 찍은 면과 반대쪽을 보는 면(바닥 밑면, 얇은 벽 뒷면)은 칠하지 않는다. 모서리(직각)는 양쪽 다 칠해진다
    #define BACKFACE_DOT -0.25

    struct appdata
    {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float2 uv     : TEXCOORD0;
    };

    struct v2f
    {
        float4 pos         : SV_POSITION;
        float3 worldPos    : TEXCOORD0;
        float3 worldNormal : TEXCOORD1;
    };

    v2f vert(appdata v)
    {
        v2f o;
        // UV(0~1)를 클립 공간(-1~1)으로 펼친다.
        // 뷰/투영은 C#에서 단위 행렬로 설정하므로, UNITY_MATRIX_P에는 Unity가 넣어주는
        // 플랫폼별 보정(렌더 텍스처 상하 반전 등)만 남는다 → 직접 #if로 뒤집지 않아도 됨
        o.pos = mul(UNITY_MATRIX_P, float4(v.uv * 2.0 - 1.0, 0.0, 1.0));
        o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
        o.worldNormal = UnityObjectToWorldNormal(v.normal);
        return o;
    }

    // sin을 쓰지 않는 해시. GPU마다 결과가 달라지지 않게 한다
    float hash13(float3 p3)
    {
        p3 = frac(p3 * 0.1031);
        p3 += dot(p3, p3.zyx + 31.32);
        return frac((p3.x + p3.y) * p3.z);
    }

    // 0~1 3D 값 노이즈
    float valueNoise(float3 p)
    {
        float3 i = floor(p);
        float3 f = frac(p);
        float3 u = f * f * (3.0 - 2.0 * f);
        return lerp(lerp(lerp(hash13(i), hash13(i + float3(1, 0, 0)), u.x),
                         lerp(hash13(i + float3(0, 1, 0)), hash13(i + float3(1, 1, 0)), u.x), u.y),
                    lerp(lerp(hash13(i + float3(0, 0, 1)), hash13(i + float3(1, 0, 1)), u.x),
                         lerp(hash13(i + float3(0, 1, 1)), hash13(i + float3(1, 1, 1)), u.x), u.y), u.z);
    }

    // 페인트가 튄 자국 모양 (0~1). 모양은 브러시 위치·반경으로만 정해지므로 모든 클라이언트에서 같다
    float SplatterMask(float3 worldPos, float dist, float3 brushPos, float radius, float edgeRoughness, float dropletAmount)
    {
        float3 dir = (worldPos - brushPos) / max(dist, 1e-5);
        // 자국마다 모양이 달라지되, 조준이 미세하게 흔들려도 같은 모양이 유지되도록 브러시 크기 단위로 시드를 끊는다
        float seed = hash13(floor(brushPos / radius)) * 97.0;

        // 중심 덩어리: 방향에 따라 반경을 흔들어 큰 굴곡을, 월드 노이즈로 자잘한 거친 가장자리를 만든다
        float lobes = valueNoise(dir * 2.5 + seed) * 0.7 + valueNoise(dir * 7.0 + seed) * 0.3;
        float grain = valueNoise(worldPos / (radius * 0.12));
        float edge = radius * (1.0 + ((lobes - 0.5) + (grain - 0.5) * 0.4) * edgeRoughness);
        float blob = smoothstep(edge, edge * 0.85, dist);

        // 주변 방울: 월드 노이즈의 봉우리만 점으로 남기고, 중심에서 멀어질수록 사라지게 한다
        float drops = valueNoise(worldPos / (radius * 0.3) + seed);
        float threshold = lerp(1.0, 0.72, dropletAmount);
        float ring = 1.0 - smoothstep(radius * 1.1, radius * SPLATTER_REACH, dist);
        float dropMask = smoothstep(threshold, threshold + 0.04, drops) * ring;

        return max(blob, dropMask);
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        // UV 공간에서는 삼각형 방향이 제각각이므로 컬링 끔
        Cull Off ZWrite Off ZTest Always

        // Pass 0: 브러시
        Pass
        {
            // 결과 = 기존값 * (1 - mask) + 브러시값 * mask  (기존 lerp(existing, value, mask)와 동일)
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 _BrushPos;
            float _BrushRadius;
            float _BrushValue;
            float _Splatter;
            float _EdgeRoughness;
            float _DropletAmount;

            fixed4 frag(v2f i) : SV_Target
            {
                float dist = distance(i.worldPos, _BrushPos.xyz);
                float mask;
                if (_Splatter > 0.5)
                {
                    // 칠하기: 페인트가 튄 자국 모양
                    if (dist > _BrushRadius * SPLATTER_REACH) discard;
                    mask = SplatterMask(i.worldPos, dist, _BrushPos.xyz, _BrushRadius, _EdgeRoughness, _DropletAmount);
                }
                else
                {
                    // 지우기: dist < radius*0.7은 완전 적용, radius*0.7 ~ radius는 페더링, 그 밖은 기존값 유지
                    mask = smoothstep(_BrushRadius, _BrushRadius * 0.7, dist);
                }

                float value = _BrushValue * mask;
                return fixed4(value, value, value, mask);
            }
            ENDCG
        }

        // Pass 1: 픽셀당 월드 면적 측정
        Pass
        {
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragArea

            float4 fragArea(v2f i) : SV_Target
            {
                // 옆/위 픽셀로 한 칸 갈 때 월드 위치가 얼마나 변하는지 → 두 변으로 만든 평행사변형 넓이 = 픽셀 하나의 월드 면적
                float3 dx = ddx(i.worldPos);
                float3 dy = ddy(i.worldPos);
                float area = length(cross(dx, dy));
                return float4(area, 0, 0, 0);
            }
            ENDCG
        }

        // Pass 2: 묶음 칠하기 (자국 여러 개를 한 번에)
        Pass
        {
            // Pass 0과 같은 블렌딩. 겹친 자국은 가장 진한 값(max)으로 합친다
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBatch

            float4 _Dabs[MAX_DABS];       // xyz = 월드 중심, w = 반경(m)
            float4 _DabNormals[MAX_DABS]; // xyz = 찍은 면의 노멀. 0이면 방향을 가리지 않는다
            float _DabCount;
            float _EdgeRoughness;
            float _DropletAmount;

            fixed4 fragBatch(v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float mask = 0.0;
                int count = (int)_DabCount;
                for (int d = 0; d < MAX_DABS; d++)
                {
                    if (d >= count) break;
                    float3 brushPos = _Dabs[d].xyz;
                    float radius = _Dabs[d].w;
                    float dist = distance(i.worldPos, brushPos);
                    if (dist > radius * SPLATTER_REACH) continue;
                    if (dot(n, _DabNormals[d].xyz) < BACKFACE_DOT) continue;
                    mask = max(mask, SplatterMask(i.worldPos, dist, brushPos, radius, _EdgeRoughness, _DropletAmount));
                }
                if (mask <= 0.0) discard;
                return fixed4(mask, mask, mask, mask);
            }
            ENDCG
        }
    }
}
