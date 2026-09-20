using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Soumen.Services;

public sealed class VNavmeshIpc
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>> pathfind;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<Vector3?> flagToPoint;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;
    private readonly ICallGateSubscriber<object> stop;

    public VNavmeshIpc(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        isReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveCloseTo = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>(
            "vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathfind = pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>(
            "vnavmesh.Nav.Pathfind");
        isRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = pluginInterface.GetIpcSubscriber<bool>(
            "vnavmesh.SimpleMove.PathfindInProgress");
        flagToPoint = pluginInterface.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");
        nearestPoint = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>(
            "vnavmesh.Query.Mesh.NearestPoint");
        stop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsInstalled => pluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded && plugin.InternalName.Equals("vnavmesh", StringComparison.OrdinalIgnoreCase));

    public bool IsReady()
    {
        try
        {
            return isReady.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public bool IsBusy()
    {
        try
        {
            return isRunning.InvokeFunc() || pathfindInProgress.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public Vector3? ResolveFlagPoint()
    {
        try
        {
            return flagToPoint.InvokeFunc();
        }
        catch
        {
            return null;
        }
    }

    public Vector3? NearestPoint(Vector3 point)
    {
        try
        {
            return nearestPoint.InvokeFunc(point, 120f, 300f);
        }
        catch
        {
            return null;
        }
    }

    public bool MoveCloseTo(Vector3 destination, bool fly, float range)
    {
        try
        {
            return moveCloseTo.InvokeFunc(destination, fly, range);
        }
        catch
        {
            return false;
        }
    }

    public Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to, bool fly)
    {
        try
        {
            return pathfind.InvokeFunc(from, to, fly);
        }
        catch
        {
            return null;
        }
    }

    public void Stop()
    {
        try
        {
            stop.InvokeAction();
        }
        catch
        {
            // Missing IPC providers are expected when vnavmesh is disabled or unloading.
        }
    }
}
