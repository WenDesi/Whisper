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
