namespace Soumen.Models;

public enum LeaderAutomationState
{
    Inactive,
    LookingForMap,
    MovingMapFromSaddlebag,
    RestockingTravel,
    RestockingMarket,
    RestockingSaddlebag,
    DecipheringMap,
    ConfirmingDecipher,
    OpeningDecodedMap,
    WaitingForFlag,
    Navigating,
    WaitingForParty,
    Digging,
    ApproachingChest,
    WaitingForCombat,
    Combat,
    ReopeningChest,
    WaitingForLootOrPortal,
    EnteringPortal,
    Dungeon,
    Waiting,
    Error,
}
