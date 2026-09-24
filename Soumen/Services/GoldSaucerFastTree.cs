namespace Soumen.Services;

/// <summary>
/// A bounded, opt-in packet sequence for Out on a Limb. It can start games
/// automatically and handles up to six rounds per game. Every transition
/// waits for the corresponding server response.
/// </summary>
internal sealed class GoldSaucerFastTree : IDisposable
{
    private const uint Prepare = 0x0107000E;
    private const uint Difficulty = 0x0109000E;
    private const uint Swing = 0x010A000E;
    private const uint Continue = 0x000B000E;
    private const int MaxRounds = 6;
    private static readonly TimeSpan BetweenGames = TimeSpan.FromSeconds(3);

    private readonly GoldSaucerPacketTrace trace;
    private readonly DiagnosticLogger diagnostics;
    private readonly GoldSaucerTreeSearch search = new();
    private Stage stage;
    private DateTime deadline;
    private int swings;
    private int rounds;
    private int difficulty;
    private int plannedGames;
    private int completedGames;
    private bool enabled;
    private bool batchRunning;
    private bool stopRequested;

    private enum Stage { Idle, NextGameDelay, Start, Prepare, Difficulty, Swing, ContinueDelay, Continuing, Finishing, Finish }

    internal GoldSaucerFastTree(GoldSaucerPacketTrace trace, DiagnosticLogger diagnostics)
    {
        this.trace = trace;
        this.diagnostics = diagnostics;
        trace.Observed += OnPacket;
    }

    internal string Status { get; private set; } = "已关闭";
    internal bool IsEnabled => enabled;
    internal bool IsBatchRunning => batchRunning || stopRequested;
    internal bool CanStopBatch => batchRunning;

    internal void SetEnabled(bool value)
    {
        enabled = value;
        batchRunning = false;
        stopRequested = false;
        plannedGames = 0;
        completedGames = 0;
        Reset(value ? "等待手动开始小游戏并选择难度" : "已关闭");
    }

    internal void StartBatch(int games)
    {
        if (!enabled || !Plugin.ClientState.IsLoggedIn || Plugin.ClientState.TerritoryType != 388)
        {
            Status = "已停止：请先进入金蝶游乐场再开始";
            return;
        }

        if (stage != Stage.Idle || IsBatchRunning)
        {
            Status = "请等待当前小游戏结束，再启动自动挑战";
            return;
        }

        plannedGames = Math.Clamp(games, 1, 100);
        completedGames = 0;
        batchRunning = true;
        stopRequested = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        Status = $"自动挑战已启动，计划 {plannedGames} 局，准备第 1 局";
        diagnostics.Write("砍树高速", Status);
    }

    internal void StopBatch()
    {
        if (!batchRunning)
        {
            return;
        }

        batchRunning = false;
        stopRequested = true;
        if (stage is Stage.Idle or Stage.NextGameDelay)
        {
            Reset($"已停止自动挑战，完成 {completedGames} 局");
            return;
        }

        if (stage == Stage.Start)
        {
            Status = "正在停止：等待开局确认后结束当前小游戏";
            diagnostics.Write("砍树高速", Status);
            return;
        }

        if (stage is Stage.Finishing or Stage.Finish)
        {
            Status = "正在停止：等待当前小游戏结算";
            diagnostics.Write("砍树高速", Status);
            return;
        }

        Send(14, 0, true, Stage.Finish, "停止：结束当前小游戏");
    }

    internal void Update()
    {
        if (!enabled)
        {
            return;
        }

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ClientState.TerritoryType != 388)
        {
            trace.ClearSender();
            if (IsBatchRunning || stage != Stage.Idle)
            {
                Stop("已停止：离开金蝶区域");
            }

            return;
        }

        var now = DateTime.UtcNow;
        if (stage == Stage.Idle)
        {
            if (batchRunning)
            {
                StartNextGame(now);
            }

            return;
        }

        if (now < deadline)
        {
            return;
        }

        if (stage == Stage.NextGameDelay)
        {
            stage = Stage.Idle;
            deadline = now + TimeSpan.FromSeconds(15);
            StartNextGame(now);
            return;
        }

        if (stage == Stage.ContinueDelay)
        {
            Send(Continue, 0, false, Stage.Continuing, $"第 {rounds} 回合完成，申请续局");
            return;
        }

        if (stage == Stage.Finishing)
        {
            Send(14, 0, true, Stage.Finish, "发送结算");
            return;
        }

