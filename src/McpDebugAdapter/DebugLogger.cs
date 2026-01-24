using System.Diagnostics;

namespace McpDebugAdapter;

/// <summary>
/// Provides debug logging functionality that outputs to both console (stderr) and debugger.
/// This allows diagnostics to be captured in Visual Studio's Debug Output window and console logs.
/// </summary>
public static class DebugLogger
{
    private static readonly object _lock = new();
    private static bool _enabled = true;
    private static TextWriter? _logWriter;

    /// <summary>
    /// Gets or sets whether debug logging is enabled.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Sets a custom log writer (e.g., for TCP clients).
    /// </summary>
    public static void SetLogWriter(TextWriter? writer)
    {
        lock (_lock)
        {
            _logWriter = writer;
        }
    }

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public static void Log(string message)
    {
        WriteLog("INFO", message);
    }

    /// <summary>
    /// Logs a debug message.
    /// </summary>
    public static void LogDebug(string message)
    {
        WriteLog("DEBUG", message);
    }

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    public static void LogWarning(string message)
    {
        WriteLog("WARN", message);
    }

    /// <summary>
    /// Logs an error message.
    /// </summary>
    public static void LogError(string message)
    {
        WriteLog("ERROR", message);
    }

    /// <summary>
    /// Logs an error message with exception details.
    /// </summary>
    public static void LogError(string message, Exception ex)
    {
        WriteLog("ERROR", $"{message}: {ex.Message}");
        WriteLog("ERROR", $"Stack trace: {ex.StackTrace}");
    }

    /// <summary>
    /// Logs a JSON-RPC request or response for debugging.
    /// </summary>
    public static void LogJsonRpc(string direction, string json)
    {
        WriteLog("JSONRPC", $"{direction}: {json}");
    }

    private static void WriteLog(string level, string message)
    {
        if (!_enabled)
            return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var formattedMessage = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            // Write to stderr (console output that won't interfere with MCP stdio protocol)
            Console.Error.WriteLine(formattedMessage);

            // Write to debugger output (visible in Visual Studio Debug Output window)
            Debug.WriteLine(formattedMessage);

            // Write to custom log writer if set (e.g., for TCP notifications)
            try
            {
                _logWriter?.WriteLine(formattedMessage);
                _logWriter?.Flush();
            }
            catch
            {
                // Ignore errors writing to custom log writer
            }
        }
    }
}
