// One lit material for a whole palette.
//
// This project's meshes are flat-shaded and untextured: colour is the only thing distinguishing a
// plaster wall from a tiled roof from a painted shutter. URP/Lit cannot read vertex colours, so until
// this existed every tint had to be its own material — and a material is a submesh, and a submesh is a
// draw call. A town tile carried twelve of them, and ReportDrawCallBudget said so.
//
// Everything here is the standard URP forward-lit path. The only thing that is not stock is one
// multiply in the fragment shader: albedo = _BaseColor * vertex colour. That is the whole feature.
//
// Hand-written rather than authored in Shader Graph on purpose. A .shader is HLSL in a text file, so it
// diffs and reviews like the rest of this project; a .shadergraph is packed binary-ish YAML, which is
// exactly the kind of asset the conventions here say not to hand-write and which nobody can read in a
// pull request.
Shader "Horizon/VertexTintLit"
{
    Properties
    {
        _BaseColor("Base Colour", Color) = (1,1,1,1)
        _Smoothness("Smoothness", Range(0,1)) = 0.1
        _Metallic("Metallic", Range(0,1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            // The keywords that actually matter on a mobile forward renderer with one shadowed
            // directional light and baked lightmaps. Leaving them out does not make the shader smaller,
            // it makes it silently ignore the lighting the rest of the world is using.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _Smoothness;
                float _Metallic;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 colour     : COLOR;
                float2 lightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float4 colour      : TEXCOORD2;
                float  fogFactor   : TEXCOORD3;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 4);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // --- Wind.
            //
            // The sway mask is the vertex colour's alpha, inverted: 1 - a. Everything in this project
            // writes 255, so the default reads as rigid and terrain, buildings and roads stay still
            // without anyone marking them. See VegetationMeshBuffer.ApplySway.
            //
            // _HorizonWind is xyz = direction times strength, w = time scale. Written once a frame by
            // Horizon.Game's WindDirector, so every plant and every pass agrees about the weather.
            float4 _HorizonWind;

            float3 HorizonSway(float3 positionWS, float alpha)
            {
                float sway = 1.0 - alpha;
                if (sway <= 0.001)
                {
                    return positionWS;
                }

                // Phase from world position, so neighbouring plants are never in step. Two frequencies
                // that do not divide into each other, or the whole wood breathes as one object.
                float phase = positionWS.x * 0.35 + positionWS.z * 0.27;
                float t = _Time.y * _HorizonWind.w;

                float gust = sin(t + phase) * 0.65 + sin(t * 1.73 + phase * 2.31) * 0.35;

                // Across the wind as well as along it, or every tree leans in one plane and the wood
                // reads as a flag rather than as foliage.
                float3 along = _HorizonWind.xyz;
                float3 across = float3(-along.z, 0.0, along.x);

                return positionWS + (along * gust + across * gust * 0.35) * sway;
            }

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS);

                float3 swayed = HorizonSway(position.positionWS, input.colour.a);

                output.positionCS = TransformWorldToHClip(swayed);
                output.positionWS = swayed;
                output.normalWS = normal.normalWS;
                output.colour = input.colour;
                output.fogFactor = ComputeFogFactor(position.positionCS.z);

                OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, output.lightmapUV);
                OUTPUT_SH(normal.normalWS, output.vertexSH);

                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                SurfaceData surface = (SurfaceData)0;

                // The one line this shader exists for.
                surface.albedo = _BaseColor.rgb * input.colour.rgb;
                surface.alpha = 1.0h;
                surface.metallic = _Metallic;
                surface.smoothness = _Smoothness;
                surface.occlusion = 1.0h;

                InputData lighting = (InputData)0;
                lighting.positionWS = input.positionWS;
                lighting.normalWS = normalize(input.normalWS);
                lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                lighting.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                lighting.fogCoord = input.fogFactor;
                lighting.bakedGI = SAMPLE_GI(input.lightmapUV, input.vertexSH, lighting.normalWS);
                lighting.shadowMask = half4(1, 1, 1, 1);

                half4 colour = UniversalFragmentPBR(lighting, surface);
                colour.rgb = MixFog(colour.rgb, input.fogFactor);

                return colour;
            }
            ENDHLSL
        }

        // Buildings cast shadows and the terrain receives them, so this pass is not optional — without
        // it the town would sit in its own light while every tree beside it had a shadow.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _Smoothness;
                float _Metallic;
            CBUFFER_END

            float3 _LightDirection;
            float3 _LightPosition;

            // --- Wind.
            //
            // The sway mask is the vertex colour's alpha, inverted: 1 - a. Everything in this project
            // writes 255, so the default reads as rigid and terrain, buildings and roads stay still
            // without anyone marking them. See VegetationMeshBuffer.ApplySway.
            //
            // _HorizonWind is xyz = direction times strength, w = time scale. Written once a frame by
            // Horizon.Game's WindDirector, so every plant and every pass agrees about the weather.
            float4 _HorizonWind;

            float3 HorizonSway(float3 positionWS, float alpha)
            {
                float sway = 1.0 - alpha;
                if (sway <= 0.001)
                {
                    return positionWS;
                }

                // Phase from world position, so neighbouring plants are never in step. Two frequencies
                // that do not divide into each other, or the whole wood breathes as one object.
                float phase = positionWS.x * 0.35 + positionWS.z * 0.27;
                float t = _Time.y * _HorizonWind.w;

                float gust = sin(t + phase) * 0.65 + sin(t * 1.73 + phase * 2.31) * 0.35;

                // Across the wind as well as along it, or every tree leans in one plane and the wood
                // reads as a flag rather than as foliage.
                float3 along = _HorizonWind.xyz;
                float3 across = float3(-along.z, 0.0, along.x);

                return positionWS + (along * gust + across * gust * 0.35) * sway;
            }


            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;

                // Read here only so the shadow can sway with the tree. Without it the wind moves the
                // canopy and leaves its shadow standing, which is worse than no wind at all.
                float4 colour     : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            ShadowVaryings ShadowVertex(ShadowAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = HorizonSway(
                    TransformObjectToWorld(input.positionOS.xyz), input.colour.a);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 toLight = normalize(_LightPosition - positionWS);
                #else
                    float3 toLight = _LightDirection;
                #endif

                // ApplyShadowBias hands back a nudged *world* position, not a clip one — the bias is what
                // stops a surface shadowing itself into acne, and it is applied before the projection
                // rather than after it.
                positionWS = ApplyShadowBias(positionWS, normalWS, toLight);
                output.positionCS = TransformWorldToHClip(positionWS);

                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return output;
            }

            half4 ShadowFragment(ShadowVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // URP wants a depth pass for anything that might end up in the depth prepass or a depth texture.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _Smoothness;
                float _Metallic;
            CBUFFER_END

            // --- Wind.
            //
            // The sway mask is the vertex colour's alpha, inverted: 1 - a. Everything in this project
            // writes 255, so the default reads as rigid and terrain, buildings and roads stay still
            // without anyone marking them. See VegetationMeshBuffer.ApplySway.
            //
            // _HorizonWind is xyz = direction times strength, w = time scale. Written once a frame by
            // Horizon.Game's WindDirector, so every plant and every pass agrees about the weather.
            float4 _HorizonWind;

            float3 HorizonSway(float3 positionWS, float alpha)
            {
                float sway = 1.0 - alpha;
                if (sway <= 0.001)
                {
                    return positionWS;
                }

                // Phase from world position, so neighbouring plants are never in step. Two frequencies
                // that do not divide into each other, or the whole wood breathes as one object.
                float phase = positionWS.x * 0.35 + positionWS.z * 0.27;
                float t = _Time.y * _HorizonWind.w;

                float gust = sin(t + phase) * 0.65 + sin(t * 1.73 + phase * 2.31) * 0.35;

                // Across the wind as well as along it, or every tree leans in one plane and the wood
                // reads as a flag rather than as foliage.
                float3 along = _HorizonWind.xyz;
                float3 across = float3(-along.z, 0.0, along.x);

                return positionWS + (along * gust + across * gust * 0.35) * sway;
            }

            struct DepthAttributes
            {
                float4 positionOS : POSITION;

                // As in the shadow pass: depth has to agree with what was drawn.
                float4 colour     : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings DepthVertex(DepthAttributes input)
            {
                DepthVaryings output = (DepthVaryings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = HorizonSway(
                    TransformObjectToWorld(input.positionOS.xyz), input.colour.a);

                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 DepthFragment(DepthVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
