Shader "Custom/PlacementGridHighlight"
{
    Properties
    {
        _CenterPos  ("Center Position (World XZ)", Vector) = (0, 0, 0, 0)
        _GridOrigin ("Grid Origin (BottomLeft XZ)", Vector) = (0, 0, 0, 0)
        _Radius     ("Radius (World Units)",        Float)  = 8.0
        _CellSize   ("Cell Size (World Units)",     Float)  = 1.0
        _LineWidth  ("Line Width (Cell Fraction)",  Float)  = 0.04
        _FadeStart  ("Fade Start (0=center 1=edge)",Float)  = 0.55
        _MaxAlpha   ("Max Alpha",                   Float)  = 0.75
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent+1"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        ZWrite Off
        ZTest  LEqual
        Blend  SrcAlpha OneMinusSrcAlpha
        Cull   Off

        Pass
        {
            Name "PlacementGridHighlight"

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _CenterPos;
                float4 _GridOrigin;
                float  _Radius;
                float  _CellSize;
                float  _LineWidth;
                float  _FadeStart;
                float  _MaxAlpha;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                VertexPositionInputs pi = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = pi.positionCS;
                OUT.positionWS = pi.positionWS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 worldXZ  = IN.positionWS.xz;
                float2 centerXZ = _CenterPos.xz;

                // ── 원형 마스크 & 가장자리 페이드 ──────────────────────────
                float dist = length(worldXZ - centerXZ) / max(_Radius, 0.001);
                if (dist > 1.0) discard;

                float fade = 1.0 - smoothstep(_FadeStart, 1.0, dist);

                // ── 그리드 라인: GridOrigin(BottomLeft) 기준 정렬 ─────────
                float2 gridCoord = (worldXZ - _GridOrigin.xz) / max(_CellSize, 0.001);
                float2 g         = frac(gridCoord);

                float lw    = saturate(_LineWidth);
                float lineX = step(1.0 - lw, g.x) + step(g.x, lw);
                float lineZ = step(1.0 - lw, g.y) + step(g.y, lw);
                float alpha = saturate(lineX + lineZ) * fade * _MaxAlpha;

                return half4(1.0, 1.0, 1.0, alpha);
            }
            ENDHLSL
        }
    }
}
