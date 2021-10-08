#include "Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common.hlsl"

#define HDRColorThreshold (0.5h)
#define FilteredHDRMaskThreshold (1.175h)
#define FilteredHDRMaskThresholdKnee (0.5h)

#define ContrastThreshold (0.0625h)
#define RelativeThreshold (0.125h)

#define FXAA_SPAN_MAX           (8.0h)
#define FXAA_REDUCE_MUL         (0.25h * (1.0h / 12.0h))
#define FXAA_REDUCE_MIN         (1.0h / 128.0h)

struct HDRLuminanceData {
    half2 m, ne, nw, se, sw;
    half highest, lowest, contrast;
};

half LinearRgbToLuminance(half3 linearRgb) {
    return dot(linearRgb, half3(0.2126729h,  0.7151522h, 0.0721750h));
}

//cheap filter
half HDRFilter(half3 color)
{
    half brightness = Max3(color.r, color.g, color.b);
    return brightness - HDRColorThreshold;
}

half FilteredHDRMask(half mask)
{
    // Thresholding
    half softness = clamp(mask - FilteredHDRMaskThreshold + 0.5h, 0.0, 2.0h * FilteredHDRMaskThresholdKnee);
    softness = (softness * softness) / (4.0h * FilteredHDRMaskThresholdKnee + 1e-4);
    half multiplier = max(mask - FilteredHDRMaskThreshold, softness) / max(mask, 1e-4);
    
    mask *= multiplier;

    return mask;
}


half3 Fetch(float2 coords, float2 offset, TEXTURE2D_X(tex))
{
    float2 uv = coords + offset;
    return (SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz);
}

// store the luma in the x channel and the hdr value in the y
// we need the luma for blending calculations and we need to the hdr for masking
half2 SampleHDRFilterLuminance (half2 uv, half4 texelSize, TEXTURE2D_X(tex), int uOffset = 0, int vOffset = 0) {
    half luma = 0.0;

    uv += texelSize.xy * float2(uOffset, vOffset);
    half3 color =  SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz;

    // filtering again helps remove text and other issues
    half hdrMask = HDRFilter(color);
    half filteredMask = FilteredHDRMask(hdrMask);

    luma = LinearRgbToLuminance((color.rgb));

    return half2(luma, filteredMask);
}
// ok we need to know when sampling luminance if a pixel in our neighbou0rhood has a hdr value
// ok we need to know when sampling luminance if a pixel in our neighbou0rhood has a hdr value
HDRLuminanceData SampleHDRFilterLuminanceNeighborhood (half2 uv, half4 texelSize, TEXTURE2D_X(tex)) {
    
    HDRLuminanceData l;
    l.m = saturate(SampleHDRFilterLuminance(uv, texelSize, tex, 0, 0));
    l.ne = saturate(SampleHDRFilterLuminance(uv, texelSize, tex, 1,  1));
    l.nw = saturate(SampleHDRFilterLuminance(uv, texelSize, tex, -1,  1));
    l.se = saturate(SampleHDRFilterLuminance(uv, texelSize, tex, 1, -1));
    l.sw = saturate(SampleHDRFilterLuminance(uv, texelSize, tex, -1, -1));

    return l;
}

HDRLuminanceData SampleHDRFilterLuminanceNeighborhoodNS (half2 uv, half4 texelSize, TEXTURE2D_X(tex)) {
    
    HDRLuminanceData l;
    l.m = (SampleHDRFilterLuminance(uv, texelSize, tex, 0, 0));
    l.ne = (SampleHDRFilterLuminance(uv, texelSize, tex, 1,  1));
    l.nw = (SampleHDRFilterLuminance(uv, texelSize, tex, -1,  1));
    l.se = (SampleHDRFilterLuminance(uv, texelSize, tex, 1, -1));
    l.sw = (SampleHDRFilterLuminance(uv, texelSize, tex, -1, -1));

    return l;
}


// if my neighbourhood lacks a HDR pixel then we should skip me
bool ShouldSkipPixel_HDRFilter(HDRLuminanceData l) {
    half isHDR = l.m.y + l.ne.y + l.nw.y + l.se.y + l.sw.y;
    return isHDR > 0;
}

// if my contrast is too low then skip me
bool ShouldSkipPixel_Contrast (HDRLuminanceData l) {
    float threshold =
        max(ContrastThreshold, RelativeThreshold * l.highest);
    return l.contrast < threshold;
}

