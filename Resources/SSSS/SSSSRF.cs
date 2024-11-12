using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine.Assertions;

namespace Elysia
{
    public class SSSSRF : ScriptableRendererFeature
    {
        [System.Serializable]
        public class PassSetting
        {
            public string profilerTag = "Separable Subsurface Scatter";
            public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingTransparents;
            
            [Range(0,3)]
            public float m_subsurfaceScaler = 0.25f;
            [Tooltip("rgb控制次表面颜色，a控制次表面强度")]
            public Color m_subsurfaceColor;
            [Tooltip("光线随距离增加而衰减的程度, 数值越小表示对应方向上光线衰减得越快，数值越大表示衰减得越慢")]
            public Color m_subsurfaceFalloff;

            [Range(0, 255)]
            public int m_refValue;
        }

        public PassSetting m_passSetting = new PassSetting();
        SSSSRenderPass m_SSSSPass;
    
        public override void Create()
        {
            m_SSSSPass = new SSSSRenderPass(m_passSetting);
        }
    
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            m_SSSSPass.ConfigureInput(ScriptableRenderPassInput.Motion | ScriptableRenderPassInput.Depth);
            
            m_SSSSPass.Setup(renderer);
            renderer.EnqueuePass(m_SSSSPass);
        }
        
        
        
        
        class SSSSRenderPass : ScriptableRenderPass
        {
            # region Declaration
            private PassSetting       _passSetting;
            private UniversalRenderer _renderer;
            private Shader            _SSSSShader;
            private Material          _SSSSMaterial;
            
            private RenderTextureDescriptor  _descriptor;
            private RenderTargetIdentifier   _cameraRT;
            private RenderTargetIdentifier   _tempRT;
            private RenderTargetIdentifier   _blurRT;
            
            private List<Vector4>     _kernelList = new List<Vector4>();
            static class ShaderIDs
            {
                public static int m_mainTexID      = Shader.PropertyToID("_MainTex");
                public static int m_tempTexID      = Shader.PropertyToID("_TempTex");
                public static int m_blurTexID      = Shader.PropertyToID("_BlurTex");
                public static int m_SSSSScale      = Shader.PropertyToID("_SSSSScale");
                public static int m_kernel         = Shader.PropertyToID("_KernelArray");
            }
            #endregion

            #region Setup
            public SSSSRenderPass(PassSetting passSetting)
            {
                _passSetting = passSetting;
                this.renderPassEvent = _passSetting.passEvent;
                
                _SSSSShader = Shader.Find("Elysia/S_SSSS");
                _SSSSMaterial = new Material(_SSSSShader);
                Assert.IsTrue(_SSSSMaterial != null);
            }

            public void Setup(ScriptableRenderer renderer)
            {
                _renderer = (UniversalRenderer)renderer;
            }
            
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                _descriptor                 = renderingData.cameraData.cameraTargetDescriptor;
                _descriptor.msaaSamples     = 1;
                _descriptor.depthBufferBits = 0;
                
                InitRTI(ref _tempRT, ShaderIDs.m_tempTexID, _descriptor, cmd,
                    1, 1, RenderTextureFormat.DefaultHDR, 0, true, true, FilterMode.Point);
                InitRTI(ref _blurRT, ShaderIDs.m_blurTexID, _descriptor, cmd,
                    1, 1, RenderTextureFormat.DefaultHDR, 0, true, true, FilterMode.Point);
                
                Vector3 subsurfaceColor = new Vector3(_passSetting.m_subsurfaceColor.r, _passSetting.m_subsurfaceColor.g, _passSetting.m_subsurfaceColor.b);
                Vector3 subsurfaceFalloff = new Vector3(_passSetting.m_subsurfaceFalloff.r, _passSetting.m_subsurfaceFalloff.g, _passSetting.m_subsurfaceFalloff.b);
                SSSSLibrary.SeparableSSS_ComputeKernel(ref _kernelList, 25, subsurfaceColor, subsurfaceFalloff);
                
                _SSSSMaterial.SetInt("_RefValue", _passSetting.m_refValue);
                SetupViewProjectionMatrix(renderingData.cameraData);
                _SSSSMaterial.SetVectorArray(ShaderIDs.m_kernel, _kernelList);
                _SSSSMaterial.SetFloat(ShaderIDs.m_SSSSScale, _passSetting.m_subsurfaceScaler);
                _SSSSMaterial.SetFloat("_DistanceToProjectionWindow", 1.0f / (0.5f * Mathf.Tan(math.radians(renderingData.cameraData.camera.fieldOfView))));
                
