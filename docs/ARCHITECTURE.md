# Soumen 架构说明

## 目标

首个里程碑只解决一件事：可靠地把队伍聊天中的同地图坐标链接转成可中止、可观察的 vnavmesh 导航任务。

## 数据流

1. `IChatGui.ChatMessage` 收到聊天事件。
2. 仅接受 `Party` 与 `CrossParty`，直接读取 `MapLinkPayload`。
3. 复制 Territory、Map、RawX、RawY、显示坐标和发送者，不持有聊天事件对象。
4. 500ms 防抖期间只保留最新坐标。
5. 等传送、读图和过场状态结束，再比较当前 Territory。
6. 使用 `AgentMap.SetFlagMapMarker` 写入游戏旗标。
7. 优先用 `vnavmesh.Query.Mesh.FlagToPoint` 解析目标三维坐标，失败时使用 Raw 坐标与 `NearestPoint` 回退。
8. 使用 `vnavmesh.SimpleMove.PathfindAndMoveCloseTo` 导航。
9. 路径停止但未进入容差范围时自动重试；到达后使用 GeneralAction 23 下坐骑。

## 状态机

`Disabled → Idle → Debouncing → WaitingForPlayer → Mounting → WaitingForVnavmesh → Navigating → Dismounting → Idle`

任一阶段发生不可恢复异常会停止 vnavmesh 并进入 `Error`。收到更新旗标时会停止旧路线，回到 `Debouncing`，因此不会出现多个导航任务并发控制人物。

## 依赖边界

- Dalamud 原生：聊天事件、地图链接、人物状态、目标清除、旗标设置、坐骑动作、UI、配置。
- vnavmesh：三维落点解析、路径规划、人物移动和停止。
- 后续可选：Lifestream（跨地图）、Auto-Target 或战斗插件（战斗阶段）。可选依赖必须通过适配器隔离，核心状态机不得直接依赖其具体 UI 或本地化文本。

## 下一阶段

- G18 藏宝图点位与队员轮次队列。
- 自动挖掘、宝箱识别和交互。
- 战斗/索敌适配层，以及基于可配置规则而不是硬编码中文系统文本的事件处理。
- 传送门与巡梦金库流程。
- 失败恢复、暂停条件、角色级配置与运行统计。
