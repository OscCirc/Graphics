# Experimental shared Octahedral Probe Atlas

This sample implements two explicitly scheduled reflection-capture pipelines. Both keep the published results in an Oct atlas keyed by **probe ID**, rather than retaining a Cubemap for every probe.

It is an opt-in research sample. Import it from HDRP's Package Manager Samples list. On its own it uses the supplied `SampleSharedOctProbe` HLSL function and does not replace built-in probe shading. The optional [HDRP integration sample](../SharedOctahedralProbeHDRPIntegration/README.md) uploads each result into the built-in atlas and retains Lit shading. HDRP 17.6 is the reference source; the standalone runtime API also compiles against Tuanjie 2022.3.

## Pipelines

**Shared Cubemap:**

```text
capture callback renders six faces into one shared Cubemap
  -> GGX radiance prefilter writes each roughness mip directly into the probe's atlas tile
  -> publish probe ID + region + content version
  -> reuse the same capture Cubemap for the next probe
```

**Single face:**

```text
BeginFaces(probe ID)
  -> capture a 90-degree view into one shared Texture2D
  -> CopyFace decodes each Oct pixel, selects its Cube face, projects into the 2D input
  -> retain only the raw Oct workspace; reuse the 2D face texture
  -> repeat for six distinct faces, in any order, potentially across frames
CompleteFaces(probe ID, version)
  -> GGX prefilter samples the complete raw Oct and writes the final atlas mips
  -> publish the new version and release the workspace lease
```

The single-face path never allocates a Cubemap. Raw Oct and the 2D face are shared working textures, not per-probe resources. All six faces must be present before filtering; GGX sample directions cross face boundaries. Existing published atlas data stays unchanged during partial capture or cancellation.

## Files and reading order

1. `SharedOctahedralProbeAtlas.cs`: workspace ownership, stable IDs, atlas allocation, capture/filter/publication order, versions and disposal.
2. `SharedOctahedralProbe.compute`: `CopyFace`, `FilterCube`, `FilterOct`, and a sampling kernel used by the tests.
3. `SharedOctahedralProbeSampling.hlsl`: direction encoding, border-aware coordinates, and fractional LOD interpolation.
4. `SharedOctahedralProbeExample.cs`: optional component, one probe per frame or one face per frame. Assign a disabled source Camera, the compute asset, and probe transforms. The component owns its working camera and does not alter the supplied camera.

The example accepts probe **transforms**, not `ReflectionProbe` components, so importing it does not silently enable or disable the existing HDRP probe system. Avoid running the stock realtime capture as well when evaluating workspace memory.

`useSingleTileWorkspace` lets a `probeUpdated` subscriber consume a completed tile synchronously before the example removes its mapping and reuses it. The HDRP bridge enables this mode, so its intermediate prefiltered Oct texture has one tile regardless of probe count. Standalone users normally keep this option disabled.

## Minimal API

```csharp
using UnityEngine.Rendering.HighDefinition.Experimental;

// Create once; all sizes must be powers of two. Dispose when the owner is disabled/destroyed.
var atlas = new SharedOctahedralProbeAtlas(filter, faceSize: 128, tileSize: 256, tilesPerRow: 2);

// Call outside render callbacks. The callback must finish submitting capture before returning.
atlas.CaptureCubemap(probeId, version, target => captureCamera.RenderToCubemap(target));

if (atlas.TryGetProbe(probeId, out Vector4 scaleOffset, out uint contentVersion))
{
    // Bind atlas.texture, scaleOffset, atlas.texture.width, and atlas.maxMip to your shader.
}
```

For single-face capture, call `BeginFaces`, then `CaptureFace` for each `CubemapFace`, then `CompleteFaces`. A lease may span frames. `CancelFaces` abandons it without changing the published result. A callback that fails should cancel the face capture. Faces use the `faceU` / `faceV` convention from HDRP's `BlitCubeTextureFace.shader`.

`RemoveProbe` makes a slot reusable. Capacity is fixed; there is no automatic eviction, repacking, resizing, or background queue. A failed/new capture can leave an unpublished reserved slot; explicitly remove an abandoned probe. Capture APIs are main-thread, graphics-queue APIs; concurrent captures and async-compute submission are unsupported.

