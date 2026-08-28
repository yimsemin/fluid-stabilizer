// Transparent water for the interactive scene (built-in render pipeline).
// Not photoreal — a depth-tinted, fresnel-rimmed translucent surface with a
// specular glint and a procedural ripple, enough to read clearly as a body of
// liquid. Full particle/SPH fluid is deliberately out of scope (determinism +
// RL step budget — design-review §6, §21).
Shader "FluidStabilizer/WaterSurface"
{
    Properties
    {
        _DeepColor ("Deep Color", Color) = (0.02, 0.17, 0.30, 1)
        _ShallowColor ("Shallow Color", Color) = (0.16, 0.50, 0.60, 1)
        _EdgeColor ("Fresnel Edge Color", Color) = (0.75, 0.92, 1.0, 1)
        _BaseAlpha ("Base Alpha", Range(0,1)) = 0.60
        _FresnelPower ("Fresnel Power", Range(0.5,8)) = 4
        _SpecPower ("Specular Power", Range(4,256)) = 90
        _SpecStrength ("Specular Strength", Range(0,4)) = 1.6
        _RippleStrength ("Ripple Normal Strength", Range(0,0.5)) = 0.06
        _RippleScale ("Ripple Scale", Float) = 11
        _RippleSpeed ("Ripple Speed", Float) = 1.4
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            float4 _DeepColor, _ShallowColor, _EdgeColor;
            float _BaseAlpha, _FresnelPower, _SpecPower, _SpecStrength;
            float _RippleStrength, _RippleScale, _RippleSpeed;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float3 wnormal : TEXCOORD1;
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i, float facing : VFACE) : SV_Target
            {
                float3 n = normalize(i.wnormal) * (facing >= 0.0 ? 1.0 : -1.0);

                // procedural ripple — perturb the normal with a few sines of world XZ
                float t = _Time.y * _RippleSpeed;
                float2 p = i.wpos.xz * _RippleScale;
                float3 dn = float3(
                    sin(p.x + t) + 0.6 * sin(p.y * 1.7 - t * 1.3),
                    0.0,
                    cos(p.y - t) + 0.6 * cos(p.x * 1.3 + t * 0.9));
                n = normalize(n + dn * _RippleStrength);

                float3 vdir = normalize(_WorldSpaceCameraPos - i.wpos);
                float ndv = saturate(dot(n, vdir));
                float fres = pow(saturate(1.0 - ndv), _FresnelPower);

                float3 baseCol = lerp(_ShallowColor.rgb, _DeepColor.rgb, ndv);
                float3 col = lerp(baseCol, _EdgeColor.rgb, fres);

                float3 ldir = normalize(_WorldSpaceLightPos0.xyz);
                float ndl = saturate(dot(n, ldir));
                col *= (unity_AmbientSky.rgb + _LightColor0.rgb * ndl * 0.6 + 0.35);

                float3 hdir = normalize(ldir + vdir);
                float spec = pow(saturate(dot(n, hdir)), _SpecPower) * _SpecStrength;
                col += _LightColor0.rgb * spec;

                float alpha = saturate(_BaseAlpha + fres * (1.0 - _BaseAlpha) + spec);
                return fixed4(col, alpha);
            }
            ENDCG
        }
    }
    Fallback "Transparent/VertexLit"
}
