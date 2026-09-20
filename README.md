# Soumen

Soumen 是一个面向 FF14 藏宝图队伍流程的 Dalamud 插件。它只监听小队和跨服小队聊天中的地图坐标链接，把实际发送过坐标的成员列为候选目的地，并通过 vnavmesh 完成路线选择、上坐骑、导航、落地和下坐骑。

## 当前功能

- 直接读取 Dalamud 的 `MapLinkPayload`，不解析本地化聊天文本。
- 只监听 `Party` 与 `CrossParty`，不读取说话、喊话、呼喊等频道。
- 不限制藏宝图等级或地图，当前地图白名单已移除。
- 每位实际发送过坐标的小队成员保留一条最新目的地，并显示 Content ID。
- 默认立即前往最新收到的坐标；手动点击某一坐标后自动锁定该目标。
- 到达后清除 X、Y 均在 ±0.5 内的同地点条目，但不会自动前往列表中的其他坐标。
- 支持在主界面暂停、继续和停止导航。
- 小队成员离队后立即移除其坐标；如果正在前往该成员的坐标，同时取消导航。
- 自动比较直达路线与“传送到本地图以太水晶后再导航”的路线成本。
- 通过移动进度检测卡住状态：先重新规划，再按设置尝试以太水晶恢复。
- 导航过程中保持 vnavmesh 运行，即使途中进入战斗也不会主动停止。
- 只在导航期间关闭 AE Assist 自动选目标，到达、暂停或停止后恢复。
- Soumen 运行期间可自动开启 BossMod Reborn AI，关闭 Soumen 时关闭。
- 到达后主动落地并下坐骑，然后停留等待挖宝、战斗或传送魔纹。

## 安装

在 Dalamud 设置的“实验性功能 → 自定义插件仓库”中添加：

```text
https://raw.githubusercontent.com/MusicYYin/Soumen/main/repo.json
```

保存后，在插件安装器中搜索 `Soumen`。

### 依赖

- 必需：`vnavmesh`
- 可选：`AE Assist V3`
- 可选：`BossMod Reborn`
- 不再需要：Something Need Doing、ChatCoordinates

## 使用方式

- `/soumen`：打开或关闭主界面。
- `/soumen on`、`/soumen off`：开启或关闭自动化。
- `/soumen pause`、`/soumen resume`：暂停或继续当前目标。
- `/soumen stop`：停止当前导航，但保留目的地列表供手动选择。

## 当前边界

`0.2.x` 聚焦坐标选择和移动闭环。它不会自动挖掘、识别宝箱、开箱或进入传送魔纹；到达一个坐标后也不会把剩余坐标当作队列依次执行。

## 开发与发布

项目基于 `Dalamud.NET.Sdk 15.0.0`。本机需要 .NET 10 SDK，并已通过 XIVLauncher 启动过一次 Dalamud。

```powershell
dotnet build Soumen.slnx -c Release
```

推送四段式版本标签（例如 `0.2.0.0`）后，GitHub Actions 会构建并发布可由第三方仓库直接安装的 `Soumen.zip`。

## AI 使用披露

本项目的架构、文档和部分实现由 OpenAI Codex 协助生成，并由仓库维护者负责实机测试、审核和后续维护。

## 风险提示

Dalamud 和第三方插件不属于 Square Enix 官方功能。自动化功能可能违反游戏服务条款；使用者需自行判断并承担风险。建议先在安全环境中观察运行状态，并随时使用暂停或停止导航。
