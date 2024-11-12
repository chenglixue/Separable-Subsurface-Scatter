#pragma once
#include "Assets/Resources/Library/Common.hlsl"
#include "Assets/Resources/Library/BRDF.hlsl"

#define _Sample_Nums 25

cbuffer UnityPerMaterial
{
    int    _RefValue;
    float  _DistanceToProjectionWindow;
    
    float4 _CameraDepthTexture_TexelSize;
    
    float4 _KernelArray[_Sample_Nums];
    half   _SSSSScale;
}

Texture2D<float4>  _CameraColorTexture;
Texture2D<float>  _CameraDepthTexture;
Texture2D<float4> _MainTex;
Texture2D<float3> _NoiseTex;
Texture2D<float4> _GBuffer0;
Texture2D<float4> _GBuffer1;
Texture2D<float4> _GBuffer2;
Texture2D<float4> _GBuffer3;
samplerCUBE _SpecularIBLTex;
Texture2D<float3> _SpecularFactorLUTTex;

struct VSInput
{
    float4 positionOS : POSITION;

    float2 uv         : TEXCOORD0;
};

struct PSInput
{
    float2 uv   : TEXCOORD0;
    
    float4 positionCS  : SV_POSITION;
};

struct PSOutput
{
    float4      color           : SV_TARGET;
};

PSInput VS(VSInput i)
{
    PSInput o = (PSInput)0;

    o.uv = i.uv;

    o.positionCS = mul(UNITY_MATRIX_MVP, i.positionOS);

    return o;
}

float GetDeviceDepth(float2 uv)
{
    return _CameraDepthTexture.SampleLevel(Smp_ClampU_ClampV_Point, uv, 0).r;
}
float GetLinearEyeDepth(float rawDepth)
{
    return LinearEyeDepth(rawDepth, _ZBufferParams);
}

float3 SSSSBlur(float3 sceneColor, float2 uv, float2 SSSSIntensity)
{
    float eyeDepth = GetLinearEyeDepth(GetDeviceDepth(uv));
    float blurLength = _DistanceToProjectionWindow / eyeDepth;

    float2 uvOffset = blurLength * SSSSIntensity;
    float3 blurColor = sceneColor * _KernelArray[0].rgb;

    [unroll(64)]
    for(int i = 1; i < _Sample_Nums; ++i)
    {
        float2 SSSSUV = uv + uvOffset * _KernelArray[i].a;
        
        float3 SSSSSceneColor = _MainTex.SampleLevel(Smp_ClampU_ClampV_Linear, SSSSUV, 0).rgb;
        float  SSSSEyeDepth = _CameraDepthTexture.SampleLevel(Smp_ClampU_ClampV_Linear, SSSSUV, 0);

        float DPTimes300 = _DistanceToProjectionWindow * 300;    // 经验参数
        float SSSSSCale = saturate(DPTimes300 * SSSSIntensity * abs(eyeDepth - SSSSEyeDepth));
        SSSSSceneColor = lerp(SSSSSceneColor, sceneColor, SSSSSCale);
        blurColor += _KernelArray[i].rgb * SSSSSceneColor;
    }

    return blurColor;
}


