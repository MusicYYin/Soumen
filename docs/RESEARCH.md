# 调研记录

本项目初始实现参考了以下公开资料的接口与工程模式，但没有复制其业务代码或资源：

- Dalamud 官方 SamplePlugin：SDK 版本、插件服务注入、WindowSystem 和构建目录。
- Dalamud 源码：`IChatGui`、`IHandleableChatMessage`、`XivChatType`、`ICondition` 等 API 定义。
- PunishXIV/PandorasBox：从 `MapLinkPayload` 读取坐标并设置地图旗标的做法。
- BlackCleaverLoli/MissFisher：第三方仓库 JSON 与稳定下载链接的分发方式；仅借鉴信息层级和简洁 UI 方向，其源代码并未公开。
- anmili2022/Beastmaster：vnavmesh/Lifestream 依赖隔离、同地图导航和 GitHub Release 分发流程。
- sofia819/ffxiv-map-link：聊天地图链接监听与重复链接过滤。
- cycleapple/xiv-party-treasure-helper：藏宝图数据、地图链接和自定义仓库结构。
- vnavmesh IPC 的公开调用方：`Nav.IsReady`、`Query.Mesh.FlagToPoint`、`SimpleMove.PathfindAndMoveCloseTo`、`Path.IsRunning` 与 `Path.Stop`。

关键决定：

- 以 `Dalamud.NET.Sdk/15.0.0` 为基线。
- 不再通过聊天文本中的地图中文名和坐标正则表达式判断旗标。
- 不再依赖 ChatCoordinates 把坐标写回游戏旗标。
- 首版只保留 vnavmesh 为强依赖，其他自动化能力逐步以可选适配器加入。
- 第三方仓库采用仓库根目录 `repo.json` + GitHub Release 中 `Soumen.zip` 的模式。
