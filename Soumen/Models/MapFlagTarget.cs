using System.Numerics;

namespace Soumen.Models;

public sealed record MapFlagTarget(
    long Serial,
    string Sender,
    string SenderName,
    uint SenderWorldId,
    ulong SenderContentId,
    uint TerritoryId,
    uint MapId,
    int RawX,
    int RawY,
    float MapX,
    float MapY,
    string PlaceName,
    DateTime ReceivedAtUtc,
    bool IsOwnTreasure = false,
    bool IsCrossParty = false)
{
    public string SenderKey
        => IsOwnTreasure
            ? "self:treasure"
            : SenderContentId != 0
            ? $"cid:{SenderContentId}"
            : $"name:{SenderName}@{SenderWorldId}";

    public Vector3 ToWorld(float height)
        => new(RawX / 1000f, height, RawY / 1000f);
}