## Sampling and mip semantics

Output mip 0 holds unfiltered radiance. Higher published mips hold GGX prefiltered radiance, with the default Core perceptual-roughness/mip mapping. Each mip has one texel of padding on **each** side; the highest published mip still has a 2x2 interior. `maxMip = log2(tileSize) - 2`. Allocated mips below that resolution are deliberately not used, so clamp LOD with the helper.

```hlsl
#include "SharedOctahedralProbeSampling.hlsl"

float3 radiance = SampleSharedOctProbe(
    probeAtlas, linearClampSampler, reflectionDirection, lod,
    probeScaleOffset, atlasSize, maxMip);
```

This layout differs from HDRP's current atlas border contract. One texel per mip has a different normalized width at each mip. The helper therefore computes UV separately for `floor(lod)` and `ceil(lod)`, does two bilinear samples and blends their results. Using one UV with hardware trilinear filtering would not implement that contract correctly. Changing `FilterMode` alone is insufficient.

The output is **radiance**, not a complete material shader. Fresnel/FGD, proxy-volume parallax correction, probe influence weights and reflection hierarchy still belong to the material/light loop.

## Scope of the filter

The filter is a self-contained GGX split-sum radiance prefilter using Hammersley importance samples and normalized NdotL weights. It is not a bitwise port of HDRP's optimized Fibonacci/LUT filtering. Both source paths sample source mip 0, allowing a small, testable single-face experiment without an additional ordinary-mip pyramid. High-contrast HDR environments can need more samples; this implementation has not been optimized or benchmarked against HDRP.

Charlie/fabric convolution, sky capture state/exposure parity, normalizing probe data, and a platform-complete production sampler are outside this prototype. The camera example copies ordinary Camera settings; it does not reproduce all `HDProbe` camera/frame settings. Within-face camera orientation has a dedicated Editor test, but it has not run on this machine because the Editor license is invalid.

Single-face bilinear sampling near Cube-face boundaries can clamp to the current face. The analytic tests quantify its current error; overscan or an Oct-aware reconstruction step would be needed for stricter edge-quality targets.

## Memory model

RGBAHalf uses 8 bytes per texel. Ignoring allocation alignment, the atlas is approximately:

```text
(tileSize * tilesPerRow)^2 * 8 * 4/3
```

Working color storage is independent of the number of probe IDs:

```text
Cube-only path: faceSize^2 * 6 * 8
Face-only path: faceSize^2 * 8 + tileSize^2 * 8
```

The single-face camera target additionally requests a shared 24-bit depth buffer. Camera/render-pipeline transient buffers and allocation alignment are not included above. Workspaces are allocated lazily and retained until `Dispose`. Using both modes on the same instance retains both sets of workspaces. These are layout estimates, not measured VRAM savings. There is no per-probe source texture or CPU-readable pixel copy in the runtime implementation. Atlas capacity and quality, rather than the number of occupied slots alone, determine its allocated memory.

## Tests

See [the validation tools](../../../../Tools/OctahedralProbe/README.md) and their checked-in report. Tests cover HDR preservation, tile isolation, actual Oct borders, six-face reprojection, roughness filtering, fractional LOD, cancellation, versions and cleanup. Editor camera/lifecycle tests require a valid Editor license; native D3D11 tests execute the exact compute source without the Editor.

## Integration with built-in HDRP probes

Do not assign the shared texture to every `probe.texture`: ordinary cache identity is based on texture identity and captures can be queued before consumption. The optional bridge instead enables an explicit runtime-only external provider on Custom reflection probes and uploads each completed tile before workspace reuse.

The repository now includes `HDProbe.EnableExternalOctahedralCapture`, `HDRenderPipeline.UploadExternalOctahedralProbe`, a stable per-probe cache key, cache-miss recapture requests, and an upload shader that converts this sample's border/mip contract to HDRP's existing contract. It also preserves the frozen capture position for parallax correction. Standard probe behavior is unchanged until the API is explicitly enabled.

See the [integration instructions and validation boundary](../SharedOctahedralProbeHDRPIntegration/README.md). Full HDRP package compilation and scene rendering have not been executed on this machine; they need a compatible, activated Editor. The native GPU tests validate the actual upload helper, not the complete Unity render pipeline.
