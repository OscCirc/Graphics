using System;

namespace UnityEngine.Rendering.HighDefinition.Experimental
{
    // Assign a disabled capture Camera and probe transforms. One probe is processed per LateUpdate.
    // The atlas is available for a custom shader through SampleSharedOctProbe; built-in HDRP probe shading is unchanged.
    public sealed class SharedOctahedralProbeExample : MonoBehaviour
    {
        public ComputeShader filter;
        public Camera captureCamera;
        public Transform[] probes = Array.Empty<Transform>();
        public bool captureOneFacePerFrame;
        public bool useSingleTileWorkspace;
        [Range(16, 256)] public int faceSize = 128;
        [Range(32, 512)] public int tileSize = 256;

        public SharedOctahedralProbeAtlas atlas { get; private set; }
        public Vector3 lastCapturePosition { get; private set; }
        public event Action<int, uint> probeUpdated;
        // Optional bridge hook. Runs once per capture, before views are generated.
        public Action<Transform, Camera, bool> configureCaptureCamera;
        int m_NextProbe, m_NextFace;
        uint m_Version;
        int m_ActiveProbeId;
        Vector3 m_CapturePosition;
        Quaternion m_CaptureRotation;
        Camera m_WorkCamera;

        static readonly Vector3[] kDirections = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        static readonly Vector3[] kUp = { Vector3.up, Vector3.up, Vector3.back, Vector3.forward, Vector3.up, Vector3.up };

        void OnValidate()
        {
            faceSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(faceSize), 16, 256);
            tileSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(tileSize), 32, 512);
        }

        void LateUpdate()
        {
            if (filter == null || captureCamera == null || probes == null || probes.Length == 0) return;
            if (atlas == null)
            {
                int row = useSingleTileWorkspace ? 1 : Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(probes.Length)));
                atlas = new SharedOctahedralProbeAtlas(filter, faceSize, tileSize, row);
                // Never change the supplied camera or its target/transform. Copy its settings to an owned disabled camera.
                var go = new GameObject("Shared Oct probe capture camera") { hideFlags = HideFlags.HideAndDontSave };
                m_WorkCamera = go.AddComponent<Camera>();
                m_WorkCamera.CopyFrom(captureCamera);
                m_WorkCamera.enabled = false;
                m_WorkCamera.cameraType = CameraType.Reflection;
            }
            m_NextProbe %= probes.Length;
            Transform probe = probes[m_NextProbe];
            if (probe == null)
            {
                if (m_NextFace != 0) atlas.CancelFaces(m_ActiveProbeId);
                m_NextFace = 0;
                ++m_NextProbe;
                return;
            }
            int probeId = probe.GetInstanceID();
            try
            {
                if (captureOneFacePerFrame || m_NextFace != 0)
                {
                    if (m_NextFace == 0)
                    {
                        m_ActiveProbeId = probeId;
                        m_WorkCamera.transform.SetPositionAndRotation(probe.position, Quaternion.identity);
                        configureCaptureCamera?.Invoke(probe, m_WorkCamera, false);
                        m_CapturePosition = m_WorkCamera.transform.position;
                        lastCapturePosition = m_CapturePosition;
                        m_CaptureRotation = m_WorkCamera.transform.rotation;
                        atlas.BeginFaces(probeId);
                    }
                    atlas.CaptureFace(m_ActiveProbeId, (CubemapFace)m_NextFace, (target, face) =>
                    {
                        m_WorkCamera.transform.SetPositionAndRotation(m_CapturePosition, m_CaptureRotation * Quaternion.LookRotation(kDirections[(int)face], kUp[(int)face]));
                        m_WorkCamera.fieldOfView = 90;
                        m_WorkCamera.aspect = 1;
                        m_WorkCamera.ResetProjectionMatrix();
                        m_WorkCamera.ResetWorldToCameraMatrix();
                        m_WorkCamera.targetTexture = target;
                        m_WorkCamera.Render();
                        m_WorkCamera.targetTexture = null;
                    });
                    if (++m_NextFace == 6)
                    {
                        atlas.CompleteFaces(m_ActiveProbeId, NextVersion());
                        Publish(m_ActiveProbeId);
                        m_NextFace = 0;
                        ++m_NextProbe;
                    }
                }
                else
                {
                    m_WorkCamera.transform.SetPositionAndRotation(probe.position, Quaternion.identity);
                    configureCaptureCamera?.Invoke(probe, m_WorkCamera, true);
                    lastCapturePosition = m_WorkCamera.transform.position;
                    if (atlas.CaptureCubemap(probeId, NextVersion(), target => m_WorkCamera.RenderToCubemap(target)))
                    {
                        Publish(probeId);
                        ++m_NextProbe;
                    }
                }
            }
            catch (Exception error)
            {
                Debug.LogException(error, this);
                enabled = false;
            }
        }

        uint NextVersion()
        {
            if (++m_Version == 0) ++m_Version;
            return m_Version;
        }

        void Publish(int probeId)
        {
            probeUpdated?.Invoke(probeId, m_Version);
            if (useSingleTileWorkspace) atlas.RemoveProbe(probeId);
        }

        public bool RequestCapture(int probeId)
        {
            if (m_NextFace != 0 || probes == null) return false;
            for (int i = 0; i < probes.Length; ++i)
            {
                if (probes[i] == null || probes[i].GetInstanceID() != probeId) continue;
                m_NextProbe = i;
                return true;
            }
            return false;
        }

        void OnDisable()
        {
            atlas?.Dispose();
            atlas = null;
            m_NextFace = 0;
            if (m_WorkCamera != null)
            {
                if (Application.isPlaying) Destroy(m_WorkCamera.gameObject);
                else DestroyImmediate(m_WorkCamera.gameObject);
            }
        }
    }
}
