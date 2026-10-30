// Threads on the board. Each chord reads its visible length (winding animation) and is drawn as a ribbon
// facing the camera, at its own height above the board, so the depth shows when the camera orbits.
// Thread width is physical (_Width) but never under _MinPixels pixels; opacity is set so the
// "ink" per unit length stays constant (_Ink): from afar the portrait tone is the same, up close you see a thin dense thread.
// In shadow mode the same thread falls on the board, offset away from the light, wider and fainter.
// Multiplicative blending, so crossings darken like real thread.
Shader "Threadline/Chords"
{
    Properties
    {
        _ThreadColor ("Thread color", Color) = (0.04, 0.04, 0.05, 1)
        _Opacity ("Opacity multiplier", Range(0, 3)) = 1
        _Width ("Physical width", Float) = 0.00035
        _MinPixels ("Min width in pixels", Float) = 1.5
        _Ink ("Ink per unit length", Float) = 0.00011
        _AlphaMax ("Max alpha", Range(0, 1)) = 0.9
        _Sheen ("Sheen", Range(0, 1)) = 0.3
        [HideInInspector] _Nails ("Nails", Float) = 256
        [HideInInspector] _Radius ("Radius", Float) = 0.5
        _LiftMin ("Lift min", Float) = 0.001
        _LiftMax ("Lift max", Float) = 0.006
        [Toggle] _ShadowMode ("Shadow mode", Float) = 0
        _ShadowOffset ("Shadow offset per lift (xy)", Vector) = (0.46, -0.51, 0, 0)
        _ShadowWidth ("Shadow width multiplier", Float) = 3
        _ShadowOpacity ("Shadow opacity multiplier", Float) = 0.35
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend DstColor Zero
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_VisTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _ThreadColor;
                float _Opacity;
                float _Width;
                float _MinPixels;
                float _Ink;
                float _AlphaMax;
                float _Sheen;
                float _Nails;
                float _Radius;
                float _LiftMin;
                float _LiftMax;
                float _ShadowMode;
                float4 _ShadowOffset;
                float _ShadowWidth;
                float _ShadowOpacity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 ab : TEXCOORD0;
                float2 ends : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float side : TEXCOORD0;
                float alpha : TEXCOORD1;
            };

            float Hash(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                int a = (int)(v.ab.x + 0.5);
                int b = (int)(v.ab.y + 0.5);
                float vis = LOAD_TEXTURE2D_LOD(_VisTex, int2(a, b), 0).r;
                if (vis <= 0.001)
                {
                    o.positionCS = 0;
                    o.side = 0;
                    o.alpha = 0;
                    return o;
                }

                float ta = a * TWO_PI / _Nails;
                float tb = b * TWO_PI / _Nails;
                float3 pa = float3(cos(ta), sin(ta), 0) * _Radius;
                float3 pb = float3(cos(tb), sin(tb), 0) * _Radius;
                // Smoothed winding: the thread end runs from nail a to nail b
                float3 p1 = lerp(pa, pb, smoothstep(0.0, 1.0, vis));
                float lift = lerp(_LiftMin, _LiftMax, Hash(float2(a, b)));
                float3 p = lerp(pa, p1, v.ends.x);

                bool shadow = _ShadowMode > 0.5;
                if (shadow)
                {
                    p.xy += _ShadowOffset.xy * lift;
                    p.z = -0.0004;
                }
                else p.z = -lift;

                float3 posWS = TransformObjectToWorld(p);
                // Size of a pixel in world units at the thread depth
                float depth = max(1e-4, -TransformWorldToView(posWS).z);
                float pxWorld = 2.0 * depth / (abs(UNITY_MATRIX_P[1][1]) * _ScreenParams.y);
                float width = max(_Width, _MinPixels * pxWorld);
                float alpha = min(_AlphaMax, _Ink / width) * _Opacity;
                if (shadow)
                {
                    width *= _ShadowWidth;
                    alpha *= _ShadowOpacity / _ShadowWidth;
                }

                float3 dirWS = TransformObjectToWorldDir(p1 - pa);
                float3 viewWS = GetWorldSpaceNormalizeViewDir(posWS);
                float3 sideWS = cross(dirWS, viewWS);
                sideWS = dot(sideWS, sideWS) > 1e-10 ? normalize(sideWS) : float3(0, 1, 0);
                posWS += sideWS * (v.ends.y * width * 0.5);

                o.positionCS = TransformWorldToHClip(posWS);
                o.side = v.ends.y;
                o.alpha = alpha;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // Round thread profile: denser in the middle; 1.5 brings the average across the width back to alpha
                half profile = saturate(1.0 - i.side * i.side);
                half a = min(0.97, i.alpha * profile * 1.5);
                half3 col = _ThreadColor.rgb + _Sheen * pow(1.0 - abs(i.side), 6.0) * 0.35;
                return half4(lerp(half3(1.0, 1.0, 1.0), col, a), 1.0);
            }
            ENDHLSL
        }
    }
}
