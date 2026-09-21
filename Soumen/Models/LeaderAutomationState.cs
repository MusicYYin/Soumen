namespace Soumen.Models;

public enum LeaderAutomationState
{
    Inactive,
    LookingForMap,
    RestockingTravel,
    RestockingMarket,
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
