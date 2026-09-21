# Soumen

FF14 Dalamud 藏宝图坐标导航插件。

## 功能

- 读取小队和跨服小队发送的坐标。
- 每名发送者只保留最后一个坐标。
- 默认导航至最新坐标，也可手动选择并锁定目标。
- 支持以太水晶路线比较、上坐骑、飞行、落地和卡住重算。
- 可自动接受队友传送邀请，并在宝物库结束后退本。
- 可自动收集 Vault Oneiron 的金袋和银袋。
- 可按清单丢弃本轮挖宝中新获得的独立整堆物品。
- 可配合 AE Assist 和 BossMod Reborn 使用。

## 安装

在 Dalamud 的自定义插件仓库中添加：

```text
https://raw.githubusercontent.com/MusicYYin/DalamudPlugins/main/pluginmaster.json
```

保存后搜索 `Soumen`。

## 依赖

- `vnavmesh`：必需
- `AE Assist V3`：可选
- `BossMod Reborn`：可选

## 命令

- `/soumen`：打开界面
- `/soumen on`、`/soumen off`：开启或关闭
- `/soumen pause`、`/soumen resume`：暂停或继续
- `/soumen stop`：停止导航

## 配置文件

```text
XIVLauncherCN\pluginConfigs\Soumen\config.json
```

首次运行新版时会自动迁移原有设置。

## 开发

基于 `Dalamud.NET.Sdk 15.0.0` 和 .NET 10。

```powershell
dotnet build Soumen.slnx -c Release
```
