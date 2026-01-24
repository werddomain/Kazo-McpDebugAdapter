using System.Diagnostics;
using System.Text.Json;

namespace McpDebugAdapter;

/// <summary>
/// Manages launching .NET applications and attaching debuggers to them.
/// This provides a more reliable alternative to direct launch debugging.
/// </summary>
public class ProcessLaunchManager
{
    /// <summary>
    /// Launches a .NET application and returns the process information for debugging.
    /// </summary>
    /// <param name="programPath">Path to the .NET program (DLL or EXE)</param>
    /// <param name="args">Command line arguments</param>
    /// <param name="workingDirectory">Working directory</param>
    /// <param name="waitForDebugger">If true, the process will wait for a debugger to attach</param>
    /// <returns>Process information and success status</returns>
    public static async Task<(bool Success, Process? Process, string Message)> LaunchForDebuggingAsync(
        string programPath, 
        string[]? args = null, 
        string? workingDirectory = null,
        bool waitForDebugger = false)
    {
        try
        {
            if (!File.Exists(programPath))
            {
                return (false, null, $"Program not found: {programPath}");
            }

            var processStartInfo = new ProcessStartInfo();
            
            // Determine how to launch the program
            if (Path.GetExtension(programPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Direct executable
                processStartInfo.FileName = programPath;
                processStartInfo.Arguments = args != null ? string.Join(" ", args.Select(EscapeArgument)) : string.Empty;
            }
            else if (Path.GetExtension(programPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // .NET DLL - need to run with dotnet
                processStartInfo.FileName = "dotnet";
                var allArgs = new List<string> { programPath };
                if (args != null)
                    allArgs.AddRange(args);
                processStartInfo.Arguments = string.Join(" ", allArgs.Select(EscapeArgument));
            }
            else
            {
                return (false, null, $"Unsupported file type: {Path.GetExtension(programPath)}");
            }

            processStartInfo.WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(programPath) ?? Environment.CurrentDirectory;
            processStartInfo.UseShellExecute = false;
            processStartInfo.RedirectStandardOutput = true;
            processStartInfo.RedirectStandardError = true;
            processStartInfo.RedirectStandardInput = true;

            // Add environment variable to make the process wait for debugger if requested
            if (waitForDebugger)
            {
                processStartInfo.EnvironmentVariables["DOTNET_WaitForDebugger"] = "1";
                // Alternative methods for waiting:
                processStartInfo.EnvironmentVariables["COMPlus_EnableDiagnostics"] = "1";
            }

            DebugLogger.LogDebug($"Launching process: {processStartInfo.FileName} {processStartInfo.Arguments}");
            DebugLogger.LogDebug($"Working directory: {processStartInfo.WorkingDirectory}");
            if (waitForDebugger)
            {
                DebugLogger.LogDebug("Process will wait for debugger to attach");
            }

            var process = new Process { StartInfo = processStartInfo };
            
            // Set up output handling
            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    DebugLogger.LogDebug($"Process stdout: {e.Data}");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    DebugLogger.LogDebug($"Process stderr: {e.Data}");
                }
            };

            if (!process.Start())
            {
                return (false, null, "Failed to start process");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            DebugLogger.Log($"Process started with PID: {process.Id}");

            // Wait a moment for the process to initialize
            await Task.Delay(500);

            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                return (false, null, $"Process exited immediately with code {exitCode}");
            }

            var message = waitForDebugger 
                ? $"Process launched (PID: {process.Id}) and waiting for debugger to attach"
                : $"Process launched (PID: {process.Id}) and ready for debugging";

            return (true, process, message);
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Failed to launch process: {ex.Message}", ex);
            return (false, null, $"Launch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds suitable .NET processes for debugging.
    /// </summary>
    /// <param name="processName">Optional process name filter</param>
    /// <returns>List of processes that can be debugged</returns>
    public static List<ProcessInfo> FindDebuggableProcesses(string? processName = null)
    {
        var processes = new List<ProcessInfo>();

        try
        {
            var allProcesses = Process.GetProcesses();
            
            foreach (var process in allProcesses)
            {
                try
                {
                    // Skip if we can't access process info
                    if (string.IsNullOrEmpty(process.ProcessName))
                        continue;

                    // Filter by name if specified
                    if (!string.IsNullOrEmpty(processName) && 
                        !process.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Try to determine if it's a .NET process
                    var isNetProcess = IsNetProcess(process);
                    if (!isNetProcess)
                        continue;

                    processes.Add(new ProcessInfo
                    {
                        Id = process.Id,
                        Name = process.ProcessName,
                        MainWindowTitle = process.MainWindowTitle,
                        StartTime = process.StartTime,
                        HasExited = process.HasExited
                    });
                }
                catch
                {
                    // Skip processes we can't access
                    continue;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Error finding processes: {ex.Message}");
        }

        return processes.OrderBy(p => p.Name).ThenBy(p => p.Id).ToList();
    }

    private static bool IsNetProcess(Process process)
    {
        try
        {
            // Check if process name suggests it's a .NET app
            var processName = process.ProcessName.ToLowerInvariant();
            
            if (processName.Contains("dotnet") || 
                processName.Contains(".net") ||
                processName.EndsWith(".vshost"))
            {
                return true;
            }

            // Try to get the main module to see if it's managed
            var mainModule = process.MainModule;
            if (mainModule != null)
            {
                var fileName = mainModule.FileName.ToLowerInvariant();
                return fileName.Contains("dotnet") || 
                       fileName.EndsWith(".exe") ||
                       fileName.EndsWith(".dll");
            }
        }
        catch
        {
            // If we can't access the process info, skip it
        }

        return false;
    }

    private static string EscapeArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";

        if (arg.Contains(' ') || arg.Contains('\t') || arg.Contains('\n') || 
            arg.Contains('\v') || arg.Contains('"'))
        {
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        }

        return arg;
    }
}

/// <summary>
/// Information about a process that can be debugged.
/// </summary>
public class ProcessInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MainWindowTitle { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public bool HasExited { get; set; }

    public override string ToString()
    {
        var title = !string.IsNullOrEmpty(MainWindowTitle) ? $" ({MainWindowTitle})" : "";
        return $"[{Id}] {Name}{title}";
    }
}