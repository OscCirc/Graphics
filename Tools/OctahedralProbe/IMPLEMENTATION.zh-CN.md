# 共享捕获与 Oct 探针：实现导读

这是基于 HDRP 17.6 的实验分支，包含独立原型和显式启用的 HDRP 接入口。不是已经完成全部场景验收的生产版本。


## 第二阶段：接入 HDRP 自己的三种模式

现在已加入原调度的接入口，不再只依赖第一版 Custom 样例：

- **Realtime**：勾选 `Store Raw Oct Radiance`，仍保持 HDRP 的 Realtime 模式。EveryFrame、OnEnable、OnDemand、可见性、请求依赖和分帧由原逻辑决定。公共二维面每执行完一面即写入该 Probe 的 pending Oct。
- **发布与失败重试**：每 Probe 保留独立的 pending / published 数据。六面实际执行完成且调度器发出完成步骤后才交换；重新调度的面会撤销旧完成位，失败会撤销发布请求。旧图像和旧捕获位置在此之前不变。
- **Baked**：原有烘焙临时 Cube 转成原始 Oct EXR，缓存哈希区分格式，导入后为线性、无 mip 的 Texture2D。
- **Custom**：兼容原 Cube，也可直接指定符合原始 Oct 约定的 Texture2D；Custom 烘焙同样支持 Oct。探针右键菜单可把已指定的 Cube 转成新 Oct 资产并替换本 Probe 引用，原 Cube 资产保留。
- **GGX / Charlie**：原始 Oct 源先还原到公共临时 Cube 并生成普通 mip，再交给原 HDRP 过滤器，最终仍写入原 Oct 反射图集。这条新路径支持现有过滤器；下面第一版外部预过滤上传接口仍是 GGX-only。
- **编辑器**：增加存储选项、Texture2D 输入校验、Oct 纹理预览和 chrome gizmo 方向采样。保留 Baked 的原 SH 归一化流程。

主要新调用链：

```text
GenerateProbeRenderRequests → AddHDProbeRenderRequests
  → HDProbe.NextRenderSteps（原更新语义）
  → 公共二维面 RenderRequest
  → CompleteOctahedralProbeFace → 写入 pending Oct
  → 六面与发布条件齐全 → 发布 raw Oct / 版本 / 捕获位置
  → ReflectionProbeTextureCache.UpdateTexture
  → RestoreRawOctahedralCube → 原 GGX/Charlie → 原图集
```

这里的“raw Oct”只有原始辐射度与 mip0 的每边一个 texel 边框，不是第一版样例带粗糙度 mip 的预过滤 Oct。两种资源不能混用。实时前台结果按 Probe 身份作为图集缓存键，换前台纹理不会每次申请新槽位。

**仍需说明的边界**：这不是消除全部 Cube。Baked 场景捕获仍有临时 Cube，原过滤器前仍有公共 Cube。实时更新期间可能同时存在前后台 Oct，因此峰值显存不保证下降。多探针分帧时保留各自后台，当前没有增加一个强制限流调度器。天空、雾/云的天空消费者、引擎原生 Cube API 等独立系统未改；URP 已由用户在其他位置实现，本分支不重复修改。

本阶段执行了 20 项 GPU、23 项纯 .NET 检查以及 6 个 DXC 阶段编译；相关代码做了 52 项语法检查。新增 Editor 用例已准备，完整 HDRP 编译与实际场景、烘焙/导入仍受本机 Editor 版本和许可证限制。详见 [本阶段验证记录](Validation/MODES-VALIDATION.md) 与 [使用说明](../../Packages/com.unity.render-pipelines.high-definition/Documentation~/Reflection-Probe-Octahedral.md)。

以下内容记录第一阶段独立原型和外部预过滤 API，不能将其限制与新 raw Oct 路径混淆。

## 第一阶段实现了什么

有两条路径：

```text
共享 Cubemap：
一个公共 Cube → GGX → 一个公共预过滤 Oct 工作区 → HDRP 原有图集

逐面路径：
一个公共二维面 → 逐面写入公共原始 Oct → 六面齐全后 GGX
    → 一个公共预过滤 Oct 工作区 → HDRP 原有图集
```

逐面路径不申请完整 Cubemap。接入 HDRP 后，公共预过滤工作区只有一个 tile；每个 Probe 的持久结果位于 HDRP 原有图集中。工作区数量不随 Probe 数量增长。

HDRP 内置路径默认保持原行为。只有主动注册的 Custom Reflection Probe 使用外部捕获路径。没有改引擎 C++，也没有替换 Lit 的 `SampleEnv`。

## 按调用链读代码

