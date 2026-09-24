using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace Soumen.Services;

/// <summary>
/// Observes the same native packet path used by Neko. This class never modifies or sends packets.
/// Keep the hook callbacks small: decoding and file writes happen on the framework thread.
/// </summary>
public sealed unsafe class GoldSaucerPacketTrace : IDisposable
{
    private const uint EventId = 0x00240006;
    private const ushort StartUp = 0x00B5;
    private const ushort ActionUp = 0x008C;
    private const ushort FinishUp = 0x03D6;
    private const ushort StartDown = 0x02E1;
    private const ushort PlayDown = 0x01FD;
    private const ushort FirstActionDown = 0x0390;
    private const ushort OtherActionDown = 0x00FB;
    private const ushort AlternateActionDown = 0x0267;
    private const ushort FinishDown = 0x00D9;
    private const int MaxEntries = 48;

    private readonly DiagnosticLogger diagnostics;
    private readonly ConcurrentQueue<PacketRecord> pending = new();
    private readonly Queue<string> recent = new();
    private readonly List<ExpectedReply> waiting = new();
    private readonly Hook<ReceivePacketDelegate>? receiveHook;
    private readonly Hook<SendPacketDelegate>? sendHook;
    private DateTime? markedAt;
    private string markedStage = string.Empty;
    private volatile bool enabled;
    private nint zoneClient;

    internal event Action<PacketRecord>? Observed;

    private delegate void ReceivePacketDelegate(PacketDispatcher* dispatcher, uint targetId, byte* packet);
    private delegate bool SendPacketDelegate(nint zoneClient, nint packet, uint a3, uint a4, bool a5);

    public GoldSaucerPacketTrace(DiagnosticLogger diagnostics)
    {
        this.diagnostics = diagnostics;
        try
        {
            // PacketDispatcher.OnReceivePacket is virtual slot 1 (FFXIVClientStructs).
            var virtualTableAddress = (nint)PacketDispatcher.StaticVirtualTablePointer;
            if (virtualTableAddress == 0)
            {
                throw new InvalidOperationException("PacketDispatcher virtual table was not resolved.");
            }

            var receiveAddress = Marshal.ReadIntPtr(virtualTableAddress + nint.Size);
            if (receiveAddress == 0)
            {
                throw new InvalidOperationException("PacketDispatcher receive function was not resolved.");
            }
            receiveHook = Plugin.Interop.HookFromAddress<ReceivePacketDelegate>(receiveAddress, Receive);
            // Same ZoneClient.SendPacket signature used by the user's NPATool.
            var sendAddress = Plugin.SigScanner.ScanText(
                "48 83 EC ?? 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 ?? 44 89 44 24 ?? 4C 8D 44 24 ?? 44 89 4C 24 ?? 44 0F B6 4C 24");
            if (sendAddress == 0)
            {
                throw new InvalidOperationException("ZoneClient send function was not resolved.");
            }
            sendHook = Plugin.Interop.HookFromAddress<SendPacketDelegate>(sendAddress, Send);
            receiveHook.Enable();
            sendHook.Enable();
            Available = true;
        }
        catch (Exception exception)
        {
            receiveHook?.Dispose();
            sendHook?.Dispose();
            AvailabilityReason = $"无法安装抓包钩子：{exception.GetType().Name}：{exception.Message}";
            diagnostics.Write("砍树封包", AvailabilityReason);
        }
    }

    public bool Available { get; }
    internal bool CanSend => enabled && Available && zoneClient != 0;
    public string AvailabilityReason { get; } = string.Empty;
    public IReadOnlyCollection<string> Recent => recent;

    public void SetEnabled(bool value)
    {
        enabled = value && Available;
        if (value && !Available)
        {
            Add(AvailabilityReason);
        }

        if (!value)
        {
            waiting.Clear();
            markedAt = null;
        }
    }

    public void Mark(string stage, string expected)
    {
        if (!enabled)
        {
            return;
        }

        Drain();
        markedStage = stage;
        markedAt = DateTime.UtcNow;
        Add($"步骤 {stage}；等待 {expected}");
    }

