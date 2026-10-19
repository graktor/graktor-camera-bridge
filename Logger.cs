namespace ZendoriaCameraBridge;

public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>Console + a plain rolling log file next to the exe -- this runs unattended at a shop, so whatever went wrong needs to be readable after the fact, not just scrolled past.</summary>
public class FileLogger : ILogger
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "bridge.log");
    private readonly object _lock = new();

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        Console.WriteLine(line);
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
                // Keep the log from growing forever on a machine nobody looks at for months.
                if (new FileInfo(_path).Length > 5 * 1024 * 1024)
                {
                    File.WriteAllText(_path, "(log trimmed)" + Environment.NewLine);
                }
            }
            catch { /* logging must never crash the agent */ }
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
}
