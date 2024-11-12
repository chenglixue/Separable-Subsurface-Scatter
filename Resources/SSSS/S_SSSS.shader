Shader "Elysia/S_SSSS"
{
    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off
        
        HLSLINCLUDE
        #include "Assets/Resources/SSSS/SSSS.hlsl"
        ENDHLSL

        Pass
        {
            Name "SSSS Horizon Blur"
            Stencil
            {
                Ref [_RefValue]
                Comp Equal
                pass Keep
            }

            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment HorizonBlur

            void HorizonBlur(PSInput i, out PSOutput o)
            {
                float2 uv = i.uv;

                float  SSSSIntensity = _SSSSScale * _CameraDepthTexture_TexelSize.x;
                float4 sceneColor = _MainTex.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);
                float3 blurColor = SSSSBlur(sceneColor.rgb, i.uv, float2(SSSSIntensity, 0));

                o.color = float4(blurColor, sceneColor.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "SSSS Vertical Blur"
            
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment VerticalBlur

            void VerticalBlur(PSInput i, out PSOutput o)
            {
                float2 uv = i.uv;

                float  SSSSIntensity = _SSSSScale * _CameraDepthTexture_TexelSize.y;
                float4 sceneColor = _MainTex.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);
                float3 blurColor = SSSSBlur(sceneColor.rgb, i.uv, float2(0, SSSSIntensity));

                o.color = float4(blurColor, sceneColor.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name"SSSS Specular"
            
            HLSLPROGRAM
            #include "Assets/Resources/Library/BRDF.hlsl"
            #pragma vertex VS
            #pragma fragment Specular
            void Specular(PSInput i, out PSOutput o)
            {
                float2 uv       = i.uv;
                Light mainLight = GetMainLight();

                MyBRDFData brdfData = (MyBRDFData)0;
                uint materialID     = FLT_MAX;
                float3 reflectDir = 0;
                InitBRDFData(uv, mainLight, brdfData, materialID, reflectDir);

                float3 result = 0;
                if(materialID == 1)
                {
                    float3 sourceColor = _MainTex.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).rgb;
                    float3 directLight   = ShadeDirectSpecular(brdfData);
                    float3 indirectLight = ShadeIndirectSpecular(brdfData, reflectDir);
                    result += sourceColor + directLight + indirectLight;
                    
                    o.color = float4(result, brdfData.opacity);
                }
                else
                {
                    o.color.rgb = _CameraColorTexture.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).rgb;
                }
            }
            ENDHLSL
        }
    }
}
