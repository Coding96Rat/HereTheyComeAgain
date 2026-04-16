// =============================================================================
// FPS_SharpDetail_Functions.hlsl
// Standalone HLSL utility library for FPS_SharpDetailLit.shader
// Also usable as Custom Function node bodies in URP Shader Graph.
//
// Compatible with Unity 6 URP 17 / Forward+
// =============================================================================
#ifndef FPS_SHARP_DETAIL_FUNCTIONS_HLSL
#define FPS_SHARP_DETAIL_FUNCTIONS_HLSL

// -----------------------------------------------------------------------------
// 1. NORMAL MAP BLENDING
// -----------------------------------------------------------------------------

// Reoriented Normal Mapping (RNM)
// Most accurate method. Preserves both normals' curvature fully.
// Use for detail normals with significant tilt (rocks, fabric, metal).
// n1 = base normal (tangent-space, from UnpackNormalScale)
// n2 = detail normal (tangent-space, from UnpackNormalScale)
void BlendNormals_RNM_float(float3 n1, float3 n2, out float3 result)
{
    float3 t = n1 + float3(0.0, 0.0, 1.0);
    float3 u = n2 * float3(-1.0, -1.0, 1.0);
    result = normalize(t * dot(t, u) - u * t.z);
}

// Whiteout Blend
// Good balance of accuracy and cost. Better than UDN for large detail tilts.
// Recommended for general use when RNM overhead is undesirable.
void BlendNormals_Whiteout_float(float3 n1, float3 n2, out float3 result)
{
    result = normalize(float3(n1.xy + n2.xy, n1.z * n2.z));
}

// UDN (Unity Detail Normal) Blend — cheapest
// Best for very subtle detail normals (dust, light scratches).
void BlendNormals_UDN_float(float3 n1, float3 n2, out float3 result)
{
    result = normalize(float3(n1.xy + n2.xy, n1.z));
}

// -----------------------------------------------------------------------------
// 2. DETAIL ALBEDO BLENDING
// -----------------------------------------------------------------------------

// Overlay Blend
// Darks in the detail map darken the base; brights lighten it.
// Neutral grey (0.5) = no change. Ideal for adding micro-contrast without
// shifting the overall hue. Does NOT blur at close range.
//
// base    : base albedo (0..1)
// overlay : detail map (0..1), authored as grey-neutral
// strength: blend amount (0 = no effect, 1 = full overlay)
void OverlayBlend_float(float3 base, float3 overlay, float strength, out float3 result)
{
    float3 lo = 2.0 * base * overlay;
    float3 hi = 1.0 - 2.0 * (1.0 - base) * (1.0 - overlay);
    float3 overlayResult = lerp(lo, hi, step(0.5, base));
    result = lerp(base, overlayResult, strength);
}

// Soft Light Blend — gentler overlay variant for subtle surface variation
void SoftLightBlend_float(float3 base, float3 overlay, float strength, out float3 result)
{
    float3 soft = (1.0 - 2.0 * overlay) * base * base + 2.0 * overlay * base;
    result = lerp(base, soft, strength);
}

// Linear Light Blend — high-contrast, good for bold detail maps (e.g. brick seams)
void LinearLightBlend_float(float3 base, float3 overlay, float strength, out float3 result)
{
    float3 ll = saturate(base + 2.0 * overlay - 1.0);
    result = lerp(base, ll, strength);
}

// -----------------------------------------------------------------------------
// 3. PER-PIXEL SHARPENING  (no extra texture samples)
// -----------------------------------------------------------------------------

// Derivative-based Edge Contrast Sharpener
// Uses ddx_fine/ddy_fine (per-pixel screen-space derivatives) to locate edges,
// then boosts local contrast at those edges — emulating an in-shader unsharp mask.
//
// How it works:
//   1. Compute luminance gradient magnitude (= edge presence proxy)
//   2. Above a noise-gate threshold, boost: push darks darker, brights brighter
//   3. The boost is self-attenuating: large flat areas are unaffected
//
// strength  : [0..1] sharpening intensity
// threshold : [0..0.1] gradient magnitude below which no sharpening is applied
//             (prevents noise amplification on smooth surfaces)
void SharpenEdgeContrast_float(float3 color, float strength, float threshold, out float3 result)
{
    float lum   = dot(color, float3(0.299, 0.587, 0.114));
    float lumDX = ddx_fine(lum);
    float lumDY = ddy_fine(lum);

    float edgeMag  = length(float2(lumDX, lumDY));
    float edgeMask = saturate((edgeMag - threshold) / max(threshold * 2.0, 1e-5));

    // Unsharp mask direction: push color away from 0.5 (midpoint)
    float3 boost = (color - 0.5) * strength * edgeMask;
    result = saturate(color + boost);
}

// Luminance-Preserving Sharpener
// Sharpens only the luminance channel; RGB channels are scaled proportionally.
// Eliminates color fringing artifacts that can appear at strong RGB edge boosts.
void SharpenLuminance_float(float3 color, float strength, float threshold, out float3 result)
{
    float lum   = dot(color, float3(0.299, 0.587, 0.114));
    float lumDX = ddx_fine(lum);
    float lumDY = ddy_fine(lum);

    float edgeMag  = length(float2(lumDX, lumDY));
    float edgeMask = saturate((edgeMag - threshold) / max(threshold * 2.0, 1e-5));

    float lumBoost = (lum - 0.5) * strength * edgeMask;
    float newLum   = saturate(lum + lumBoost);

    // Scale RGB by luminance ratio (preserves hue/saturation)
    result = (lum > 1e-5) ? color * (newLum / lum) : color;
}

// -----------------------------------------------------------------------------
// 4. SHADER GRAPH CUSTOM FUNCTION WRAPPERS
// Signature format: void FunctionName_float(inputs..., out outputs...)
// In Shader Graph → Custom Function Node → Type: File
//   Function: <FunctionName>_float
// -----------------------------------------------------------------------------

// Wrapper: full detail layer application (albedo + normal + sharpen in one node)
// Inputs:  baseAlbedo, baseNormalTS, detailAlbedo, detailNormalTS
//          detailBlend [0..1], normalBlend [0..1], sharpenStrength, sharpenThreshold
// Outputs: finalAlbedo, finalNormalTS
void ApplyDetailLayer_float(
    float3 baseAlbedo,      float3 baseNormalTS,
    float3 detailAlbedo,    float3 detailNormalTS,
    float  detailBlend,     float  normalBlend,
    float  sharpenStrength, float  sharpenThreshold,
    out float3 finalAlbedo, out float3 finalNormalTS)
{
    // Albedo: overlay blend
    float3 overlayAlbedo;
    OverlayBlend_float(baseAlbedo, detailAlbedo, detailBlend, overlayAlbedo);

    // Normal: RNM blend
    float3 blendedNormal;
    BlendNormals_RNM_float(baseNormalTS, detailNormalTS * float3(1,1,normalBlend), blendedNormal);

    // Sharpening on final albedo
    float3 sharpened;
    SharpenLuminance_float(overlayAlbedo, sharpenStrength, sharpenThreshold, sharpened);

    finalAlbedo   = sharpened;
    finalNormalTS = blendedNormal;
}

#endif // FPS_SHARP_DETAIL_FUNCTIONS_HLSL
