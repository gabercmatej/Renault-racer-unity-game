// One fullscreen draw after the world and before the UI: vignette + a faint warm horizon
// haze. Procedural (no texture), a handful of ALU per pixel, premultiplied output
// (Blend One OneMinusSrcAlpha) so a single blend does both jobs:
//   vignette  : darkens toward a cool near-black   -> alpha
//   haze glow : screen-blends a warm light band    -> colour + alpha
// It ignores the object transform and draws in clip space, so it needs no camera maths.
Shader "RenaultRacer/LookOverlay"
{
    Properties
    {
        _Vig ("Vignette (strength, start, end, aspect relief)", Vector) = (0, 0.6, 1.4, 0.75)
        _VigColor ("Vignette colour", Color) = (0.02, 0.03, 0.05, 1)
        _Glow ("Glow (intensity, horizon y, width, side falloff)", Vector) = (0, 0.5, 0.14, 0.5)
        _GlowColor ("Glow colour", Color) = (1, 0.82, 0.58, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "IgnoreProjector" = "True" "RenderType" = "Transparent" }
        Cull Off
        ZWrite Off
        ZTest Always
        ColorMask RGB
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Vig;
            float4 _VigColor;
            float4 _Glow;
            float4 _GlowColor;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                // Mesh corners are (+-1, +-1): clip space directly. _ProjectionParams.x is -1
                // when rendering into a flipped target, same as a normal projection would.
                o.pos = float4(v.vertex.x, v.vertex.y * _ProjectionParams.x, 0.5, 1.0);
                o.uv = v.vertex.xy * 0.5 + 0.5;       // y up, whatever the target
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 p = (i.uv - 0.5) * 2.0;
                float asp = _ScreenParams.x / _ScreenParams.y;
                // Wide phones: pull the sides in so 19.5:9 is not darkened more than 16:9.
                p.x *= clamp(1.7778 / asp, _Vig.w, 1.0);
                float r = length(p * float2(1.0, 0.92));
                float v = smoothstep(_Vig.y, _Vig.z, r);
                v = v * v * (1.4 - 0.4 * v) * _Vig.x;

                // Horizon haze: tighter below the horizon (it melts into the road), wider above.
                float dy = i.uv.y - _Glow.y;
                dy /= _Glow.z * (dy < 0.0 ? 0.55 : 1.0);
                float g = exp(-dy * dy) * (1.0 - _Glow.w * p.x * p.x) * _Glow.x;

                float a = 1.0 - (1.0 - v) * (1.0 - g);
                float3 rgb = v * _VigColor.rgb * (1.0 - g) + g * _GlowColor.rgb;

                // Blue-noise-ish dither so the soft gradients do not band on 8-bit panels.
                float n = frac(52.9829189 * frac(dot(i.pos.xy, float2(0.06711056, 0.00583715)))) - 0.5;
                float on = step(0.0001, a);
                return float4(rgb + n * (1.0 / 255.0) * on, a + n * (1.0 / 255.0) * on);
            }
            ENDCG
        }
    }
    Fallback Off
}
