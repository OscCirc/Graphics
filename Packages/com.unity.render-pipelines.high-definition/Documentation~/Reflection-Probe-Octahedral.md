# Experimental raw Oct reflection probe storage

This branch adds raw Oct radiance sources to HDRP's existing reflection probe system. It retains the HDRP Baked, Custom and Realtime modes and their existing light-loop/IBL behavior. It does not change the native ReflectionProbe API or convert the sky system.

**Status:** experimental. The GPU conversion helpers and managed publication rules have independent tests. Full HDRP assembly compilation, ShaderLab import, the Editor baking pipeline and scene rendering require a compatible, activated Unity Editor and have not been validated on the implementation machine.

## Realtime

Enable **Store Raw Oct Radiance** on an HDRP Reflection Probe, or set `probe.useOctahedralRadiance = true`. Keep the probe in HDRP **Realtime** mode. EveryFrame, OnEnable, OnDemand, visibility checks, render-request dependencies and `timeSlicing` continue through the existing HDRP scheduler. `RequestRenderNextUpdate()` continues to request an OnDemand update.

Each scheduled view renders into a shared two-dimensional face target. Its result is copied into the current probe's pending raw Oct before the next request can overwrite that face. Capture settings are frozen across the cycle. A probe publishes only after six distinct, successfully executed faces and the scheduler's publication step. Culling/render failures reschedule the affected face and completion step. First-frame retries invalidate older completion bits before new requests execute.

The last published texture and capture position remain unchanged during an incomplete update. Concurrent or time-sliced probes retain independent pending Oct textures; this version does not impose a one-probe-at-a-time budget. Runtime disable and encoding changes release pending resources. Disable retains the published texture, as in the Cube path; destruction releases it. OnDemand remains explicitly requested, including after re-enable or opting into storage. Resolution/format changes restart capture. Published realtime sources use stable **probe** cache keys even when their underlying texture changes.

## Baked and Custom

Enable **Store Raw Oct Radiance** before baking a Baked probe or using **Bake as new Reflection Texture** in Custom mode. Existing HDRP scene capture still uses a temporary Cube, but writes a square raw Oct EXR. Baking cache hashes distinguish Cube and Oct output. The importer enforces Texture2D shape, linear color, no generated mips, clamp addressing and the capture size after applying presets.

Custom sources can be a legacy Cube or a raw Oct Texture2D. Texture2D interpretation is specific to non-planar reflection probes; do not assign arbitrary square images. The source must use the raw layout below. Invalid shape, non-power-of-two size, nonlinear format and uncreated RenderTextures are rejected as invalid probe data.

The ReflectionProbe context menu **Convert Assigned Cubemap to Raw Oct** converts an existing Baked/Custom source into a new EXR under `Assets/ReflectionProbeOct` and replaces only that probe's reference. It preserves the old Cube asset. It supports Undo of probe settings/reference changes; generated files remain independent assets. It requires an active HDRP pipeline.

The Inspector uses the Texture editor for raw Oct previews. The chrome capture-point gizmo samples the raw Oct direction mapping explicitly. These previews show source radiance, not a roughness-filtered result.

## Raw source contract and filtering

- Linear, unencoded HDR radiance. RGBM or other encoded HDR inputs must be decoded first.
- Square, power-of-two Texture2D, at least 8 texels wide.
- Core Oct Quad direction convention, one border texel per side at mip zero.
- No roughness prefiltering in the asset. Additional mips are ignored.
- Capture/export size is `max(64, 2 * cubeFaceResolution)`.

On a reflection-cache update HDRP reconstructs a Cube from the raw Oct and generates its ordinary source mip pyramid. It then invokes the **existing GGX and, when enabled, Charlie filters** and existing Cube-to-atlas upload. Final Lit sampling, atlas padding, roughness convention and per-BSDF slices remain the stock path. Restored Cubes are shared by size, not retained by each probe. The minimum reconstructed face size is 64, as required by stock convolution's seven mip levels.

This differs from the earlier external sample API: `UploadExternalOctahedralProbe` accepts an **already prefiltered** GGX tile with borders at every roughness mip, bypasses stock filtering, and still requires GGX-only settings. Do not assign that prefiltered sample atlas as a raw Custom source. `ConvertReflectionProbeToOctahedral` records raw conversion on a caller's command buffer and does not perform filtering.

Baked normalization SH remains on the existing HDRP baking/normalization path. The external provider API still bypasses normalization when matching SH was not supplied. Changing the radiance representation does not synthesize new SH or new bake metadata.

## Memory, performance and remaining coverage

This removes per-probe **Cube** storage for opted-in realtime probes and converted Baked/Custom assets. It does not remove all Cube allocations or guarantee lower peak VRAM. At face size N, a raw Oct uses approximately `4*N*N` texels versus `6*N*N` for a Cube without mips. A realtime probe being updated may hold both its published and pending Oct; shared face targets, restored Cubes, stock filter intermediates and the destination atlas add further allocations. Pools are keyed by resolution/format and remain until pipeline disposal.

Raw Oct reconstruction adds resampling and GPU work. High-contrast HDR seams, exposure/sky equivalence, BC6H compression, camera orientation on each backend and fair scene memory/frame-time comparisons still need Editor measurements. The existing standalone sample microbenchmark does not time this native-scheduler path.

A 2N-wide raw Oct also contains fewer texels than six N-wide faces, so this is a quality/storage tradeoff, not a lossless representation change. Stock realtime Cube targets allocate a mip chain (approximately `8*N*N` texels in total); published plus pending raw Oct can already reach that amount before shared resources are counted.

Planar reflection probes retain their two-dimensional perspective projection and existing filter. APV/light probes retain SH storage. Sky Cube/CubeArray generation, sky importance sampling, fog/cloud consumers, arbitrary material Cubemap properties, Shader Graph/VFX Cube nodes and native engine Cube interfaces are separate systems and are not migrated by this change. URP is outside this branch's work; the user's existing URP Oct implementation is not modified.

See `Tools/OctahedralProbe/Validation/MODES-VALIDATION.md` and the Chinese implementation guide for reproducible checks and the precise validation boundary.
