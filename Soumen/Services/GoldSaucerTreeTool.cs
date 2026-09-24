using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Soumen.Services;

/// <summary>
/// Plays an already opened Out on a Limb round through the game's UI. No event packets or
/// hardcoded opcodes are sent. The UI node numbers and hit result offsets must be checked
/// after a client update. The search strategy is adapted from Saucy (BSD-3-Clause).
/// </summary>
public sealed unsafe class GoldSaucerTreeTool : IDisposable
{
    private const int BotanistHitPendingOffset = 0x2D1;
    private const int BotanistHealthOffset = 0x328;
    private static readonly int[] StartingPoints = [20, 50, 80];
    private static readonly uint[] DifficultyNodes = [41, 44, 47];
    private static readonly int[] DifficultyHeights = [20, 40, 340];

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private GoldSaucerPacketTrace? packetTrace;
    private readonly List<HitResult> results = [];
    private DateTime nextClickUtc;
    private DateTime pendingSinceUtc;
    private int? target;
    private int? pendingCursor;
    private uint? healthBeforeHit;
    private uint previousSwings;
    private bool previousPending;
    private bool wasPlaying;

    public GoldSaucerTreeTool(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        if (configuration.GoldSaucerTreeEnabled || configuration.GoldSaucerTreePacketTraceEnabled)
        {
            EnsurePacketTrace().SetEnabled(true);
        }
        ResetRound();
        Status = configuration.GoldSaucerTreeEnabled ? "等待砍树小游戏界面" : "已关闭";
        Plugin.Framework.Update += OnUpdate;
    }

    public string Status { get; private set; } = "等待砍树小游戏界面";

    public bool HasError { get; private set; }

    public IReadOnlyCollection<string> RecentPackets => packetTrace?.Recent ?? Array.Empty<string>();

    public string PacketTraceStatus => packetTrace == null
        ? "封包诊断未开启"
        : packetTrace.Available ? "封包钩子已安装（等待实机验证）" : packetTrace.AvailabilityReason;

    public void SetEnabled(bool enabled)
    {
        configuration.GoldSaucerTreeEnabled = enabled;
        configuration.Save();
        if (enabled || configuration.GoldSaucerTreePacketTraceEnabled)
        {
            EnsurePacketTrace().SetEnabled(true);
        }
        else
        {
            packetTrace?.SetEnabled(false);
        }
        HasError = false;
        ResetRound();
        Status = enabled ? "等待砍树小游戏界面" : "已关闭";
        diagnostics.Write("工具", $"金蝶砍树已{(enabled ? "开启" : "关闭")}。");
    }

    public void SetDifficulty(int difficulty)
    {
        configuration.GoldSaucerTreeDifficulty = Math.Clamp(difficulty, 0, DifficultyNodes.Length - 1);
        configuration.Save();
    }

    public void SetPacketTraceEnabled(bool enabled)
    {
        configuration.GoldSaucerTreePacketTraceEnabled = enabled;
        configuration.Save();
        if (enabled || configuration.GoldSaucerTreeEnabled)
        {
            EnsurePacketTrace().SetEnabled(true);
        }
        else
        {
            packetTrace?.SetEnabled(false);
        }
    }