1. [SharedOctahedralProbeExample.cs](../../Packages/com.unity.render-pipelines.high-definition/Samples~/SharedOctahedralProbe/SharedOctahedralProbeExample.cs)：`LateUpdate` 顺序更新 Probe，或者每帧更新一个面。相机是自有实例，不改动传入相机。
2. [SharedOctahedralProbeAtlas.cs](../../Packages/com.unity.render-pipelines.high-definition/Samples~/SharedOctahedralProbe/SharedOctahedralProbeAtlas.cs)：`CaptureCubemap` 或 `BeginFaces → CaptureFace → CompleteFaces` 管理共享工作区，检查六面齐全，过滤后发布版本。
3. [SharedOctahedralProbe.compute](../../Packages/com.unity.render-pipelines.high-definition/Samples~/SharedOctahedralProbe/SharedOctahedralProbe.compute)：`CopyFace` 按方向选择当前 Cube 面，保留其他区域；`FilterCube` 和 `FilterOct` 分别从两种输入计算 GGX 结果。
4. [SharedOctahedralProbeHDRPBridge.cs](../../Packages/com.unity.render-pipelines.high-definition/Samples~/SharedOctahedralProbeHDRPIntegration/SharedOctahedralProbeHDRPBridge.cs)：监听 `probeUpdated`，立刻消费刚完成的公共 tile；之后原型才回收该 tile。
5. [HDRenderPipeline.ExternalOctahedralProbe.cs](../../Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/HDRenderPipeline.ExternalOctahedralProbe.cs)：`UploadExternalOctahedralProbe` 验证布局、版本和活动管线，提交图集上传，再发布捕获位置和版本。
6. [ReflectionProbeTextureCache.cs](../../Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Reflection/ReflectionProbeTextureCache.cs)：外部路径按 Probe 身份分配图集区域。上传通过新材质将输入布局转成 HDRP 原有布局。
7. [OctahedralProbeSource.hlsl](../../Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Reflection/OctahedralProbeSource.hlsl)：计算源 Oct 的 mip 与边框坐标，完成粗糙度级别重映射。
8. [LightLoop.cs](../../Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightLoop/LightLoop.cs)：允许只有图集数据而没有 `probe.texture` 的外部 Probe，仍使用原有环境光数据与材质采样。

## 关键设计

**缓存身份与工作纹理分离。** 普通路径仍使用源纹理的 EntityId；外部路径改用 Probe 的 EntityId 和目标尺寸。同一公共纹理服务多个 Probe，不会把它们识别成同一个缓存项。

**缓存失效后请求重捕获。** 公共纹理此时可能已经属于另一个 Probe，不能按旧版 `NeedsUpdate → UpdateTexture` 直接读取它。外部缓存失效返回无效区域，并设置 `externalOctahedralUpdateRequested`，桥接组件据此安排捕获。

**新旧结果分开。** 六面未齐全、捕获取消或失败时，不发布新版本。原型的逐面写入发生在原始 Oct 工作区；最终粗糙度结果和图集上传在六面完成后执行。

**记录真实捕获位置。** 分帧捕获期间冻结位置和旋转。上传时记录该捕获位置，之后 Lit 的视差校正使用它，而不是错误地把 Probe 最新位置当作旧数据的捕获中心。

**两套边框约定在上传端转换。** 原型每级 mip 每边保留一个 texel；HDRP 有自己的 padding 参数。新的上传 Shader 负责转换，最终 Lit 继续按 HDRP 的旧约定读取。

**GGX-only。** 第一版没有提供 Charlie 数据，开启 Fabric Convolution 时明确拒绝接入，避免读取不存在的过滤结果。

**不复用旧的归一化 SH。** 外部捕获没有上传对应的 APV 反射探针归一化系数，因此该功能在外部 Probe 上关闭，避免错误地套用旧烘焙数据；原有序列化数据不被改写。

## 如何验证性能与显存

这次原生 GPU 微基准使用 RTX 5060 Laptop GPU、64×64 输入面、64×64 输出 tile、256 个 GGX 样本、五个粗糙度级别，预热后执行 20 次命令列表，使用 GPU timestamp：

| 固定测试工作 | 单次 GPU 时间 |
|---|---:|
| 完整 Cube 输入的预过滤及 Oct 输出 | 约 0.112 ms |
| 六面重投影加 Oct 输入的预过滤 | 约 0.167 ms |

这里不含相机捕获、输入准备、CPU 提交、Unity 调度、HDRP 图集上传和 Lit。它说明这组工作负载中逐面路径本身更贵，不能据此宣称 Oct 自动更快，也不能将它推广为真实场景的帧耗结论。

显存方面验证了资源结构和复用，尚未在 Unity 场景里实测 VRAM。固定图集是预分配资源，空槽位不会自动释放显存；源/目标分辨率和质量也必须一起考虑。

后续公平场景对比应使用同一场景、固定曝光、同一 Probe 数量和分辨率、同一相机路线，关闭 SSR/RT 反射后比较：

- 原生探针使用 OnDemand，安排与新路径相同的每 Probe 更新周期。
- 对比旧路径、共享 Cube、逐面路径，分别记录显存峰值、常驻量、捕获/过滤/上传 GPU 时间、更新延迟和接缝。
- 逐面模式当前六帧完成捕获，卷积与上传仍集中在最后一帧；它并没有把全部卷积工作继续分帧。
- 不能把“原生每帧更新全部 Probe”和“新路径每帧只更新一个 Probe”直接比较后声称算法提速。
- 原型使用 Hammersley GGX，HDRP 有自己的优化过滤实现，必须同时检查高亮 HDR 环境的质量差异。

## 已验证与未验证

- 14 项原生 D3D11 检查通过，包括实际上传 helper 的边框、粗糙度 mip、源区域与复用顺序。
- 12 项直接执行生产布局校验代码的 .NET 测试通过。
- 原型 C# 对本地团结引擎程序集编译通过，零警告、零错误。
- 修改的 HDRP/桥接代码在 Player、Editor 配置下共 22 项语法检查通过；这不是整个 HDRP 程序集的类型编译。
- 实际 Core 顶点函数及上传 helper 的像素阶段包装通过 DXC 编译。
- 15 项独立 Editor 测试与 6 项 HDRP 状态/键/归一化测试已准备，但未执行。机器上没有匹配 HDRP 17.6 的 Unity 6 Editor；现有团结 Editor 又因许可证状态无法启动。

完整测试数据见 [VALIDATION.md](Validation/VALIDATION.md)。接入步骤见 [HDRP 桥接样例说明](../../Packages/com.unity.render-pipelines.high-definition/Samples~/SharedOctahedralProbeHDRPIntegration/README.md)。
