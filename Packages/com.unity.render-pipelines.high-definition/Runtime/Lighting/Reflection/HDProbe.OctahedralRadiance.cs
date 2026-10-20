using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    public abstract partial class HDProbe
    {
        [SerializeField] bool m_UseOctahedralRadiance;
        RTHandle m_PendingOctahedralRadiance;
        ProbeFaceCompletion m_OctahedralFaces;
        ProbeCapturePositionSettings m_OctahedralPositionSettings;
        bool m_HasOctahedralPositionSettings;
        Vector3 m_OctahedralCapturePosition;
        Vector3 m_PendingOctahedralCapturePosition;

        /// <summary>Stores baked/realtime reflection radiance as a raw Oct texture. Planar probes are unaffected.</summary>
        /// <remarks>Raw Oct sources use linear radiance, a square power-of-two size and one border texel per side.
        /// Custom Texture2D sources are interpreted as raw Oct regardless of this capture/output setting.</remarks>
        public bool useOctahedralRadiance
        {
            get => m_UseOctahedralRadiance;
            set
            {
                if (m_UseOctahedralRadiance == value) return;
                bool hadPendingCapture = m_PendingOctahedralRadiance != null || HasRemainingRenderSteps();
                m_UseOctahedralRadiance = value;
                ReleaseOctahedralCapture();
                if (type == ProbeSettings.ProbeType.ReflectionProbe)
                {
                    m_RealtimeTexture?.Release();
                    m_RealtimeTexture = null;
                    m_RealtimeRenderCount = 0;
                    m_RemainingRenderSteps = ProbeRenderSteps.None;
                    m_HasPendingRenderRequest = false;
                    // Preserve an outstanding request, but do not synthesize a new OnDemand update.
                    if (hadPendingCapture) RequestRenderNextUpdate();
                }
            }
        }

        internal bool usesOctahedralRealtime => type == ProbeSettings.ProbeType.ReflectionProbe &&
            mode == ProbeSettings.Mode.Realtime && m_UseOctahedralRadiance;
        internal bool hasOctahedralRadiance => type == ProbeSettings.ProbeType.ReflectionProbe &&
            texture != null && texture.dimension == TextureDimension.Tex2D && !usesExternalOctahedralCapture;
        internal Vector3 octahedralCapturePosition => m_OctahedralCapturePosition;
        internal RenderTexture pendingOctahedralRadiance => m_PendingOctahedralRadiance;

        internal static bool IsValidRawOctTexture(Texture source) => source != null &&
            source.dimension == TextureDimension.Tex2D && source.width == source.height &&
            source.width >= 8 && Mathf.IsPowerOfTwo(source.width) && !GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat) &&
            (!(source is RenderTexture rt) || rt.IsCreated());

        internal ProbeCapturePositionSettings GetOctahedralPositionSettings(Transform viewer)
        {
            if (!m_HasOctahedralPositionSettings)
            {
                m_OctahedralPositionSettings = ProbeCapturePositionSettings.ComputeFrom(this, viewer);
                m_HasOctahedralPositionSettings = true;
            }
            return m_OctahedralPositionSettings;
        }

        internal void AllocateOctahedralCapture(GraphicsFormat format)
        {
            int size = Mathf.Max(64, (int)cubeResolution * 2);
            if (m_PendingOctahedralRadiance != null && (m_PendingOctahedralRadiance.rt.width != size ||
                m_PendingOctahedralRadiance.rt.graphicsFormat != format))
            {
                ReleaseOctahedralCapture();
                RepeatRenderSteps(ProbeRenderSteps.ReflectionProbeMask);
            }
            if (m_PendingOctahedralRadiance != null) return;
            var target = new RenderTexture(size, size, 0, format)
            {
                name = $"Probe pending raw Oct ({name})", useMipMap = false,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (!target.Create())
            {
                CoreUtils.Destroy(target);
                throw new InvalidOperationException("Unable to allocate pending Oct reflection radiance.");
            }
            m_PendingOctahedralRadiance = RTHandles.Alloc(target, transferOwnership: true);
            m_OctahedralFaces = default;
        }

        internal void RecordOctahedralFace(CubemapFace face, Vector3 capturePosition)
        {
            m_OctahedralFaces.Record((int)face);
            // All views use the frozen capture settings; publish the actual camera position.
            m_PendingOctahedralCapturePosition = capturePosition;
            TryPublishOctahedralCapture();
        }

        internal void RequestOctahedralPublication()
        {
            m_OctahedralFaces.RequestPublication();
            TryPublishOctahedralCapture();
        }

        internal void BeginOctahedralRenderSteps(ProbeRenderSteps steps)
            => m_OctahedralFaces.InvalidateFaces((int)steps & 63);

        void TryPublishOctahedralCapture()
        {
            if (!m_OctahedralFaces.canPublish || m_PendingOctahedralRadiance == null) return;
            // Publish only after all executed faces and the scheduler's completion step agree.
            // The previous published texture remains untouched throughout a partial capture.
            var previous = m_RealtimeTexture;
            m_OctahedralCapturePosition = m_PendingOctahedralCapturePosition;
            m_RealtimeTexture = m_PendingOctahedralRadiance;
            m_PendingOctahedralRadiance = null;
            previous?.Release();
            if (++m_RealtimeRenderCount == 0) ++m_RealtimeRenderCount;
            m_RealtimeTexture.rt.IncrementUpdateCount();
            m_OctahedralFaces = default;
            m_HasOctahedralPositionSettings = false;
        }

        internal void ReleaseOctahedralCapture()
        {
            m_PendingOctahedralRadiance?.Release();
            m_PendingOctahedralRadiance = null;
            m_OctahedralFaces = default;
            m_HasOctahedralPositionSettings = false;
        }
    }
}
