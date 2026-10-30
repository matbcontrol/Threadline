// All tensioned threads are drawn into a coarse coverage texture (same coordinates as the target).
// Chord scoring takes the residual as target minus coverage.
Shader "Threadline/Coverage"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_StateTex);
            float _Nails;
            float _CoverageWidth;
            float _LineDarkness;
            float _UVFlip;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 ab : TEXCOORD0;
                float2 ends : TEXCOORD1;
            };

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                int a = (int)(v.ab.x + 0.5);
                int b = (int)(v.ab.y + 0.5);
                float on = step(0.5, LOAD_TEXTURE2D_LOD(_StateTex, int2(a, b), 0).r);

                float ta = a * TWO_PI / _Nails;
                float tb = b * TWO_PI / _Nails;
                float2 pa = 0.5 + 0.5 * float2(cos(ta), sin(ta));
                float2 pb = 0.5 + 0.5 * float2(cos(tb), sin(tb));
                float2 dir = normalize(pb - pa);
                float2 uv = lerp(pa, pb, v.ends.x) + float2(-dir.y, dir.x) * (v.ends.y * _CoverageWidth * 0.5);

                // Row 0 of the target texture is uv.y = 0; on D3D clip y = +1 lands in row 0
                float2 clip = uv * 2.0 - 1.0;
                o.positionCS = float4(clip.x, clip.y * _UVFlip, 0.5, 1.0) * on;
                return o;
            }

            float frag(Varyings i) : SV_Target
            {
                return _LineDarkness;
            }
            ENDHLSL
        }
    }
}
