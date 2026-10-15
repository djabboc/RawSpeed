# RAW 编辑器调研与实现依据

调研日期：2026-10-15。目标是 Windows / C# / WPF 上的轻量照片处理工具：快速浏览、构图、调色、元数据和可导入风格。下表比较的是产品定位与设计思路，**没有在相同硬件上运行这些产品的对比基准**，因此不作“比 Lightroom 快几倍”之类的结论。

## 1. 市场工具比较

| 工具 | 相关能力与定位 | 值得借鉴的做法 | 对本项目的取舍 |
| --- | --- | --- | --- |
| Photoshop + Adobe Camera Raw | Camera Raw 负责 RAW 显影、白平衡、曲线、构图等；Photoshop 提供图层、合成、精修 | 将 RAW 显影参数和像素编辑分层；提供一致的调整控件 | 首版聚焦照片显影与快速调整，不引入图层/合成系统。Adobe 官方页本次返回 403，未核验当前版本的具体新增功能和性能数字 [1] |
| Lightroom / Lightroom Classic | 面向摄影的非破坏性调整、预设、图库与批量工作流；Adobe 提供 Denoise / Enhance 相关文档 | 调整参数独立于原片；快捷切片、撤销、批量复用；将慢处理移出交互路径 | 先交付单图编辑、文件夹浏览和可保存预设，图库数据库不是首版前提。官方页面本次返回 403，不能据此声称支持某一最新机型或 GPU 性能水平 [2] |
| RapidRAW | 官方仓库说明为非破坏性、GPU 加速 RAW 编辑器；Rust + React + Tauri + wgpu，RAW 解码使用 rawler；支持 LUT、预设、边车和降噪 | GPU/CPU 阶段分离、预览尺寸分级、减少纹理上传、LRU 缓存、仅为可见图像生成缩略图、ROI 渲染 | 借鉴交互和数据流思想；首版提供通用 `.cube` LUT 导入，不承诺读取 RapidRAW 的全部私有预设与边车结构 [3] |
| DxO PhotoLab / PureRAW | DeepPRIME 系列面向 RAW 去噪与去马赛克；属于专有产品技术 | 将高质量降噪作为独立、可取消的耗时任务；预览与最终输出分离 | 快速传统降噪可先独立交付；不能把双边滤波等同于 DeepPRIME，也不能从营销文案推导可复现算法。官方页面本次返回 403 [4] |
| darktable | 官方列出非破坏性流程、4×32-bit 浮点缓冲、OpenCL、ICC 色彩管理、多级缓存、XMP 边车、Exiv2 元数据 | 线性/浮点处理、色彩管理、分层缓存、参数持久化 | 参考成熟架构，避免首版暴露过多模块；它的 GPL-3.0 代码不能当作任意许可证的现成代码直接复制 [5] |
| RawTherapee | 官方列出非破坏性 32-bit 浮点处理、高级去马赛克、色彩与细节处理、暗场/平场等能力，GPLv3 | 明确图像质量边界、保留高精度中间数据、使用可验证的处理流程 | 首版建立可测的基础管线；高级相机标定、双去马赛克和科学预处理留作后续独立能力 [6] |

RapidRAW 仓库在本次访问时使用 **AGPL-3.0**。网上早期介绍可能提及不同许可证或功能规模，发布前应以实际采用的版本为准。本项目没有复用其源代码。

## 2. “DGO” 的含义

摄影领域的 DGO 常指 Canon **Dual Gain Output**，即传感器双增益读出。这是拍摄硬件/读出技术，不能靠对既有 JPG 或单张 RAW 加一个软件滤镜来重建另一份传感器读出数据。[7]

用户也可能想表达 **DxO** 的 DeepPRIME。两者不是同一个概念。首版将需求落实为可交互的快速、保留边缘的传统降噪，并如实标注算法边界；不宣称实现 Canon DGO 或复刻 DxO AI。后续如加入神经网络降噪，应单独记录模型来源、权重许可证、目标噪声分布、耗时、显存以及细节损失评测。

