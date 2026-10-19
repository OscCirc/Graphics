# Validation recorded on 2026-10-20

Reference: Graphics commit `a7e4c051d256a781ab362c64316b125a1e104694`, HDRP package 17.6.0. Implementation is the experimental sample and opt-in HDRP upload bridge on branch `codex/shared-oct-probe-prototype`.

## Passed: C# compilation

`Test-Compile.ps1` compiled the runtime atlas, example component and Editor test suite against the installed Tuanjie 2022.3.62t15 managed assemblies. Result: **0 warnings, 0 errors**. This checks C# API/type compatibility; it does not execute Unity native methods or establish that HDRP 17.6 runs in that Editor.

## Passed: native GPU validation

`Run-NativeGpuTests.ps1` compiled and executed the sample's original `.compute`, sampling `.hlsl` and production Oct upload helper on **NVIDIA GeForce RTX 5060 Laptop GPU**, using a hardware D3D11 device and RGBAHalf textures. All **14 checks passed**. The compiler was the Windows SDK's D3D compiler; the harness was built with MSVC 14.51.

| Check | Observed metric |
|---|---:|
| Four compute entry points compile | Pass |
| Shared capture cube does not overwrite a previously published tile | Maximum color error 0 |
| Constant HDR radiance across all five tested GGX mips | Maximum RGB-channel error 0.00195312 |
| Cube-to-Oct direction field, including padding, against analytic directions | Maximum error 0.00147307 |
| Six single faces against the complete Cubemap path | Maximum error 0.00292969 |
| Oct-input GGX against Cubemap-input GGX across roughness mips | Maximum error 0.00146484 |
| Fractional LOD / seams with a contrasting adjacent probe | Maximum error 0.000146508 |
| Fractional LOD against separately sampled integer levels | Maximum error 9.31e-10 |
| High-frequency variance after GGX filtering | Rough/smooth variance ratio 0.000543684 |
| Partial face update preserves the other face regions | Maximum error 0 |
| Production upload helper compiles | Pass |
| Shared source can be reused after an upload without changing an older destination tile | Maximum error 0.000976562 |
| Production destination padding and seven-level roughness conversion | Maximum error 0.00100833 |
| Production source-region selection | Maximum error 0.000976562 |

Unless otherwise noted, errors are maximum absolute RGB-channel errors in linear radiance. These are analytic test fixtures at source face size 64, Oct tile size 64, 256 GGX samples, and output mips 0 through 4. They are not a measured error bound for arbitrary HDR scenes or a comparison against stock HDRP's filtering implementation. Numerical fixtures and thresholds are in `NativeGpuTests.cpp`.

The production upload fixtures additionally use 256-pixel destination tiles in a 512-pixel atlas, initial total padding 16, last fixed-padding mip 3 and destination roughness levels 0 through 6. Expected values are computed independently from read-back source mips, including bilinear interpolation, source region, roughness-level remapping and border extension.

[Machine-readable results](native-results.json).

## Passed: layout, syntax and stage checks

- **12 .NET tests** execute the actual `OctahedralProbeSourceLayout.cs` production source, with no copied validator or Unity runtime dependency. [Results](managed-layout-results.json).
- **22 syntax checks** parse the modified HDRP sources, bridge and tests under Player/Editor preprocessor configurations using Roslyn. These establish syntax validity, not a full HDRP assembly/type build. [Results](syntax-results.json).
- **2 DXC stage checks** compile the actual Core `VertQuadPadding` vertex function and a pixel-stage wrapper calling the production upload helper, with Unity D3D11/XR aliases. This does not compile/import the full ShaderLab pass through Unity. [Results](shader-stage-results.json).

## GPU timestamp microbenchmark

Sources are prepared before measurement. A deferred command list is recorded first, executed three times for warmup, then timed over 20 executions with D3D11 timestamp/disjoint queries. Face size 64, tile size 64, 256 GGX samples, five roughness levels:

| Submitted GPU work | Mean timestamp interval per execution |
|---|---:|
| Cube-input prefilter plus Oct output | 0.111946 ms |
| Six face reprojections plus Oct-input prefilter | 0.166597 ms |

This workload made the single-face path slower. It is not a frame-time improvement claim. The measurement excludes camera rendering, texture preparation/uploads, CPU command recording, Unity scheduling, the production atlas upload and Lit shading. Resolution/quality, refresh period and sample count must be controlled in a real scene comparison. No application VRAM saving has been measured. [Raw timing record](microbenchmark.json).

Cubemap path and single-face path, respectively (the previews are enlarged from 64x64):

![Cubemap direction field](cube-direction.png)

![Single-face direction field](face-direction.png)

High-frequency input and roughness mip 3:

![Unfiltered environment](roughness-mip0.png)

![GGX mip 3](roughness-mip3.png)

## Not executed: Editor integration suite

The installed Tuanjie Editor batch launch exited before project import/compilation because its licensing client reported **No valid Unity Editor license found**. The runner was not launched with `-nographics`. No license settings were modified.

The 15 standalone Editor tests and 6 HDRP-specific NUnit tests therefore have **no runtime pass result**. They include actual Camera.RenderToCubemap versus six Camera.Render views (with asymmetric wall patches), workspace ownership, publication/cancellation/version behavior, slot reuse, failure cleanup, disposal, texture-free validity, stable cache keys and normalization-state handling. The HDRP tests require a matching Editor in addition to an active license.

The independent D3D11 run does not validate Unity camera orientation, C# runtime lifecycle, RenderGraph scheduling, or integration into HDRP's built-in probe light loop.

## Not measured

- Full HDRP 17.6 project compilation / rendering, requiring a matching Editor. The upload bridge remains experimental and unverified in a complete scene.
- Frame-time or VRAM savings in an application scene.
- Charlie/fabric filtering, all graphics backends, exposure/sky parity, or production-quality high-contrast HDR seams. APV normalization is explicitly bypassed for external probes without matching SH.

The sample README documents these boundaries and the integration points that remain.
