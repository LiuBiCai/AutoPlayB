# AutoPlay 脚本引擎原型

Windows 自动化引擎。每个工作进程运行一个编译后的 C# 脚本；进程之间互不共享截图或脚本状态。画面源与输出分别抽象，支持 Win32 窗口截图和消息输入，也支持通过 LunarNX 的本地命名管道截图及控制 Xbox 手柄。引擎提供模板匹配、颜色匹配、Windows OCR 和可选的识别标注。

这是基础原型，不包含多进程管理器、进程重启或资源调度。尚未对几十个窗口进行压力测试，不能保证具体游戏中的帧率或资源占用。建议按需降低截图和 OCR 频率、缩小搜索区域，并先对目标游戏实测。

## 环境和构建

- Windows 10 1809 或更新版本，.NET 9 SDK；本机需要可用的 Windows 桌面会话。
- 首次构建需还原 NuGet 依赖（OpenCvSharp 及其 Windows 原生运行库）。
- OCR 使用 Windows 自带 OCR，必须在 Windows 设置中安装所需语言的 OCR 功能。示例使用 `zh-CN`；未安装会明确报错。

在项目根目录执行：

```powershell
dotnet build AutoPlay.sln
dotnet run --no-build --project AutoPlay.Tests\AutoPlay.Tests.csproj
dotnet run --no-build --project AutoPlay.Host\AutoPlay.Host.csproj -- list
```

选择 `list` 输出的窗口句柄，例如：

```powershell
dotnet run --no-build --project AutoPlay.Host\AutoPlay.Host.csproj -- run 0x123456 .\SampleScript\bin\Debug\net9.0-windows10.0.17763.0\SampleScript.dll --test
```

`--test` 启用覆盖层；示例还会在当前工作目录写入 `diagnostics-<窗口句柄>.png`。橙色框为搜索区域，绿色框为命中位置。覆盖层不接收鼠标输入，每次识别更新，跟随窗口移动；截图标注由脚本显式调用 `SaveAnnotated`。覆盖层始终置顶，可能出现在遮挡目标窗口的其他窗口之上，测试结束会关闭。按 Ctrl+C 请求脚本停止。

示例测试模式不点击，正常模式会点击识别到的“开始”。**引擎本身不会阻止测试模式下的输入**：自定义脚本必须自行检查 `context.TestMode`。

多个窗口可分别启动工作进程，例如在项目根目录：

```powershell
$hostDll = (Resolve-Path .\AutoPlay.Host\bin\Debug\net9.0-windows10.0.17763.0\AutoPlay.Host.dll).Path
$scriptDll = (Resolve-Path .\SampleScript\bin\Debug\net9.0-windows10.0.17763.0\SampleScript.dll).Path
$handles = @('0x123456', '0x234567') # 替换为 list 中实际句柄
foreach ($handle in $handles) {
    Start-Process dotnet -ArgumentList @("`"$hostDll`"", 'run', $handle, "`"$scriptDll`"", '--test')
}
```

每个进程单独停止。不要对同一窗口同时运行多个会操作窗口的脚本。

## LunarNX 管道

参照 [LunarNX 开发文档](../LunarNX/docs/development.md) 启动 Windows Xbox Remote Play，使用不同的 profile 和 pipe 名称，例如：

```powershell
& E:\Source\Github\LunarNX\build\windows-msvc\Release\LunarNX_Desktop.exe --profile account1 --console "Living Room Xbox" --automation --pipe-name LunarNX-account1
```

使用 LunarNX 输出的本地管道名或完整 `\\.\pipe\...` 路径，在本项目根目录启动：

```powershell
dotnet run --no-build --project AutoPlay.Host\AutoPlay.Host.csproj -- run-pipe LunarNX-account1 .\LunarSample\bin\Debug\net9.0-windows10.0.17763.0\LunarSample.dll --test
```

示例在测试模式保存 `lunarnx-diagnostics.png`，不发送手柄指令。需要实时覆盖层时，先用 `list` 找到**该 LunarNX 流窗口**的句柄，然后在命令末尾追加 `--overlay-window 0x123456`。覆盖层将截图坐标按截图和窗口客户端尺寸缩放；如果选错窗口或窗口内容并非对应的截图，标注会错位。未指定句柄时仍可保存标注截图，但不显示覆盖层。

脚本模式与手动模式由 LunarNX 窗口的 F8 切换；管道在手动模式仍能返回成功，但输入会被忽略。LunarNX 输入是**完整状态替换**：遗漏的按钮和轴归零，超过 250 毫秒未刷新以及断开连接都会释放。`SetStateAsync` 仅发送一次，不会自动保持；`HoldAsync` 每约 100 毫秒刷新并在结束或取消时请求释放。调用 `HoldAsync` 期间不要并发进行截图或其他管道请求，否则串行管道上的耗时请求可能让状态超时；操作系统调度或服务端延迟也可能造成短暂释放。管道失败或请求取消后连接关闭，不自动重连或重放输入；需重启脚本。单次请求最长等待 8 秒，截图限制为宽高各不超过 4096 像素。

每个 LunarNX 实例需要独立的 profile 和管道、对应一个脚本工作进程；参考 LunarNX 文档避免多个 profile 同时使用同一账号。管道可控制已登录的会话，不要通过网络转发，也不要分享令牌。

## 编写脚本

参考 [Win32 示例](SampleScript/ExampleScript.cs)和 [LunarNX 示例](LunarSample/ExampleScript.cs)。创建 C# 类库并引用 `AutoPlay.Engine` 项目；程序集中必须恰有一个非抽象 `IGameScript` 实现，具有公共无参构造函数。构建后将 DLL 路径传给运行器。脚本与运行器在同一进程内执行，**只运行可信脚本**。如果脚本使用额外 NuGet 依赖，在脚本项目中设置 `CopyLocalLockFileAssemblies` 为 `true`，并保留输出目录及其 `.deps.json`。

```csharp
using var frame = context.Capture();
var image = frame.FindImage(@"assets\button.png", threshold: 0.95);
var color = frame.FindColor(System.Drawing.Color.Red, tolerance: 10);
var text = await frame.FindTextAsync("开始", "zh-CN", cancellationToken: cancellationToken);
if (context.TestMode)
    frame.SaveAnnotated("debug.png");
