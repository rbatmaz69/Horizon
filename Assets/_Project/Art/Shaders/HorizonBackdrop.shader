// The ground beyond the ground.
//
// This world is a 200 m corridor of terrain either side of every road — TerrainShape.CorridorWidth —
// hanging in nothing. WorldPreview_Overview shows it plainly: a ribbon of hillside in an empty plane.
// While driving, exponential-squared fog hides the edge, so the corridor never reads as an edge. What
// it reads as instead is a world with no distance in it: every straight ends in a flat wall of fog
// colour, and the concept this project is written against asks for wide vistas.
//
// So there is a ring of mountain silhouettes standing behind everything, and nothing else about the
// world has to change to get one. It is not terrain, nothing streams it, nothing collides with it, and
// it costs one draw call and a few hundred triangles for the whole world.
//
// WHY IT IS NOT LIT AND NOT FOGGED. Fog is what it is standing in for. A ridge at the edge of the
// corridor resolves to exactly RenderSettings.fogColor and then stops existing; this draws the colour
// the fog would have gone on producing if there had been anything out there to produce it. Running the
// world's fog over it would therefore erase it, and lighting it would make a distant range respond to
// a sun 3 km closer to it than the range is wide. The colours come from the clock instead, through
// globals, on the same rule the sky shader states at length: a skybox and a backdrop are both things
// with no per-instance state to hang a MaterialPropertyBlock on, and writing the material would leave
// M_Backdrop modified in a player's working tree the moment they drove at dusk.
//
// WHY IT IS ORDINARY OPAQUE GEOMETRY AND WRITES DEPTH. The obvious build is the opposite — a
// Background queue with ZWrite off, so it can never occlude anything — and it does not work, for a
// reason no picture would have explained. URP draws the skybox *after* the opaque queues, filling
// wherever depth was never written; a backdrop that writes no depth is therefore painted over by the
// sky in its entirety, every frame, and what ships is a feature that builds, logs its triangle count
// and cannot be seen. So it is opaque geometry at the far end of the queue, and the far plane is what
// has to contain it — see BackdropBuilder.MinimumFarPlane, which every camera in this project reads.
//
// It does occlude, then, and the rings are placed where that costs nothing: past 640 m the fog has
// taken better than nine tenths of anything real, and by construction there is nothing real out there
// at all — TerrainShape.CorridorWidth is 200 m.
Shader "Horizon/Backdrop"
{
    Properties
    {
        // Authoring only. None of these is written at run time.
        _PeakLift("Peak Lightening", Range(0,1)) = 0.40
        _Emergence("Rise out of the Haze", Range(0.5,6)) = 2.2
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry+500"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        // Cull Off rather than a winding rule. The camera stands inside every ring, so exactly one
        // face of each curtain is ever visible, and getting the winding wrong on a ring built from an
        // angle sweep is a silhouette that vanishes at one bearing and nowhere else — the kind of fault
        // that only shows in a picture taken from the one place nobody photographs. Three hundred and
        // eighty triangles do not care.
        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "Backdrop"

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            // No multi_compile_fog, for the reason at the top of this file.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _PeakLift;
                float _Emergence;
            CBUFFER_END

            // Written every frame by TimeOfDayController.PushSky, and by nothing else.
            float4 _HorizonBackdropNear; // rgb linear. A ridge close enough to have a colour of its own.
            float4 _HorizonBackdropFar;  // rgb linear. The air itself — exactly RenderSettings.fogColor.
            float4 _HorizonBackdropPeak; // rgb linear. What a summit tends towards.

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 colour     : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 mix        : TEXCOORD0; // x depth through the rings, y height up the silhouette
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                // r is which ring this vertex belongs to, 0 nearest and 1 furthest. g is how high this
                // vertex stands against the tallest summit in the whole backdrop — 0 along the bottom
                // hem of every curtain, and up to 1 at the highest point of the highest ring, so a low
                // saddle never reaches the value a peak does. Both are baked by BackdropBuilder; nothing
                // here is a function of world position, so neither shifts when the rig moves.
                output.mix = float2(input.colour.r, input.colour.g);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 air = _HorizonBackdropFar.rgb;

                // Distance first: a far ring is closer to the air's own colour, which is what makes
                // three flat silhouettes read as depth rather than as three flat silhouettes.
                float3 body = lerp(_HorizonBackdropNear.rgb, air, input.mix.x);

                // Then the hem. Every curtain hangs a long way below its own ridge line, and the bottom
                // of it is the air exactly — which is what stops the backdrop having a visible bottom
                // edge. It needs one: over the strait, or from a summit, there is no terrain at all
                // below the horizon to hide behind, and a ring that simply stopped would draw a hard
                // horizontal line across open water. Ending in the colour the sky already is there means
                // there is nothing to see.
                body = lerp(air, body, saturate(input.mix.y * _Emergence));

                // Then height. A summit catches more of whatever is above the horizon, and squaring it
                // keeps the lightening on the tops rather than spread evenly up a slope. This world has
                // no texture anywhere; without this a ridge is a cut-out.
                body = lerp(body, _HorizonBackdropPeak.rgb,
                            saturate(_PeakLift * input.mix.y * input.mix.y * (1.0 - input.mix.x)));

                return half4(body, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
