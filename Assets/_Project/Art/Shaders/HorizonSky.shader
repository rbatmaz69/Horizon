// One sky, for every hour and every weather.
//
// There used to be two, and neither of them was driven. The fair sky was Unity's stock
// Skybox/Procedural, which reads the sun's direction and nothing else this project decides; the bad
// one was a fixed grey ramp on a 64x32 texture, swapped in above Overcast 0.60 with hysteresis. So
// Hazy at 0.45 never changed the sky at all, and the grey one read exactly the same at midnight as at
// noon — a bug that had been recorded in CLAUDE.md and could not be fixed inside a painted texture.
//
// Everything here is one continuum instead. Overcast thickens the cloud and flattens the dome; the
// hour moves four colours and the sun; and there is nothing left to swap.
//
// Hand-written for the reason Horizon/VertexTintLit gives: a .shader is HLSL in a text file and
// reviews like the rest of the project.
//
// THE ONE STRUCTURAL RULE. Everything the clock drives is a *global* uniform, declared outside
// Properties and outside UnityPerMaterial, exactly as _HorizonWind is in the other shader. A skybox
// has no renderer, so MaterialPropertyBlock is not available to it; and writing the material instead
// would leave M_Sky modified in the working tree the moment a player tried the rain, which is the
// hazard TownLights, WetSurfaces and QualityDirector all document. If a name below ever appears in
// Properties as well, the material's serialized value shadows the global and the sky renders a
// plausible static dome — which is why ValidateSky asserts that none of them do.
Shader "Horizon/Sky"
{
    Properties
    {
        // Authoring only. None of these is written at run time.
        _CloudTex("Cloud Field (R broad, G detail)", 2D) = "black" {}

        _CloudScale("Cloud Scale", Float) = 0.24
        _CloudDetailScale("Cloud Detail Scale", Float) = 2.7
        _CloudDetailWeight("Cloud Detail Weight", Range(0,1)) = 0.24

        // sin of the lowest elevation the cloud plane is evaluated at, and the band it fades in over.
        _CloudHorizon("Cloud Ray Floor", Range(0.005,0.3)) = 0.045
        _CloudRise("Cloud Rise", Range(0.01,0.4)) = 0.09

        // Where the coverage threshold sits at Overcast 0, and where at Overcast 1.
        _CoverClear("Cover, Clear", Range(0,1)) = 0.75
        _CoverFull("Cover, Overcast", Range(0,1)) = 0.15
        _EdgeClear("Edge Softness, Clear", Range(0.01,1)) = 0.22
        _EdgeFull("Edge Softness, Overcast", Range(0.01,1)) = 0.34

        // Three flat tones across a cloud, and the width of the two steps between them. Never zero:
        // a hard step on a mipped, minified field crawls on a moving sky.
        _ToneSplitLow("Tone Split, Low", Range(0,1)) = 0.42
        _ToneSplitHigh("Tone Split, High", Range(0,1)) = 0.68
        _ToneSplitWidth("Tone Split Width", Range(0.005,0.2)) = 0.03

        _HorizonTightness("Horizon Tightness", Float) = 3.6

        _SunInner("Sun Disc Inner", Float) = 0.012
        _SunOuter("Sun Disc Outer", Float) = 0.019
        _SunHaloChord("Sun Halo Reach", Float) = 0.42
        _SunHaloStrength("Sun Halo Strength", Range(0,1)) = 0.55
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            Name "Sky"

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            // No multi_compile_fog, deliberately. A skybox that fogs is a skybox that is the fog
            // colour, and against a 600 m far plane with the fog wall inside it the sky is the only
            // thing in the frame the air is not in front of. That is the whole reason it matters.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _CloudTex_ST;
                float _CloudScale;
                float _CloudDetailScale;
                float _CloudDetailWeight;
                float _CloudHorizon;
                float _CloudRise;
                float _CoverClear;
                float _CoverFull;
                float _EdgeClear;
                float _EdgeFull;
                float _ToneSplitLow;
                float _ToneSplitHigh;
                float _ToneSplitWidth;
                float _HorizonTightness;
                float _SunInner;
                float _SunOuter;
                float _SunHaloChord;
                float _SunHaloStrength;
            CBUFFER_END

            TEXTURE2D(_CloudTex);
            SAMPLER(sampler_CloudTex);

            // Written every frame by TimeOfDayController.PushSky, and by nothing else. See the note at
            // the top of this file for why these are globals rather than material properties.
            float4 _HorizonSkyHorizon;    // rgb linear. The same colour as RenderSettings.fogColor.
            float4 _HorizonSkyZenith;     // rgb linear.
            float4 _HorizonSkyCloudLit;   // rgb linear. Cloud tops, lit by the sun.
            float4 _HorizonSkyCloudShade; // rgb linear. Undersides, lit by the sky.
            float4 _HorizonSun;           // xyz direction TO the sun, w disc brightness (linear).
            float4 _HorizonSunTint;       // rgb linear sun colour, a how far up the sun is.
            float4 _HorizonSkyDrift;      // xy tap one's UV offset, zw tap two's. From WindDirector.
            float _HorizonOvercast;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 directionOS : TEXCOORD0;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                // On the skybox mesh, object space is the view direction. Unity's own Panoramic and
                // Procedural skyboxes both do exactly this, and it holds for the reflection cubemap
                // render too, which draws the same mesh with six view matrices.
                output.directionOS = input.positionOS.xyz;

                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.directionOS);

                // --- The dome.
                //
                // saturate rather than abs, so everything below the skyline is exactly the horizon
                // colour — which is exactly RenderSettings.fogColor, which is exactly what every
                // distant ridge in this world dissolves into. Any other colour down there is a seam
                // under the horizon, and the preview frames that turn fog off are where it would show.
                float up = saturate(direction.y);
                float band = exp2(-up * _HorizonTightness);
                float3 sky = lerp(_HorizonSkyZenith.rgb, _HorizonSkyHorizon.rgb, band);

                // --- The halo round the sun.
                //
                // The chord, not 1 - dot. For a sun disc under a degree across the dot form is about
                // 1e-4, which is under half's useful resolution — correct in the editor and blocky or
                // missing on a phone. length(direction - sunward) for the same angle is 0.014.
                float chord = length(direction - _HorizonSun.xyz);
                float halo = saturate(1.0 - chord / max(_SunHaloChord, 0.001));
                halo = halo * halo * halo;

                // A lerp and never an add. Written additively, a halo on a bright afternoon sky lifts
                // the whole sunset quadrant over the bloom knee; written as a lerp it is bounded by the
                // sun's own colour and the sky cannot bloom at all. See PushSky for the arithmetic.
                sky = lerp(sky, _HorizonSunTint.rgb, halo * _SunHaloStrength * _HorizonSunTint.a);

                // --- The cloud, projected on a plane rather than wrapped on the dome.
                //
                // Chosen over a lat-long lookup and over noise in the fragment shader, and the reason
                // that matters most is not the ALU count: perspective foreshortening falls out of the
                // projection for nothing, so cloud cells converge towards the skyline exactly as they
                // do overhead. That convergence is what reads as sky at a 60 degree field of view.
                //
                // The ray floor keeps dir.xz/dir.y from blowing up at the horizon, where the UV
                // derivatives would otherwise explode into moire. It is never seen, because _CloudRise
                // has faded the cloud out over the last few degrees before it — which is also what real
                // cloud does, receding into haze.
                float denominator = max(direction.y, _CloudHorizon);
                float2 plane = direction.xz / denominator;

                float2 uv1 = plane * _CloudScale + _HorizonSkyDrift.xy;
                float2 uv2 = plane * (_CloudScale * _CloudDetailScale) + _HorizonSkyDrift.zw;

                half broad = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, uv1).r;
                half detail = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, uv2).g;
                half field = lerp(broad, detail, _CloudDetailWeight);

                half cover = lerp(_CoverClear, _CoverFull, _HorizonOvercast);
                half edge = lerp(_EdgeClear, _EdgeFull, _HorizonOvercast);

                half alpha = smoothstep(cover, cover + edge, field);
                alpha *= smoothstep(0.0, _CloudRise, direction.y);

                // Three flat tones, because this world is flat shaded and a soft gradient inside a
                // cloud would be the one smoothly shaded thing in the frame. Narrow smoothsteps rather
                // than steps: a hard edge on a minified mipped field crawls once the sky is moving.
                half tone =
                    smoothstep(_ToneSplitLow - _ToneSplitWidth,
                               _ToneSplitLow + _ToneSplitWidth, field) * 0.5
                  + smoothstep(_ToneSplitHigh - _ToneSplitWidth,
                               _ToneSplitHigh + _ToneSplitWidth, field) * 0.5;

                half3 cloud = lerp(_HorizonSkyCloudShade.rgb, _HorizonSkyCloudLit.rgb, tone);
                half3 colour = lerp(sky, cloud, alpha);

                // --- The disc. The only additive term in this shader, and therefore the only thing in
                // the sky that can reach the bloom threshold — which is the whole design: the sun
                // blooms and nothing else does, by construction rather than by discipline.
                //
                // (1 - alpha) costs nothing, the value is already in a register, and it does two jobs:
                // a cloud drifting across the sun occludes it, and at full overcast the disc is gone
                // without a second knob deciding so.
                half disc = 1.0 - smoothstep(_SunInner, _SunOuter, chord);
                colour += _HorizonSunTint.rgb * (_HorizonSun.w * disc * (1.0 - alpha));

                return half4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
