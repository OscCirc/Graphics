# Experimental HDRP shared-Oct bridge

This adapter connects the shared-capture prototype to HDRP's existing reflection probe atlas. Lit continues to use its normal environment-light records, proxy-volume intersection, distance-based roughness, FGD and influence weights. It does not introduce an alternate material shader or an external global atlas binding.

**Validation status:** the source prefilter and production upload math passed native D3D11 GPU checks. The standalone sample compiles against installed Tuanjie assemblies, and modified HDRP files pass syntax checks. A complete HDRP 17.6 build and this bridge's scene rendering are **not yet verified**: this machine has no matching Unity 6 Editor, and its installed Tuanjie Editor cannot launch with the current license.

## Setup in a compatible HDRP Editor

1. Use the modified HDRP/Core packages from this branch with an Editor compatible with HDRP 17.6. Do not load HDRP 17.6 into the installed 2022.3 Editor to test this bridge.
2. Import **Experimental Shared Oct Probe HDRP Integration** and its **Experimental Shared Octahedral Probe Atlas** dependency from Package Manager.
3. Disable `lightLoopSettings.supportFabricConvolution` in the active HDRP Asset. This prototype supplies GGX data only; enabling Charlie/fabric convolution is explicitly rejected rather than reading a missing slice.
4. Create ordinary HDRP Reflection Probes and set their mode to **Custom**. Leave their source Cubemap assignment empty for a clean memory experiment. Existing baked/custom asset assignments are not erased automatically. Probe influence/proxy settings remain the ordinary HDRP controls.
5. Add `SharedOctahedralProbeExample` and `SharedOctahedralProbeHDRPBridge` to an otherwise empty GameObject **before entering Play Mode**. Assign a source Camera, the sample compute shader, and the Reflection Probe transforms to the example. The bridge expects each transform to have `HDAdditionalReflectionData` in Custom mode.
6. Enable `captureOneFacePerFrame` to try the single-face path; disable it for the shared complete-Cubemap path. Use powers-of-two capture/tile sizes. For visual comparisons, account for HDRP's normal Oct-size policy: at a 128-pixel Cube face it reserves a 512-pixel Oct tile, while the prototype's default tile is 256.
7. Enter Play Mode and compare against ordinary probes in a controlled room with fixed exposure, SSR/RT reflection disabled, and a roughness sweep. A failed/missing upload has no source texture fallback; HDRP omits that probe and the usual reflection hierarchy can fall back to sky.

The bridge configures the owned capture camera from the probe's settings, uses realtime-reflection frame defaults, includes the Cube Y-flip fix, and freezes capture position/rotation for a six-frame capture. The source camera is not modified. Full exposure/sky/history equivalence still needs a matching-Editor scene comparison.

APV reflection-probe SH normalization is disabled for externally captured probes: the provider does not supply matching SH, and reusing old baked coefficients would incorrectly normalize the new radiance. Stored baked normalization data is not modified; ordinary probes retain their existing behavior.

Do not change the transform list, workspace mode or ownership while a capture is running. Disable/re-enable the example and bridge together to reconfigure them. Only one provider can register a probe at a time. The bridge does not change serialized probe modes or restore a caller's previous realtime scheduling automatically.

## Actual resource flow

```text
one shared Cube OR one shared 2D face + raw Oct
    -> GGX -> one shared prefiltered Oct tile
    -> synchronous graphics-queue upload into HDRP's existing atlas
    -> publish the probe's nonzero version and frozen capture position
    -> reuse the workspace for the next probe
```

The example removes its temporary per-probe mapping after the upload callback. The bridge has a single-tile source atlas, not an extra persistent source atlas containing all probes. HDRP's destination atlas remains preallocated according to the HDRP Asset. Source/workspace allocation is independent of the number of probes; actual VRAM and frame time have not been measured in an application.

The existing native `ReflectionProbe` component is retained for culling, as it is in stock HDRP (`HDAdditionalReflectionData.PrepareCulling` already uses Custom native probes for this purpose). No full-size source Cubemap is assigned to the native or additional-data probe by this adapter.

## Public opt-in API

```csharp
probe.mode = ProbeSettings.Mode.Custom; // Explicit caller choice; the bridge requires this in advance.
probe.EnableExternalOctahedralCapture();

var layout = new OctahedralProbeSourceLayout(
    sourceAtlas.width, tileSize, tileOffsetX, tileOffsetY, maximumInitializedRoughnessMip);

bool uploaded = pipeline.UploadExternalOctahedralProbe(
    probe, sourceAtlas, layout, nonzeroContentVersion, frozenCapturePositionWS);

// On eviction/invalidation, recapture instead of reading whichever probe now owns the shared source.
if (probe.externalOctahedralUpdateRequested)
    ScheduleCapture(probe);

// On provider removal:
probe.DisableExternalOctahedralCapture();
```

Call the upload on the main thread **outside rendering callbacks**, after rendering/filtering and before submitting work that overwrites the shared source. It executes a pooled CommandBuffer on the graphics queue. It consumes the source immediately and retains no source texture reference on the probe. A false return means no new version was published. Version zero is reserved for invalid data.

The source is a square power-of-two Texture2D/RenderTexture with aligned power-of-two tiles. It uses Core's Oct **Quad** direction convention, one border texel per side at each initialized mip, and the normalized Core perceptual-roughness/mip relation. The helper validates dimensions, alignment and the available mip range, including default-initialized layouts. The production shader resamples into HDRP's destination padding and seven-level roughness convention; `SampleEnv` is unchanged.

Changing a probe's mode disables its external state. Enabling external capture releases any old *owned realtime* capture target but does not destroy asset textures. Atlas eviction, reallocation after resolution changes, or atlas clearing returns no stale shared source data: the provider receives a recapture request. Providers must budget retries when the configured atlas cannot fit the requested probes.

## Code map

- `HDProbe.ExternalOctahedral.cs`: provider ownership, readiness, version and capture position.
- `HDRenderPipeline.ExternalOctahedralProbe.cs`: checked public upload API and command submission.
- `ReflectionProbeTextureCache.cs`: per-probe keys, direct upload, residency checks and recapture requests.
- `OctahedralProbeSourceLayout.cs`: pure managed layout validation, used directly by standalone tests.
- `OctahedralProbeSource.hlsl` and `BlitPrefilteredOctahedralProbe.shader`: source-mip/border conversion into the native atlas.
- `LightLoop.cs`: atlas-only validity and the actual frozen capture position; ordinary `SampleEnv` remains unchanged.

See [validation tools](../../../../Tools/OctahedralProbe/README.md) and [the recorded results](../../../../Tools/OctahedralProbe/Validation/VALIDATION.md). HDRP-specific Editor state/key tests are in `Tests/Editor/ExternalOctahedralProbeTests.cs`; these are prepared but have not executed on this machine.
