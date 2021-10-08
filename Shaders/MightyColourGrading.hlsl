///////////////////////////////////////////////////////////////////////////////
//                      Global COLOUR GRADING Functions                      //
///////////////////////////////////////////////////////////////////////////////
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"


static const half3 _GREY = half3(0.2126, 0.7152, 0.0722);
static const half3 _LUMINANCE_WEIGHT = half3(0.2126h, 0.7152h, 0.0722h);
static const half _GAMMA = 2.2h;

half _USE_COLOURGRADE;

// GLOBAL VARIABLES
TEXTURE2D(_ColourGrade_LUT); SAMPLER(sampler_ColourGrade_LUT);
half4 _ColourGrade_LUT_Params;

#define LutParams           _ColourGrade_LUT_Params.xyz
#define LutContribution     _ColourGrade_LUT_Params.w

half4 _ColourGrade_ColourCorrection_Params;
#define _Contrast          _ColourGrade_ColourCorrection_Params.x
#define _Brightness        _ColourGrade_ColourCorrection_Params.y
#define _Saturation        _ColourGrade_ColourCorrection_Params.z
#define _Vibrancy          _ColourGrade_ColourCorrection_Params.w

half3 Contrast(half3 input)
{
    half  midpoint = pow(0.5, 2.2);
    half3 colour = (input - midpoint) * _Contrast + midpoint;

    return colour;
}

half3 Contrast(half3 input, half value)
{   
    half  midpoint = pow(0.5, 2.2);
    half3 colour = (input - midpoint) * value + midpoint;

    return colour;
}

half3 Brightness(half3 input)
{
    // this should be additive but the work was previously balanced as a multiplication
    half3 colour = input.rgb * _Brightness;

    return colour;
}

half3 Brightness(half3 input, half value)
{
    // this should be additive but the work was previously balanced as a multiplication
    half3 colour = input.rgb * value;

    return colour;
}

half3 Saturation(half3 input)
{
    half grey = dot(input, _GREY);
    half3 ds = half3(grey, grey, grey);
    half3 colour = lerp(ds, input, _Saturation);

    return colour;
}

half3 Saturation(half3 input, half value)
{
    half grey = dot(input, _GREY);
    half3 ds = half3(grey, grey, grey);
    half3 colour = lerp(ds, input, value);

    return colour;
}

half3 Vibrance(half3 input)
{
    half average = (input.r + input.g + input.b) / 3.0;
    half mx = max(input.r, max(input.g, input.b));
    half amt = (mx - average) * (-_Vibrancy * 3.0);
    half3 colour = lerp(input.rgb, mx.rrr, amt);

    return colour;
}

half3 Vibrance(half3 input, half value)
{
    half average = (input.r + input.g + input.b) / 3.0;
    half mx = max(input.r, max(input.g, input.b));
    half amt = (mx - average) * (-value * 3.0);
    half3 colour = lerp(input.rgb, mx.rrr, amt);

    return colour;
}

half3 Exposure(half3 input, half value)
{
    return input.rgb * pow(2.0, value);
}

half3 Gamma(half3 input, half value)
{
    return pow(input.rgb, value.xxx);
}

half3 LUTColourGrading(half3 input, TEXTURE2D_PARAM(lutTex, lutSampler))
{    
    float3 inputLutSpace = saturate(LinearToLogC(input)); // LUT space is in LogC

    input = saturate(input);
    half3 outLut = ApplyLut2D(TEXTURE2D_ARGS(lutTex, lutSampler), input, LutParams);

    half3 colour = lerp(input, outLut, LutContribution);
    
    return colour;
}

half3 ApplyColourGrading(half3 input)
{
    half3 colour = input;

    
    colour = LUTColourGrading(colour, TEXTURE2D_ARGS(_ColourGrade_LUT, sampler_ColourGrade_LUT));


    return colour;
}

///////////////////////////////////////////////////////////////////////////////
// TONE MAPPNG
///////////////////////////////////////////////////////////////////////////////

half3 ReinhardToneMapping(half3 color)
{
    color *= color / 1 + color;
    return color;
}


half3 LumaBasedReinhardToneMapping(half3 color)
{
    float luma = Luminance(color);
    float toneMappedLuma = luma / (1. + luma);
    color *= toneMappedLuma / luma;
    return color;
}

half3 WhitePreservingLumaBasedReinhardToneMapping(half3 color)
{
    float white = 2.;
    float luma = Luminance(color);
    float toneMappedLuma = luma * (1. + luma / (white*white)) / (1. + luma);
    color *= toneMappedLuma / luma;
    return color;
}

half3 GammaCorrection(half3 color)
{
    return pow(color, 1.0h / _GAMMA);
}

half3 InverseGammaCorrection(half3 color)
{
    return pow(color, 1.0h * _GAMMA);
}

real3 MightyNeutralTonemap(real3 x)
{
    // Tonemap
    const real a = 0.2;
    const real b = 0.29;
    const real c = 0.24;
    const real d = 0.272;
    const real e = 0.02;
    const real f = 0.3;
    const real whiteLevel = 4.3;
    const real whiteClip = 1.0;

    real3 whiteScale = (1.0).xxx / NeutralCurve(whiteLevel, a, b, c, d, e, f);
    x = NeutralCurve(x * whiteScale, a, b, c, d, e, f);
    x *= whiteScale;

    // Post-curve white point adjustment
    x /= whiteClip.xxx;

    return x;
}

///////////////////////////////////////////////////////////////////////////////
// ENCODING
///////////////////////////////////////////////////////////////////////////////

float4 RGBMEncode( float3 color ) {
  float4 rgbm;
  color *= 1.0 / 6.0;
  rgbm.a = saturate( max( max( color.r, color.g ), max( color.b, 1e-6 ) ) );
  rgbm.a = ceil( rgbm.a * 255.0 ) / 255.0;
  rgbm.rgb = color / rgbm.a;
  return rgbm;
}

float3 RGBMDecode( float4 rgbm ) {
  return 6.0 * rgbm.rgb * rgbm.a;
}

half4 EncodeForHDR(half3 color)
{
    // color = LinearToGamma22(color);
    half4 rgbm = EncodeRGBM(color);
    
    return rgbm;
}

half3 DecodeRGBM(half3 color)
{
    half3 rgb = DecodeRGBM(color);
    // rgb.xyz = Gamma22ToLinear(rgb.xyz);
    
    return rgb;
}