uint UnpackMaterialFlags(float packedMaterialFlags)
{
    return uint((packedMaterialFlags * 255.0h) + 0.5h);
}
void InitBRDFData(float2 uv, Light light, out MyBRDFData brdfData, out int materialFlags, out float3 reflectDirWS)
{
    float4 GBuffer0 = _GBuffer0.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);
    float4 GBuffer1 = _GBuffer1.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);
    float4 GBuffer2 = _GBuffer2.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);
    float4 GBuffer3 = _GBuffer3.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);

    float  rawDepth     = _CameraDepthTexture.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);
    float4 positionNDC  = float4(uv * 2 - 1, rawDepth, 1);
    float4 positionWS   = mul(Matrix_I_VP, positionNDC);
    positionWS /= positionWS.w;
    float3 lightDir     = SafeNormalize(light.direction);
    float3 viewDir      = SafeNormalize(GetCameraPositionWS() - positionWS);
    float3 halfVector   = SafeNormalize(viewDir + lightDir);

    materialFlags = UnpackMaterialFlags(GBuffer1.a);

    brdfData.albedo     = GBuffer0.rgb;
    brdfData.opacity    = GBuffer0.a;
    brdfData.emission   = 0;
    brdfData.specular   = 0;
    brdfData.metallic   = GBuffer1.r;
    brdfData.roughness  = GBuffer1.g;
    brdfData.roughness2 = pow2(brdfData.roughness);
    brdfData.AO         = GBuffer1.b;
    brdfData.normal     = GBuffer2.rgb;

    brdfData.F0 = lerp(0.04, brdfData.albedo, brdfData.metallic);
    brdfData.radiance = light.color;
    
    brdfData.halfVector = halfVector;
    brdfData.NoL = max(dot(brdfData.normal, lightDir), FLT_EPS);
    brdfData.NoV = max(dot(brdfData.normal, viewDir), FLT_EPS);
    brdfData.NoH = max(dot(brdfData.normal, halfVector), FLT_EPS);
    brdfData.HoV = max(dot(halfVector, viewDir), FLT_EPS);
    brdfData.HoL = max(dot(halfVector, lightDir), FLT_EPS);
    brdfData.HoX = 0;
    brdfData.HoY = 0;

    brdfData.LobeA = GBuffer2.a;
    brdfData.LobeB = GBuffer3.a;

    reflectDirWS = reflect(-viewDir, brdfData.normal);
}

float3 DualSpecularGGX(float AverageRoughness, float Lobe0Roughness, float Lobe1Roughness, float LobeMix, MyBRDFData brdfData)
{
    float AverageAlpha2 = Pow4(AverageRoughness);
    float Lobe0Alpha2 = Pow4(Lobe0Roughness);
    float Lobe1Alpha2 = Pow4(Lobe1Roughness);

    // Generalized microfacet specular
    float D = lerp(NDF_GGX(Lobe0Alpha2, brdfData.NoH), NDF_GGX(Lobe1Alpha2, brdfData.NoH), LobeMix);
    float G = Vis_SmithJointApprox(AverageAlpha2, brdfData.NoV, brdfData.NoL);
    float3 F = SchlickFresnel(brdfData.HoV, brdfData.F0);

    return D * G * F;
}
float3 ShadeDirectSpecular(MyBRDFData brdfData)
{
    float lobeARoughness = brdfData.roughness * brdfData.LobeA;
    lobeARoughness = lerp(1.f, lobeARoughness, saturate(brdfData.opacity * 10.0f));
    float lobeBRoughness = brdfData.roughness * brdfData.LobeB;
    lobeBRoughness = lerp(1.f, lobeBRoughness, saturate(brdfData.opacity * 10.0f));
    float lobeMix = 0.15f;

    float3 specular = DualSpecularGGX(brdfData.roughness, lobeARoughness, lobeBRoughness, lobeMix, brdfData);
    specular *= brdfData.NoL;

    return specular;
}
float3 ShadeIndirectSpecular(MyBRDFData brdfData, float3 reflectDir)
{
    float rgh                = brdfData.roughness * (1.7 - 0.7 * brdfData.roughness);
    float lod                = 6.f * rgh;
    float3 GISpecularColor   = texCUBElod(_SpecularIBLTex, float4(reflectDir, lod)).rgb;
    float3 GISpecularFactor  = _SpecularFactorLUTTex.SampleLevel(Smp_RepeatU_RepeatV_Linear, float2(brdfData.NoV, brdfData.roughness), 0).rgb;
    float3 GISpecular        = (GISpecularFactor.r * brdfData.F0 + GISpecularFactor.g) * GISpecularColor;

    return GISpecular;
}