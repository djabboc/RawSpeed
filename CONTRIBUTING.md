# 参与开发

本项目目前聚焦 Windows 上的轻量 JPG / Nikon NEF / Olympus ORF 编辑。提交修改前，请先阅读 [架构说明](docs/architecture.md)、[测试说明](docs/testing.md) 和 [第三方依赖](THIRD-PARTY-NOTICES.md)，明确已有能力与精度边界。

## 开发环境与运行

使用 Windows 10/11 x64、.NET 9 SDK。完整的安装、命令行运行、Visual Studio 断点调试和故障处理见 [本地开发指南](docs/development.md)。第一次还原需要访问 NuGet；SDK 选择遵循 `global.json`。

在仓库根目录执行：

```powershell
dotnet restore RawSpeed.sln --locked-mode
dotnet build RawSpeed.sln -c Debug --no-restore
dotnet run --project src/RawSpeed.App -c Debug --no-build
```

WPF 项目针对 `net9.0-windows`，应用进程为 x64。Core、Imaging、Metadata 为独立的 `net9.0` 项目；不要将 WPF 类型或文件对话框引入这些模块。

## 验证修改

当前测试是可执行控制台程序；仅执行 `dotnet test` 不会运行这些检查。完整的常规验证使用以下脚本，涵盖 Core、App、滤镜目录、导出与偏好以及 WPF 控件：

```powershell
./scripts/Test.ps1
# 仅在备齐本地样片后执行
./scripts/Test.ps1 -WithSamples
```

调试单个套件时也可直接运行相应项目：

```powershell
dotnet run --project tests/RawSpeed.Core.Tests -c Release
dotnet run --project tests/RawSpeed.App.Tests -c Release
dotnet run --project tests/RawSpeed.IntegrationTests -c Release
```

Core 测试检查数学与边界；App 测试在 STA / WPF Dispatcher 上检查配方、历史、切图、取消和渲染状态，不打开窗口或系统对话框。Integration 使用 `example/nikon.NEF`、`example/olympus.ORF`、`example/P1010002.JPG`，验证真实文件完整流程。样片不随仓库发布，没有这些文件时请明确记录“未执行样片集成测试”，不要把样片缺失当成产品测试通过。

按修改范围执行必要验证，避免为纯文档变更重复所有重型图像处理。像素算法修改应运行 Core；状态或配方修改应运行 App；解码、导出、跨模块数据格式变化应运行集成测试。影响 WPF 布局和交互时，还应实际打开界面，检查相应功能及常用显示缩放。

性能工作可另外执行：

```powershell
dotnet run --project tests/RawSpeed.Core.Tests -c Release -- --benchmark
dotnet run --project tools/RawSpeed.IoProbe -c Release
```

报告包含 CPU、内存、Release 配置、图片尺寸、预览尺寸、冷/热状态和采样方式。把首屏、真实 RAW 显影、已解码图像的滑块处理、完整尺寸导出分别记录；不以 shader 或单个循环的耗时代表用户等待时间。第一次运行的 JIT 与磁盘缓存影响应写清楚。

## 实现约定

- 保持职责边界：Core 处理像素和可验证的参数；Imaging 处理文件/native 生命周期；Metadata 列出可解出的字段；App 协调状态与用户交互。
- 输入像素不得原地修改。明确像素排列、位深、颜色空间、坐标系和所有权；不得把相机内嵌 JPEG 宣称为真正 RAW 显影。
- 在后台执行耗时任务，传播取消 token，并在每个异步阶段之后检查是否过期。任何旧任务都不应覆盖新选中照片或最后一次编辑。
- 使用已有配方版本和范围校验；更改持久化格式时增加版本与迁移策略。不要静默接受 NaN、无穷数、无效裁切或不支持的 LUT 结构。
- 保持源文件只读和输出不覆盖约定。导出通过临时文件发布，失败和取消清理临时结果；边车保存可以替换同名边车，但不能改动原图。
- 对实际问题添加有意义的回归测试。优先验证用户可观察行为、数值不变量和边界，不编写简单复述实现的测试。
- 避免不必要的新依赖。引入或升级依赖时，更新实际版本、许可证与 native 清单，并检查目标 RID 的发布输出。
- 使用 UTF-8，保持 nullable 检查与现有格式；公开 API 的注释应说明行为、精度或生命周期等实际约束。

## 提交问题与变更

问题报告应包含可复现步骤、预期与实际行为、系统/运行时版本、相机型号、文件扩展名、相关参数和异常信息。样张、EXIF 中的 GPS/姓名以及本机路径可能包含个人信息，只上传有权公开的必要材料；可用最小合成文件重现时优先使用合成夹具。

一个变更尽量解决一个明确问题。PR 描述写清原来的触发条件、修改后的行为、测试命令及结果、性能/精度取舍与尚未验证的范围。不要提交 `bin/`、`obj/`、测试导出、个人边车或用户原片。需要引入新测试资源时，同时说明来源和再分发许可。

不应将“传统降噪已实现”写成“等同 DeepPRIME”，也不应将“WPF 使用系统合成”写成“RAW 图像处理已 GPU 加速”。涉及 16-bit、scene-linear、AI 或 GPU 的功能应具备相应的数据路径与验证后再更新能力说明。

本文件不建立额外 CLA，也不替维护者选定项目许可证；提交和发布前以仓库实际许可证及适用第三方条款为准。
