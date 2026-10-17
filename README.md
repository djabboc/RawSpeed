# RawSpeed

面向 Windows 的轻量 RAW 照片编辑器，使用 **C#、WPF、.NET 9**。当前版本 **0.2 技术预览**，支持 JPEG、Nikon NEF、Olympus ORF，重点是快速构图、调色和选片。

## 运行

Windows 10/11 x64。开发需要 .NET 9 SDK（仓库使用 `global.json` 选择 SDK）；本地编译与调试见 [开发指南](docs/development.md)。在仓库根目录执行：

```powershell
dotnet restore RawSpeed.sln --locked-mode
dotnet build RawSpeed.sln -c Debug --no-restore
dotnet run --project src/RawSpeed.App -c Debug --no-build
```

支持拖入文件或文件夹、命令行传入照片路径。首次显示相机内嵌预览，再替换为 LibRaw 真实显影结果；默认工作图最长边 1600 像素。点击 **100%** 或按 **D** 加载原尺寸细节。鼠标滚轮围绕指针缩放，拖动照片平移，**F** 适应窗口；缩放百分比也可直接输入。

## 编辑与选片

- **编辑**：曝光、对比度、高光、阴影、中间调、色温、色调、色相、饱和度；灰点取样白平衡；RGB 合成及独立 R/G/B 自由点曲线。单击曲线加点、拖动调整、右键或 Delete 删除内点，端点输出值可调；输入/输出可直接填写 0–255。
- **构图**：八个边角控制点、移动裁框、Shift 拖动重新框选；松开鼠标即生效，切出构图页显示裁切结果。支持 ±180° 自由旋转、90° 旋转、镜像和比例构图；旋转自动填满画布。
- **降噪**：独立亮度、彩色噪点和细节保护，源图坐标多尺度处理。建议用原尺寸 100% 预览判断；降噪结果缓存可供后续调色复用。
- **LUT**：搜索和选择列表中的风格，导入标准 3D `.cube`，调整 LUT 强度。自然、暖胶片、黑白是参数预设，强度滑块仅作用于 LUT。
- **EXIF**：显示底层库可解码的全部目录与标签，包括 tag ID、MakerNotes、GPS、IPTC、XMP 和解析警告；支持搜索、复制。未知或加密的相机私有信息不保证可解释。
- 所有数值调节都支持直接输入与恢复默认；滑条双击亦可恢复。曲线支持当前通道和全部通道重置，构图与降噪可分别重置。保留撤销/重做。
- 原图、左右和可拖动划线对比使用相同构图，缩放和平移同步。这里的“原图”是未调色、未降噪的解码图，保留当前裁切和旋转以便对齐。
- **1–5** 设置星级，**0** 清除；支持按最低星级筛选、按星级分组、评级后自动下一张。左右箭头切图。
- 拖动左右面板及直方图的分隔线改变布局，缩略图大小可调。设置随退出保存。

**Ctrl+O / Ctrl+S / Ctrl+E** 打开、保存配方、导出；**Ctrl+Z / Ctrl+Y** 撤销、重做；**B / Y / C** 原图、切换对比方式、构图。窗口右上角可修改键盘及滚轮快捷键，支持冲突检查和恢复默认。输入数字时不会误触星级快捷键。

配方保存到原文件旁的 `文件名.rawspeed.json`，下次打开恢复；未保存的调整在退出时提示。星级和界面偏好保存到 `%LocalAppData%\RawSpeed`，不会写入原始照片。

## 导出与风格资源

导出对话框支持 JPEG/PNG/TIFF、质量/压缩、渐进 JPEG、原尺寸/长边/指定宽高、保持比例、禁止放大、DPI、sRGB/Adobe RGB、8/16 位容器，以及常用拍摄 EXIF 与可选 GPS。输出使用新文件名，不覆盖现有文件。详细说明见 [导出与快捷键](docs/export-and-shortcuts.md)。

