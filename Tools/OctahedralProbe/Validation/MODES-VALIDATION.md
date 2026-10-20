# Native HDRP probe-mode expansion, 2026-10-20

This run extends the earlier standalone/external-provider experiment. It is not a complete HDRP Editor acceptance result.

## Implemented source paths

| Path | Source/storage implementation | Runtime filter path |
|---|---|---|
| Realtime EveryFrame / OnEnable / OnDemand | Existing scheduler; shared face; per-probe pending/published raw Oct | Raw Oct -> shared Cube -> stock GGX/Charlie -> existing atlas |
| Realtime time slicing | Existing `NextRenderSteps`; frozen settings; per-face completion and retry invalidation | Same, after complete publication |
| Baked | Existing scene capture; raw Oct EXR output, encoding-aware bake hash and Texture2D import | Same stock filters on a cache miss |
| Custom | Cube compatibility, raw Oct input, Custom bake output and conversion context menu | Same stock filters on a cache miss |
| Planar | Existing projection and storage retained | Existing planar filtering |
| External prefiltered sample | Earlier opt-in API retained | GGX-only direct upload; different source contract |

The runtime destination remains HDRP's preallocated Oct atlas. This branch does not migrate sky or native engine Cube APIs. User-provided URP work is outside the changes.

## Executed checks

- **20 D3D11 GPU checks passed**, RTX 5060 Laptop GPU, RGBAHalf. The new raw source cases verify Cube-to-Oct directions/borders, six-face reconstruction, all six reconstructed Cube orientations, constant HDR preservation and partial-write isolation. The raw source is 128x128 for 64x64 Cube faces. Observed maximum direction-field error was 0.00147218; six-face versus complete-Cube error 0.00292969; restored-face direction error 0.000972301; constant HDR and non-owned-pixel changes were zero in these fixtures.
- Raw GPU cases execute production `OctahedralProbeRadiance.hlsl` through a compute adapter. FXC cannot preprocess current Core Common.hlsl; the adapter supplies equivalent Oct packing from the earlier sample. **The real Core packing and production ShaderLab HLSLINCLUDE are compiled separately with DXC.** This distinction matters; neither run is a Unity ShaderLab import.
- **6 DXC stages passed**: previous upload vertex/helper stages plus the actual new shader's `Vert`, `CopyFace`, `OctToCube` and `CubeToOct`. Only the Unity ShaderVariables include is replaced by the test's Core/XR alias context.
- **23 .NET cases passed**, directly compiling production layout and `ProbeFaceCompletion` sources. They include all 720 completion orders, duplicates, incomplete publication, invalid masks, resets, independent probes and withdrawal of older completion/publication on rescheduling or failure.
- **52 Player/Editor syntax parses passed** over 26 relevant files. This does not resolve full HDRP assembly types or replace a package build.

Machine-readable results are in `native-modes-results.json`, `managed-modes-results.json`, `shader-modes-results.json` and `syntax-modes-results.json`. The pre-existing `VALIDATION.md` and original reports remain a record of the earlier implementation.

## Prepared but not executed

`OctahedralProbeModeTests.cs` adds **20 Editor cases** for the three native refresh modes, time slicing, publication gating, retention of old texture/position/version, cancellation, stable cache identity, both asset modes, invalid sources, storage switching, OnDemand opt-in/re-enable behavior, destruction cleanup, format restart, Cube compatibility, nonlinear source rejection, planar exclusion and the EXR importer contract.

These and the existing 15 standalone / 6 external-provider tests have not run: this machine has no Editor matching HDRP 17.6; the installed Tuanjie 2022.3 Editor previously failed licensing before import. No license settings were changed. Full package semantic compilation, Unity camera Y orientation, RenderGraph request ordering, retries caused by actual sky/culling failures, EXR bake/import execution, compression and final scene lighting remain unverified.

## Performance boundary

The native harness's optional timing still measures the **earlier sample prefilter workload**, not native HDRP capture or stock GGX/Charlie. It is not used to claim a performance gain for this expansion. No application VRAM or frame-time measurements have been obtained. Pending/published raw textures can increase peak memory during concurrent updates; shared Cube reconstruction intentionally trades extra work for retaining stock filtering behavior.
