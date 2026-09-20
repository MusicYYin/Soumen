# Soumen

Soumen 是一个面向 FF14 藏宝图队伍流程的 Dalamud 插件。当前 `0.1.x` 阶段先完成 G18 场景中的自动跟旗导航闭环：监听队伍或跨服队伍聊天中的地图坐标链接，在人物状态稳定且位于同一地图时自动上坐骑，通过 vnavmesh 飞往旗标，并在到达后下坐骑。

## 当前功能

- 直接读取 Dalamud 的 `MapLinkPayload`，不解析本地化聊天文本。
- 仅监听小队和跨服小队频道。
- 默认仅响应活着的记忆（TerritoryType `1192`）的坐标链接。
- 新旗标覆盖旧路线，带 500ms 防抖。
- 传送、读图和过场期间保留最新旗标，状态稳定后再判断地图。
- 自动上坐骑、清除当前目标、调用 vnavmesh 飞行导航。
- 路径意外停止但尚未到达时自动续飞。
- 到达后调用独立的下坐骑动作，不使用可能反向上坐骑的切换命令。
- 主界面显示运行状态、最新坐标、依赖状态和最近事件。
- `/soumen stop` 可立即停止导航；`/soumen` 打开主界面。

## 安装

在 Dalamud 设置的“实验性功能 → 自定义插件仓库”中添加：

```text
https://raw.githubusercontent.com/MusicYYin/Soumen/main/repo.json
```

保存后，在插件安装器中搜索 `Soumen`。

### 依赖

- 必需：`vnavmesh`
- 不再需要：Something Need Doing、ChatCoordinates

## 当前边界

这一版只自动处理同地图跟旗、上坐骑、导航和下坐骑。它不会自动解读藏宝图、传送、挖掘、选怪、战斗、开箱或进入传送门。默认的“仅 G18 区域”实际上是对活着的记忆 Territory ID 的白名单，并不能判断某个队伍旗标是否真的来自藏宝图。

## 开发

项目基于 `Dalamud.NET.Sdk 15.0.0`。本机需要 .NET 10 SDK，并已通过 XIVLauncher 启动过一次 Dalamud。

```powershell
dotnet build Soumen/Soumen.csproj -c Release
```

发布包由 GitHub Actions 在推送四段式版本标签（例如 `0.1.0.0`）时自动生成。

## AI 使用披露

本项目的初始架构、文档和部分实现由 OpenAI Codex 协助生成，并由仓库维护者负责实机测试、审核和后续维护。该披露保留在仓库中，便于追踪来源与责任边界。

## 风险提示

Dalamud 和第三方插件不属于 Square Enix 官方功能。自动化功能可能违反游戏服务条款；使用者需自行判断并承担风险。建议先在安全环境中观察运行状态，并随时使用紧急停止。
