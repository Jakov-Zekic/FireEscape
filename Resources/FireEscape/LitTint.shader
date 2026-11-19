Shader "FireEscape/LitTint"
{
    Properties
    {
        _Color ("Colour", Color) = (1,1,1,1)
        _EmissionColor ("Emission", Color) = (0,0,0,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 150
        CGPROGRAM
        #pragma surface surf Lambert addshadow
        #pragma target 2.0
        fixed4 _Color;
        fixed4 _EmissionColor;
        struct Input { float3 worldPos; };
        void surf(Input IN, inout SurfaceOutput o)
        {
            o.Albedo = _Color.rgb;
            o.Emission = _EmissionColor.rgb;
            o.Alpha = _Color.a;
        }
        ENDCG
    }
    Fallback "Diffuse"
}
