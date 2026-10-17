# 本地编译与调试

本文适用于 Windows 10/11 x64，在仓库根目录使用 PowerShell 执行命令。日常开发直接构建和启动应用，无需创建安装包或 GitHub Release。

## 1. 准备环境

- 安装 Git 和 **x64 .NET 9 SDK**。只安装 Desktop Runtime 不能编译项目。
- 仓库的 `global.json` 指定 `9.0.300`，`rollForward: latestFeature` 允许选择已安装的较新 .NET 9 特性带和补丁。安装 9.0.300 或更高的 9.0.x SDK；只有 .NET 10 SDK 不满足此配置。
- 使用 Visual Studio 时，建议 **Visual Studio 2022 17.14 或更高的兼容版本**，安装“.NET 桌面开发”工作负载及 .NET 9 SDK。17.12 开始支持 .NET 9，但本仓库的 9.0.300 特性带对应 17.14；更新 SDK 时也应核对 IDE 兼容性。
- 首次还原需要访问 NuGet。Magick.NET 的 Windows native 依赖随 NuGet 包提供，无需单独安装 ImageMagick 或 LibRaw。

首次获取源码：

```powershell
git clone https://github.com/djabboc/RawSpeed.git
Set-Location RawSpeed
dotnet --list-sdks
dotnet --version
```

已有本地仓库时直接进入该目录。`dotnet --version` 应在仓库中执行，确认 `global.json` 实际选择的 SDK。当前目标框架为 `net9.0` / `net9.0-windows`，应用进程固定为 x64；升级到受支持的 .NET LTS 应作为独立变更验证。

## 2. 还原、编译和运行

```powershell
dotnet restore RawSpeed.sln --locked-mode
dotnet build RawSpeed.sln -c Debug --no-restore
dotnet run --project src/RawSpeed.App -c Debug --no-build
```

`--locked-mode` 按已提交的 `packages.lock.json` 还原，依赖声明与锁文件不一致时会失败。只有明确修改依赖时才重新生成并审查锁文件，不要为绕过错误直接删除它们。

应用输出目录为 `src/RawSpeed.App/bin/Debug/net9.0-windows/`，可执行文件为 `RawSpeed.exe`。它依赖同目录的库、资源及 native 文件，不能只复制 exe 运行。修改代码后重新执行 build；`--no-build` 不会自动编译修改。

启动时打开一张或多张照片，用 `--` 分隔应用参数，带空格的路径加引号：

```powershell
dotnet run --project src/RawSpeed.App -c Debug --no-build -- "D:\Photos\sample.NEF" "D:\Photos\sample 2.JPG"
```

也可在应用内按 Ctrl+O、选择文件夹或拖入照片。支持 `.jpg`、`.jpeg`、`.nef` 和 `.orf`。以上 `dotnet run` 只启动应用，**不会附加调试器或命中 IDE 断点**。

## 3. Visual Studio 断点调试

1. 打开根目录的 `RawSpeed.sln`，等待依赖还原完成。
2. 在解决方案资源管理器中右键 `RawSpeed.App`，选择“设为启动项目”；配置选择 **Debug**。解决方案显示 Any CPU 时，应用项目仍由 `PlatformTarget=x64` 生成 x64 进程，无需为此改项目属性。
3. 在 `src/RawSpeed.App/MainWindow.xaml.cs` 的构造函数设置断点，按 **F5** 开始调试。F10 单步跳过、F11 单步进入、Shift+F5 停止；Ctrl+F5 是不调试启动。
4. 需要自动打开照片时，进入启动项目的“属性 → 调试 → 打开调试启动配置文件 UI”，将命令行参数设置为 `"D:\Photos\sample.NEF"`。这里填写应用参数，不含 `dotnet run` 或 `--`。个人路径不要提交到仓库。
5. 调试已由命令行启动的应用时，使用“调试 → 附加到进程”，选择对应的 **RawSpeed.exe**，使用托管 .NET 调试。确保运行的是当前 Debug 输出，并已加载匹配的 PDB；多个同名进程可通过进程路径或 PID 区分。

`App.xaml` 的 `StartupUri` 创建主窗口；`App.xaml.cs` 安装 UI 异常处理。定位问题时可从以下位置下断点：

| 目标 | 入口或断点 |
| --- | --- |
| 启动、快捷键、视图事件 | `src/RawSpeed.App/MainWindow.xaml.cs` |
| 切图、配方恢复、渲染调度 | `src/RawSpeed.App/ViewModels/EditorViewModel.cs` 的 `LoadPhotoAsync`、`RenderAsync` |
| 配方保存与读取 | `src/RawSpeed.App/Services/RecipeStore.cs` 的 `SaveAsync`、`LoadAsync` |
| 滤镜来源和缓存 | `src/RawSpeed.App/Services/LutCatalogService.cs` |
| RAW 显影、图像导出 | `src/RawSpeed.Imaging/ImageFileService.cs` 的 `LoadPreviewAsync`、`ExportAsync` |
| 像素处理 | `src/RawSpeed.Core/ImageProcessor.cs` 的 `Render` |
| EXIF 读取 | `src/RawSpeed.Metadata/MetadataService.cs` 的 `Read` |