else if (image is not null)
    context.Window.Click(image.Center);
```

每次 `Capture` 返回一帧；该帧上的识别共用截图和像素数据，模板按路径在工作进程内缓存，修改模板后需重启进程。使用 `using` 释放帧；脚本循环应使用支持取消的 `Task.Delay`。不要并发使用同一 `ScriptContext` 或 `RecognitionFrame`。

通用脚本使用 `await context.CaptureAsync(cancellationToken)`，Win32 的同步 `Capture()` 和 `Window` 接口仍然可用。`context.Input` 提供窗口点击和按键，`context.Gamepad` 提供手柄状态与释放；未配置相应输出时调用会报错。LunarNX 只提供手柄输入，识别坐标不能直接当作鼠标点击。可自行实现 `IFrameSource`、`IWindowInput`、`IGamepadOutput` 和 `IOcrProvider`，通过 `ScriptContext` 构造函数组合；画面源应返回调用者拥有的 `Format32bppArgb` 位图，调用者负责释放自定义源和输出。

OCR 默认使用 `WindowsOcrProvider`；创建 `ScriptContext` 时可通过 `ocrProvider` 参数注入任何实现 `IOcrProvider` 的实现，无需修改识别调用。提供者接收已裁剪的 `Bitmap`、可选语言标签和取消令牌，并返回相对于该位图左上角的 `OcrTextLine` 列表；`RecognitionFrame.FindTextAsync` 会按 `region` 参数加回截图坐标、进行不区分大小写的文本查找，并在测试标注中显示命中。命中框必须为正尺寸且完全位于传入图像内，违规结果会抛出 `InvalidDataException`。`Confidence` 可选，由具体 OCR 提供者定义；Windows OCR 当前不返回该分数。

```csharp
using var context = new ScriptContext(
    source,
    ocrProvider: new MyOcrProvider());
