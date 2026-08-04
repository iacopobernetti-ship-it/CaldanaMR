Shader "Custom/VertexColorURPThermal"
{
    Properties
    {
        _Mode ("Render Mode (0=Scientific, 1=Visual, 2=VR)", Range(0,2)) = 1
        _ColorIntensity ("Color Intensity", Range(0,1)) = 0.45
        _EmissionStrength ("Emission Strength", Range(0,1)) = 0.20
        _ShadowStrength ("Shadow Strength", Range(0,1)) = 0.65
        _MinShadowBrightness ("Min Shadow Brightness", Range(0,1)) = 0.35
        _AmbientStrength ("Ambient Strength", Range(0,2)) = 0.75
        _Smoothness ("Smoothness", Range(0,1)) = 0.10
        _Metallic ("Metallic", Range(0,1)) = 0.00
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float4 tangentWS   : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
                float4 color       : COLOR;
                half   fogFactor   : TEXCOORD4;
            };

            half _Mode;
            half _ColorIntensity;
            half _EmissionStrength;
            half _ShadowStrength;
            half _MinShadowBrightness;
            half _AmbientStrength;
            half _Smoothness;
            half _Metallic;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = positionInputs.positionCS;
                OUT.positionWS = positionInputs.positionWS;
                OUT.normalWS = normalInputs.normalWS;
                OUT.tangentWS = float4(normalInputs.tangentWS.xyz, IN.tangentOS.w);
                OUT.shadowCoord = GetShadowCoord(positionInputs);
                OUT.color = IN.color;
                OUT.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS);
                float3 baseColor = saturate(IN.color.rgb);

                half colorIntensity = _ColorIntensity;
                half emissionStrength = _EmissionStrength;
                half shadowStrength = _ShadowStrength;
                half minShadowBrightness = _MinShadowBrightness;
                half ambientStrength = _AmbientStrength;

                if (_Mode < 0.5)
                {
                    colorIntensity = 0.20;
                    emissionStrength = 0.05;
                    shadowStrength = 0.35;
                    minShadowBrightness = 0.55;
                    ambientStrength = 0.35;
                }
                else if (_Mode < 1.5)
                {
                    colorIntensity = _ColorIntensity;
                    emissionStrength = _EmissionStrength;
                    shadowStrength = _ShadowStrength;
                    minShadowBrightness = _MinShadowBrightness;
                    ambientStrength = _AmbientStrength;
                }
                else
                {
                    colorIntensity = 0.35;
                    emissionStrength = 0.25;
                    shadowStrength = 0.45;
                    minShadowBrightness = 0.45;
                    ambientStrength = 0.90;
                }

                SurfaceData surfaceData;
                surfaceData.albedo = baseColor * colorIntensity;
                surfaceData.alpha = 1.0;
                surfaceData.metallic = _Metallic;
                surfaceData.specular = half3(0.0, 0.0, 0.0);
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = half3(0.0, 0.0, 1.0);
                surfaceData.emission = baseColor * emissionStrength;
                surfaceData.occlusion = 1.0;
                surfaceData.clearCoatMask = 0.0;
                surfaceData.clearCoatSmoothness = 0.0;

                InputData inputData;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord = IN.shadowCoord;
                inputData.fogCoord = IN.fogFactor;
                inputData.vertexLighting = VertexLighting(IN.positionWS, normalWS);
                inputData.bakedGI = SampleSH(normalWS) * ambientStrength;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1,1,1,1);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);

                Light mainLight = GetMainLight(IN.shadowCoord);
                half shadowAtten = lerp(minShadowBrightness, 1.0, mainLight.shadowAttenuation);
                half shadowBlend = lerp(1.0, shadowAtten, shadowStrength);

                color.rgb *= shadowBlend;

                color.rgb += baseColor * emissionStrength * 0.5;

                color.rgb = saturate(color.rgb);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);

                return color;
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}