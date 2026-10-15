# RawSpeed

一个面向 Windows 的轻量 RAW 照片编辑器，使用 **C#、WPF、.NET 9**。注重快速构图、调色、完整可解码元数据查看和清晰可维护的代码。当前为 **0.1 技术预览**。

## 使用

Windows 10/11 x64，开发需要 .NET 9 SDK；运行非独立发行包需要 .NET 9 Desktop Runtime。

```powershell
dotnet build RawSpeed.sln -c Release
dotnet run --project src/RawSpeed.App -c Release
```

也可以将照片作为命令行参数传入，或拖入窗口。打开文件夹后在左侧选择照片，右侧调整参数；切换 EXIF 标签可搜索、选择并复制元数据。工作图最长边为 1600 像素，界面的“预览 1:1”表示工作图像素，不代表原尺寸 RAW。

- `Ctrl+O` 打开照片；`Ctrl+S` 保存配方；`Ctrl+E` 导出。
- `B` 切换原图；`C` 框选裁切；`Esc` 退出裁切/对比。
- `Ctrl+Z` 撤销；`Ctrl+Y` 重做；双击调整滑条归零。
- 配方写入原文件旁的 `文件名.rawspeed.json`，再次打开自动恢复。未保存的编辑仅保留在当前进程，退出时会提醒。
- 导出重新解码完整尺寸，支持 JPEG（质量 95）、无损 PNG、8-bit TIFF。选择新的输出文件名；现有文件一律不会被覆盖。
- 导入标准 **3D `.cube`** LUT，支持 `DOMAIN_MIN/MAX` 和强度混合。`presets/` 提供两个原创示例，参数来自 `scripts/Generate-Presets.ps1`。LUT 应为适合 sRGB 的滤镜，不自动转换 Log/HDR LUT。

## 已实现

JPEG、Nikon NEF、Olympus ORF；LibRaw 真实显影、相机白平衡、EXIF 方向和输入 ICC 转换；RAW 内嵌 JPEG 先显示，随后替换为真实显影结果。

旋转、镜像、拉直、自由裁切和比例裁切；曝光、对比度、高光/阴影、相对色温/色调、色相/饱和度；三控制点平滑曲线及准确曲线图；快速保边降噪；自然/暖胶片/黑白风格和 `.cube` 导入；RGB 直方图、原图对比、撤销/重做。

元数据查看保留底层库可读取的所有目录和标签、数字 tag ID、Nikon/Olympus MakerNotes、GPS、IPTC、XMP 属性与解析警告。未知、加密或尚未被解析器支持的私有字段不保证能解释，**不宣称覆盖所有相机的所有 EXIF 私有信息**。

## 性能与验证

使用提供的 Nikon D4、Olympus E-M5MarkII 样片实测，32 逻辑处理器、Windows x64、Release：

| 阶段 | NEF | ORF | JPG |
| --- | ---: | ---: | ---: |
| RAW 内嵌预览读取 | 368 ms | 207 ms | — |
| 真正解码到 1600px 工作图 | 1,574 ms | 1,870 ms | 253 ms |
| 组合调整的预热中位数 | 16.4 ms | 17.0 ms | 15.3 ms |
| 完整尺寸解码 | 1,388 ms | 1,671 ms | 275 ms |

组合调整包含调色、LUT、降噪、几何与直方图，测量对象为工作图经裁切后的处理，**不包含界面呈现、解码及导出**；工作图预热不代表首次响应。未清空磁盘缓存，数字不是跨机器或竞品性能承诺。完整记录和覆盖边界见 [测试说明](docs/testing.md)。

```powershell
./scripts/Test.ps1                 # 编译、核心测试和 WPF 模型测试
./scripts/Test.ps1 -WithSamples    # 另验证 example 三样张的完整处理/导出/回读
./scripts/Test.ps1 -Benchmark      # 核心性能基准
./scripts/Publish.ps1              # artifacts/publish/win-x64
./scripts/Publish.ps1 -SelfContained
```

真实样张测试验证源文件 SHA-256 保持不变、PNG 像素逐字节无损、JPEG 回读、ICC/颜色标记、元数据、损坏文件、取消及并发导出保护。样张和生成图像不纳入 Git，CI 使用生成夹具运行不依赖照片的测试。

## 当前边界

- **编辑缓冲和导出为 8-bit sRGB。** LibRaw 在 16-bit 下显影后转为工作缓冲；曝光、白平衡和曲线发生在显示域，不能等同专业软件的场景线性 RAW 工作流。极端曝光恢复、精确色温、16-bit TIFF、相机配置文件和 GPU 管线属于后续工作。
- 降噪为 CPU 可分离双边滤波，未经训练、不是 AI 降噪；不能宣称达到 DxO DeepPRIME。DGO 通常指传感器双增益输出，与软件后处理不同。
- 工作图缩放会改变噪点表现，降噪最终效果以原尺寸导出为准。当前没有原尺寸细节预览或分块 GPU 渲染。
- 导出带 sRGB 色彩声明，当前不复制原片 EXIF/IPTC/XMP/GPS。原始文件和旁车配方分别保留。
- Native RAW 调用开始后无法立即中止；取消会等待该调用结束并丢弃结果。编辑渲染支持行级取消和最新结果优先。
- 三张样片验证不代表所有 Nikon/Olympus 机型和压缩类型兼容。没有镜头校正、局部蒙版、批量导出、目录数据库、XMP/LR 预设导入或 AI 模型。
- .NET 9 是当前机器可用 SDK，正式长期维护前应迁移到受支持的 .NET LTS 并重跑测试。

## 工程与开源准备

`src/RawSpeed.Core` 是无第三方依赖的像素处理核心；`Imaging` 负责 native 解码/编码；`Metadata` 负责只读标签提取；`App` 负责 WPF/MVVM 与任务调度。参见 [架构](docs/architecture.md)、[竞品调研](docs/research.md)、[贡献指南](CONTRIBUTING.md)。

包含 EditorConfig、可复现构建配置、Windows CI、单独测试工程、发布脚本及 [第三方声明](THIRD-PARTY-NOTICES.md)。未初始化或发布 GitHub 仓库；项目自身开源许可证应由仓库所有者在发布前选定。发布 native 二进制时需保留完整上游许可证并满足其对应义务，不能仅依据 C# 包的外层许可证判断。
