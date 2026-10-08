// Shared colour grade for every WORLD-layer shader (road, props, panorama, car, particles).
// Driven by three global vectors that Effects/LookGrade.cs pushes once per frame, so one
// component tunes the whole picture and no extra render pass or texture is needed.
//
//   _RRGrade0 : x = master blend (0 = exact identity), y = saturation, z = S-curve mix, w = exposure
//   _RRGrade1 : xyz = shadow tone offset (added, weighted toward dark), w = black lift
//   _RRGrade2 : xyz = highlight tone offset (added, weighted toward light)
//
// Unset globals read as 0, so before LookGrade exists the master blend is 0 = identity.
// Grade STRAIGHT (un-premultiplied) colour, after fog/haze has been mixed in: the haze and
// the panorama then go through the same function and meet at the horizon without a seam.
#ifndef RR_GRADE_INCLUDED
#define RR_GRADE_INCLUDED

float4 _RRGrade0;
float4 _RRGrade1;
float4 _RRGrade2;

half3 RRGrade(half3 c0)
{
    float3 c = c0 * _RRGrade0.w;
    float l = dot(c, float3(0.299, 0.587, 0.114));
    c = lerp(float3(l, l, l), c, _RRGrade0.y);
    c = saturate(c);
    // Smoothstep S-curve: fixes 0 and 1, so no clipped or crushed ends; z mixes it in.
    c = lerp(c, c * c * (3.0 - 2.0 * c), _RRGrade0.z);
    l = dot(c, float3(0.299, 0.587, 0.114));
    float sh = (1.0 - l) * (1.0 - l);
    c += _RRGrade1.xyz * sh + _RRGrade2.xyz * (l * l);
    c += _RRGrade1.w * (1.0 - c);
    return (half3)(c0 + (saturate(c) - c0) * _RRGrade0.x);
}

#endif
