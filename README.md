# WhisperDesk

实时语音转文字桌面应用，支持多家 STT 服务商，并可通过 LLM 对转写结果进行智能后处理。

## 功能特性

- **实时流式转写** — 麦克风音频实时捕获并流式推送至 STT 提供商，低延迟出字
- **多 STT 提供商** — 内置 Azure Cognitive Services Speech、火山引擎（豆包）两套实现，按配置切换
- **LLM 后处理** — 转写完成后可调用 Azure OpenAI 对文本进行清洗与润色
- **全局快捷键** — 系统级热键，无论焦点在哪都能一键开始/停止录音
- **悬浮窗** — 小型浮层实时显示转写状态，不打断当前工作窗口
- **系统托盘** — 最小化至任务栏，常驻后台
- **单文件发布** — Release 构建输出自包含单文件，无需预装 .NET 运行时

## 界面与操作

界面采用雾白与岩蓝配色，整理后的文字是主内容；“查看原文”与正文对齐，默认无灰底，悬停时轻微高亮，展开后直接显示可选择、可复制的原文，不再嵌套卡片。也可用“复制文字”手动复制整理结果。麦克风设置位于右上角。

默认按住 `RightAlt` 说话，松开后整理并预览，再自动输入；`RightAlt+Shift` 仍用于语音指令。主窗口也可通过“开始录音 / 结束录音”按钮操作，处理期间按钮显示当前阶段。最小化后保留原有悬浮麦克风、预览纠正和托盘工作流。

主窗口、录音浮层和结果气泡共享 `src/ui/WhisperDesk/Themes/LightTheme.xaml` 中的颜色、字体与控件样式；录音和错误使用独立的语义色，不仅依靠颜色提示状态。

设置弹窗打开和关闭均使用 240 ms 的缩放、淡入淡出动画；关闭时立即开始，不再先停顿 180 ms。

麦克风设置先显示，再异步连接设备；加载、设备列表、空状态和错误提示共用固定大小的内容区域，弹窗和底部按钮不会随加载结果跳动，较长的列表或错误信息在内部滚动。音量每 100 ms 更新一次，慢请求不会堆积；关闭设置时异步释放监测设备。普通听写只获取目标窗口信息，不扫描编辑器或读取剪贴板；语音指令所需的选区与文件上下文在独立 STA 线程读取，避免阻塞界面。

录音浮层的五条波动显示实际麦克风音量的短时变化，不再播放循环动画。PCM 音量在采集线程计算，经独立的 gRPC 流传递最新值，UI 每 50 ms 读取一次；慢速连接或忙碌的界面不会积压旧音量。停止录音或音频中断后音量归零，波动使用绘制缩放而非反复调整布局。复制文字、上下文读取和日志写盘也在后台执行，UI 只接收状态并绘制。

文字整理把口述作为独立的转写数据处理，只清理口癖、语法和标点。例如“你再 review 这个”应保留为这句话，而不是回答、执行 review 或索要原文；真正的语音指令仍由独立的指令阶段处理。

## 技术栈

| 层级 | 技术 |
|------|------|
| 运行时 | .NET 9.0 |
| UI 框架 | WPF + Material Design Themes |
| MVVM | CommunityToolkit.Mvvm |
| 音频采集 | NAudio (WASAPI) |
| STT — Azure | Microsoft.CognitiveServices.Speech v1.42 |
| STT — 火山引擎 | 自定义 WebSocket 协议集成 |
| LLM | OpenAI SDK v2.2 (Azure OpenAI endpoint) |
| 全局热键 | H.Hooks |
| 系统托盘 | Hardcodet.NotifyIcon.Wpf |
| 测试 | xUnit + Moq |

## 项目结构

```
WhisperDesk/
├── src/
│   ├── core/
│   │   └── WhisperDesk.Core/          # 流水线编排、音频路由、后处理阶段
│   ├── stt/
│   │   ├── WhisperDesk.Stt.Contract/  # IStreamingSttProvider 接口及数据模型
│   │   ├── WhisperDesk.Stt/           # DI 注册入口
│   │   └── providers/
│   │       ├── WhisperDesk.Stt.Provider.Azure/       # Azure 实现
│   │       └── WhisperDesk.Stt.Provider.Volcengine/  # 火山引擎实现
│   ├── llm/
│   │   ├── WhisperDesk.Llm.Contract/  # ILlmProvider 接口及数据模型
│   │   ├── WhisperDesk.Llm/           # DI 注册入口
│   │   └── providers/
│   │       └── WhisperDesk.Llm.Provider.AzureOpenAI/ # Azure OpenAI 实现
│   └── ui/
│       └── WhisperDesk/               # WPF 主程序（MVVM，视图，托盘，热键）
├── tests/
│   └── WhisperDesk.Tests/
├── Directory.Build.props              # 公共构建属性、模块路径变量
└── Directory.Packages.props           # 集中管理 NuGet 包版本
```

### 架构依赖规则

```
WhisperDesk (UI)
  → WhisperDesk.Core
  → WhisperDesk.Stt            (DI 注册)
  → WhisperDesk.Llm            (DI 注册)

WhisperDesk.Core
  → WhisperDesk.Stt.Contract   (仅接口)
  → WhisperDesk.Llm.Contract   (仅接口)

WhisperDesk.Stt
  → WhisperDesk.Stt.Contract
  → 所有 STT Provider 实现

WhisperDesk.Llm
  → WhisperDesk.Llm.Contract
  → 所有 LLM Provider 实现
```

Contract 项目无任何项目依赖，Provider 实现只依赖自己的 Contract。

## 快速开始

### 前置要求

- Windows 10/11
- .NET 9 SDK
- 至少一个已配置的服务商密钥（Azure Speech 或火山引擎）

### 构建与运行

```bash
# 构建全部项目
dotnet build

# 运行应用
dotnet run --project src/ui/WhisperDesk/WhisperDesk.csproj

# 运行测试
dotnet test

# 发布自包含单文件（Release）
dotnet publish -c Release
```

### 配置

在 `appsettings.json`（或用户机密 / 环境变量）中填入服务商信息：

```json
{
  "Pipeline": {
    "SttProvider": "Azure",
    "LlmProvider": "AzureOpenAI",
    "Languages": ["zh-CN", "en-US"]
  },
  "AzureStt": {
    "SubscriptionKey": "<your-key>",
    "Region": "<your-region>"
  },
  "AzureOpenAI": {
    "Endpoint": "https://<your-resource>.openai.azure.com/",
    "ApiKey": "<your-key>",
    "DeploymentName": "<your-deployment>"
  }
}
```

将 `SttProvider` 设置为 `"Volcengine"` 并填入 `VolcengineStt` 节点即可切换到火山引擎。不需要 LLM 后处理时将 `LlmProvider` 留空即可。

## 扩展新提供商

以新增 STT 提供商为例：

1. 在 `src/stt/providers/` 下创建 `WhisperDesk.Stt.Provider.<Name>/`
2. 实现 `IStreamingSttProvider` 接口
3. 在 `WhisperDesk.Stt/SttServiceRegistration.cs` 的 switch 中添加注册分支
4. UI 项目添加项目引用

LLM 提供商同理，参考 `src/llm/` 目录结构。

## 许可证

本项目版权归作者所有，暂未开源授权。
