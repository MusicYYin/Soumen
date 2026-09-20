using System.Text;

namespace Soumen.Services;

public sealed class DiagnosticLogger
{
    private readonly Configuration configuration;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, DateTime> throttledEntries = [];

    public DiagnosticLogger(Configuration configuration)
    {
        this.configuration = configuration;
        DirectoryPath = Plugin.PluginInterface.GetPluginConfigDirectory();
        FilePath = Path.Combine(DirectoryPath, "diagnostic.log");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public void Write(string category, string message)
    {
        if (!configuration.DiagnosticMode)
        {
            return;
        }

        try
        {
            lock (syncRoot)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(
                    FilePath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{category}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "Failed to append the Soumen diagnostic log.");
        }
    }

    public void WriteThrottled(string key, string category, string message, TimeSpan interval)
    {
        if (!configuration.DiagnosticMode)
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (syncRoot)
        {
            if (throttledEntries.TryGetValue(key, out var lastWrite) && now - lastWrite < interval)
            {
                return;
            }

            throttledEntries[key] = now;
        }

        Write(category, message);
    }

    public void WriteException(string category, string operation, Exception exception)
        => Write(category, $"{operation} 失败：{exception.GetType().Name}: {exception.Message}");
}
