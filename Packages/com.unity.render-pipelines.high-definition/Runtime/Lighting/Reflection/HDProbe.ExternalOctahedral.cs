using System;

namespace UnityEngine.Rendering.HighDefinition
{
    public abstract partial class HDProbe
    {
        // Runtime-only opt-in. No source texture is owned or retained by the probe.
        bool m_ExternalOctahedralCapture;
        bool m_ExternalOctahedralReady;
        bool m_ExternalOctahedralUpdateRequested;
        uint m_ExternalOctahedralVersion;
        Vector3 m_ExternalOctahedralCapturePosition;

        internal bool usesExternalOctahedralCapture => m_ExternalOctahedralCapture && mode == ProbeSettings.Mode.Custom &&
            type == ProbeSettings.ProbeType.ReflectionProbe;
        internal bool hasExternalOctahedralData => usesExternalOctahedralCapture && m_ExternalOctahedralReady;
        internal uint externalOctahedralVersion => m_ExternalOctahedralVersion;
        internal Vector3 externalOctahedralCapturePosition => m_ExternalOctahedralCapturePosition;

        /// <summary>Whether atlas eviction or invalidation has requested a new external capture.</summary>
        public bool externalOctahedralUpdateRequested => usesExternalOctahedralCapture && m_ExternalOctahedralUpdateRequested;

        /// <summary>Enables externally scheduled Oct uploads for a Custom reflection probe.</summary>
        /// <remarks>Use HDRenderPipeline.UploadExternalOctahedralProbe after capture. The provider owns scheduling and source resources.</remarks>
        public void EnableExternalOctahedralCapture()
        {
            if (mode != ProbeSettings.Mode.Custom || type != ProbeSettings.ProbeType.ReflectionProbe)
                throw new InvalidOperationException("External Oct capture requires a Custom, non-planar reflection probe.");
            if (m_ExternalOctahedralCapture)
                throw new InvalidOperationException("This probe already has an external capture provider.");
            // A probe switched from Realtime no longer needs its old owned capture target.
            m_RealtimeTexture?.Release();
            m_RealtimeTexture = null;
            m_ExternalOctahedralCapture = true;
            m_ExternalOctahedralReady = false;
            m_ExternalOctahedralUpdateRequested = true;
            m_ExternalOctahedralVersion = 0;
        }

        /// <summary>Returns the probe to the ordinary Custom-texture path without changing its serialized settings.</summary>
        public void DisableExternalOctahedralCapture()
        {
            m_ExternalOctahedralCapture = false;
            m_ExternalOctahedralReady = false;
            m_ExternalOctahedralUpdateRequested = false;
            m_ExternalOctahedralVersion = 0;
        }

        internal void PublishExternalOctahedralCapture(uint version, Vector3 capturePositionWS)
        {
            m_ExternalOctahedralVersion = version;
            m_ExternalOctahedralCapturePosition = capturePositionWS;
            m_ExternalOctahedralReady = true;
            m_ExternalOctahedralUpdateRequested = false;
        }

        internal void RequestExternalOctahedralCapture() => m_ExternalOctahedralUpdateRequested = true;
    }
}
