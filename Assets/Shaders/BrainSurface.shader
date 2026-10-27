// Vertex-color Lambert surface for the SOFA brain: the fold-depth shading
// baked per vertex (sulci darker, gyri lit) multiplies into the lit albedo,
// which Unity's built-in Standard shader cannot do (it ignores COLOR).
Shader "SofaRobotAI/BrainSurface"
{
    Properties
    {
        _Color ("Main Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Lambert

        struct Input
        {
            float4 color : COLOR;
        };

        fixed4 _Color;

        void surf (Input IN, inout SurfaceOutput o)
        {
            o.Albedo = IN.color.rgb * _Color.rgb;
        }
        ENDCG
    }
    Fallback "Legacy Shaders/Diffuse"
}
