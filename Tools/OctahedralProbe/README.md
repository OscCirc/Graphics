# Octahedral probe validation

The implementation is in [the experimental HDRP sample](../../Packages/com.unity.render-pipelines.high-definition/Samples~/SharedOctahedralProbe/README.md).

## Run checks

From the repository root, in PowerShell:

```powershell
# Compiles the runtime sample, example and Editor tests against installed Editor assemblies.
./Tools/OctahedralProbe/Test-Compile.ps1

# Compiles and runs the original sample HLSL on a real D3D11 hardware device.
# Requires MSVC C++ tools and the Windows SDK; no Editor license is involved.
./Tools/OctahedralProbe/Run-NativeGpuTests.ps1

# Full Editor integration tests, including actual camera rendering and C# resource lifecycle.
# Requires an activated Editor. Do not add -nographics.
./Tools/OctahedralProbe/Run-GpuTests.ps1
```

All scripts accept `-OutputPath`. The Editor scripts accept `-EditorRoot` or `-EditorPath` respectively. Defaults use the locally installed Tuanjie 2022.3.62t15. The GPU test runner creates an isolated generated project under its output folder, copies only the prototype sources, and does not open or modify an existing user project. Outputs default to the ignored `artifacts/` directory.

The native harness uses D3DCompile, shader reflection, RGBAHalf SRV/UAV textures and synchronous GPU readback. It executes `FilterCube`, `CopyFace`, `FilterOct` and `SampleAtlas` from the **same** `.compute` and `.hlsl` files as the sample. It does not substitute a CPU implementation or a different shader language. Native tests validate texture math, not Unity's C# scheduler or camera orientation.

The Editor suite additionally checks source texture reuse, content versions, incomplete capture publication, workspace ownership, failed capture cleanup, slot reuse, disposal, and six actual camera views. Its asymmetric room patches detect within-face UV flips.

## Recorded run

See [VALIDATION.md](Validation/VALIDATION.md) and [native-results.json](Validation/native-results.json). Every result is labeled by backend; native GPU success is not presented as an Editor/HDRP integration pass.
