// Opaque, lightly-lit road surface for the interactive scene (built-in RP).
// Samples a tiled road texture (asphalt + lane markings) and multiplies by the
// per-vertex colour (pothole / bump shading). ZWrite On so it never fights the
// ground plane the way a transparent strip does.
Shader "FluidStabilizer/RoadRibbon"
{
    Properties
    {
        _MainTex ("Road", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            ZWrite On
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 wnormal : TEXCOORD1;
                fixed4 color : COLOR;
            };

            v2f vert (appdata_full v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.wnormal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i, float facing : VFACE) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                float3 n = normalize(i.wnormal) * (facing >= 0.0 ? 1.0 : -1.0);
                float ndl = saturate(dot(n, normalize(_WorldSpaceLightPos0.xyz)));
                float3 lit = unity_AmbientSky.rgb + 0.30 + _LightColor0.rgb * ndl * 0.75;
                return fixed4(tex.rgb * i.color.rgb * lit, 1.0);
            }
            ENDCG
        }
    }
    Fallback "VertexLit"
}
