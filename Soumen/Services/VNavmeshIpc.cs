using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Soumen.Services;

public sealed class VNavmeshIpc
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly DiagnosticLogger diagnostics;
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>> pathfind;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<Vector3?> flagToPoint;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> movePath;
    private readonly ICallGateSubscriber<float> getPathTolerance;
    private readonly ICallGateSubscriber<float, object> setPathTolerance;
    private readonly ICallGateSubscriber<object> stop;

    public VNavmeshIpc(IDalamudPluginInterface pluginInterface, DiagnosticLogger diagnostics)
    {
        this.pluginInterface = pluginInterface;
        this.diagnostics = diagnostics;
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
        movePath = pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        getPathTolerance = pluginInterface.GetIpcSubscriber<float>("vnavmesh.Path.GetTolerance");
        setPathTolerance = pluginInterface.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance");
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
        catch (Exception exception)
        {
            diagnostics.WriteThrottled("vnav-ready", "vnavmesh", $"读取就绪状态失败：{exception.Message}", TimeSpan.FromSeconds(10));
            return false;
        }
    }

    public bool IsBusy()
    {
        try
        {
            return isRunning.InvokeFunc() || pathfindInProgress.InvokeFunc();
        }
        catch (Exception exception)
        {
            diagnostics.WriteThrottled("vnav-busy", "vnavmesh", $"读取运行状态失败：{exception.Message}", TimeSpan.FromSeconds(10));
            return false;
        }
    }

    public Vector3? ResolveFlagPoint()
    {
        try
        {
            return flagToPoint.InvokeFunc();
        }
        catch (Exception exception)
        {
            diagnostics.WriteException("vnavmesh", "解析游戏地图旗标", exception);
            return null;
        }
    }

    public Vector3? NearestPoint(Vector3 point)
        => NearestPoint(point, 120f, 300f);

    public Vector3? NearestPoint(Vector3 point, float horizontalRange, float verticalRange)
    {
        try
        {
            return nearestPoint.InvokeFunc(point, horizontalRange, verticalRange);
        }
        catch (Exception exception)
        {
            diagnostics.WriteException("vnavmesh", $"查找最近网格点 {Format(point)}", exception);
            return null;
        }
    }

    public bool MoveCloseTo(Vector3 destination, bool fly, float range)
    {
        try
        {
            return moveCloseTo.InvokeFunc(destination, fly, range);
        }
        catch (Exception exception)
        {
            diagnostics.WriteException(
                "vnavmesh",
                $"开始导航至 {Format(destination)}，fly={fly}，range={range:F1}",
                exception);
            return false;
        }
    }

    public Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to, bool fly)
    {
        try
        {
            return pathfind.InvokeFunc(from, to, fly);
        }
        catch (Exception exception)
        {
            diagnostics.WriteException(
                "vnavmesh",
                $"计算路线 {Format(from)} -> {Format(to)}，fly={fly}",
                exception);
            return null;
        }
    }

    public bool MoveAlongPath(IReadOnlyList<Vector3> waypoints, bool fly)
    {
        if (waypoints.Count == 0)
        {
            return false;
        }

        try
        {
            movePath.InvokeAction(waypoints.ToList(), fly);
            return true;
        }
        catch (Exception exception)
        {
            diagnostics.WriteException("vnavmesh", $"开始连续路线，共 {waypoints.Count} 个路径点", exception);
            return false;
        }
    }

    public float? GetPathTolerance()
    {
        try
        {
            return getPathTolerance.InvokeFunc();
        }
        catch (Exception exception)
        {
            diagnostics.WriteThrottled(
                "vnav-tolerance-get",
                "vnavmesh",
                $"读取路径容差失败：{exception.Message}",
                TimeSpan.FromSeconds(10));
            return null;
        }
    }

    public void SetPathTolerance(float tolerance)
    {
        try
        {
            setPathTolerance.InvokeAction(tolerance);
        }
        catch (Exception exception)
        {
            diagnostics.WriteThrottled(
                "vnav-tolerance-set",
                "vnavmesh",
                $"设置路径容差失败：{exception.Message}",
                TimeSpan.FromSeconds(10));
        }
    }

    public void Stop()
    {
        try
        {
            stop.InvokeAction();
        }
        catch (Exception exception)
        {
            diagnostics.WriteThrottled("vnav-stop", "vnavmesh", $"停止导航失败：{exception.Message}", TimeSpan.FromSeconds(10));
        }
    }

    private static string Format(Vector3 point)
        => $"({point.X:F1}, {point.Y:F1}, {point.Z:F1})";
}
