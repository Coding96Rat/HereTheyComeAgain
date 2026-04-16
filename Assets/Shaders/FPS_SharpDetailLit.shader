// =============================================================================
// FPS_SharpDetailLit.shader
// High-performance URP Lit shader for 3D FPS environments
//
// Features:
//   - Advanced normal mapping with Reoriented Normal Mapping (RNM) blend
//   - Multi-layered detail textures (independent Tiling/Offset)
//   - Per-pixel derivative-based sharpening (zero extra texture samples)
//   - Unity 6 URP Forward+ compatible (#pragma multi_compile _ _FORWARD_PLUS)
//   - GPU Instancing (#pragma multi_compile_instancing)
//   - SRP Batcher compatible (full UnityPerMaterial CBUFFER in every pass)
//   - Passes: ForwardLit, ShadowCaster, DepthOnly, DepthNormals
// =============================================================================
Shader "Custom/FPS_SharpDetailLit"
{
    Properties
    {
        // ── Base Layer ──────────────────────────────────────────────────────
        [MainTexture] _BaseMap        ("Albedo",           2D)           = "white" {}
        [MainColor]   _BaseColor      ("Base Color",       Color)        = (1,1,1,1)
        _NormalMap                    ("Normal Map",       2D)           = "bump"  {}
        _NormalStrength               ("Normal Strength",  Range(0,3))   = 1.0

        // ── Detail Layer (independent tiling / offset) ───────────────────
        [Header(Detail Layer)]
        _DetailAlbedo                 ("Detail Albedo",           2D)           = "grey"  {}
        _DetailNormal                 ("Detail Normal Map",        2D)           = "bump"  {}
        _DetailTiling                 ("Detail Tiling",            Vector)       = (4,4,0,0)
        _DetailOffset                 ("Detail Offset",            Vector)       = (0,0,0,0)
        _DetailBlendStrength          ("Albedo Blend Strength",    Range(0,1))   = 0.5
        _DetailNormalStrength         ("Normal Blend Strength",    Range(0,2))   = 1.0

        // ── PBR ─────────────────────────────────────────────────────────
        [Header(PBR)]
        _MetallicGlossMap             ("Metallic(R) Smooth(A)",   2D)           = "white" {}
        _Metallic                     ("Metallic",                 Range(0,1))   = 0.0
        _Smoothness                   ("Smoothness",               Range(0,1))   = 0.5
        _OcclusionMap                 ("Occlusion",                2D)           = "white" {}
        _OcclusionStrength            ("Occlusion Strength",       Range(0,1))   = 1.0

        // ── Emission ────────────────────────────────────────────────────
        [Header(Emission)]
        _EmissionMap                  ("Emission",                 2D)           = "black" {}
        [HDR] _EmissionColor          ("Emission Color",           Color)        = (0,0,0,1)

        // ── Per-Pixel Sharpening ────────────────────────────────────────
        [Header(Sharpening)]
        _SharpenStrength              ("Sharpen Strength",         Range(0,1))   = 0.3
        _SharpenThreshold             ("Sharpen Noise Gate",       Range(0,0.1)) = 0.02

        // ── Internal (set by ShaderGUI) ─────────────────────────────────
        [HideInInspector] _Cutoff     ("Alpha Cutoff",             Range(0,1))   = 0.5
        [HideInInspector] _Cull       ("Cull",                     Float)        = 2.0
        [HideInInspector] _SrcBlend   ("Src Blend",                Float)        = 1.0
        [HideInInspector] _DstBlend   ("Dst Blend",                Float)        = 0.0
        [HideInInspector] _ZWrite     ("ZWrite",                   Float)        = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"             = "Opaque"
            "RenderPipeline"         = "UniversalPipeline"
            "Queue"                  = "Geometry"
            "UniversalMaterialType"  = "Lit"
            "IgnoreProjector"        = "True"
        }
        LOD 300

        // ================================================================
        // PASS 1 — Forward Lit (ForwardLit + Forward+)
        // ================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend  [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull   [_Cull]

            HLSLPROGRAM
            #pragma target 4.5

            #pragma vertex   ForwardLitVert
            #pragma fragment ForwardLitFrag

            // ── URP lighting variants ──────────────────────────────────
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _FORWARD_PLUS               // Unity 6 Forward+ cluster lighting
            #pragma multi_compile_fog
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            // ── GPU Instancing ─────────────────────────────────────────
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"
            #include "Assets/Shaders/HLSL/FPS_SharpDetail_Functions.hlsl"

            // ── Texture Declarations ───────────────────────────────────
            TEXTURE2D(_BaseMap);            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);          SAMPLER(sampler_NormalMap);
            TEXTURE2D(_DetailAlbedo);       SAMPLER(sampler_DetailAlbedo);
            TEXTURE2D(_DetailNormal);       SAMPLER(sampler_DetailNormal);
            TEXTURE2D(_MetallicGlossMap);   SAMPLER(sampler_MetallicGlossMap);
            TEXTURE2D(_OcclusionMap);       SAMPLER(sampler_OcclusionMap);
            TEXTURE2D(_EmissionMap);        SAMPLER(sampler_EmissionMap);

            // ── Material CBUFFER (SRP Batcher compatible) ──────────────
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                float4 _NormalMap_ST;
                half   _NormalStrength;

                float4 _DetailTiling;           // xy = tiling, zw unused
                float4 _DetailOffset;           // xy = offset, zw unused
                half   _DetailBlendStrength;
                half   _DetailNormalStrength;

                float4 _MetallicGlossMap_ST;
                half   _Metallic;
                half   _Smoothness;
                float4 _OcclusionMap_ST;
                half   _OcclusionStrength;

                float4 _EmissionMap_ST;
                half4  _EmissionColor;

                half   _SharpenStrength;
                half   _SharpenThreshold;
                half   _Cutoff;
            CBUFFER_END

            // ── Vertex I/O ─────────────────────────────────────────────
            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float4 tangentOS    : TANGENT;
                float2 uv           : TEXCOORD0;
                float2 lightmapUV   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS               : SV_POSITION;
                float2 uv                       : TEXCOORD0;
                float2 detailUV                 : TEXCOORD1;
                float3 positionWS               : TEXCOORD2;
                float3 normalWS                 : TEXCOORD3;
                float4 tangentWS                : TEXCOORD4;  // xyz=tangent, w=bitangent sign
                half4  fogAndVertexLight        : TEXCOORD5;  // x=fog, yzw=vertex light
                float4 shadowCoord              : TEXCOORD6;
                float2 normalizedScreenSpaceUV  : TEXCOORD7;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ── Vertex Shader ──────────────────────────────────────────
            Varyings ForwardLitVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs posInputs  = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;

                // Base UV (uses _BaseMap_ST tiling/offset from the inspector)
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);

                // Detail UV: independent tiling and offset, bypasses ST packing
                OUT.detailUV = IN.uv * _DetailTiling.xy + _DetailOffset.xy;

                // Tangent frame (bitangent sign packed into tangentWS.w)
                real bitangentSign = IN.tangentOS.w * GetOddNegativeScale();
                OUT.normalWS  = normInputs.normalWS;
                OUT.tangentWS = float4(normInputs.tangentWS, bitangentSign);

                // Fog + per-vertex lighting (baked into a single interpolator)
                half fogFactor    = ComputeFogFactor(posInputs.positionCS.z);
                half3 vertexLight = VertexLighting(posInputs.positionWS, normInputs.normalWS);
                OUT.fogAndVertexLight = half4(fogFactor, vertexLight);

                OUT.shadowCoord             = GetShadowCoord(posInputs);
                OUT.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(posInputs.positionCS);

                OUTPUT_LIGHTMAP_UV(IN.lightmapUV, unity_LightmapST, OUT.staticLightmapUV);
                OUTPUT_SH(OUT.normalWS.xyz, OUT.vertexSH);

                return OUT;
            }

            // ── Fragment Shader ────────────────────────────────────────
            half4 ForwardLitFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                #ifdef LOD_FADE_CROSSFADE
                    LODFadeCrossFade(IN.positionCS);
                #endif

                // ── [A] Base textures ──────────────────────────────────
                half4 baseAlbedo = SAMPLE_TEXTURE2D(_BaseMap,   sampler_BaseMap,   IN.uv) * _BaseColor;

                // Unpack base normal (tangent space)
                float3 baseNormalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, IN.uv),
                    _NormalStrength
                );

                // ── [B] Detail textures (independent UV scale) ─────────
                //   Detail albedo authored as neutral grey (0.5) = no change.
                //   Detail normal authored as flat (0.5, 0.5, 1.0) = no change.
                half3  detailAlbedoSample = SAMPLE_TEXTURE2D(_DetailAlbedo, sampler_DetailAlbedo, IN.detailUV).rgb;
                float3 detailNormalTS     = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, IN.detailUV),
                    _DetailNormalStrength
                );

                // Overlay blend: adds micro-contrast without hue shift or blurring
                float3 blendedAlbedo;
                OverlayBlend_float(baseAlbedo.rgb, detailAlbedoSample, _DetailBlendStrength, blendedAlbedo);

                // RNM normal blend: preserves curvature of both layers precisely
                float3 blendedNormalTS;
                BlendNormals_RNM_float(baseNormalTS, detailNormalTS, blendedNormalTS);

                // ── [C] Per-pixel sharpening (zero extra texture samples) ──
                //   Uses ddx_fine/ddy_fine on luminance to detect edges,
                //   then boosts local contrast via luminance-preserving scale.
                float3 sharpenedAlbedo;
                SharpenLuminance_float(blendedAlbedo, _SharpenStrength, _SharpenThreshold, sharpenedAlbedo);

                // ── [D] PBR surface properties ─────────────────────────
                half4 metallicGloss = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, IN.uv);
                half  metallic      = metallicGloss.r * _Metallic;
                half  smoothness    = metallicGloss.a * _Smoothness;

                half  occlusion     = lerp(1.0h,
                    SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, IN.uv).g,
                    _OcclusionStrength);

                half3 emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).rgb
                               * _EmissionColor.rgb;

                // ── [E] Normal TS → World Space ────────────────────────
                float3 normalWS     = normalize(IN.normalWS);
                float3 tangentWS    = normalize(IN.tangentWS.xyz);
                float  bSign        = IN.tangentWS.w;
                float3 bitangentWS  = cross(normalWS, tangentWS) * bSign;

                // tangentToWorld rows: T, B, N (used by URP for decals)
                half3x3 tangentToWorld = half3x3(tangentWS, bitangentWS, normalWS);

                // Final world-space surface normal (after blending)
                float3 finalNormalWS = NormalizeNormalPerPixel(
                    TransformTangentToWorld(blendedNormalTS, tangentToWorld)
                );

                // ── [F] Fill URP InputData ─────────────────────────────
                InputData inputData = (InputData)0;
                inputData.positionWS             = IN.positionWS;
                inputData.positionCS             = IN.positionCS;
                inputData.normalWS               = finalNormalWS;
                inputData.viewDirectionWS        = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord            = IN.shadowCoord;
                inputData.fogCoord               = IN.fogAndVertexLight.x;
                inputData.vertexLighting         = IN.fogAndVertexLight.yzw;
                inputData.tangentToWorld         = tangentToWorld;
                inputData.normalizedScreenSpaceUV = IN.normalizedScreenSpaceUV;
                inputData.bakedGI                = SAMPLE_GI(IN.staticLightmapUV, IN.vertexSH, finalNormalWS);
                inputData.shadowMask             = SAMPLE_SHADOWMASK(IN.staticLightmapUV);

                // ── [G] Fill URP SurfaceData ───────────────────────────
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo              = sharpenedAlbedo;
                surfaceData.metallic            = metallic;
                surfaceData.specular            = 0.0h;
                surfaceData.smoothness          = smoothness;
                surfaceData.normalTS            = blendedNormalTS;
                surfaceData.emission            = emission;
                surfaceData.occlusion           = occlusion;
                surfaceData.alpha               = baseAlbedo.a;
                surfaceData.clearCoatMask       = 0.0h;
                surfaceData.clearCoatSmoothness = 0.0h;

                // ── [H] Decal DBuffer (must run before UniversalFragmentPBR) ──
                #if defined(_DBUFFER_MRT1) || defined(_DBUFFER_MRT2) || defined(_DBUFFER_MRT3)
                    ApplyDecalToSurfaceData(IN.positionCS, surfaceData, inputData);
                    // Re-derive world normal after decal may have changed normalTS
                    inputData.normalWS = NormalizeNormalPerPixel(
                        TransformTangentToWorld(surfaceData.normalTS, tangentToWorld)
                    );
                #endif

                // ── [I] PBR lighting (handles Forward+ cluster loop internally) ──
                half4 finalColor = UniversalFragmentPBR(inputData, surfaceData);
                finalColor.rgb   = MixFog(finalColor.rgb, inputData.fogCoord);

                return finalColor;
            }
            ENDHLSL
        }

        // ================================================================
        // PASS 2 — Shadow Caster
        // Supports both directional and punctual (point/spot) shadows.
        // ================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest  LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Full UnityPerMaterial CBUFFER (SRP Batcher requirement)
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                float4 _NormalMap_ST;
                half   _NormalStrength;
                float4 _DetailTiling;
                float4 _DetailOffset;
                half   _DetailBlendStrength;
                half   _DetailNormalStrength;
                float4 _MetallicGlossMap_ST;
                half   _Metallic;
                half   _Smoothness;
                float4 _OcclusionMap_ST;
                half   _OcclusionStrength;
                float4 _EmissionMap_ST;
                half4  _EmissionColor;
                half   _SharpenStrength;
                half   _SharpenThreshold;
                half   _Cutoff;
            CBUFFER_END

            // Renderer-set globals (NOT in material CBUFFER; written by the URP shadow system)
            float3 _LightDirection;
            float3 _LightPosition;
            float4 _ShadowBias;     // x = depth bias, y = normal bias

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVaryings ShadowVert(ShadowAttributes IN)
            {
                ShadowVaryings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 posWS    = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

                // Direction to light source
                #ifdef _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - posWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                // Shadow bias: offset along light dir (depth) + normal (normal bias)
                // Equivalent to ApplyShadowBias(), inlined for Unity 6 compatibility.
                // _ShadowBias.x = depth bias, _ShadowBias.y = normal bias scale.
                float invNdotL = 1.0 - saturate(dot(lightDir, normalWS));
                posWS += lightDir  * _ShadowBias.x;
                posWS += normalWS  * (invNdotL * _ShadowBias.y);

                float4 posCS = TransformWorldToHClip(posWS);

                // Shadow pancaking: clamp near-plane vertices to avoid
                // shadow holes on surfaces facing away from the light
                #if UNITY_REVERSED_Z
                    posCS.z = min(posCS.z, posCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    posCS.z = max(posCS.z, posCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                OUT.positionCS = posCS;
                return OUT;
            }

            half4 ShadowFrag(ShadowVaryings IN) : SV_Target
            {
                #ifdef LOD_FADE_CROSSFADE
                    LODFadeCrossFade(IN.positionCS);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ================================================================
        // PASS 3 — Depth Only
        // Writes depth pre-pass for depth priming / early-Z optimization.
        // ================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   DepthOnlyVert
            #pragma fragment DepthOnlyFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                float4 _NormalMap_ST;
                half   _NormalStrength;
                float4 _DetailTiling;
                float4 _DetailOffset;
                half   _DetailBlendStrength;
                half   _DetailNormalStrength;
                float4 _MetallicGlossMap_ST;
                half   _Metallic;
                half   _Smoothness;
                float4 _OcclusionMap_ST;
                half   _OcclusionStrength;
                float4 _EmissionMap_ST;
                half4  _EmissionColor;
                half   _SharpenStrength;
                half   _SharpenThreshold;
                half   _Cutoff;
            CBUFFER_END

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            DepthVaryings DepthOnlyVert(DepthAttributes IN)
            {
                DepthVaryings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half DepthOnlyFrag(DepthVaryings IN) : SV_Target
            {
                #ifdef LOD_FADE_CROSSFADE
                    LODFadeCrossFade(IN.positionCS);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ================================================================
        // PASS 4 — Depth Normals
        // Writes depth + view-space normals for SSAO, contact shadows, etc.
        // Includes the full detail normal blend so SSAO is accurate at
        // close range (important in FPS where surfaces fill the screen).
        // ================================================================
        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   DepthNormalsVert
            #pragma fragment DepthNormalsFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shaders/HLSL/FPS_SharpDetail_Functions.hlsl"

            TEXTURE2D(_NormalMap);    SAMPLER(sampler_NormalMap);
            TEXTURE2D(_DetailNormal); SAMPLER(sampler_DetailNormal);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                float4 _NormalMap_ST;
                half   _NormalStrength;
                float4 _DetailTiling;
                float4 _DetailOffset;
                half   _DetailBlendStrength;
                half   _DetailNormalStrength;
                float4 _MetallicGlossMap_ST;
                half   _Metallic;
                half   _Smoothness;
                float4 _OcclusionMap_ST;
                half   _OcclusionStrength;
                float4 _EmissionMap_ST;
                half4  _EmissionColor;
                half   _SharpenStrength;
                half   _SharpenThreshold;
                half   _Cutoff;
            CBUFFER_END

            struct DNAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DNVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float2 detailUV   : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float4 tangentWS  : TEXCOORD3;  // xyz=tangent, w=sign
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            DNVaryings DepthNormalsVert(DNAttributes IN)
            {
                DNVaryings OUT = (DNVaryings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.detailUV   = IN.uv * _DetailTiling.xy + _DetailOffset.xy;
                OUT.normalWS   = normInputs.normalWS;

                real bitangentSign = IN.tangentOS.w * GetOddNegativeScale();
                OUT.tangentWS = float4(normInputs.tangentWS, bitangentSign);

                return OUT;
            }

            half4 DepthNormalsFrag(DNVaryings IN) : SV_Target
            {
                #ifdef LOD_FADE_CROSSFADE
                    LODFadeCrossFade(IN.positionCS);
                #endif

                // Sample and blend normals (same logic as ForwardLit pass)
                float3 baseNormalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, IN.uv),
                    _NormalStrength
                );
                float3 detailNormalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, IN.detailUV),
                    _DetailNormalStrength
                );

                float3 blendedNormalTS;
                BlendNormals_RNM_float(baseNormalTS, detailNormalTS, blendedNormalTS);

                // Reconstruct TBN and convert to world space
                float3 normalWS    = normalize(IN.normalWS);
                float3 tangentWS   = normalize(IN.tangentWS.xyz);
                float3 bitangentWS = cross(normalWS, tangentWS) * IN.tangentWS.w;
                half3x3 TBN        = half3x3(tangentWS, bitangentWS, normalWS);

                float3 finalNormalWS = NormalizeNormalPerPixel(
                    TransformTangentToWorld(blendedNormalTS, TBN)
                );

                // Encode normals in view space with oct-quad packing
                // (matches the format expected by URP SSAO / contact shadows)
                float3 normalVS = TransformWorldToViewDir(finalNormalWS, true);
                half2  encoded  = PackNormalOctQuadEncode(normalVS) * 0.5h + 0.5h;

                return half4(encoded, 0.0h, 0.0h);
            }
            ENDHLSL
        }

    } // SubShader

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
