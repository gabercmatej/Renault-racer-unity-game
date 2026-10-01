// Roadside prop sprites (Phase 10). Like Sprites/Default, with one difference that
// matters: distance haze is a BLEND toward the haze colour, not a tint. Multiplying a
// textured sprite by a haze colour darkens it; the far road fades toward light haze,
// so a tinted prop would read as a dark cut-out against it.
//
// Vertex colour carries the data, not a tint: r = fog (1 clear, 0 fully hazed),
// a = opacity. SpriteRenderer.color is how RoadsideSprites sets both.
Shader "RenaultRacer/PropSprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Haze ("Haze", Color) = (0.72, 0.82, 0.92, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "True" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Haze;

            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                return fixed4(lerp(_Haze.rgb, t.rgb, i.color.r), t.a * i.color.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
