// Visible thread length for the animation: moves toward 1 while the chord is on and toward 0 once it is removed.
// The thread winds from nail a to nail b and unwinds back.
Shader "Threadline/Vis"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_StateTex);
            TEXTURE2D(_VisTex);
            float _VisStep;

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(uint id : SV_VertexID)
            {
                Varyings o;
                float2 uv = float2((id << 1) & 2, id & 2);
                o.positionCS = float4(uv * 2.0 - 1.0, 0.5, 1.0);
                return o;
            }

            float frag(Varyings i) : SV_Target
            {
                int2 px = int2(i.positionCS.xy);
                float on = LOAD_TEXTURE2D(_StateTex, px).r;
                float v = LOAD_TEXTURE2D(_VisTex, px).r;
                return saturate(v + (on > 0.5 ? _VisStep : -_VisStep));
            }
            ENDHLSL
        }
    }
}
