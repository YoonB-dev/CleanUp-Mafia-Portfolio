Shader "Custom/PaintOverlayURP"
{
    Properties
    {
        _PaintMask ("Paint Mask (R = 오염도)", 2D) = "black" {}
        _PaintColor ("Paint Color", Color) = (0.55, 0.06, 0.05, 1)
        _Smoothness ("Smoothness (페인트 광택)", Range(0, 1)) = 0.35
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }

        Pass
        {
            Name "PaintOverlay"
            Tags { "LightMode"="UniversalForward" }

            ZWrite Off
            ZTest LEqual
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // 주변 표면과 같은 조명(메인 라이트/그림자/추가 라이트/환경광)을 받도록 URP 라이팅 키워드를 켠다
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // 텍스처 및 샘플러 선언
            TEXTURE2D(_PaintMask);
            SAMPLER(sampler_PaintMask);

            // URP SRP Batcher 호환을 위해 텍스처 변환 변수와 Material 프로퍼티를 CBUFFER에 함께 넣어줍니다.
            CBUFFER_START(UnityPerMaterial)
                float4 _PaintMask_ST; // UV Scale/Offset 사용 시 필요
                half4 _PaintColor;
                half _Smoothness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                // TRANSFORM_TEX를 써두면 Tiling/Offset 기능도 정상 작동합니다.
                OUT.uv = TRANSFORM_TEX(IN.uv, _PaintMask);
                return OUT;
            }

            half4 frag(Varyings IN, bool frontFace : SV_IsFrontFace) : SV_Target
            {
                if (!frontFace)
                {
                    return half4(0, 0, 0, 0);
                }

                half mask = SAMPLE_TEXTURE2D(_PaintMask, sampler_PaintMask, IN.uv).r;
                if (mask <= 0.001h)
                {
                    return half4(0, 0, 0, 0);
                }

                float3 normalWS = normalize(IN.normalWS);

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionHCS);
                inputData.bakedGI = SampleSH(normalWS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = _PaintColor.rgb;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion = 1;
                surfaceData.alpha = mask * _PaintColor.a;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.a = surfaceData.alpha;
                return color;
            }
            ENDHLSL
        }
    }
}