    private GoldSaucerPacketTrace EnsurePacketTrace()
        => packetTrace ??= new GoldSaucerPacketTrace(diagnostics);

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        packetTrace?.Dispose();
    }

    private void OnUpdate(IFramework _)
    {
        packetTrace?.Drain();
        if (!configuration.GoldSaucerTreeEnabled)
        {
            return;
        }

        if (!Plugin.ClientState.IsLoggedIn || Plugin.Condition[ConditionFlag.BetweenAreas])
        {
            ResetRound();
            Status = "等待进入游戏";
            return;
        }

        try
        {
            var game = Plugin.GameGui.GetAddonByName<AtkUnitBase>("MiniGameBotanist", 1);
            if (game != null && game->IsVisible)
            {
                if (!wasPlaying)
                {
                    diagnostics.Write("工具", "检测到砍树小游戏，开始本回合。");
                }

                UpdateGame(game);
                return;
            }

            if (wasPlaying)
            {
                diagnostics.Write("工具", "砍树小游戏已关闭，清除本回合状态。");
                ResetRound();
            }

            var difficulty = Plugin.GameGui.GetAddonByName<AtkUnitBase>("MiniGameAimg", 1);
            if (difficulty != null && difficulty->IsVisible)
            {
                UpdateDifficulty(difficulty);
                return;
            }

            Status = "等待手动打开砍树小游戏";
        }
        catch (Exception exception)
        {
            configuration.GoldSaucerTreeEnabled = false;
            configuration.Save();
            packetTrace?.SetEnabled(configuration.GoldSaucerTreePacketTraceEnabled);
            HasError = true;
            ResetRound();
            Status = "已停止：小游戏界面与预期不符，请检查客户端版本和日志";
            Plugin.Log.Error(exception, "Gold Saucer tree tool stopped due to unexpected UI data.");
            diagnostics.Write("工具", $"{Status}：{exception}");
        }
    }

    private void UpdateDifficulty(AtkUnitBase* addon)
    {
        var difficulty = configuration.GoldSaucerTreeDifficulty;
        var reference = addon->GetNodeById(DifficultyNodes[difficulty]);
        var cursor = addon->GetNodeById(39);
        var button = addon->GetComponentButtonById(37);
        if (reference == null || cursor == null || button == null)
        {
            throw new InvalidOperationException("MiniGameAimg nodes are missing.");
        }

        Status = "正在选择砍树难度";
        var cursorY = 400 - cursor->Height;
        if (cursorY >= reference->Y && cursorY < reference->Y + DifficultyHeights[difficulty]
            && button->IsEnabled && CanClick(TimeSpan.FromMilliseconds(450)))
        {
            diagnostics.Write("工具", $"点击砍树难度 {difficulty}，游标位置 {cursorY}。");
            packetTrace?.Mark($"点击难度 {difficulty}", "动作结果");
            button->ClickAddonButton(addon);
        }
    }

    private void UpdateGame(AtkUnitBase* addon)
    {
        if (addon->AtkValuesCount <= 11 || addon->AtkValues == null
            || addon->AtkValues[0].Type != AtkValueType.UInt
            || addon->AtkValues[11].Type != AtkValueType.UInt)
        {
            throw new InvalidOperationException("MiniGameBotanist values are missing or have changed type.");
        }

        var state = addon->AtkValues[0].UInt;
        var swings = addon->AtkValues[11].UInt;
        if (state > 10 || swings > 10)
        {
            throw new InvalidOperationException("MiniGameBotanist state is outside expected bounds.");
        }

        var health = *(uint*)((byte*)addon + BotanistHealthOffset);
        var pending = *((byte*)addon + BotanistHitPendingOffset) != 0;
        if (health > 10000)
        {
            throw new InvalidOperationException("MiniGameBotanist health offset is invalid.");
        }

        if (!wasPlaying || (state == 3 && swings == 10 && previousSwings != 10))
        {
            ResetRound();
            wasPlaying = true;
        }

        if (pendingCursor.HasValue && healthBeforeHit.HasValue
            && ((previousPending && !pending)
                || (swings < previousSwings && !pending)
                || (DateTime.UtcNow - pendingSinceUtc > TimeSpan.FromSeconds(2) && !pending)))
        {
            RecordHit(healthBeforeHit.Value, health, pendingCursor.Value);
            pendingCursor = null;
            healthBeforeHit = null;
            target = null;
        }

        previousSwings = swings;
        previousPending = pending;
        if (state != 3 || swings == 0 || pendingCursor.HasValue)
        {
            Status = "等待砍树结果";
            return;
        }

        target ??= ChooseTarget();
        if (target == null)
        {
            Status = "本回合没有可尝试的位置";
            return;
        }

        Status = $"砍树中 · 目标 {target}% · 剩余 {swings} 次";
        var cursor = addon->GetNodeById(17);
        var button = addon->GetComponentButtonById(24);
        if (cursor == null || button == null)
        {
            throw new InvalidOperationException("MiniGameBotanist button or cursor is missing.");
        }

        var position = (cursor->Rotation + 0.733f) / 1.466f * 100f;
        if (Math.Abs(position - target.Value) <= 3f && button->IsEnabled
            && CanClick(TimeSpan.FromMilliseconds(1200)))
        {
            pendingCursor = target;
            healthBeforeHit = health;
            pendingSinceUtc = DateTime.UtcNow;
            diagnostics.Write("工具", $"砍树挥击：目标 {target.Value}%，游标 {position:F1}%，生命值 {health}，剩余 {swings} 次。");
            packetTrace?.Mark($"挥击 {target.Value}%", "动作结果");
            button->ClickAddonButton(addon);
        }
    }

    private bool CanClick(TimeSpan delay)
    {
        if (DateTime.UtcNow < nextClickUtc)
        {
            return false;
        }

        nextClickUtc = DateTime.UtcNow + delay;
        return true;
    }

    private void RecordHit(uint previousHealth, uint health, int position)
    {
        var closest = results.MinBy(result => Math.Abs(result.Position - position));
        if (closest == null || health > previousHealth)
        {
            return;
        }

        closest.Power = health == 0 ? HitPower.Maximum : (previousHealth - health) switch
        {
            400 => HitPower.Strong,
            100 => HitPower.Weak,
            0 => HitPower.Nothing,
            _ => HitPower.Unobserved,
        };
        diagnostics.Write("工具", $"砍树结果：落点 {position}%，生命值 {previousHealth} → {health}，判断 {closest.Power}。");
    }

    private int? ChooseTarget()
    {
        var strong = results.FirstOrDefault(result => result.Power == HitPower.Strong);
        if (strong != null)
        {
            return strong.Position;
        }

        for (var index = 0; index < results.Count; index++)
        {
            if (results[index].Power != HitPower.Weak)
            {
                continue;
            }

            foreach (var neighbor in new[] { index - 1, index + 1 })
            {
                if (neighbor >= 0 && neighbor < results.Count && results[neighbor].Power == HitPower.Unobserved)
                {
                    return results[neighbor].Position;
                }
            }
        }

        foreach (var start in StartingPoints)
        {
            var nearest = results.MinBy(result => Math.Abs(result.Position - start));
            if (nearest?.Power == HitPower.Unobserved)
            {
                return nearest.Position;
            }
        }

        return results.FirstOrDefault(result => result.Power == HitPower.Unobserved)?.Position;
    }

    private void ResetRound()
    {
        results.Clear();
        for (var position = 0; position <= 100; position += 10)
        {
            results.Add(new HitResult(position));
        }

        target = null;
        pendingCursor = null;
        healthBeforeHit = null;
        previousPending = false;
        previousSwings = 0;
        wasPlaying = false;
        nextClickUtc = DateTime.MinValue;
    }

    private sealed class HitResult(int position)
    {
        public int Position { get; } = position;
        public HitPower Power { get; set; }
    }

    private enum HitPower
    {
        Unobserved,
        Nothing,
        Weak,
        Strong,
        Maximum,
    }
}