UI 会捕获并显示部分异常；想在抛出位置查看调用栈，可在“调试 → 窗口 → 异常设置”中对目标 CLR 异常启用“引发时中断”。取消任务会正常产生 `OperationCanceledException`，应结合调用上下文判断。渲染在后台线程运行，调试时可查看“线程”和“并行堆栈”窗口；不要在每个像素或高频滑块事件中无条件中断。

## 4. 验证修改

常规验证会以 Release 配置构建整个解决方案，将编译警告视为错误，然后运行各控制台测试和 WPF 控件检查：

```powershell
./scripts/Test.ps1
```

测试项目不是 VSTest/xUnit 测试工程，`dotnet test` 和 Visual Studio“测试资源管理器”不能替代上述脚本。需要调试某个套件时，将对应测试项目设为启动项目并按 F5，或在命令行单独运行，例如：

```powershell
dotnet run --project tests/RawSpeed.App.Tests -c Debug
dotnet run --project tests/RawSpeed.Core.Tests -c Debug
```

可选真实样片放在根目录的 `example/`，准确文件名为 `nikon.NEF`、`olympus.ORF`、`P1010002.JPG`，然后运行：

```powershell
./scripts/Test.ps1 -WithSamples
./scripts/Test.ps1 -Benchmark
```

`-Benchmark` 增加算法基准，不自动启用真实样片集成测试。样片和个人编辑配方不进入 Git；没有样片时记录“未执行样片集成测试”。默认脚本使用生成夹具，不要求用户照片或可选社区预设。可选社区预设的获取方式及许可见 [滤镜目录](lut-catalog.md)。

测试输出位于忽略的 `artifacts/`：`integration-results.json` 为最近一次样片报告，`integration-runs/` 保存独立运行结果，`ui/` 和 `export-ui/` 保存离屏控件图。离屏测试不能替代真实窗口、高 DPI、鼠标拖动及大图库验收。性能比较使用 Release，并记录硬件、图片尺寸、冷/热状态和测量范围。详见 [测试说明](testing.md)。

## 5. 数据位置与常见问题

| 现象或数据 | 处理方式或位置 |
| --- | --- |
| 找不到兼容 SDK、NETSDK1045 | 在仓库中检查 `dotnet --list-sdks` / `dotnet --version`；安装满足 `global.json` 的 x64 .NET 9 SDK，并更新 Visual Studio。 |
| NuGet 还原失败 | 检查网络、代理、包源和具体错误；重新运行 `dotnet restore RawSpeed.sln --locked-mode`。依赖更新应连同锁文件审查。 |
| 构建时报文件被占用、MSB3021/MSB3027 | 先停止调试并关闭从该输出目录启动的 RawSpeed 或测试进程，再构建；不要在应用运行时清理其输出目录。 |
| native DLL 加载失败、BadImageFormatException | 确认 Windows x64 和完整输出目录，重新还原并构建；不要混用旧发布目录的 DLL 或单独复制 exe。 |
| 断点空心、源代码与模块不一致 | 确认启动项目、Debug 配置、附加进程路径和 PDB；停止旧进程，重新构建启动。 |
| WPF 提示线程必须是 STA | WPF 对象须在 STA 线程和 Dispatcher 上使用；App 测试已有相应宿主，新增测试应沿用。 |
| 点取消后 RAW 任务仍运行片刻 | LibRaw/native 调用不能安全地中途打断；完成当前调用后检查取消并丢弃过期结果。排队的 native 工作也会等待进程内信号量。 |
| 编辑配方 | 原文件旁的 `<原文件名含扩展名>.rawspeed.json`，例如 `sample.NEF.rawspeed.json`；保存需要该目录写权限。 |
| 星级与偏好 | `%LocalAppData%\RawSpeed\ratings.json`、`preferences.json`。排查设置前先关闭应用并备份对应文件。 |
| 派生 LUT 缓存 | `%LocalAppData%\RawSpeed\LutCache`；原始预设来自应用输出目录的 `presets/`。 |

原始照片保持只读；导出使用新文件名，已有目标文件会被拒绝。首次 RAW 显影可能耗时数秒，默认编辑缓冲为 8-bit sRGB；选择 16-bit 导出容器不代表全程 16-bit 编辑。

## 6. 可选的本地打包

仅在需要验证脱离源码目录的运行环境时使用；这不是开发步骤，也不会创建或上传 GitHub Release：

```powershell
./scripts/Publish.ps1 -SelfContained
```

默认输出到 `artifacts/publish/win-x64/`；自定义 `-OutputDirectory` 必须是仓库内 `artifacts/publish/` 的子目录。脚本在新的临时目录完成打包后替换目标；已有输出保留为同级的 `<目标目录名>.previous-<GUID>`，失败时保留原有输出。省略 `-SelfContained` 时，目标电脑需安装 .NET 9 Desktop Runtime。脚本保留第三方声明；本地社区预设没有声明可再分发许可证，默认排除。`-IncludeLocalPresets` 仅用于自行准备的本机资源验证，不表示获得对外分发授权。当前源码上传范围和第三方许可要求见 [贡献指南](../CONTRIBUTING.md) 与 [第三方声明](../THIRD-PARTY-NOTICES.md)。
