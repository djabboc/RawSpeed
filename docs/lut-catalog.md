# 滤镜来源、适配和再分发

本地目录包含 31 项：3 个 RawSpeed 调整配方、2 个 RawSpeed 示例 Cube、6 个 RapidRAW 实际内置电影 LUT，以及从官方社区库下载的 20 个完整 `.rrpreset`。后者是参数配方，并不是 20 个原版 Cube。不会把生成的近似风格冒称原版。

## 固定来源

| 来源 | 固定提交 | 数量与格式 | 许可 |
| --- | --- | --- | --- |
| [RapidRAW](https://github.com/CyberTimon/RapidRAW/tree/56aa54b1d16afab7f3ad53a32845c093545704b3/src-tauri/resources/film_luts) | `56aa54b1d16afab7f3ad53a32845c093545704b3` | 6 个 `.cube` | LUT 独立采用 CC BY-SA 4.0 |
| [RapidRAW-Presets](https://github.com/CyberTimon/RapidRAW-Presets/tree/2e08bee4484a0a55db9b91553b19c96c926ef3cc) | `2e08bee4484a0a55db9b91553b19c96c926ef3cc` | 20 个 `.rrpreset`，附原始 manifest | 上游没有提供独立许可 |

RapidRAW 程序采用 AGPL-3.0，但程序的许可不能自动用于独立社区仓库。已检查社区仓库树、GitHub license 字段、投稿模板和原文件；未发现明确的许可条款。RawSpeed 只提交名称/作者/URL/SHA256/长度清单和固定版本下载脚本，`community-local` 已由 Git 忽略，公开发布默认排除它。用户可在本机执行：

```powershell
powershell -ExecutionPolicy Bypass -File presets/rapidraw/download-community.ps1
```

脚本校验所有下载的 SHA256，已存在且校验通过的文件不会重新下载。重新构建本地应用后可读取这些配方；也可用 `-DestinationPath` 指向本地安装目录的 `presets/rapidraw/community-local/originals`。不需要运行应用时联网，也没有发送照片。

6 个电影 LUT 的作者是 **Andrea Volpato**，规范来源为 [spektrafilm](https://github.com/andreavolpato/spektrafilm)。`film/originals` 内全部文件保持字节不变，含原许可全文；`film/sources.json` 有校验值，`film/CHANGELOG.txt` 记录适配行为。它们及派生 LUT 保持 CC BY-SA 4.0；成品照片不受该共享方式要求约束。

## 电影输入约定

六个原 LUT 的输入均为 **Panasonic V-Log**，输出为 sRGB。不能直接套在显示 sRGB 图像上。RawSpeed 的独立适配过程为：

1. 显示 sRGB 通过标准逆传递函数转线性 RGB。
2. 按 `x <= .01 ? 5.6x + .125 : .241514 log10(x + .00873) + .598206` 编码 V-Log。
3. 对保留的原 LUT 进行三线性插值，输出显示 sRGB。
4. 将复合映射采样为 33×33×33 Cube；用户强度在原图和结果之间混合。

RapidRAW 在场景线性 RAW 管线中以电影 LUT 替换常规色调映射，并使用四面体插值。这里是显示域适配，不会重建已裁掉的高光，不承诺和 RapidRAW 逐像素一致。界面、生成文件头以及变更记录均明确标注区别。

## 社区配方覆盖范围

原始 JSON 完整保留，包括当前不支持的字段。RawSpeed 使用自己的颜色算法，不复制 RapidRAW 的 AGPL shader。以下控制被近似适配：曝光和亮度、温度/色调、对比度、阴影/高光、黑白场、饱和度/自然饱和度、全局色相、8 色 HSL、RGB/主曲线及阴影/中间调/高光/全局颜色分级。

- 曲线使用已保存 `curves` 节点，先主曲线再各通道；坐标归一化为 0–1，按 X 排序，同 X 保留最后一个点，线性分段插值。它不是 RapidRAW 的样条曲线。内部用 1025 点查表，之后合成 33³ Cube。
- `curveMode=parametric` 暂用已保存 `curves` 近似，并明确列入限制；没有静默忽略参数化曲线。
- 色温为显示 RGB 增益近似，不是 RAW 白平衡；亮度与曝光以 EV 近似叠加。HSL 为相邻色带插值。分区调色采用亮度权重。
- 空间效果不能塞进逐像素颜色 Cube。启用的清晰度、结构、去雾、锐化、亮度/彩色降噪、暗角、颗粒、辉光、光晕、耀斑等逐项列于 `UnsupportedAdjustments`。另外，未知非零参数、活动色彩校准、嵌套 LUT、非 basic tone mapper 也会列出。调节这些效果的附属字段在主效果未实现时不重复列出。
- “完整复制”指原始配方数据完整保留；适配输出是颜色近似，不是原程序的完整渲染再现。某些预设因此会有明显观感差别。

## 缓存与路径

扫描不生成 LUT；选择风格后在后台生成，缓存默认位于 `%LOCALAPPDATA%/RawSpeed/LutCache`，构造器可指定其他目录。缓存文件名含适配器版本、种类和原始文件 SHA256；原文件变化不会复用旧缓存。写入先使用唯一临时文件，完成后原子替换；取消或失败会清理临时文件。无须写 Program Files。电影适配缓存同时带原许可和变更说明。

缓存只加速颜色转换，不包含照片像素。社区缓存仅用于用户本地，不随公开包再分发。版本升级应递增 `AdapterVersion` 以使缓存失效。

## 验证

`tests/RawSpeed.CatalogTests` 独立验证目录项、来源完整性、缓存重用/源变更、取消和损坏恢复、原图不变、电影的 V-Log 输入适配以及社区黑白/曲线颜色行为；同时验证本地星级持久化。该项目不依赖 WPF 启动，不会更改样张。

2026-10-16 的 Release 验证：6 组测试全部通过，本地 31 项全部可扫描，26 个导入原风格均生成有效 LUT。六个电影各取 1000 个确定性随机 sRGB 输入，33³ Cube 与直接复合映射的最大归一化通道误差为 **0.015254**（约 3.9/255）；这是采样近似误差，不是与 RapidRAW 完整渲染的比较。曲线固定参考值误差低于 0.001，灰度控制保持 R=G=B。星级测试覆盖 30 个并行更新、大小写和相对段路径归一化、损坏 JSON 备份恢复。