// // // this needs an effective way of reducing the size of the blur
half3 FXAA_HDRFilter(half2 uv, TEXTURE2D_X(tex), half4 texelSize, half mask)
{   
    #ifdef _FXAA_OFF
        return SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz;
    #endif

    if(mask <= 0) {
        return (SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz);
    }

    HDRLuminanceData l = SampleHDRFilterLuminanceNeighborhood (uv, texelSize, tex); //  we are still getting text into this equation and not enough HDR values

    
    if (!ShouldSkipPixel_HDRFilter(l)) {
        return SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz;
    }

    l.highest = Max3(l.m.x, l.nw.x, Max3(l.ne.x, l.sw.x, l.se.x));
    l.lowest = Min3(l.m.x, l.nw.x, Min3(l.ne.x, l.sw.x, l.se.x));

    l.contrast = l.highest - l.lowest;

    // should ignore pixel?
    if (ShouldSkipPixel_Contrast(l)) {
        return SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz;
    }

    half2 dir;
    dir.x = -((l.nw.x + l.ne.x) - (l.sw.x + l.se.x));
    dir.y = ((l.nw.x + l.sw.x) - (l.ne.x + l.se.x));

    half lumaSum = l.m.x + l.nw.x + l.sw.x + l.se.x + l.ne.x;

    half dirReduce = max(lumaSum * FXAA_REDUCE_MUL, FXAA_REDUCE_MIN);
    half rcpDirMin = rcp(min(abs(dir.x), abs(dir.y)) + dirReduce);

    dir = min((FXAA_SPAN_MAX).xx, max((-FXAA_SPAN_MAX).xx, dir * rcpDirMin)) * texelSize.xy;
    
    // half3 rgb[4];

    // [unroll (4)] for (int i=0; i < 4; i++) 
    // {                
    //     rgb[i] = saturate(Fetch(uv, dir * (i / 3.0 - 0.5), tex));
    // }

    // half3 rgbA = 0.5 * (rgb[1] + rgb[2]);
    // half3 rgbB = rgbA * 0.5 + 0.25 * (rgb[0] + rgb[3]);

    // half lumaB = LinearRgbToLuminance(rgbB);

    // half3 color = ((lumaB < l.lowest) || (lumaB > l.highest)) ? rgbA : rgbB; // this is either or? maybe we should try a blend...


    // cheaper and nicer blending
    half3 rgb[4];

    [unroll (4)] for (int i=0; i < 4; i++) 
    {                
        rgb[i] = saturate(Fetch(uv, dir * (i / 3.0 - 0.5), tex));
    }

    half3 rgbA = 0.5 * (rgb[1] + rgb[2]);
    half3 rgbB = rgbA * 0.5 + 0.25 * (rgb[0] + rgb[3]);

    half lumaB = LinearRgbToLuminance(rgbB);

    half3 color = (rgbA + rgbB) * 0.5h;

    return (color);    
 
}


half3 FXAA_HDRFilter_NS(half2 uv, TEXTURE2D_X(tex), half4 texelSize, half mask)
{   
    #ifdef _FXAA_OFF
        return SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz;
    #endif

    if(mask <= 0) {
        return (SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz);
    }

    HDRLuminanceData l = SampleHDRFilterLuminanceNeighborhoodNS (uv, texelSize, tex); //  we are still getting text into this equation and not enough HDR values

    
    if (!ShouldSkipPixel_HDRFilter(l)) {
        return SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz;
    }

    l.highest = Max3(l.m.x, l.nw.x, Max3(l.ne.x, l.sw.x, l.se.x));
    l.lowest = Min3(l.m.x, l.nw.x, Min3(l.ne.x, l.sw.x, l.se.x));

    l.contrast = l.highest - l.lowest;

    // should ignore pixel?
    if (ShouldSkipPixel_Contrast(l)) {
        return SAMPLE_TEXTURE2D_X(tex, sampler_LinearClamp, uv).xyz;
    }

    half2 dir;
    dir.x = -((l.nw.x + l.ne.x) - (l.sw.x + l.se.x));
    dir.y = ((l.nw.x + l.sw.x) - (l.ne.x + l.se.x));

    half lumaSum = l.m.x + l.nw.x + l.sw.x + l.se.x + l.ne.x;

    half dirReduce = max(lumaSum * FXAA_REDUCE_MUL, FXAA_REDUCE_MIN);
    half rcpDirMin = rcp(min(abs(dir.x), abs(dir.y)) + dirReduce);

    dir = min((FXAA_SPAN_MAX).xx, max((-FXAA_SPAN_MAX).xx, dir * rcpDirMin)) * texelSize.xy;
    
    // half3 rgb[4];

    // [unroll (4)] for (int i=0; i < 4; i++) 
    // {                
    //     rgb[i] = saturate(Fetch(uv, dir * (i / 3.0 - 0.5), tex));
    // }

    // half3 rgbA = 0.5 * (rgb[1] + rgb[2]);
    // half3 rgbB = rgbA * 0.5 + 0.25 * (rgb[0] + rgb[3]);

    // half lumaB = LinearRgbToLuminance(rgbB);

    // half3 color = ((lumaB < l.lowest) || (lumaB > l.highest)) ? rgbA : rgbB; // this is either or? maybe we should try a blend...


    // cheaper and nicer blending
    half3 rgb[4];

    [unroll (4)] for (int i=0; i < 4; i++) 
    {                
        rgb[i] = Fetch(uv, dir * (i / 3.0 - 0.5), tex);
    }

    half3 rgbA = 0.5 * (rgb[1] + rgb[2]);
    half3 rgbB = rgbA * 0.5 + 0.25 * (rgb[0] + rgb[3]);

    half lumaB = LinearRgbToLuminance(rgbB);

    half3 color = (rgbA + rgbB) * 0.5h;

    return (color);    
 
}
