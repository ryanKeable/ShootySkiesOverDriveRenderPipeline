using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Default renderer for Universal RP.
    /// This renderer is supported on all Universal RP supported platforms.
    /// It uses a classic forward rendering strategy with per-object light culling.
    /// </summary>
    public sealed class DelMarRenderer : ScriptableRenderer
    {
        const int k_DepthStencilBufferBits = 32;
        const string k_CreateCameraTextures = "Create Camera Texture";

        DrawObjectsPass m_RenderOpaqueForwardPass;
        DrawSkyboxPass m_DrawSkyboxPass;
        DrawObjectsPass m_RenderTransparentForwardPass;

        RenderTargetHandle m_ActiveCameraColorAttachment;
        RenderTargetHandle m_ActiveCameraDepthAttachment;
        RenderTargetHandle m_CameraColorAttachment;
        RenderTargetHandle m_ColorGradingLut;


        public DelMarPostProcessingPass m_DelMarPostProcessingPass;
        StencilState m_DefaultStencilState;

        Material m_UberMaterial;
        Material m_BloomMaterial;

        ForwardLights m_ForwardLights;

        public DelMarRenderer(DelMarRendererData data) : base(data)
        {
            m_UberMaterial = data.materialToBlit_Uber;
            m_BloomMaterial = data.materialToBlit_Bloom;

            StencilStateData stencilData = data.defaultStencilState;
            m_DefaultStencilState = StencilState.defaultValue;
            m_DefaultStencilState.enabled = stencilData.overrideStencilState;
            m_DefaultStencilState.SetCompareFunction(stencilData.stencilCompareFunction);
            m_DefaultStencilState.SetPassOperation(stencilData.passOperation);
            m_DefaultStencilState.SetFailOperation(stencilData.failOperation);
            m_DefaultStencilState.SetZFailOperation(stencilData.zFailOperation);

            // Note: Since all custom render passes inject first and we have stable sort,
            // we inject the builtin passes in the before events.
            m_RenderOpaqueForwardPass = new DrawObjectsPass("Render Opaques", true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference);
            m_DrawSkyboxPass = new DrawSkyboxPass(RenderPassEvent.BeforeRenderingSkybox);
            m_RenderTransparentForwardPass = new DrawObjectsPass("Render Transparents", false, RenderPassEvent.BeforeRenderingTransparents, RenderQueueRange.transparent, data.transparentLayerMask, m_DefaultStencilState, stencilData.stencilReference);


            // RenderTexture format depends on camera and pipeline (HDR, non HDR, etc)
            // Samples (MSAA) depend on camera and pipeline
            m_CameraColorAttachment.Init("_CameraColorTexture");

            m_ForwardLights = new ForwardLights();

            m_DelMarPostProcessingPass = new DelMarPostProcessingPass(RenderPassEvent.BeforeRenderingPostProcessing, data.postProcessData, m_UberMaterial, m_BloomMaterial);

        }

        /// <inheritdoc />
        public override void Setup(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            ref CameraData cameraData = ref renderingData.cameraData;
            RenderTextureDescriptor cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;

            // intermediary scale -- discontinueing this as it is unsued
            // cameraTargetDescriptor.width = (int)((float)renderingData.cameraData.cameraTargetDescriptor.width * intermediaryRenderScale);
            // cameraTargetDescriptor.height = (int)((float)renderingData.cameraData.cameraTargetDescriptor.height * intermediaryRenderScale);

            bool isStereoEnabled = cameraData.isStereoEnabled;
            bool createColorTexture = true;

            // Configure all settings require to start a new camera stack (base camera only)
            m_ActiveCameraColorAttachment = (createColorTexture) ? m_CameraColorAttachment : RenderTargetHandle.CameraTarget;
            m_ActiveCameraDepthAttachment = RenderTargetHandle.CameraTarget;

            CreateCameraRenderTarget(context, cameraTargetDescriptor);

            // if rendering to intermediate render texture we don't have to create msaa backbuffer
            int backbufferMsaaSamples = 1; //(intermediateRenderTexture) ? 1 : cameraTargetDescriptor.msaaSamples;

            if (Camera.main == camera && camera.cameraType == CameraType.Game && cameraData.targetTexture == null)
                SetupBackbufferFormat(backbufferMsaaSamples, isStereoEnabled);

            ConfigureCameraTarget(m_ActiveCameraColorAttachment.Identifier(), m_ActiveCameraDepthAttachment.Identifier());


            EnqueuePass(m_RenderOpaqueForwardPass);

            if (camera.clearFlags == CameraClearFlags.Skybox && RenderSettings.skybox != null)
                EnqueuePass(m_DrawSkyboxPass);

            EnqueuePass(m_RenderTransparentForwardPass);

            // ENABLE UNITY POST PROCESSING
            #region UnityPP

            bool applyPostProcessing = cameraData.postProcessEnabled;
            // Post-processing will resolve to final target. No need for final blit pass.
            if (applyPostProcessing) {
                bool anyPostProcessing = renderingData.postProcessingEnabled; //anyPostProcessing
                var destination = RenderTargetHandle.CameraTarget; //applyFinalPostProcessing ? m_AfterPostProcessColor : RenderTargetHandle.CameraTarget;

                m_DelMarPostProcessingPass.Setup(cameraTargetDescriptor, m_ActiveCameraColorAttachment, destination);
                EnqueuePass(m_DelMarPostProcessingPass);
            }

            #endregion

#if UNITY_EDITOR
            if (renderingData.cameraData.isSceneViewCamera) {
                // Scene view camera should always resolve target (not stacked)
                Assertions.Assert.IsTrue(true, "Editor camera must resolve target upon finish rendering.");
            }
#endif
        }

        public override void SetupLights(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_ForwardLights.Setup(context, ref renderingData);
        }

        /// <inheritdoc />
        public override void FinishRendering(CommandBuffer cmd)
        {
            if (m_ActiveCameraColorAttachment != RenderTargetHandle.CameraTarget) {
                cmd.ReleaseTemporaryRT(m_ActiveCameraColorAttachment.id);
                m_ActiveCameraColorAttachment = RenderTargetHandle.CameraTarget;
            }

            if (m_ActiveCameraDepthAttachment != RenderTargetHandle.CameraTarget) {
                cmd.ReleaseTemporaryRT(m_ActiveCameraDepthAttachment.id);
                m_ActiveCameraDepthAttachment = RenderTargetHandle.CameraTarget;
            }
        }

        void CreateCameraRenderTarget(ScriptableRenderContext context, RenderTextureDescriptor descriptor)
        {
            CommandBuffer cmd = CommandBufferPool.Get(k_CreateCameraTextures);

            int msaaSamples = descriptor.msaaSamples;

            if (m_ActiveCameraColorAttachment != RenderTargetHandle.CameraTarget) {
                bool useDepthRenderBuffer = m_ActiveCameraDepthAttachment == RenderTargetHandle.CameraTarget;
                var colorDescriptor = descriptor;

                colorDescriptor.depthBufferBits = k_DepthStencilBufferBits; //(useDepthRenderBuffer) ? k_DepthStencilBufferBits : 0;
                cmd.GetTemporaryRT(m_ActiveCameraColorAttachment.id, colorDescriptor, FilterMode.Bilinear);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void SetupBackbufferFormat(int msaaSamples, bool stereo)
        {
#if ENABLE_VR && ENABLE_VR_MODULE
            bool msaaSampleCountHasChanged = false;
            int currentQualitySettingsSampleCount = QualitySettings.antiAliasing;
            if (currentQualitySettingsSampleCount != msaaSamples &&
                !(currentQualitySettingsSampleCount == 0 && msaaSamples == 1))
            {
                msaaSampleCountHasChanged = true;
            }

            // There's no exposed API to control how a backbuffer is created with MSAA
            // By settings antiAliasing we match what the amount of samples in camera data with backbuffer
            // We only do this for the main camera and this only takes effect in the beginning of next frame.
            // This settings should not be changed on a frame basis so that's fine.
            QualitySettings.antiAliasing = msaaSamples;

            if (stereo && msaaSampleCountHasChanged)
                XR.XRDevice.UpdateEyeTextureMSAASetting();
#else
            QualitySettings.antiAliasing = msaaSamples;
#endif
        }
    }
}
