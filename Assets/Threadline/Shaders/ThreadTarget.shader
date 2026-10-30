// Turns the model snapshot into the "darkness" the threads must build up: 1 where lots of thread is needed, 0 on light areas and outside the circle.
// Fullscreen triangle from SV_VertexID, no matrices.
Shader "Threadline/Target"
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

            TEXTURE2D(_SceneTex);
            SAMPLER(sampler_linear_clamp);
            float4 _Texel;
            float _Contrast;
            float _Black;
            float _Gamma;
            float _EdgeBoost;
            float _FlipY;
            float _Sharpen;
            float _SharpenRadius;

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(uint id : SV_VertexID)
            {
                Varyings o;
                float2 uv = float2((id << 1) & 2, id & 2);
                o.positionCS = float4(uv * 2.0 - 1.0, 0.5, 1.0);
                return o;
            }

            float Lum(float2 uv)
            {
                // The project uses linear color, but threads build darkness in perceived brightness
                float3 c = pow(saturate(SAMPLE_TEXTURE2D_LOD(_SceneTex, sampler_linear_clamp, uv, 0).rgb), 1.0 / 2.2);
                return dot(c, float3(0.2126, 0.7152, 0.0722));
            }

            float frag(Varyings i) : SV_Target
            {
                float2 uv = i.positionCS.xy * _Texel.xy;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;
                float2 c = uv - 0.5;
                if (dot(c, c) > 0.25) return 0.0;

                // Unsharp mask: facial features are smaller than the nail spacing and get lost without it
                float2 t = _Texel.xy;
                float lum = Lum(uv);
                float blur = 0.0;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float ang = k * (TWO_PI / 8.0);
                    blur += Lum(uv + float2(cos(ang), sin(ang)) * t * _SharpenRadius);
                }
                blur /= 8.0;
                lum += (lum - blur) * _Sharpen;

                float d = pow(saturate((1.0 - lum - _Black) * _Contrast), _Gamma);

                // Edges get an extra boost, threads read best along them
                float gx = Lum(uv + float2(t.x, 0.0)) - Lum(uv - float2(t.x, 0.0));
                float gy = Lum(uv + float2(0.0, t.y)) - Lum(uv - float2(0.0, t.y));
                return saturate(d + sqrt(gx * gx + gy * gy) * _EdgeBoost);
            }
            ENDHLSL
        }
    }
}
