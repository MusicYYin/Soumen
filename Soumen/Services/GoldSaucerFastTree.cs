namespace Soumen.Services;

/// <summary>
/// A bounded, opt-in packet sequence for one Out on a Limb round. The user
/// opens the minigame and picks difficulty normally; this handles the swings.
/// Every transition waits for the corresponding server response.
/// </summary>
internal sealed class GoldSaucerFastTree : IDisposable
{
    private const uint Prepare = 0x0107000E;
    private const uint Difficulty = 0x0109000E;
    private const uint Swing = 0x010A000E;

    private readonly GoldSaucerPacketTrace trace;
    private readonly DiagnosticLogger diagnostics;
    private readonly GoldSaucerTreeSearch search = new();
    private Stage stage;
    private DateTime deadline;
    private int swings;
    private int difficulty;
    private bool enabled;

    private enum Stage { Idle, Start, Prepare, Difficulty, Swing, Finishing, Finish }

    internal GoldSaucerFastTree(GoldSaucerPacketTrace trace, DiagnosticLogger diagnostics)
    {
        this.trace = trace;
        this.diagnostics = diagnostics;
        trace.Observed += OnPacket;
    }

    internal string Status { get; private set; } = "已关闭";
    internal bool IsEnabled => enabled;

    internal void SetEnabled(bool value)
    {
        enabled = value;
        Reset(value ? "等待手动开始小游戏并选择难度" : "已关闭");
    }

    internal void Update()
    {
        if (!enabled || stage == Stage.Idle)
        {
            return;
        }

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ClientState.TerritoryType != 388)
        {
            Stop("已停止：离开金蝶区域");
            return;
        }

        if (DateTime.UtcNow < deadline)
        {
            return;
        }

        if (stage == Stage.Finishing)
        {
            Send(14, 0, true, Stage.Finish, "发送结算");
            return;
        }

        Stop($"已停止：等待 {stage} 的回包超过 4 秒；可按 ESC 退出并查看 diagnostic.log");
    }

    private void OnPacket(GoldSaucerPacketTrace.PacketRecord packet)
    {
        if (!enabled)
        {
            return;
        }

        if (packet.Direction == "发送")
        {
            if (packet.Opcode == 0x00B5 && packet.EventId == 0x00240006 && stage == Stage.Idle
                && Plugin.ClientState.TerritoryType == 388)
            {
                search.Reset();
                swings = 0;
                Advance(Stage.Start, "观察到开局，等待小游戏画面");
            }
            else if (packet.Opcode == 0x03D6 && stage is not (Stage.Idle or Stage.Finish))
            {
                Reset("已停止：游戏结束了本局");
            }

            return;
        }

        if (packet.Direction != "接收" || Plugin.ClientState.TerritoryType != 388)
        {
            return;
        }

        switch (stage)
        {
            case Stage.Start when packet.Opcode == 0x01FD:
                Send(Prepare, 0, false, Stage.Prepare, "请求进入难度选择");
                break;
            case Stage.Prepare when packet.Opcode == 0x0390 && packet.Category == 0x0315000E:
                // Do not infer difficulty from object names: use the explicit selection.
                // The user chooses it in the Soumen Tools tab before starting.
                Send(Difficulty, (uint)difficulty, false, Stage.Difficulty, "提交难度");
                break;
            case Stage.Difficulty when packet.Opcode == 0x00FB && packet.Category == 0x0917000E
                                       && packet.First == 10 && packet.Second == 10 && packet.Third == 5:
                Send(Swing, (uint)search.Current, false, Stage.Swing, "第一次挥击");
                break;
            case Stage.Swing when packet.Opcode == 0x00FB && packet.Category == 0x0918000E
                                  && packet.First <= 3 && packet.Second <= 10:
                swings++;
                diagnostics.Write("砍树高速", $"第 {swings} 次：位置 {search.Current}，强度 {packet.First}，剩余 {packet.Second}，结束标志 {packet.Third}");
                if (packet.Second == 0 || packet.Third == 1 || swings >= 10)
                {
                    stage = Stage.Finishing;
                    deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                    Status = "本回合结束，等待结算";
                }
                else
                {
                    var next = search.RecordResult((int)packet.First);
                    Send(Swing, (uint)next, false, Stage.Swing, $"第 {swings + 1} 次挥击");
                }

                break;
            case Stage.Finish when packet.Opcode == 0x00D9:
                Reset("本局已结算；需要继续请手动开始下一局");
                break;
        }
    }

    // The UI lists Titan, Morbol, Cactuar; the wire format is Cactuar=0,
    // Morbol=1, Titan=2, as verified by four manual rounds in the user's log.
    internal void SelectDifficulty(int selection) => difficulty = 2 - Math.Clamp(selection, 0, 2);

    private void Send(uint category, uint value, bool finish, Stage next, string label)
    {
        if (!trace.SendFastAction(category, value, finish))
        {
            Stop($"已停止：{label}发送失败；检查封包钩子和日志");
            return;
        }

        Advance(next, $"{label}（0x{category:X8}，参数 {value}）");
    }

    private void Advance(Stage next, string message)
    {
        stage = next;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        Status = message;
        diagnostics.Write("砍树高速", message);
    }

    private void Stop(string reason)
    {
        enabled = false;
        Reset(reason);
    }

    private void Reset(string reason)
    {
        stage = Stage.Idle;
        swings = 0;
        search.Reset();
        Status = reason;
        diagnostics.Write("砍树高速", reason);
    }

    public void Dispose() => trace.Observed -= OnPacket;
}