    public void Drain()
    {
        while (pending.TryDequeue(out var record))
        {
            try { Observed?.Invoke(record); }
            catch (Exception exception) { diagnostics.Write("砍树封包", $"动作处理异常：{exception}"); }
            var label = record.Opcode switch
            {
                StartUp => "UP_EventStart",
                ActionUp => "UP_EventAction",
                FinishUp => "UP_EventFinish",
                StartDown => "DOWN_EventStart",
                PlayDown => "DOWN_EventPlayN",
                FirstActionDown => "DOWN_EventActionResultN (首个动作)",
                OtherActionDown => "DOWN_EventActionResultN (后续动作)",
                AlternateActionDown => "DOWN_EventActionResultN (额外动作结果)",
                FinishDown => "DOWN_EventFinish",
                _ => "未知",
            };
            var detail = record.Direction == "发送"
                ? $" event=0x{record.EventId:X8} category=0x{record.Category:X8} {record.Header}"
                : $" head={record.Header}";
            Add($"{record.Direction} {label} 0x{record.Opcode:X4}{detail}");
            if (record.Direction == "发送")
            {
                markedAt = null;
                waiting.Add(new ExpectedReply(record.Opcode, record.Category, DateTime.UtcNow));
            }
            if (record.Direction == "接收")
            {
                var matching = waiting.FindIndex(item => item.Opcode switch
                {
                    StartUp => record.Opcode is StartDown or PlayDown,
                    ActionUp => record.Opcode is FirstActionDown or OtherActionDown or AlternateActionDown,
                    FinishUp => record.Opcode == FinishDown,
                    _ => false,
                });
                if (matching >= 0)
                {
                    var item = waiting[matching];
                    waiting.RemoveAt(matching);
                    Add($"候选回包：发送 0x{item.Opcode:X4}/类别 0x{item.Category:X8}，"
                        + $"接收 0x{record.Opcode:X4}/类别 0x{record.Category:X8}，"
                        + $"相隔 {(DateTime.UtcNow - item.SentUtc).TotalMilliseconds:F0} ms（仅按时间和类型关联）");
                }
            }
        }

        if (markedAt is { } time && DateTime.UtcNow - time > TimeSpan.FromSeconds(4))
        {
            Add($"步骤 {markedStage}：4 秒内未观察到本小游戏的发送；检查触发动作和界面状态");
            markedAt = null;
        }

        for (var i = waiting.Count - 1; i >= 0; i--)
        {
            var item = waiting[i];
            if (DateTime.UtcNow - item.SentUtc <= TimeSpan.FromSeconds(4))
            {
                continue;
            }

            Add($"发送 0x{item.Opcode:X4}/类别 0x{item.Category:X8} 后 4 秒没有匹配回包；检查收到的其他 opcode 与返回内容");
            waiting.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        enabled = false;
        zoneClient = 0;
        sendHook?.Dispose();
        receiveHook?.Dispose();
    }

    /// <summary>
    /// Builds a fresh 36-byte event payload in the game's 32-byte outgoing packet envelope.
    /// Only called on the framework thread after a real EventStart has supplied ZoneClient.
    /// </summary>
    internal bool SendFastAction(uint category, uint param1 = 0, bool finish = false)
    {
        if (!CanSend || sendHook == null || Plugin.ClientState.TerritoryType != 388)
        {
            return false;
        }

        const int packetSize = 68;
        var packet = stackalloc byte[packetSize];
        new Span<byte>(packet, packetSize).Clear();
        *(ushort*)packet = finish ? FinishUp : ActionUp;
        *(uint*)(packet + 8) = packetSize - 32;
        *(uint*)(packet + 32) = EventId;
        *(uint*)(packet + 36) = category;
        *(uint*)(packet + 40) = param1;
        var result = sendHook.Original(zoneClient, (nint)packet, 0, 0, false);
        pending.Enqueue(new PacketRecord("发送", finish ? FinishUp : ActionUp,
            EventId, category, $"param1={param1} length={packetSize} sendArgs=0/0/False "
                + $"result={result} builtBy=Soumen", 0, 0, 0));
        return result;
    }

    internal void ClearSender() => zoneClient = 0;

    private bool Send(nint zoneClient, nint packet, uint a3, uint a4, bool a5)
    {
        PacketRecord? observation = null;
        if (enabled && packet != 0)
        {
            try
            {
                var opcode = *(ushort*)packet;
                if (opcode is StartUp or ActionUp or FinishUp)
                {
                    var body = (byte*)packet + 32;
                    var id = opcode == StartUp ? *(uint*)(body + 8) : *(uint*)body;
                    if (id == EventId)
                    {
                        if (opcode == StartUp)
                        {
                            this.zoneClient = zoneClient;
                        }
                        var claimedLength = *(uint*)((byte*)packet + 8) + 32;
                        var capturedLength = claimedLength is >= 52 and <= 80 ? (int)claimedLength : 52;
                        var argument = opcode == StartUp ? 0U : *(uint*)(body + 8);
                        observation = new PacketRecord("发送", opcode, id,
                            opcode == StartUp ? *(uint*)(body + 12) : *(uint*)(body + 4),
                            $"param1={argument} length={claimedLength} "
                            + $"sendArgs={a3}/{a4}/{a5} "
                            + $"raw={Convert.ToHexString(new ReadOnlySpan<byte>((byte*)packet, capturedLength))}",
                            0, 0, 0);
                    }
                }
            }
            catch (Exception) { /* A diagnostic hook must never interrupt the game. */ }
        }

        var result = sendHook!.Original(zoneClient, packet, a3, a4, a5);
        if (observation.HasValue)
        {
            pending.Enqueue(observation.Value);
        }

        return result;
    }

    private void Receive(PacketDispatcher* dispatcher, uint targetId, byte* packet)
    {
        if (enabled && packet != null)
        {
            try
            {
                // The legacy ECommons callback runs before the game processes the packet.
                // Its data pointer is (packet - 16) + 32; Neko's +12/+16/+28
                // therefore correspond to original packet offsets +44/+48/+60.
                var originalPacket = packet - 16;
                var opcode = *(ushort*)(originalPacket + 18);
                if (opcode is StartDown or PlayDown or FirstActionDown or OtherActionDown or AlternateActionDown or FinishDown)
                {
                    var isResult = opcode is FirstActionDown or OtherActionDown or AlternateActionDown;
                    if (!isResult || *(uint*)(originalPacket + 32) == EventId)
                    {
                        var resultParam = isResult ? *(uint*)(originalPacket + 40) : 0;
                        var first = isResult ? *(uint*)(originalPacket + 44) : 0;
                        var second = isResult ? *(uint*)(originalPacket + 48) : 0;
                        var extra1 = isResult ? *(uint*)(originalPacket + 52) : 0;
                        var extra2 = isResult ? *(uint*)(originalPacket + 56) : 0;
                        var third = isResult ? originalPacket[60] : (byte)0;
                        var category = isResult ? *(uint*)(originalPacket + 36) : 0U;
                        var state = !isResult ? string.Empty
                            : first == 10 && second == 10 && third == 5 ? $"数值命中 Neko 初始化条件（实际接收 opcode=0x{opcode:X4}；还需核对地区、运行状态及接收回调）"
                            : second > 0 && third != 5 ? "数值命中 Neko 后续处理条件（还需地区及运行状态）"
                            : second == 0 ? "数值命中 Neko 回合计数条件（还需地区及运行状态）"
                            : "未命中 Neko 已确认的结果分支";
                        pending.Enqueue(new PacketRecord("接收", opcode, 0, category,
                            (isResult
                                ? $"category=0x{category:X8} body[8]={resultParam} neko[12]={first} "
                                    + $"neko[16]={second} body[20]={extra1} body[24]={extra2} "
                                    + $"neko[28]={third} ({state}) "
                                : string.Empty)
                            + $"raw={Convert.ToHexString(new ReadOnlySpan<byte>(originalPacket,
                                opcode is PlayDown or FirstActionDown or OtherActionDown or AlternateActionDown ? 64 : 32))}",
                            first, second, third));
                    }
                }
            }
            catch (Exception) { /* A diagnostic hook must never interrupt the game. */ }
        }

        receiveHook!.Original(dispatcher, targetId, packet);
    }

    private void Add(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        recent.Enqueue(line);
        while (recent.Count > MaxEntries)
        {
            recent.Dequeue();
        }

        diagnostics.Write("砍树封包", message);
    }

    internal readonly record struct PacketRecord(string Direction, ushort Opcode, uint EventId, uint Category,
        string Header, uint First, uint Second, byte Third);
    private readonly record struct ExpectedReply(ushort Opcode, uint Category, DateTime SentUtc);
}
