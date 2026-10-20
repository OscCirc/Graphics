using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        readonly Dictionary<(int, GraphicsFormat), RTHandle> m_OctahedralCaptureFaces = new Dictionary<(int, GraphicsFormat), RTHandle>();
        bool m_OctahedralRenderRequestSucceeded;
        OctahedralProbeRaster m_OctahedralProbeRaster;
        OctahedralProbeRaster octahedralProbeRaster => m_OctahedralProbeRaster ??
            (m_OctahedralProbeRaster = new OctahedralProbeRaster(runtimeShaders.octahedralProbeRadiancePS));

        RTHandle GetOctahedralCaptureFace(int size, GraphicsFormat format)
        {
            var key = (size, format);
            if (!m_OctahedralCaptureFaces.TryGetValue(key, out var face))
            {
                var target = new RenderTexture(size, size, 0, format)
                {
                    name = "Shared HDRP Oct probe capture face", useMipMap = false,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                };
                if (!target.Create())
                {
                    CoreUtils.Destroy(target);
                    throw new InvalidOperationException("Unable to allocate the shared Oct probe capture face.");
                }
                face = RTHandles.Alloc(target, transferOwnership: true);
                m_OctahedralCaptureFaces.Add(key, face);
            }
            return face;
        }

        void CompleteOctahedralProbeFace(in RenderRequest request, ScriptableRenderContext context, CommandBuffer cmd)
        {
            var probe = request.octahedralProbe;
            if (probe == null || !probe.isActiveAndEnabled || !probe.usesOctahedralRealtime) return;
            if (!request.octahedralFaceValid || !m_OctahedralRenderRequestSucceeded ||
                !ReferenceEquals(request.octahedralPending, probe.pendingOctahedralRadiance))
            {
                RetryOctahedralProbeFace(request);
                return;
            }
            try
            {
                octahedralProbeRaster.CopyFace(cmd, request.octahedralCaptureFace,
                    probe.pendingOctahedralRadiance, request.octahedralFace);
                // Execute on the same queue before the next request reuses this shared face.
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                probe.RecordOctahedralFace(request.octahedralFace, request.hdCamera.camera.transform.position);
            }
            catch
            {
                RetryOctahedralProbeFace(request);
                throw;
            }
        }

        void RetryOctahedralProbeFace(in RenderRequest request)
        {
            var probe = request.octahedralProbe;
            if (probe == null || !probe.isActiveAndEnabled || !probe.usesOctahedralRealtime) return;
            var face = ProbeRenderStepsExt.FromCubeFace(request.octahedralFace);
            probe.BeginOctahedralRenderSteps(face);
            probe.RepeatRenderSteps(face | ProbeRenderSteps.IncrementRenderCount);
        }

        /// <summary>Records conversion of a complete Cube into raw Oct radiance, with one border texel per side.</summary>
        /// <remarks>This does not perform roughness filtering. Use linear, square power-of-two destination textures.</remarks>
        public void ConvertReflectionProbeToOctahedral(CommandBuffer cmd, Texture cube, RenderTexture destination)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (!ReferenceEquals(RenderPipelineManager.currentPipeline, this))
                throw new InvalidOperationException("Convert through the currently active HDRP pipeline instance.");
            if (cube == null || cube.dimension != TextureDimension.Cube)
                throw new ArgumentException("A Cubemap source is required.", nameof(cube));
            if (!HDProbe.IsValidRawOctTexture(destination) || !destination.IsCreated())
                throw new ArgumentException("Create a square power-of-two Oct target of at least eight texels.", nameof(destination));
            octahedralProbeRaster.FromCube(cmd, cube, destination);
        }

        void CleanupOctahedralProbeCapture()
        {
            foreach (var face in m_OctahedralCaptureFaces.Values) face.Release();
            m_OctahedralCaptureFaces.Clear();
            m_OctahedralProbeRaster?.Dispose();
            m_OctahedralProbeRaster = null;
        }
    }
}