```

### 内置 OCR 提供者

运行器可用 `--ocr windows|rapid|paddle|tesseract` 选择后端；默认是 Windows OCR。模型不随仓库或 NuGet 自动下载。每个脚本进程会各自初始化和持有所选 OCR 模型；几十个窗口同时启用深度学习 OCR 会重复占用内存和 CPU/GPU，建议先测单实例资源，再决定是否降低窗口数或单独部署共享识别服务。

**RapidOCR（.NET/ONNX）**：安装 RapidOCRLib NuGet 依赖已包含在构建中；从 RapidOCR 项目下载匹配的 PP-OCRv5 mobile 检测、方向分类、识别模型及字典，放入同一目录：

```text
ch_PP-OCRv5_mobile_det.onnx
ch_ppocr_mobile_v2.0_cls_infer.onnx
ch_PP-OCRv5_rec_mobile_infer.onnx
ppocrv5_dict.txt
```

```powershell
dotnet run --no-build --project AutoPlay.Host\AutoPlay.Host.csproj -- run-pipe LunarNX-account1 .\LunarSample\bin\Debug\net9.0-windows10.0.17763.0\LunarSample.dll --ocr rapid --ocr-models .\models\rapid --ocr-threads 1
```

模型决定识别语言，`--ocr-language` 不改变 RapidOCR 模型。RapidOCRLib 使用 Emgu CV 原生依赖；分发前请核对其依赖的许可和目标架构。

**Tesseract**：安装目录下需有对应语言的 `.traineddata` 文件（例如 `eng.traineddata`、`chi_sim.traineddata`）。`--ocr-language` 使用 Tesseract 语言代码，可用 `chi_sim+eng` 组合语言：

```powershell
dotnet run --no-build --project AutoPlay.Host\AutoPlay.Host.csproj -- run-pipe LunarNX-account1 .\LunarSample\bin\Debug\net9.0-windows10.0.17763.0\LunarSample.dll --ocr tesseract --ocr-models .\tessdata --ocr-language chi_sim+eng
```

常见语言标签会映射为 `en-US`→`eng`、`zh-CN`→`chi_sim`、`zh-TW`→`chi_tra`、`ja-JP`→`jpn`、`ko-KR`→`kor`；其他值直接作为 Tesseract 语言代码使用。

**PaddleOCR（官方 Python 推理包）**：此提供者通过随程序部署的 JSON 行协议 worker 调用本机 Python，需自行安装 PaddleOCR 3.x、兼容的 PaddlePaddle、NumPy 和 OpenCV Python 依赖。提供已准备好的检测与识别模型目录，避免引擎自行下载模型：

```powershell
dotnet run --no-build --project AutoPlay.Host\AutoPlay.Host.csproj -- run-pipe LunarNX-account1 .\LunarSample\bin\Debug\net9.0-windows10.0.17763.0\LunarSample.dll --ocr paddle --ocr-det .\models\paddle\det --ocr-rec .\models\paddle\rec --ocr-python C:\path\to\venv\Scripts\python.exe --ocr-language ch
```

`--ocr-language` 传给 PaddleOCR（`zh-CN` 映射为 `ch`、`zh-TW` 映射为 `chinese_cht`、`en-US` 映射为 `en`、`ja-JP` 映射为 `japan`）；默认 `ch`。提供者在每个工作进程中启动一个常驻 Python worker，并按语言懒加载引擎。PaddleOCR Python/模型安装未包含在本仓库依赖中。

以上示例中的 `run-pipe` 也可换成 `run <窗口句柄>`。模型版本和语言数据须与所用引擎兼容；本次验证覆盖 Windows OCR 与提供者接口，未包含 RapidOCR/Tesseract 模型文件或 PaddleOCR Python 环境，因此这三种后端仍需在目标机器准备模型后做实际识别和性能验证。

所有区域和命中坐标均为**截图的物理像素**，区域必须完全落在截图内。Win32 截图对应客户端区域，LunarNX 截图对应 SDL 渲染输出（可能与客户端尺寸不同）。`FindImage` 使用原始尺寸的彩色模板，不进行缩放或旋转；分数为 `1 - RGB 均方根误差 / 255`，范围为 0 到 1。`FindColor` 按 RGB 各通道容差返回第一个匹配像素。`FindTextAsync` 不区分大小写，返回首个包含目标文本的 OCR **整行**边界，而非单词边界。识别未命中返回 `null`；无效区域、缺失模板、不可用 OCR 等错误会抛出异常。

## 兼容性与限制

- 截图使用 `PrintWindow` 客户端模式；许多 DirectX、硬件加速或受保护的游戏不支持该方式，即使 API 返回成功也可能得到黑屏或旧画面。窗口最小化时引擎会报错，不会切换为前台截图。
- 输入使用 `PostMessage` 发送到顶层窗口，不移动真实鼠标、不保证改变实际键盘状态。使用 Raw Input、DirectInput、子窗口接收消息或特殊保护的游戏可能完全忽略输入；目标进程权限高于运行器时 Windows 也可能阻止消息。务必先在测试环境验证。
- 窗口句柄关闭后可能被系统复用；长时间运行时应自行监控目标进程并在窗口重建后重新启动脚本。
- 标注不会自动保存；如需历史记录，应在脚本中自行选择路径及保存频率。不要在高频循环中无条件写盘。
