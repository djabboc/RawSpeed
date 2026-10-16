# 第三方依赖与发行材料

本文件是当前依赖的来源、许可证索引与发行准备记录，核验日期为 2026-10-16。它不替代上游完整 LICENSE / NOTICE，也不表示最终二进制发行的许可证审查已完成。项目自身许可证与署名应由项目维护者决定；这里没有为项目填写版权归属。

## 当前 .NET 依赖

已对照本地还原后的 `.nuspec` 与 `src/RawSpeed.App/obj/project.assets.json`。当前 .NET 9 应用的外部 NuGet 图包含以下四个包：

| 包 | 实际版本 | 用途 | 上游许可证与依据 |
| --- | --- | --- | --- |
| Magick.NET-Q16-AnyCPU | 15.0.0 | 图像 API 与 native 运行库 | NuGet manifest 为 Apache-2.0；native 组合另见下节。[项目](https://github.com/dlemstra/Magick.NET)、[版本](https://www.nuget.org/packages/Magick.NET-Q16-AnyCPU/15.0.0) |
| Magick.NET.Core | 15.0.0 | Magick.NET 托管 API，前者的传递依赖 | NuGet manifest 为 Apache-2.0。[包](https://www.nuget.org/packages/Magick.NET.Core/15.0.0) |
| MetadataExtractor | 2.9.3 | EXIF / MakerNotes / IPTC / XMP 等元数据 | Apache-2.0。[源码及许可证](https://github.com/drewnoakes/metadata-extractor-dotnet/blob/main/LICENSE)、[包](https://www.nuget.org/packages/MetadataExtractor/2.9.3) |
| XmpCore | 6.1.10.1 | MetadataExtractor 的 XMP 传递依赖 | 该版本 README 声明沿用 Adobe XMP SDK 的 BSD 许可证；已保留准确版本声明与 Adobe 官方 SDK 的 BSD-3-Clause 全文，历史链接受限与上游 archive 冲突见[来源记录](third-party-notices/SOURCES.md)。[包](https://www.nuget.org/packages/XmpCore/6.1.10.1) |

MetadataExtractor 在不同目标框架还声明了其他依赖；本应用使用其 `net8.0` 资产，当前 .NET 9 解析结果没有把 `System.Memory` 等旧目标框架补充包引入应用。更改目标框架后应重新以实际资产图为准。

## Magick.NET 中的 native 组件

外层 NuGet 的 Apache-2.0 **不覆盖所有打包的 native 库**。本地包的 `Notice.txt` 长约 428 KB，包含各组件完整版权/许可说明。所用 Windows x64 native 文件为 `runtimes/win-x64/native/Magick.Native-Q16-x64.dll`，因此只列四个 NuGet 包不足以描述二进制发行内容。

| 组件 | 当前包 Notice 声明版本 | 许可证核验情况 |
| --- | --- | --- |
| ImageMagick | 7.1.2-33，2026-10-10 | ImageMagick License；完整文本在包内 Notice，另见[官方许可](https://imagemagick.org/license/) |
| LibRaw | 0.22.2，2026-07-16 | 此包 Notice 的 LibRaw 段附 LGPL-2.1 正文。LibRaw 上游另提供 LGPL-2.1 / CDDL-1.0 双许可，但不能据此直接改写已经取得的二进制包的许可处理方式。[官方说明](https://www.libraw.org/about) |
| libjpeg-turbo | 3.2.0 | 包内含对应 IJG / BSD / zlib 系列说明；需保留原文和组合要求 |
| libpng | 1.6.59 | 以包内 libpng 许可正文为准 |
| lcms | 2.19.1 | 色彩管理库；以包内许可正文为准 |
| libtiff | 4.7.2 | TIFF 编解码库；以包内许可正文为准 |
| zlib | 1.3.2 | PNG 等压缩支持；以包内许可正文为准 |

包内 Notice 还列出：aom、brotli、libbzip2、cairo、libcroco、libde265、openexr、libffi、fontconfig、freetype、fribidi、gdk-pixbuf、glib、harfbuzz、libheif、libhwy、imath、libjxl、liblqr、liblzma、openh264、openjpeg、openjph、pango、pixman、libraqm、librsvg、libwebp、libxml2、libzip、zstd。这里是**包级 Notice 清单**，并未证明所有组件在每个 RID 下都启用；也不能因为界面仅允许 JPG/NEF/ORF 就认定其他已经打包的 native 组件可以忽略。准确的发布清单需结合目标 RID、native 构建配置和最终产物。

其中存在 LGPL 等具有额外要求的组件。需要审查其在组合 DLL 中的实际链接方式、对应源代码/修改、用户替换或重新链接所需材料，以及其他适用发行义务；不能只附上一个 Apache-2.0 链接便声称完成合规。

## 可复核的本地材料

NuGet 全局包目录可通过 `dotnet nuget locals global-packages --list` 查到；以下路径均相对于该目录，避免绑定某一开发者的用户名：

```text
magick.net-q16-anycpu/15.0.0/magick.net-q16-anycpu.nuspec
magick.net-q16-anycpu/15.0.0/Notice.txt
magick.net-q16-anycpu/15.0.0/runtimes/win-x64/native/Magick.Native-Q16-x64.dll
magick.net.core/15.0.0/magick.net.core.nuspec
metadataextractor/2.9.3/metadataextractor.nuspec
xmpcore/6.1.10.1/xmpcore.nuspec
```

Magick.NET 的 `.nuspec` 记录上游提交 `79bb8700d7fe3a564f62926ab299fc2520cb2269`；MetadataExtractor 记录提交 `15be36f1f918e6cdba0bfa8feb7af7f8ac13b69f`。许可证正文已从这些固定提交取得。XmpCore 6.1.10.1 官方 tag 对应提交 `66b975558d9199cd3eb49093261a61deb30f8c06`，也已保存其 README 声明。这些信息帮助定位来源，但不能代替 native 各组件对应源码与构建记录。

## 已随仓库保存的完整材料

`third-party-notices/` 已包含：

- Magick.NET 15.0.0 原始完整 native `Notice.txt`、Apache-2.0 正文与原始版权声明，覆盖 Magick.NET.Core 的托管许可证。
- MetadataExtractor 2.9.3 的完整上游 LICENSE。
- XmpCore 6.1.10.1 准确版本 README、包 manifest，以及 Adobe 官方 SDK 固定版本的完整 BSD-3-Clause 正文。
- 原始包清单、XMP 历史来源冲突证据、固定来源 URL 与 `SHA256SUMS`。

详见 [SOURCES.md](third-party-notices/SOURCES.md)。Magick.NET 完整 Notice 已与包原文作字节级哈希比对。XmpCore 的历史 Adobe EULA 地址返回 403；Adobe Java 6.1.10 POM 声明 BSD，但其 source archive 中另有冲突文字。已准确记录这一区别，没有把现代 BSD 正文伪称为历史 EULA 原件，也没有替上游裁定。

本次查看的 Magick.NET build/buildTransitive targets 负责 native 文件复制，没有看到自动把 `Notice.txt` 发布到发行目录的规则。发布脚本应复制已准备的整个 `third-party-notices/` 与本文件，并实际检查最终目录，不应假定 `dotnet publish` 会收集所有许可证。

## 发布前需要完成的材料

1. 从最终 `win-x64` 产物和依赖资产图生成清单，记录版本、下载来源、哈希及 native 构建来源。
2. 将本仓库已准备的**完整原始许可与 Notice 目录**附入发行物，并核实需要的 native 来源/重新链接材料；保留原始声明，不以本索引替换。
3. 核实所有 LGPL 或其他带附加义务的组件实际分发方式。若改为自编译裁剪版，应保存修改、构建开关和可复现构建说明，并重新测试三类输入和输出。
4. 若使用 self-contained .NET 发布，额外保留该运行时分发包要求的许可和 notices。框架依赖部署则在安装说明明确 Windows Desktop Runtime 要求。
5. 检查 LUT、样片、图标与未来模型权重各自的再分发许可。`example/` 和 `artifacts/` 当前用于本地验证，不因程序公开源码而自动获得照片的发布权。

RapidRAW、darktable、RawTherapee 的应用源码未作为本项目依赖引入。0.2 附带 RapidRAW 中的六份 Spektrafilm 原始 LUT（Andrea Volpato，CC BY-SA 4.0），原始许可、固定提交和 SHA-256 位于 `presets/rapidraw/film/`；sRGB 适配和修改说明见 [滤镜目录](docs/lut-catalog.md)。适配缓存保留来源和相同许可标注。

官方 RapidRAW-Presets 社区仓库没有声明再分发许可，其二十份配方仅下载到被忽略的 `community-local` 本地目录，不进入 Git 或默认发布包。仓库保存固定版本下载脚本与来源清单。不能把 RapidRAW 主项目的 AGPL 许可套用到这个独立仓库，也不能将参数近似适配描述为原程序逐像素复现。
