Shader "CodingRat/VAT_Lit_Smooth"
{
    Properties
    {
        _BaseMap ("Base Texture (Albedo)", 2D) = "white" {}
        _BumpMap ("Normal Map (노말맵)", 2D) = "bump" {} // 노말맵 슬롯 추가!
        _VATex ("VAT Texture (Position Data)", 2D) = "black" {}
        _AnimLength ("Animation Length (Seconds)", Float) = 1.0
        _AnimFrames ("Total Frames (eg. 30)", Float) = 30.0
        
        [Space(10)]
        _Smoothness ("Smoothness (광택)", Range(0.0, 1.0)) = 0.1

        [HideInInspector] _TimeOffset ("Time Offset", Float) = 0.0
        [HideInInspector] _IsWalking ("Is Walking", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing 
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;    // 원본 메쉬의 '부드러운 곡면' 정보 가져오기
                float4 tangentOS  : TANGENT;   // 노말맵 연산을 위한 탄젠트 정보 가져오기
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1; 
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD1; 
                float3 normalWS   : TEXCOORD2; // 월드 공간 노말
                float4 tangentWS  : TEXCOORD3; // 월드 공간 탄젠트
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap); // 노말맵 샘플러
            TEXTURE2D(_VATex);   SAMPLER(sampler_VATex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _AnimLength;
                float _AnimFrames;
                float _Smoothness;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float, _TimeOffset)
                UNITY_DEFINE_INSTANCED_PROP(float, _IsWalking)
            UNITY_INSTANCING_BUFFER_END(Props)

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float timeOffset = UNITY_ACCESS_INSTANCED_PROP(Props, _TimeOffset);
                float isWalking = UNITY_ACCESS_INSTANCED_PROP(Props, _IsWalking);
                
                float time = (_Time.y + timeOffset) * isWalking; 
                float normalizedTime = frac(time / _AnimLength); 
                float frameY = (normalizedTime * _AnimFrames + 0.5) / _AnimFrames; 
                float vertexX = input.uv2.x;

                float4 vatPos = SAMPLE_TEXTURE2D_LOD(_VATex, sampler_VATex, float2(vertexX, frameY), 0);
                float3 finalPosOS = vatPos.xyz;

                output.positionCS = TransformObjectToHClip(finalPosOS);
                output.positionWS = TransformObjectToWorld(finalPosOS); 
                
                // 부드러운 노말과 탄젠트를 월드 공간으로 변환해서 조명 연산으로 넘깁니다.
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.tangentWS = float4(TransformObjectToWorldDir(input.tangentOS.xyz), input.tangentOS.w);

                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                
                // 더 이상 ddx/ddy (플랫 쉐이딩)를 쓰지 않습니다!
                // 노말맵(근육 디테일) 텍스처 읽기
                half4 normalSample = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv);
                float3 normalTS = UnpackNormal(normalSample);

                // 탄젠트 공간 -> 월드 공간 노말로 합성 (빛이 굴곡에 맞게 맺히도록 계산)
                float3 sDir = input.tangentWS.xyz;
                float3 tDir = cross(input.normalWS, sDir) * input.tangentWS.w;
                float3x3 tangentToWorld = float3x3(sDir, tDir, input.normalWS);
                float3 finalNormalWS = normalize(mul(normalTS, tangentToWorld));

                // 최종 빛 연산
                Light mainLight = GetMainLight();
                half NdotL = saturate(dot(finalNormalWS, mainLight.direction)); 
                
                float3 viewDir = normalize(GetCameraPositionWS() - input.positionWS);
                float3 halfVector = normalize(mainLight.direction + viewDir);
                float NdotH = saturate(dot(finalNormalWS, halfVector));
                float specularModifier = pow(NdotH, exp2(10 * _Smoothness + 1));
                half3 specular = mainLight.color * specularModifier * _Smoothness;

                // 디테일한 굴곡(finalNormalWS)을 적용한 최종 컬러 반환
                half3 finalColor = albedo.rgb * (mainLight.color * NdotL + SampleSH(finalNormalWS)) + specular;

                return half4(finalColor, albedo.a);
            }
            ENDHLSL
        }
    }
}