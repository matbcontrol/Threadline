// Line space: pixel (a, b) of this texture is the chord from nail a to nail b.
// Value = signed mean residual along the chord: target minus coverage by threads already in place
// (a discrete Radon transform of the residual). A positive residual asks for a thread, a negative one
// means this spot is already overdone.
Shader "Threadline/Score"
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

            #define SAMPLES 96

            TEXTURE2D(_TargetTex);
            TEXTURE2D(_CoverageTex);
            SAMPLER(sampler_linear_clamp);
            float _Nails;
            float _MinGap;

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(uint id : SV_VertexID)
            {
                Varyings o;
                float2 uv = float2((id << 1) & 2, id & 2);
                o.positionCS = float4(uv * 2.0 - 1.0, 0.5, 1.0);
                return o;
            }

            float2 NailUV(float k)
            {
                float t = k * TWO_PI / _Nails;
                return 0.5 + 0.5 * float2(cos(t), sin(t));
            }

            float frag(Varyings i) : SV_Target
            {
                int a = (int)i.positionCS.x;
                int b = (int)i.positionCS.y;
                if (a >= b) return 0.0;
                int n = (int)_Nails;
                int gap = min(b - a, n - (b - a));
                if (gap < (int)_MinGap) return 0.0;

                float2 pa = NailUV(a);
                float2 pb = NailUV(b);
                float sum = 0.0;
                [loop] for (int s = 0; s < SAMPLES; s++)
                {
                    float2 uv = lerp(pa, pb, (s + 0.5) / SAMPLES);
                    float t = SAMPLE_TEXTURE2D_LOD(_TargetTex, sampler_linear_clamp, uv, 0).r;
                    float c = SAMPLE_TEXTURE2D_LOD(_CoverageTex, sampler_linear_clamp, uv, 0).r;
                    sum += t - c;
                }
                return sum / SAMPLES;
            }
            ENDHLSL
        }
    }
}
