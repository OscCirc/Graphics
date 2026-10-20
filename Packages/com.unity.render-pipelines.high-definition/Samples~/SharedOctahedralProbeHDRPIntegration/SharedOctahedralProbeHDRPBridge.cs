using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering.HighDefinition.Experimental
{
    /// <summary>Opt-in adapter from the shared capture sample to HDRP's existing reflection atlas and Lit shading.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SharedOctahedralProbeExample))]
    public sealed class SharedOctahedralProbeHDRPBridge : MonoBehaviour
    {
        readonly Dictionary<int, HDAdditionalReflectionData> m_Probes = new Dictionary<int, HDAdditionalReflectionData>();
        SharedOctahedralProbeExample m_Capture;

        void OnEnable()
        {
            try
            {
                m_Capture = GetComponent<SharedOctahedralProbeExample>();
                if (m_Capture.atlas != null)
                    throw new InvalidOperationException("Enable the bridge before the capture example starts. Restart the capture component to change workspace mode.");
                var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
                if (asset != null && asset.currentPlatformRenderPipelineSettings.lightLoopSettings.supportFabricConvolution)
                    throw new NotSupportedException("Disable Fabric Convolution for the GGX-only external Oct prototype.");
                foreach (Transform point in m_Capture.probes)
                {
                    if (point == null) continue;
                    var probe = point.GetComponent<HDAdditionalReflectionData>();
                    if (probe == null || probe.mode != ProbeSettings.Mode.Custom)
                        throw new InvalidOperationException("Every capture transform must have a Custom HDRP Reflection Probe. No serialized mode is changed automatically.");
                    probe.EnableExternalOctahedralCapture();
                    m_Probes[point.GetInstanceID()] = probe;
                }
                m_Capture.useSingleTileWorkspace = true;
                m_Capture.configureCaptureCamera = ConfigureCamera;
                m_Capture.probeUpdated += Upload;
            }
            catch (Exception error)
            {
                Debug.LogException(error, this);
                enabled = false;
            }
        }

        void Update()
        {
            foreach (var pair in m_Probes)
            {
                if (pair.Value == null || !pair.Value.externalOctahedralUpdateRequested) continue;
                if (m_Capture.RequestCapture(pair.Key)) break;
            }
        }

        void ConfigureCamera(Transform point, Camera camera, bool cubemap)
        {
            if (!m_Probes.TryGetValue(point.GetInstanceID(), out var probe))
                throw new InvalidOperationException("The capture list changed; re-enable the bridge to register it.");
            if (!camera.TryGetComponent<HDAdditionalCameraData>(out _)) camera.gameObject.AddComponent<HDAdditionalCameraData>();
            var capturePosition = ProbeCapturePositionSettings.ComputeFrom(probe, null);
            HDRenderUtilities.ComputeCameraSettingsFromProbeSettings(probe.settings, capturePosition,
                out var settings, out var position, 0);
            // Custom mode controls ownership; the live camera still uses realtime-reflection frame defaults.
            settings.defaultFrameSettings = FrameSettingsRenderType.RealtimeReflection;
            if (position.mode != CameraPositionSettings.Mode.ComputeWorldToCameraMatrix)
                throw new NotSupportedException("The shared-capture bridge requires transform-based capture views.");
            // Match HDRenderUtilities.FixSettings for RenderToCubemap on top-origin APIs.
            if (cubemap && SystemInfo.graphicsUVStartsAtTop) settings.flipYMode = HDAdditionalCameraData.FlipYMode.ForceFlipY;
            camera.ApplySettings(settings);
            camera.ApplySettings(position);
            camera.ResetWorldToCameraMatrix();
        }

        void Upload(int probeId, uint version)
        {
            if (!(RenderPipelineManager.currentPipeline is HDRenderPipeline pipeline))
                throw new InvalidOperationException("The external Oct bridge requires an active HDRP pipeline.");
            if (!m_Probes.TryGetValue(probeId, out var probe) || probe == null) return;
            var atlas = m_Capture.atlas;
            if (!atlas.TryGetProbe(probeId, out var region, out _)) return;
            int width = atlas.texture.width;
            var layout = new OctahedralProbeSourceLayout(width, atlas.tileSize,
                Mathf.RoundToInt(region.z * width), Mathf.RoundToInt(region.w * width), atlas.maxMip);
            pipeline.UploadExternalOctahedralProbe(probe, atlas.texture, layout, version, m_Capture.lastCapturePosition);
        }

        void OnDisable()
        {
            if (m_Capture != null)
            {
                m_Capture.probeUpdated -= Upload;
                if (m_Capture.configureCaptureCamera == ConfigureCamera) m_Capture.configureCaptureCamera = null;
            }
            foreach (var probe in m_Probes.Values)
                if (probe != null) probe.DisableExternalOctahedralCapture();
            m_Probes.Clear();
        }
    }
}