                var specularIBLTex = Resources.Load<Cubemap>("Tex/GI/Prefilter_Full02");
                var specularFactorLUTTex = Resources.Load<Texture2D>("Tex/GI/LUT");
                cmd.SetGlobalTexture("_SpecularIBLTex", specularIBLTex);
                cmd.SetGlobalTexture("_SpecularFactorLUTTex", specularFactorLUTTex);
            }
            
            void InitRTI(ref RenderTargetIdentifier RTI, int texID, RenderTextureDescriptor descriptor, CommandBuffer cmd,
                int downSampleWidth, int downSampleHeight, RenderTextureFormat colorFormat, 
                int depthBufferBits, bool isUseMipmap, bool isAutoGenerateMips,
                FilterMode filterMode)
            {
                descriptor.width           /= downSampleWidth;
                descriptor.height          /= downSampleHeight;
                descriptor.colorFormat      = colorFormat;
                descriptor.depthBufferBits  = depthBufferBits;
                descriptor.useMipMap        = isUseMipmap;
                descriptor.autoGenerateMips = isAutoGenerateMips;
            
                RTI = new RenderTargetIdentifier(texID);
                cmd.GetTemporaryRT(texID, descriptor, filterMode);
                cmd.SetGlobalTexture(texID, RTI);
            }
            
            void SetupViewProjectionMatrix(CameraData cameraData)
            {
                var viewMatrix = cameraData.GetViewMatrix();
                var projectionMatrix = cameraData.GetGPUProjectionMatrix();
                _SSSSMaterial.SetMatrix("Matrix_V", viewMatrix);
                _SSSSMaterial.SetMatrix("Matrix_I_V", viewMatrix.inverse);
                _SSSSMaterial.SetMatrix("Matrix_P", projectionMatrix);
                _SSSSMaterial.SetMatrix("Matrix_I_P", projectionMatrix.inverse);
            
                var _Curr_Matrix_VP = projectionMatrix * viewMatrix;
                _SSSSMaterial.SetMatrix("Matrix_VP", _Curr_Matrix_VP);
                _SSSSMaterial.SetMatrix("Matrix_I_VP", _Curr_Matrix_VP.inverse);
            }
            #endregion
            
            #region Execute
            void BlitSp(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier dest,
                RenderTargetIdentifier depth, Material mat, int passIndex, MaterialPropertyBlock mpb = null)
            {
                cmd.SetGlobalTexture(Shader.PropertyToID("_MainTex"), source);
                cmd.SetRenderTarget(dest, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, 
                    depth, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
                cmd.ClearRenderTarget(false, false, Color.clear);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
                cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, mat, 0, passIndex, mpb);
            }
            
            private void DoBlurHorizon(ref CommandBuffer cmd)
            {
                cmd.BeginSample("SSSS Blur Horizon");
                
                BlitSp(cmd, _cameraRT, _tempRT, _renderer.cameraDepthTarget,
                    _SSSSMaterial, 0);
                cmd.SetGlobalTexture(ShaderIDs.m_mainTexID, _tempRT);
                
                cmd.EndSample("SSSS Blur Horizon");
            }
            
            private void DoBlurVertical(ref CommandBuffer cmd)
            {
                cmd.BeginSample("SSSS Blur Vertical");
                
                cmd.Blit(_tempRT, _blurRT, _SSSSMaterial, 1);
                cmd.SetGlobalTexture(ShaderIDs.m_mainTexID, _blurRT);
                cmd.EndSample("SSSS Blur Vertical");
            }

            private void DoSpecularPass(ref CommandBuffer cmd)
            {
                cmd.Blit(_blurRT, _tempRT, _SSSSMaterial, 2);
                cmd.SetGlobalTexture(ShaderIDs.m_mainTexID, _cameraRT);
            }
            
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                CommandBuffer cmd = CommandBufferPool.Get(_passSetting.profilerTag);
                
                Assert.IsTrue(_renderer != null);
                
                {
                    _cameraRT = _renderer.cameraColorTarget;

                    if (renderingData.cameraData.isSceneViewCamera == false)
                    {
                        cmd.SetGlobalTexture(ShaderIDs.m_mainTexID, _cameraRT);
                        DoBlurHorizon(ref cmd);
                        DoBlurVertical(ref cmd);
                        DoSpecularPass(ref cmd);
                        cmd.Blit(_tempRT, _cameraRT);
                        cmd.SetGlobalTexture(ShaderIDs.m_mainTexID, _cameraRT);
                    }
                }
                
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        
            public override void OnCameraCleanup(CommandBuffer cmd)
            {
                cmd.ReleaseTemporaryRT(ShaderIDs.m_tempTexID);
                cmd.ReleaseTemporaryRT(ShaderIDs.m_blurTexID);
            }
            #endregion
        }
    }   
}


