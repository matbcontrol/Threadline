// One step of local least-squares search. m = signed mean residual along the chord,
// α = thread darkness. Adding a thread pays off if m > α/2; removing a tensioned thread pays off if without it
// the residual m + α is still below α/2, i.e. m < -α/2. Hysteresis keeps threads from flickering at the boundary,
// random thinning keeps a bundle of nearly identical chords from switching on all at once.
// Writes the new state (0 or 1) to a different texture than the one it reads.
Shader "Threadline/Select"
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

            TEXTURE2D(_ScoreTex);
            TEXTURE2D(_StateTex);
            float _Nails;
            float _MinGap;
            float _LineDarkness;
            float _Hysteresis;
            float _AddProb;
            float _AddBoost;
            float _RemoveProb;
            float _Seed;

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(uint id : SV_VertexID)
            {
                Varyings o;
                float2 uv = float2((id << 1) & 2, id & 2);
                o.positionCS = float4(uv * 2.0 - 1.0, 0.5, 1.0);
                return o;
            }

            float Hash(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float frag(Varyings i) : SV_Target
            {
                int2 px = int2(i.positionCS.xy);
                int a = px.x, b = px.y;
                if (a >= b) return 0.0;
                int n = (int)_Nails;
                int gap = min(b - a, n - (b - a));
                if (gap < (int)_MinGap) return 0.0;

                float m = LOAD_TEXTURE2D(_ScoreTex, px).r;
                bool on = LOAD_TEXTURE2D(_StateTex, px).r > 0.5;
                float h = Hash(float3(a, b, _Seed));
                float half_a = 0.5 * _LineDarkness;

                if (on)
                    return (m < -half_a - _Hysteresis && h < _RemoveProb) ? 0.0 : 1.0;
                return (m > half_a + _Hysteresis + _AddBoost && h < _AddProb) ? 1.0 : 0.0;
            }
            ENDHLSL
        }
    }
}
