# Soumen

FF14 Dalamud 自动化工具，提供藏宝图流程、狩猎车头跟随与 Sonar S 怪路线。

## 安装

在 Dalamud 自定义插件仓库中添加：

```text
https://raw.githubusercontent.com/MusicYYin/DalamudPlugins/main/pluginmaster.json
```

保存后搜索 `Soumen`。

## 依赖

- `vnavmesh`：路线导航。
- `Lifestream`：狩猎换线与跨服。
- `Sonar`：S 怪报告模式；需开启游戏聊天报告和死亡报告。
- `AE Assist`：可选，导航中临时关闭自动选目标。
- 寻宝的自动掷点与藏宝图定位可分别配合 `LazyLoot`、`Globetrotter` 等插件。

## 命令

`/soumen` 打开界面；`on`、`off`、`pause`、`resume`、`stop` 控制当前任务。
