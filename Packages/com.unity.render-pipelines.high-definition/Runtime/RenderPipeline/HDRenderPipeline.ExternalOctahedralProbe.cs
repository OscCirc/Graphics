using System;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        /// <summary>Uploads externally prefiltered GGX Oct radiance into HDRP's reflection probe atlas.</summary>
        /// <remarks>
        /// Call on the main thread outside render callbacks, after EnableExternalOctahedralCapture.
        /// The source is consumed immediately on the graphics queue and is not retained by the probe.
        /// This experimental path requires GGX-only filtering and a one-texel source border per mip.
        /// </remarks>
        /// <param name="probe">A Custom reflection probe opted into external capture.</param>
        /// <param name="source">The complete, prefiltered two-dimensional source Oct atlas.</param>
        /// <param name="layout">The source tile and its initialized roughness-mip range.</param>
        /// <param name="version">A nonzero content version independent of source texture identity.</param>
        /// <param name="capturePositionWS">The frozen world-space capture position used to render the source.</param>
        /// <returns>False if the atlas has no room; no new version is published.</returns>
        public bool UploadExternalOctahedralProbe(HDAdditionalReflectionData probe, Texture source,
            OctahedralProbeSourceLayout layout, uint version, Vector3 capturePositionWS)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!ReferenceEquals(RenderPipelineManager.currentPipeline, this))
                throw new InvalidOperationException("Upload through the currently active HDRP pipeline instance.");
            if (!probe.usesExternalOctahedralCapture)
                throw new InvalidOperationException("The probe must opt into external Oct capture in Custom mode.");
            if (probe.IsTurnedOff()) throw new InvalidOperationException("The probe resolution is set to Off.");
            if (version == 0) throw new ArgumentOutOfRangeException(nameof(version), "Version zero is reserved for invalidated data.");
            if (source.dimension != TextureDimension.Tex2D)
                throw new ArgumentException("The source must be a two-dimensional Oct atlas.", nameof(source));
            if (source is RenderTexture rt && !rt.IsCreated())
                throw new ArgumentException("The source RenderTexture has not been created.", nameof(source));
            layout.ValidateTexture(source.width, source.height, source.mipmapCount);
            if (m_TextureCaches.reflectionProbeTextureCache == null)
                throw new InvalidOperationException("HDRP reflection resources have not been initialized.");

            CommandBuffer cmd = CommandBufferPool.Get("Upload external Oct reflection probe");
            try
            {
                if (!m_TextureCaches.reflectionProbeTextureCache.UploadExternalOctahedralProbe(cmd, probe, source, layout, version))
                {
                    probe.RequestExternalOctahedralCapture();
                    return false;
                }
                Graphics.ExecuteCommandBuffer(cmd);
                probe.PublishExternalOctahedralCapture(version, capturePositionWS);
                return true;
            }
            finally { CommandBufferPool.Release(cmd); }
        }
    }
}
