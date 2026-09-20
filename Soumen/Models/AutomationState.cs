namespace Soumen.Models;

public enum AutomationState
{
    Disabled,
    Idle,
    Debouncing,
    WaitingForPlayer,
    Mounting,
    WaitingForVnavmesh,
    Navigating,
    Dismounting,
    Error,
}