        Stop($"已停止：等待 {stage} 的回包超过 4 秒；可按 ESC 退出并查看 diagnostic.log");
    }

    private void StartNextGame(DateTime now)
    {
        if (now > deadline)
        {
            Stop("已停止：15 秒内未取得区服发送连接，请查看日志");
            return;
        }

        if (!trace.CanSend)
        {
            Status = $"等待区服连接，准备第 {completedGames + 1}/{plannedGames} 局";
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        var targetId = player == null ? 0U : unchecked((uint)player.GameObjectId);
        if (targetId is 0 or 0xE0000000)
        {
            Stop("已停止：无法读取本地角色的对象 ID");
            return;
        }

        if (!trace.SendFastStart(targetId))
        {
            Stop("已停止：自动开局发送失败，请查看日志");
            return;
        }

        search.Reset();
        rounds = 0;
        swings = 0;
        Advance(Stage.Start, $"自动开始第 {completedGames + 1}/{plannedGames} 局（对象 0x{targetId:X8}）");
    }

    private void OnPacket(GoldSaucerPacketTrace.PacketRecord packet)
    {
        if (!enabled)
        {
            return;
        }

        if (packet.Direction == "发送")
        {
            if (packet.Opcode == 0x00B5 && packet.EventId == 0x00240006
                && (stage is Stage.Idle or Stage.NextGameDelay)
                && Plugin.ClientState.TerritoryType == 388)
            {
                search.Reset();
                swings = 0;
                rounds = 0;
                Advance(Stage.Start, "观察到开局，等待小游戏画面");
            }
            else if (packet.Opcode == 0x03D6 && stage is not (Stage.Idle or Stage.Finish))
            {
                if (batchRunning)
                {
                    Stop("已停止：游戏被外部操作结束");
                }
                else
                {
                    Reset("已停止：游戏结束了本局");
                }
            }

            return;
        }

        if (packet.Direction != "接收" || Plugin.ClientState.TerritoryType != 388)
        {
            return;
        }

        if (stopRequested && stage == Stage.Start && packet.Opcode == 0x01FD)
        {
            Send(14, 0, true, Stage.Finish, "停止：开局已确认，结束小游戏");
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
                    rounds++;
                    if (packet.Second == 0 && packet.Third == 1 && rounds < MaxRounds)
                    {
                        stage = Stage.ContinueDelay;
                        // Give the game a frame to process the result before requesting
                        // the next round; the server reply still gates the first swing.
                        deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
                        Status = $"第 {rounds} 回合结束，等待续局";
                        diagnostics.Write("砍树高速", Status);
                    }
                    else
                    {
                        stage = Stage.Finishing;
                        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                        Status = $"第 {rounds} 回合结束，等待结算";
                        diagnostics.Write("砍树高速", Status);
                    }
                }
                else
                {
                    var next = search.RecordResult((int)packet.First);
                    Send(Swing, (uint)next, false, Stage.Swing, $"第 {swings + 1} 次挥击");
                }

                break;
            case Stage.Continuing when packet.Opcode == 0x0267 && packet.Category == 0x0719000E
                                       && packet.First == 10 && packet.Third == 0:
                search.Reset();
                swings = 0;
                diagnostics.Write("砍树高速", $"续局回包已确认，开始第 {rounds + 1} 回合");
                Send(Swing, (uint)search.Current, false, Stage.Swing, $"第 {rounds + 1} 回合第一次挥击");
                break;
            case Stage.Finish when packet.Opcode == 0x00D9:
                if (stopRequested)
                {
                    stopRequested = false;
                    Reset($"已停止自动挑战，完成 {completedGames} 局");
                }
                else if (batchRunning)
                {
                    completedGames++;
                    if (completedGames < plannedGames)
                    {
                        Reset($"第 {completedGames}/{plannedGames} 局已结算，准备下一局");
                        stage = Stage.NextGameDelay;
                        deadline = DateTime.UtcNow + BetweenGames;
                    }
                    else
                    {
                        batchRunning = false;
                        Reset($"自动挑战完成，共 {completedGames} 局");
                    }
                }
                else
                {
                    Reset($"共 {rounds} 回合已结算；再次挑战请手动开始新一局");
                }

                break;
            case Stage.Continuing when packet.Opcode == 0x00D9:
                Stop("续局期间小游戏提前结束；请检查封包日志");
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
        batchRunning = false;
        stopRequested = false;
        trace.ClearSender();
        Reset(reason);
    }

    private void Reset(string reason)
    {
        stage = Stage.Idle;
        stopRequested = false;
        swings = 0;
        rounds = 0;
        search.Reset();
        Status = reason;
        diagnostics.Write("砍树高速", reason);
    }

    public void Dispose() => trace.Observed -= OnPacket;
}
