// Road / ground / trackside mesh. Unlit, one texture sample, no lighting.
//
// Phase 10: the road was vertex-colour only, so a texture assigned to its material did
// nothing. It now samples _MainTex with PERSPECTIVE-CORRECT coordinates. RoadRenderer
// builds its quads in 2D screen space, so plain UVs would be interpolated affinely and
// bend along each quad's diagonal wherever a segment's near and far edges differ in
// width (worst right in front of the camera). The mesh carries (u*q, v*q, q) with q
// proportional to 1/depth, and the fragment divides - the classic fix, one division.
//
// Vertex colour: rgb tints the texture, alpha is the fog factor (1 clear, 0 haze).
// With no texture assigned _MainTex is white and this is exactly the old shader.
//
// Depth is irrelevant: RoadRenderer emits bands with hill clipping, so opaque bands do
// not overlap and draw order is explicit. The same shader draws the alpha-blended
// decal pass (markings, finish line, guardrails) with _SrcBlend/_DstBlend set by code.
Shader "RenaultRacer/RoadUnlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Haze ("Haze", Color) = (0.72, 0.82, 0.92, 1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Lighting Off
            Fog { Mode Off }
            Blend [_SrcBlend] [_DstBlend]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "RRGrade.cginc"

            sampler2D _MainTex;
            fixed4 _Haze;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color  : COLOR;
                float3 uvq    : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed4 color : COLOR;
                float3 uvq   : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uvq = v.uvq;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // q is never 0: meshes that carry no projective data write q = 1.
                float2 uv = i.uvq.xy / max(i.uvq.z, 1e-5);
                fixed4 t = tex2D(_MainTex, uv);
                fixed3 c = lerp(_Haze.rgb, t.rgb * i.color.rgb, i.color.a);
                return fixed4(RRGrade(c), t.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
