# 原始许可材料的来源

获取日期：2026-10-16。本目录保存原始上游文本和核验来源；下载/复制的许可正文未改写。文件哈希见 `SHA256SUMS`。这些文件可由发布脚本整体复制到发行目录。

## 原文与版本

| 本地文件 | 来源及版本关联 |
| --- | --- |
| `Magick.NET-15.0.0-NOTICE.txt` | 从官方 NuGet `Magick.NET-Q16-AnyCPU` 15.0.0 包根目录 `Notice.txt` 逐字节复制，SHA-256 已与已还原包比较。它包含 ImageMagick 7.1.2-33、LibRaw 0.22.2 及其他 native 组件的完整声明。[NuGet 包](https://www.nuget.org/packages/Magick.NET-Q16-AnyCPU/15.0.0) |
| `Magick.NET-15.0.0-LICENSE.txt` | [官方仓库原文](https://raw.githubusercontent.com/dlemstra/Magick.NET/79bb8700d7fe3a564f62926ab299fc2520cb2269/License.txt)。固定提交与 Magick.NET-Q16-AnyCPU、Magick.NET.Core 15.0.0 的 `.nuspec` 一致；Apache-2.0 正文同时适用于这两个托管包。 |
| `Magick.NET-15.0.0-Copyright.txt` | [同一提交的原始声明](https://raw.githubusercontent.com/dlemstra/Magick.NET/79bb8700d7fe3a564f62926ab299fc2520cb2269/src/Magick.NET/Copyright.txt)。 |
| `MetadataExtractor-2.9.3-LICENSE.txt` | [官方仓库原文](https://raw.githubusercontent.com/drewnoakes/metadata-extractor-dotnet/15be36f1f918e6cdba0bfa8feb7af7f8ac13b69f/LICENSE)。固定提交与 2.9.3 `.nuspec` 一致，含 Apache-2.0 正文及上游声明。 |
| `XmpCore-6.1.10.1-README.md` | [官方 6.1.10.1 tag 对应提交的原文](https://raw.githubusercontent.com/drewnoakes/xmp-core-dotnet/66b975558d9199cd3eb49093261a61deb30f8c06/README.md)。该 README 明确声明沿用 Adobe XMP SDK 的 BSD 许可证；tag 与提交的映射通过官方 GitHub API 核验。 |
| `Adobe-XMP-SDK-2020.1-BSD-LICENSE.txt` | [Adobe 官方 SDK v2020.1 的完整 BSD-3-Clause 原文](https://raw.githubusercontent.com/adobe/XMP-Toolkit-SDK/42eb5267d1ffc1d22066438599a9e2dec020d9ca/LICENSE)。保留原有版本与版权年份，未假称它来自 XmpCore NuGet 包内部。与历史 Adobe EULA 的对应关系边界见下节。 |

`provenance/` 中四份 `.nuspec` 是实际还原的 Magick.NET-Q16-AnyCPU 15.0.0、Magick.NET.Core 15.0.0、MetadataExtractor 2.9.3、XmpCore 6.1.10.1 原始包清单，保存依赖、许可证 URL 与可用的 repository commit 信息。

## XmpCore 的历史来源边界

XmpCore 6.1.10.1 的包清单与该版本 README 指向的原始许可地址为：

<https://www.adobe.com/devnet/xmp/library/eula-xmp-library-java.html>

本次访问返回 HTTP 403；该 XmpCore tag 和 NuGet 包也没有独立的许可证正文文件。因此本目录提供其**准确版本 README/manifest 声明**，并附**Adobe 官方 SDK 固定版本的完整 BSD-3-Clause 正文**，而不是推测或自行拼写一份历史 EULA。不能声称已经从失效/受限地址取回那一版网页原文。

作为版本交叉核验，读取了 Adobe 发布到 Maven Central 的 `com.adobe.xmp:xmpcore:6.1.10`：

- [该版本 POM](https://repo.maven.apache.org/maven2/com/adobe/xmp/xmpcore/6.1.10/xmpcore-6.1.10.pom) 声明 BSD，并指向同一个 Adobe EULA；原文保存在 `provenance/Adobe-xmpcore-6.1.10.pom`。
- [该版本 sources.jar](https://repo.maven.apache.org/maven2/com/adobe/xmp/xmpcore/6.1.10/xmpcore-6.1.10-sources.jar) 的 `META-INF/LICENSE` 却包含 “ADOBE CONFIDENTIAL” 和限制传播的文字。644 字节原文单独保存在 `provenance/Adobe-xmpcore-6.1.10-ARCHIVE-LICENSE-CONFLICT.txt`，明确标为**冲突证据**，没有把它冒充为 BSD 正文。
- 所检查 sources.jar 的 SHA-256 为 `B33979C17D47A9A04D877394A602A6F4E705D66792CFEDAD769EF417565D2C1A`。该 Java archive 仅用于来源核验，不是本应用依赖，也未作为程序文件加入发行目录。

上述来源存在不一致，本项目不替上游作法律裁定。当前材料准确保存了 .NET 上游的 BSD 声明、Adobe 官方 BSD 全文和发现的不一致，供发行复核使用。

## 发行范围

保留本目录完整原始文本与 `THIRD-PARTY-NOTICES.md`，仍不能代替对最终 native 组合、LGPL 源码/重新链接材料和 self-contained .NET 运行时的实际发行检查。上游版本变更时应重新下载与核验来源，不沿用旧文件名或旧哈希。
