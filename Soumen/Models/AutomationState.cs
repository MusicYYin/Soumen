namespace Soumen.Models;

public enum AutomationState
{
    Disabled,
    Idle,
    Paused,
    WaitingForPlayer,
    PlanningRoute,
    Teleporting,
    Mounting,
    WaitingForVnavmesh,
    Navigating,
    Landing,
    Dismounting,
    Error,
}
