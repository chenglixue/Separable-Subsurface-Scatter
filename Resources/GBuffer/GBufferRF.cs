using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine.Assertions;

public class GBufferRF : ScriptableRendererFeature
{
    [System.Serializable]
    public class PassSetting
    {
        public string profilerTag = "Separable Subsurface Scatter";
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingTransparents;
    }
    public PassSetting m_passSetting = new PassSetting();
    CustomRenderPass m_ScriptablePass;
    
    public override void Create()
    {
        m_ScriptablePass = new CustomRenderPass(m_passSetting);
    }
    
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        m_ScriptablePass.Setup((UniversalRenderer)renderer);
        renderer.EnqueuePass(m_ScriptablePass);
    }
    
    class CustomRenderPass : ScriptableRenderPass
    {
        private PassSetting       _passSetting;
        private UniversalRenderer _renderer;
        private Shader            _shader;
        private Material          _material;
        static ShaderTagId        _shaderTagUniversalGBuffer = new ShaderTagId("GBuffer");
        
        private RenderTextureDescriptor  _descriptor;
        private RenderTargetIdentifier   _cameraRT;
        private RenderTargetIdentifier[] _GBufferRTIs = new RenderTargetIdentifier[4];
        static class ShaderIDs
        {
            public static int m_GBuffer0ID    = Shader.PropertyToID("_GBuffer0");
            public static int m_GBuffer1ID    = Shader.PropertyToID("_GBuffer1");
            public static int m_GBuffer2ID    = Shader.PropertyToID("_GBuffer2");
            public static int m_GBuffer3ID    = Shader.PropertyToID("_GBuffer3");
        }
        
        public CustomRenderPass(PassSetting passSetting)
        {
            _passSetting = passSetting;
            this.renderPassEvent = _passSetting.passEvent;
                
            _shader = Shader.Find("Elysia/S_PBR");
            _material = new Material(_shader);
            Assert.IsTrue(_material != null);
        }
        public void Setup(ScriptableRenderer renderer)
        {
            _renderer = (UniversalRenderer)renderer;
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
        
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            _descriptor                 = renderingData.cameraData.cameraTargetDescriptor;
            _descriptor.msaaSamples     = 1;
            _descriptor.depthBufferBits = 0;

            InitRTI(ref _GBufferRTIs[0], ShaderIDs.m_GBuffer0ID, _descriptor, cmd,
                1, 1, RenderTextureFormat.ARGBFloat, 0, true, true, FilterMode.Point);
            InitRTI(ref _GBufferRTIs[1], ShaderIDs.m_GBuffer1ID, _descriptor, cmd,
                1, 1, RenderTextureFormat.ARGBFloat, 0, true, true, FilterMode.Point);
            InitRTI(ref _GBufferRTIs[2], ShaderIDs.m_GBuffer2ID, _descriptor, cmd,
                1, 1, RenderTextureFormat.ARGBFloat, 0, true, true, FilterMode.Point);
            InitRTI(ref _GBufferRTIs[3], ShaderIDs.m_GBuffer3ID, _descriptor, cmd,
                1, 1, RenderTextureFormat.ARGBFloat, 0, true, true, FilterMode.Point);
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(_passSetting.profilerTag);
            
            Assert.IsTrue(_renderer != null);
            {
                cmd.SetRenderTarget(_GBufferRTIs, _renderer.cameraDepthTarget);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                
                SortingCriteria _sortingCriteria = SortingCriteria.RenderQueue;
                DrawingSettings _drawingSettings = CreateDrawingSettings(_shaderTagUniversalGBuffer, ref renderingData, _sortingCriteria);
                FilteringSettings _filteringSettings = new FilteringSettings(RenderQueueRange.all);
                
                context.DrawRenderers(renderingData.cullResults, ref _drawingSettings, ref _filteringSettings);
            }
            
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(ShaderIDs.m_GBuffer0ID);
            cmd.ReleaseTemporaryRT(ShaderIDs.m_GBuffer1ID);
            cmd.ReleaseTemporaryRT(ShaderIDs.m_GBuffer2ID);
            cmd.ReleaseTemporaryRT(ShaderIDs.m_GBuffer3ID);
        }
    }
}