本地完整目录为 **31 项**：3 个内置参数预设、2 个原创 `.cube`、6 个 RapidRAW 电影 LUT、20 个官方社区参数预设的近似适配。电影 LUT 的 V-Log 输入已做显式色彩适配，原文件和 CC BY-SA 4.0 许可均保留。社区预设不是原生 LUT，未支持的空间参数在界面中显示；不宣称与 RapidRAW 逐像素一致。

社区仓库没有声明可再分发许可证，因此原配方已经下载到本地，但由 Git 和默认公开发布排除。克隆仓库后可运行固定版本下载脚本获取：

```powershell
./presets/rapidraw/download-community.ps1
```

原文件、提交版本、SHA-256、作者、适配方法见 [滤镜目录](docs/lut-catalog.md)。标准 sRGB `.cube` 直接导入；任意第三方 Log/HDR LUT 不会自动猜测其输入空间。

## 验证与性能

```powershell
./scripts/Test.ps1
./scripts/Test.ps1 -WithSamples
./scripts/Test.ps1 -Benchmark
```

测试覆盖算法、WPF 会话/历史/取消、裁切交互几何、滤镜来源和缓存、星级持久化、导出和回读、快捷键、实际 WPF 控件布局及绑定。样张测试校验 JPG/NEF/ORF 原文件 SHA-256 不变。`example`、输出图像、构建缓存不进入 Git。CI 使用生成夹具，不依赖用户照片。

本机 32 逻辑处理器、Release，在 Nikon D4 NEF（4940×3292）上，亮度 70 / 彩色 85 / 细节 50 的降噪预热中位数：1600px 预览约 **51 ms**，原尺寸约 **394 ms**。这是算法时间，不包含解码、WPF 显示、文件写出；不能作为其他电脑或竞品性能承诺。详细方法、实际裁片和限制见 [降噪与性能](docs/denoise-and-performance.md) 和 [测试记录](docs/testing.md)。

## 当前边界

- **编辑缓冲仍为 8-bit sRGB**。RAW 先由 LibRaw 以 Q16 显影，再转为显示域缓冲。16-bit PNG/TIFF 和 Adobe RGB 导出不恢复已经丢失的精度或原始色域；极端高光恢复、精确 Kelvin、场景线性编辑和 GPU 管线尚未实现。
- 降噪是多线程 CPU 算法，没有 AI 模型，不声称达到 DxO DeepPRIME。强降噪仍可能软化细节；DGO 是传感器双增益技术，软件后处理不能复现硬件采集。
- 高分辨率只保留当前照片；首次真实 RAW 解码仍可能需要数秒。Native 调用开始后只能等待其结束再丢弃取消结果。导出和界面有独立取消控制。
- 拍摄 EXIF 导出使用安全字段列表，不完整复制原片 MakerNotes、IPTC、XMP 或缩略图。查看器仍显示可读取的完整元数据。
- 三张样片不代表所有 Nikon/Olympus 型号和压缩模式。尚无局部蒙版、镜头校正、AI、批量导出或 XMP/LR 预设导入。
- 当前使用机器已有的 .NET 9；长期维护应迁移受支持的 .NET LTS 并重跑测试。

## 工程

`Core` 为无第三方依赖的算法；`Imaging` 负责解码/编码；`Metadata` 负责元数据；`App` 负责 WPF/MVVM 和调度。参见 [开发指南](docs/development.md)、[架构](docs/architecture.md)、[竞品调研](docs/research.md)、[贡献指南](CONTRIBUTING.md)、[第三方声明](THIRD-PARTY-NOTICES.md)。

源码仓库：[djabboc/RawSpeed](https://github.com/djabboc/RawSpeed)。目前仅维护源码，不提供 GitHub Release；本地开发无需打包。保留原资源许可和来源；项目自身许可证尚未选定，公开源码不自动授予额外使用或再分发许可。
