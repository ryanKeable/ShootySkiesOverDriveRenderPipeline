using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal.Internal
{
    // TODO: TAA
    // TODO: Motion blur
    /// <summary>
    /// Renders the post-processing effect stack.
    /// </summary>
    public class DelMarPostProcessingPass : ScriptableRenderPass
    {
        RenderTextureDescriptor m_Descriptor;
        RenderTextureDescriptor m_FinalDescriptor;
        RenderTargetHandle m_Source;
        RenderTargetHandle m_Destination;

        const string k_RenderPostProcessingTag = "Render PostProcessing Effects";

        PostProcessData m_Data;

        // Builtin effects settings
        Bloom m_Bloom;

        // Misc
        const int k_MaxPyramidSize = 8;
        readonly GraphicsFormat m_DefaultHDRFormat;
        bool m_ResetHistory;
        bool m_IsStereo;

        Material m_BloomMaterial;
        Material m_BlitMaterial;
        int tw;
        int th;
        int maxSize;
        int iterations;
        int mipCount;
        float threshold;
        float thresholdKnee;
        float thresholdNumerator;

        float scatter;

        const float bloomRenderSizeScalar = 0.5f;

        public DelMarPostProcessingPass(RenderPassEvent evt, PostProcessData data, Material blitMaterial = null, Material bloomMaterial = null)
        {
            renderPassEvent = evt;
            m_Data = data;
            m_BlitMaterial = blitMaterial;
            m_BloomMaterial = bloomMaterial;

            m_DefaultHDRFormat = GraphicsFormat.R8G8B8A8_SRGB;

            // Bloom pyramid shader ids - can't use a simple stackalloc in the bloom function as we
            // unfortunately need to allocate strings
            ShaderConstants._BloomMipUp = new int[k_MaxPyramidSize];
            ShaderConstants._BloomMipDown = new int[k_MaxPyramidSize];

            for (int i = 0; i < k_MaxPyramidSize; i++) {
                ShaderConstants._BloomMipUp[i] = Shader.PropertyToID("_BloomMipUp" + i);
                ShaderConstants._BloomMipDown[i] = Shader.PropertyToID("_BloomMipDown" + i);
            }

            m_ResetHistory = true;
        }

        public void Setup(in RenderTextureDescriptor baseDescriptor, in RenderTargetHandle source, in RenderTargetHandle destination)
        {
            m_Descriptor = baseDescriptor;
            m_FinalDescriptor = baseDescriptor;
            m_Source = source;
            m_Destination = destination;
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            if (m_Destination == RenderTargetHandle.CameraTarget)
                return;

            // THIS IS MY MSAA DESCRIPTOR!!

            var desc = cameraTextureDescriptor;
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;

            cmd.GetTemporaryRT(m_Destination.id, desc, FilterMode.Point);
        }

        public void ResetHistory()
        {
            m_ResetHistory = true;
        }

        public bool CanRunOnTile()
        {
            // Check builtin & user effects here
            return false;
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // Start by pre-fetching all builtin effect settings we need
            // Some of the color-grading settings are only used in the color grading lut pass
            var stack = VolumeManager.instance.stack;

            m_Bloom = stack.GetComponent<Bloom>();

            var cmd = CommandBufferPool.Get(k_RenderPostProcessingTag);
            Render(cmd, ref renderingData);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            m_ResetHistory = false;
        }

        RenderTextureDescriptor GetStereoCompatibleDescriptor()
            => GetStereoCompatibleDescriptor(m_Descriptor.width, m_Descriptor.height, m_Descriptor.graphicsFormat, m_Descriptor.depthBufferBits);

        RenderTextureDescriptor GetStereoCompatibleDescriptor(int width, int height, GraphicsFormat format, int depthBufferBits = 0)
        {
            // Inherit the VR setup from the camera descriptor
            var desc = m_Descriptor;
            desc.depthBufferBits = depthBufferBits;
            desc.msaaSamples = 1;
            desc.width = width;
            desc.height = height;
            desc.graphicsFormat = format;
            return desc;
        }

        void Render(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ref var cameraData = ref renderingData.cameraData;
            m_IsStereo = renderingData.cameraData.isStereoEnabled;

            // Don't use these directly unless you have a good reason to, use GetSource() and
            // GetDestination() instead
            int source = m_Source.id;

            // Utilities to simplify intermediate target management
            int GetSource() => source;

            // Setup projection matrix for cmd.DrawMesh()
            cmd.SetGlobalMatrix(ShaderConstants._FullscreenProjMat, GL.GetGPUProjectionMatrix(Matrix4x4.identity, true));

            // Setup projection matrix for cmd.DrawMesh()
            cmd.SetGlobalMatrix(ShaderConstants._FullscreenProjMat, GL.GetGPUProjectionMatrix(Matrix4x4.identity, true));

            ProfilingSampler uberSampler = new ProfilingSampler("Uber Sampler");
            ProfilingSampler bloomSampler = new ProfilingSampler("Bloom Sampler");

            using (new ProfilingScope(cmd, uberSampler)) {

                // Bloom goes second
                using (new ProfilingScope(cmd, bloomSampler))
                    SetupBloom(cmd, GetSource());

                // Done with Uber, blit it
                cmd.SetGlobalTexture("_BlitTex", GetSource());

                // we should never load or store our frame data as this is expensive on a tiled GPU. 

                // Note: We rendering to "camera target" we need to get the cameraData.targetTexture as this will get the targetTexture of the camera stack.
                // Overlay cameras need to output to the target described in the base camera while doing camera stack.
                RenderTargetIdentifier cameraTarget = m_Destination.Identifier(); // (m_Destination == RenderTargetHandle.CameraTarget) ? cameraTarget : m_Destination.Identifier();
                cmd.SetRenderTarget(cameraTarget, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare); //why are we storing?!

                // With camera stacking we not always resolve post to final screen as we might run post-processing in the middle of the stack.
                if (m_IsStereo) {
                    Blit(cmd, GetSource(), BuiltinRenderTextureType.CurrentActive, m_BlitMaterial);
                } else {
                    cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    cmd.SetViewport(cameraData.camera.pixelRect);
                    cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_BlitMaterial);
                    cmd.SetViewProjectionMatrices(cameraData.camera.worldToCameraMatrix, cameraData.camera.projectionMatrix);
                }

                // Cleanup
                cmd.ReleaseTemporaryRT(ShaderConstants._BloomMipUp[0]);
            }
        }

        private BuiltinRenderTextureType BlitDstDiscardContent(CommandBuffer cmd, RenderTargetIdentifier rt)
        {

            // I had always assumed we needed to store the frame data to access it later but this is incorrect.
            // storing refers to a per framebuffer per frame write of the blit
            // once the texture has been generated the colour is stored in the texture?

            cmd.SetRenderTarget(new RenderTargetIdentifier(rt, 0, CubemapFace.Unknown, -1),
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
            return BuiltinRenderTextureType.CurrentActive;
        }


        #region Bloom
        void ConfigureBloom()
        {

            tw = m_Descriptor.width >> 1;
            th = m_Descriptor.height >> 1;

            // Determine the iteration count
            maxSize = Mathf.Max(tw, th);
            iterations = Mathf.FloorToInt(Mathf.Log(maxSize, 2f) - 1);
            mipCount = Mathf.Clamp(iterations, 1, k_MaxPyramidSize);

            // Pre-filtering parameters
            threshold = Mathf.GammaToLinearSpace(m_Bloom.threshold.value);
            thresholdKnee = threshold * 0.5f;
            thresholdNumerator = 4.0f * thresholdKnee + 0.0001f;
            scatter = Mathf.Lerp(0.05f, 0.95f, m_Bloom.scatter.value);

            // Material setup
            m_BloomMaterial.SetVector(ShaderConstants._Bloom_Params, new Vector4(scatter, threshold, thresholdKnee, thresholdNumerator));
            m_BlitMaterial.SetFloat(ShaderConstants._Bloom_Intenisty, m_Bloom.intensity.value);
        }

        void SetupBloom(CommandBuffer cmd, int source)
        {
            ConfigureBloom();

            var desc = GetStereoCompatibleDescriptor(tw, th, m_DefaultHDRFormat);

            // bilinear performs first basic blur at half res
            // this way we do not discard too many pixels before the blur passes
            cmd.GetTemporaryRT(ShaderConstants._BloomMipDown[0], desc, FilterMode.Bilinear);
            cmd.GetTemporaryRT(ShaderConstants._BloomMipUp[0], desc, FilterMode.Bilinear);

            cmd.Blit(source, BlitDstDiscardContent(cmd, ShaderConstants._BloomMipDown[0]), m_BloomMaterial, 0);

            // Downsample - gaussian pyramid
            // we go down again before we blur
            int lastDown = ShaderConstants._BloomMipDown[0];
            for (int i = 1; i < mipCount - 1; i++) {
                tw = Mathf.Max(1, tw >> 1);
                th = Mathf.Max(1, th >> 1);

                int mipDown = ShaderConstants._BloomMipDown[i];
                int mipUp = ShaderConstants._BloomMipUp[i];

                desc.width = tw;
                desc.height = th;

                cmd.GetTemporaryRT(mipDown, desc, FilterMode.Bilinear);
                cmd.GetTemporaryRT(mipUp, desc, FilterMode.Bilinear);

                // Classic two pass gaussian blur - use mipUp as a temporary target
                //   First pass does 2x downsampling + 9-tap gaussian using a 5-tap filter + bilinear filtering
                //   Second pass does 9-tap gaussian using a 5-tap filter + bilinear filtering
                cmd.Blit(lastDown, BlitDstDiscardContent(cmd, mipUp), m_BloomMaterial, 1);
                cmd.Blit(mipUp, BlitDstDiscardContent(cmd, mipDown), m_BloomMaterial, 2);
                lastDown = mipDown;
            }

            for (int i = mipCount - 3; i >= 0; i--) {
                int lowMip = (i == mipCount - 2) ? ShaderConstants._BloomMipDown[i + 1] : ShaderConstants._BloomMipUp[i + 1];
                int highMip = ShaderConstants._BloomMipDown[i];
                // int highMip = (i == 0) ? ShaderConstants._BloomMipDown[2] : ShaderConstants._BloomMipDown[i];

                int dst = ShaderConstants._BloomMipUp[i];

                cmd.SetGlobalTexture(ShaderConstants._MainTexLowMip, lowMip);
                cmd.Blit(highMip, BlitDstDiscardContent(cmd, dst), m_BloomMaterial, 3);
            }

            // Cleanup
            for (int i = 0; i < k_MaxPyramidSize; i++) {
                cmd.ReleaseTemporaryRT(ShaderConstants._BloomMipDown[i]);
                if (i > 0) cmd.ReleaseTemporaryRT(ShaderConstants._BloomMipUp[i]);
            }


            cmd.SetGlobalTexture(ShaderConstants._Bloom_Texture, ShaderConstants._BloomMipUp[0]);

        }

        #endregion

        #region Internal utilities

        // Precomputed shader ids to same some CPU cycles (mostly affects mobile)
        static class ShaderConstants
        {
            public static readonly int _MainTexLowMip = Shader.PropertyToID("_MainTexLowMip");
            public static readonly int _Bloom_Params = Shader.PropertyToID("_Bloom_Params");
            public static readonly int _Bloom_Intenisty = Shader.PropertyToID("_Bloom_Intensity");
            public static readonly int _Bloom_RGBM = Shader.PropertyToID("_Bloom_RGBM");
            public static readonly int _Bloom_Texture = Shader.PropertyToID("_Bloom_Texture");

            public static readonly int _TempNoMSAATex = Shader.PropertyToID("_TempNoMSAATex");
            public static readonly int _FullscreenProjMat = Shader.PropertyToID("_FullscreenProjMat");
            public static readonly int _MSAACopyString = Shader.PropertyToID("_MSAACopyString");
            public static readonly int _BloomFilter = Shader.PropertyToID("_BloomFilter");
            public static int[] _BloomMipUp;
            public static int[] _BloomMipDown;

        }

        #endregion
    }
}
