# Soumen 架构说明

## 目的地模型

Soumen 不把坐标列表视为依次执行的队列。列表只保存实际发送过坐标的成员。每位成员仅保留最新坐标，内部优先以 Content ID 识别发送者，无法取得 Content ID 时回退到角色名与服务器。

没有手动选择时，最新聊天坐标成为当前目标。点击“导航至此”后，该目标自动锁定；其他成员的新坐标仍进入列表，但不会抢占当前导航。当前发送者更新自己的坐标时，锁定目标随之更新。

到达后删除同 Territory、Map 且显示坐标 X、Y 均在 ±0.5 范围内的条目，随后进入待命，不自动选择剩余坐标。

## 数据流

1. `IChatGui.ChatMessage` 接收小队或跨服小队聊天。
2. 读取 `MapLinkPayload`，并将发送者的 PlayerPayload 与游戏小队 Content ID 对应。
3. 更新该发送者的目的地条目；自动模式选择最新条目，手动选择状态则保持当前发送者。
4. 使用游戏旗标与 `vnavmesh.Query.Mesh.FlagToPoint` 解析三维位置。
5. 可选地比较当前直达路线与已解锁的同地图以太水晶路线。
6. 自动上坐骑并调用 vnavmesh 导航。途中进入战斗不会停止 vnavmesh。
7. 监测实际位移和目标距离变化；停滞时先重算路线，再选择以太水晶恢复。
8. 进入到达范围后停止 vnavmesh，恢复 AE 自动选目标，落地并下坐骑。
9. 清除相近目的地后停留，等待挖宝、战斗、魔纹或新的聊天坐标。

## 状态机

`Disabled → Idle → WaitingForPlayer → PlanningRoute/Teleporting → Mounting → WaitingForVnavmesh → Navigating → Landing/Dismounting → Idle`

`Paused` 可以从运行中的任意导航阶段进入，并保留当前目的地和列表。`Error` 停止移动但保留目的地列表，允许用户手动重新选择。

## 外部插件边界

- vnavmesh：路径计算、路线点查询、移动和停止。
- AE Assist：只在 `Navigating` 状态发送 `/aeTargetSelector off`，其他状态恢复为 `on`。
- BossMod Reborn：Soumen 开启时发送 `/bmrai on`，关闭或卸载时发送 `/bmrai off`。

外部命令只在状态发生变化时发送，不在每帧重复执行。BossMod Reborn 与 vnavmesh 在途中战斗时均保持运行，这是当前版本按藏宝图实战需求采取的策略，需要重点实测二者的移动控制是否发生冲突。

## 下一阶段

- 根据实测数据校准传送惩罚和卡住判定。
- 增强飞行路线到地面路线的分段落地。
- 自动挖掘、宝箱识别、开箱和战斗阶段。
- 传送魔纹检测与后续副本流程。
- 可选的 DTR/右上角快捷切换入口。
