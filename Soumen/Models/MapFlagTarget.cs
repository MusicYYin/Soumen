using System.Numerics;

namespace Soumen.Models;

public sealed record MapFlagTarget(
    long Serial,
    string Sender,
    uint TerritoryId,
    uint MapId,
    int RawX,
    int RawY,
    float MapX,
    float MapY,
    string PlaceName,
    DateTime ReceivedAtUtc)
{
    public Vector3 ToWorld(float height)
        => new(RawX / 1000f, height, RawY / 1000f);
}