## 3. 面向高性能的架构选择

1. **WPF 只承担界面与显示。** RAW 解码、元数据读取、滤镜和导出放在后台；新请求取消旧请求，只有最新一帧可回到界面，防止快速拖动滑块时积压几十张过期帧。
2. **解码与调色分开。** 缓存解码结果，修改饱和度/曲线不应重新读取 RAW。预览限制长边尺寸，最终导出使用原始分辨率。缩略图、交互预览、全尺寸导出分别计时。
3. **完整 RAW 解码必须真实可辨。** 内嵌 JPEG 适合快速首屏，但不能将它标为完整 RAW 显影；导出和高质量路径应从 RAW 像素完成解码。预览回退若存在，要在 UI 清楚告知。
4. **合并像素遍历。** 曝光、白平衡、曲线、色相和饱和度尽可能在同一遍像素循环完成；曲线预计算 LUT；并行按行处理；限制并发，避免多幅大图争用全部核心。
5. **不把 CPU 优化称为 GPU 加速。** WPF 合成本身可能使用 GPU，但并不意味着 RAW 解码、降噪或调色已经在 GPU 上执行。若首版采用 CPU 后端，性能报告应直接说明。未来可在相同接口下接入 Direct3D/ComputeSharp，需以真实测试证明收益。
6. **内存是性能预算的一部分。** 2400 万像素 RGB float 图约占 275 MiB，RGBA float 约占 366 MiB；多个临时缓冲很容易超过 1 GiB。优先减少复制、复用缓冲并限制缓存，不能简单用无上限并行解决性能。
7. **色彩空间必须明确。** `.cube` 仅定义数值映射，不必然说明输入是 sRGB、线性 RGB 或 Log。首版应规定导入 LUT 的工作空间，明确它不等于 Adobe 相机 profile，也不保证重现其他软件的同名预设。

LibRaw 适合负责 RAW 数据、必要的相机处理参数和内嵌预览提取。其官方特别说明：内建的基础 RGB 转换并不以生产级显影为范围，因此“接上 LibRaw”不能等价于获得 Adobe/DxO 的完整成像质量。[8]

## 4. 完整 EXIF 的可验证定义

本项目的“完整”定义为：**列出当前解码器能够读取的全部目录和字段，不仅显示快门、光圈、ISO 等摘要**。包括 Exif、GPS、Nikon/Olympus MakerNotes、IPTC、XMP、ICC 和图像结构等实际存在的目录。

`RawSpeed.Metadata` 使用 MetadataExtractor **2.9.3**，提供：

```csharp
public sealed record MetadataEntry(string Group, string Name, string Value);
public IReadOnlyList<MetadataEntry> MetadataService.Read(string path);
```

实现保留数字 tag ID、重复目录、未知 tag 名称和解码警告。XMP 使用独立的属性路径枚举；只遍历普通 `Tags` 会遗漏 XMP 的实际字段，这是实现中特别处理的地方。[9]

不能承诺解开所有厂商加密/私有字段；库对某些二进制值只给出描述而不解释其内部结构。元数据不存在时不推算、不补造。读取不修改文件；调用方应在后台线程执行。EXIF 显示也不等于导出时已自动保留全部隐私数据；导出元数据策略必须单独明确。

已在用户提供的三张样片上完成实际读取检查：

| 样片 | 相机型号 | 返回字段数 | 解码警告 | 已确认的目录 |
| --- | --- | ---: | ---: | --- |
| `nikon.NEF` | NIKON D4 | 169 | 0 | Exif IFD0 / SubIFD、Nikon Makernote、Nikon PictureControl 1、GPS、XMP 等 |
| `olympus.ORF` | E-M5MarkII | 468 | 0 | Olympus Makernote、Equipment、Camera Settings、Raw Development、Image Processing、Focus Info 等 |
| `P1010002.JPG` | E-M5MarkII | 432 | 0 | JPEG、Exif、Olympus MakerNotes 子目录、GPS、Interop、缩略图等 |

字段数包括文件信息和目录摘要，随解码库版本变化属于正常现象。另验证空路径和不存在文件会明确报错。这验证了**这些样片**，不等于所有 Nikon/OM System 机型兼容性认证。

## 5. 开源与分发注意事项

- MetadataExtractor 2.9.3 为 Apache-2.0；保留版权与许可证。其传递依赖 XmpCore 6.1.10.1 的 BSD 许可说明也应包含在发行材料中。[9][10]
- LibRaw 官方提供 LGPL-2.1 / CDDL-1.0 双许可证选择；分发时要固定版本、明确选用的许可证，并保留随二进制附带的许可证与源码/修改信息。若通过其他图像库间接使用 LibRaw，还需检查实际构建的依赖清单，不能仅看外层包装库的许可证。[8]
- darktable / RawTherapee 为 GPLv3，RapidRAW 当前为 AGPLv3。可以研究公开行为、算法论文与接口；直接复制实现会引入对应许可证义务。[3][5][6]
- 商用 LUT、相机 profile、示例照片、品牌 logo 和 AI 权重有各自权利，不能因为程序开源就自动获得再分发许可。用户样片默认仅用于本地验证；不应自动提交 GitHub。
- 应提交依赖锁定信息、测试步骤、性能测量方法和已知限制；性能结果标明 CPU、内存、图像尺寸、Release/Debug、冷/热缓存及预览长边，不做跨机器的绝对性能承诺。

## 6. 官方来源与访问记录

以下只使用产品官方站点或官方项目仓库。访问受限的资料保留为参考入口，并明确没有成功读取其当前正文。

1. Adobe Camera Raw：[介绍文档](https://helpx.adobe.com/camera-raw/using/introduction-camera-raw.html)、[产品页](https://www.adobe.com/products/photoshop/adobe-camera-raw.html)。本次 HTTP 403。
2. Lightroom：[功能](https://www.adobe.com/products/photoshop-lightroom/features.html)、[Enhance / Denoise 文档](https://helpx.adobe.com/lightroom-classic/help/enhance-details.html)。本次 HTTP 403。
3. RapidRAW：[官方仓库 README](https://github.com/CyberTimon/RapidRAW/blob/main/README.md)、[LICENSE](https://github.com/CyberTimon/RapidRAW/blob/main/LICENSE)。README 本次读取成功；其中有架构、近期性能调整、LUT、降噪和 AGPL-3.0 说明。
4. DxO：[DeepPRIME 技术](https://www.dxo.com/technology/deepprime/)、[PureRAW](https://www.dxo.com/dxo-pureraw/)。本次 HTTP 403；未验证当前版本性能数字。
5. darktable：[官方 features](https://www.darktable.org/about/features/)。本次读取成功。
6. RawTherapee：[官方网站](https://rawtherapee.com/)。本次读取成功。
7. Canon：[Dual Gain Output 说明](https://www.usa.canon.com/learning/training-articles/training-articles-list/dual-gain-output-dgo-sensor)。本次 HTTP 403；DGO 术语需与用户实际指代区分。
8. LibRaw：[范围和许可证](https://www.libraw.org/about)、[发布包](https://www.libraw.org/download)。本次读取成功。
9. MetadataExtractor：[官方说明](https://github.com/drewnoakes/metadata-extractor-dotnet)、[XMP 实现接口](https://github.com/drewnoakes/metadata-extractor-dotnet/blob/main/MetadataExtractor/Formats/Xmp/XmpDirectory.cs)、[NuGet 版本](https://www.nuget.org/packages/MetadataExtractor/2.9.3)。官方 README、API 源码和 NuGet 版本索引本次读取成功。
10. MetadataExtractor：[LICENSE 与 XmpCore 许可声明](https://github.com/drewnoakes/metadata-extractor-dotnet/blob/main/LICENSE)。本次读取成功；XmpCore 实际包版本另经还原后的 NuGet 清单确认。
