using System.Diagnostics;
using System.Text.Json;
using McpDebugAdapter.Dap;
using McpDebugAdapter.Ui;

namespace McpDebugAdapter;

/// <summary>
/// Manages the state of a debug session including threads, frames, and breakpoints.
/// </summary>
public class DebugSession
{
    private readonly DapClient _dapClient;
    private readonly UiAutomationService _uiService;
    private readonly object _lock = new();
    private Process? _debuggedProcess;

    /// <summary>
    /// The DAP client used for communication with the debugger.
    /// </summary>
    public DapClient DapClient => _dapClient;

    /// <summary>
    /// Indicates whether a debug session is currently active.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Indicates whether the debugged program is currently paused.
    /// </summary>
    public bool IsPaused { get; private set; }

    /// <summary>
    /// The current thread ID when stopped.
    /// </summary>
    public int CurrentThreadId { get; private set; }

    /// <summary>
    /// The current frame ID when stopped.
    /// </summary>
    public int CurrentFrameId { get; private set; }

    /// <summary>
    /// The reason the debugger stopped.
    /// </summary>
    public string? StopReason { get; private set; }

    /// <summary>
    /// Path to the program being debugged.
    /// </summary>
    public string? ProgramPath { get; private set; }

    /// <summary>
    /// Map of file paths to their breakpoint lines.
    /// </summary>
    public Dictionary<string, List<int>> Breakpoints { get; } = new();

    /// <summary>
    /// Output messages from the debugger.
    /// </summary>
    public List<string> OutputMessages { get; } = new();

    /// <summary>
    /// Event fired when a debug event occurs (for notification to MCP client).
    /// </summary>
    public event Action<string, object?>? OnDebugEvent;

    public DebugSession()
    {
        _dapClient = new DapClient();
        _uiService = new UiAutomationService();

        _dapClient.OnStopped += HandleStopped;
        _dapClient.OnOutput += HandleOutput;
        _dapClient.OnTerminated += HandleTerminated;
        _dapClient.OnInitialized += HandleInitialized;
    }

    /// <summary>
    /// Starts a new debug session.
    /// </summary>
    /// <param name="programPath">Path to the program (DLL or EXE) to debug.</param>
    /// <param name="args">Command line arguments for the program.</param>
    /// <param name="stopAtEntry">Whether to stop at entry point.</param>
    /// <param name="netcoredbgPath">Path to netcoredbg executable.</param>
    public async Task<(bool Success, string Message)> LaunchAsync(
        string programPath,
        string[]? args = null,
        bool stopAtEntry = false,
        string? netcoredbgPath = null)
    {
        try
        {
            if (IsActive)
            {
                return (false, "A debug session is already active. Stop it first.");
            }

            if (!File.Exists(programPath))
            {
                return (false, $"File not found: {programPath}");
            }

            // Check .NET version compatibility
            var targetFramework = GetTargetFramework(programPath);
            if (!string.IsNullOrEmpty(targetFramework))
            {
                DebugLogger.Log($"Target framework: {targetFramework}");
                if (targetFramework.StartsWith("net10.") || targetFramework.StartsWith("net1"))
                {
                    var warningMsg = "WARNING: Debugging .NET 10+ applications may not be fully supported with netcoredbg 3.x. " +
                                   "If you experience issues, consider:\\n" +
                                   "1. Using a newer version of netcoredbg\\n" +
                                   "2. Building your application with an earlier .NET version (like net8.0 or net9.0)\\n" +
                                   "3. Using Visual Studio or Visual Studio Code's built-in debugger for .NET 10+ apps";
                    DebugLogger.LogWarning(warningMsg);
                    
                    // For now, we'll still attempt debugging but with clear warnings
                }
            }

            DebugLogger.Log($"Starting debug session for: {programPath}");
            DebugLogger.Log($"Arguments: {(args != null ? string.Join(" ", args) : "(none)")}");
            DebugLogger.Log($"Stop at entry: {stopAtEntry}");
            
            if (!string.IsNullOrEmpty(netcoredbgPath))
            {
                _dapClient.NetCoreDbgPath = netcoredbgPath;
            }

            // Use CancellationTokenSource with timeout for better control
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var cancellationToken = timeoutCts.Token;

            // Start the DAP client
            DebugLogger.LogDebug("Starting DAP client...");
            await _dapClient.StartAsync(cancellationToken: cancellationToken);

            // Initialize
            DebugLogger.LogDebug("Initializing debugger...");
            var capabilities = await _dapClient.InitializeAsync(cancellationToken);
            if (capabilities == null)
            {
                return (false, "Failed to initialize debugger - no capabilities returned.");
            }

            // Launch
            DebugLogger.LogDebug($"Launching program: {programPath}");
            var launched = await _dapClient.LaunchAsync(programPath, args, Path.GetDirectoryName(programPath), stopAtEntry, cancellationToken);
            if (!launched)
            {
                return (false, "Failed to launch program - launch request failed.");
            }

            // Set any pending breakpoints
            if (Breakpoints.Any())
            {
                DebugLogger.LogDebug($"Setting {Breakpoints.Sum(bp => bp.Value.Count)} pending breakpoints...");
                foreach (var (file, lines) in Breakpoints)
                {
                    await _dapClient.SetBreakpointsAsync(file, [.. lines], cancellationToken);
                }
            }

            // Configuration done
            DebugLogger.LogDebug("Sending configuration done...");
            await _dapClient.ConfigurationDoneAsync(cancellationToken);

            lock (_lock)
            {
                IsActive = true;
                IsPaused = stopAtEntry;
                ProgramPath = programPath;
            }

            var message = $"Debug session started for {Path.GetFileName(programPath)}";
            DebugLogger.Log(message);
            return (true, message);
        }
        catch (OperationCanceledException)
        {
            var errorMsg = "Debug session launch was cancelled (timeout or cancellation)";
            DebugLogger.LogError(errorMsg);
            return (false, errorMsg);
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to start debug session: {ex.Message}";
            DebugLogger.LogError(errorMsg, ex);
            return (false, errorMsg);
        }
    }

    private string? GetTargetFramework(string programPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(programPath);
            if (string.IsNullOrEmpty(directory))
                return null;

            // Look for .deps.json file which contains target framework info
            var depsJsonFile = Path.ChangeExtension(programPath, ".deps.json");
            if (File.Exists(depsJsonFile))
            {
                var depsContent = File.ReadAllText(depsJsonFile);
                using var depsJson = JsonDocument.Parse(depsContent);
                
                if (depsJson.RootElement.TryGetProperty("targets", out var targets))
                {
                    var firstTarget = targets.EnumerateObject().FirstOrDefault();
                    return firstTarget.Name;
                }
            }

            // Fallback: try to infer from directory name
            var parts = directory.Split(Path.DirectorySeparatorChar);
            var netFrameworkPart = parts.FirstOrDefault(p => p.StartsWith("net"));
            return netFrameworkPart;
        }
        catch (Exception ex)
        {
            DebugLogger.LogDebug($"Could not determine target framework: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sets a breakpoint at the specified file and line.
    /// </summary>
    public async Task<(bool Success, string Message)> SetBreakpointAsync(string filePath, int line)
    {
        try
        {
            // Normalize path
            filePath = Path.GetFullPath(filePath);

            lock (_lock)
            {
                if (!Breakpoints.ContainsKey(filePath))
                {
                    Breakpoints[filePath] = new List<int>();
                }

                if (!Breakpoints[filePath].Contains(line))
                {
                    Breakpoints[filePath].Add(line);
                }
            }

            if (IsActive)
            {
                var breakpoints = await _dapClient.SetBreakpointsAsync(filePath, [.. Breakpoints[filePath]]);
                var bp = breakpoints.FirstOrDefault(b => b.Line == line);

                if (bp != null && bp.Verified)
                {
                    return (true, $"Breakpoint set at {Path.GetFileName(filePath)}:{line}");
                }
                else if (bp != null)
                {
                    return (false, $"Breakpoint at {Path.GetFileName(filePath)}:{line} could not be verified: {bp.Message ?? "Unknown reason"}");
                }
            }

            return (true, $"Breakpoint queued at {Path.GetFileName(filePath)}:{line} (will be set when session starts)");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to set breakpoint: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a breakpoint at the specified file and line.
    /// </summary>
    public async Task<(bool Success, string Message)> RemoveBreakpointAsync(string filePath, int line)
    {
        try
        {
            filePath = Path.GetFullPath(filePath);

            lock (_lock)
            {
                if (Breakpoints.TryGetValue(filePath, out var lines))
                {
                    lines.Remove(line);
                }
            }

            if (IsActive && Breakpoints.TryGetValue(filePath, out var remainingLines))
            {
                await _dapClient.SetBreakpointsAsync(filePath, [.. remainingLines]);
            }

            return (true, $"Breakpoint removed from {Path.GetFileName(filePath)}:{line}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to remove breakpoint: {ex.Message}");
        }
    }

    /// <summary>
    /// Steps to the next line (Step Over).
    /// </summary>
    public async Task<(bool Success, string Message)> StepNextAsync()
    {
        try
        {
            if (!IsActive)
            {
                return (false, "No active debug session.");
            }

            if (!IsPaused)
            {
                return (false, "Program is not paused.");
            }

            var success = await _dapClient.NextAsync(CurrentThreadId);
            if (success)
            {
                lock (_lock)
                {
                    IsPaused = false;
                }
                return (true, "Stepping to next line...");
            }

            return (false, "Step next failed.");
        }
        catch (Exception ex)
        {
            return (false, $"Step next failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Steps into the current function call.
    /// </summary>
    public async Task<(bool Success, string Message)> StepInAsync()
    {
        try
        {
            if (!IsActive)
            {
                return (false, "No active debug session.");
            }

            if (!IsPaused)
            {
                return (false, "Program is not paused.");
            }

            var success = await _dapClient.StepInAsync(CurrentThreadId);
            if (success)
            {
                lock (_lock)
                {
                    IsPaused = false;
                }
                return (true, "Stepping into...");
            }

            return (false, "Step in failed.");
        }
        catch (Exception ex)
        {
            return (false, $"Step in failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Continues execution until the next breakpoint.
    /// </summary>
    public async Task<(bool Success, string Message)> ContinueAsync()
    {
        try
        {
            if (!IsActive)
            {
                return (false, "No active debug session.");
            }

            if (!IsPaused)
            {
                return (false, "Program is not paused.");
            }

            var success = await _dapClient.ContinueAsync(CurrentThreadId);
            if (success)
            {
                lock (_lock)
                {
                    IsPaused = false;
                }
                return (true, "Continuing execution...");
            }

            return (false, "Continue failed.");
        }
        catch (Exception ex)
        {
            return (false, $"Continue failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Evaluates an expression in the current context.
    /// </summary>
    public async Task<(bool Success, string Result, string? Type)> EvaluateAsync(string expression)
    {
        try
        {
            if (!IsActive)
            {
                return (false, "No active debug session.", null);
            }

            if (!IsPaused)
            {
                return (false, "Program is not paused. Cannot evaluate expressions while running.", null);
            }

            var result = await _dapClient.EvaluateAsync(expression, CurrentFrameId);
            if (result != null)
            {
                return (true, result.Result, result.Type);
            }

            return (false, "Evaluation failed.", null);
        }
        catch (Exception ex)
        {
            return (false, $"Evaluation failed: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Gets the current stack trace.
    /// </summary>
    public async Task<(bool Success, Dap.StackFrame[] Frames, string Message)> GetStackTraceAsync()
    {
        try
        {
            if (!IsActive)
            {
                return (false, [], "No active debug session.");
            }

            if (!IsPaused)
            {
                return (false, [], "Program is not paused.");
            }

            var frames = await _dapClient.GetStackTraceAsync(CurrentThreadId);
            return (true, frames, $"Retrieved {frames.Length} stack frames.");
        }
        catch (Exception ex)
        {
            return (false, [], $"Failed to get stack trace: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets local variables for the current frame.
    /// </summary>
    public async Task<(bool Success, Variable[] Variables, string Message)> GetLocalVariablesAsync()
    {
        try
        {
            if (!IsActive)
            {
                return (false, [], "No active debug session.");
            }

            if (!IsPaused)
            {
                return (false, [], "Program is not paused.");
            }

            var scopes = await _dapClient.GetScopesAsync(CurrentFrameId);
            var localScope = scopes.FirstOrDefault(s => s.Name.Equals("Locals", StringComparison.OrdinalIgnoreCase));

            if (localScope == null)
            {
                return (true, [], "No local variables.");
            }

            var variables = await _dapClient.GetVariablesAsync(localScope.VariablesReference);
            return (true, variables, $"Retrieved {variables.Length} local variables.");
        }
        catch (Exception ex)
        {
            return (false, [], $"Failed to get local variables: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the current debug session.
    /// </summary>
    public async Task<(bool Success, string Message)> StopAsync()
    {
        try
        {
            if (!IsActive)
            {
                return (false, "No active debug session.");
            }

            await _dapClient.DisconnectAsync(terminateDebuggee: true);
            await _dapClient.DisposeAsync();

            lock (_lock)
            {
                IsActive = false;
                IsPaused = false;
                CurrentThreadId = 0;
                CurrentFrameId = 0;
                StopReason = null;
                _debuggedProcess = null;
            }

            return (true, "Debug session stopped.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to stop debug session: {ex.Message}");
        }
    }

    /// <summary>
    /// Takes a screenshot of the debugged application's main window.
    /// </summary>
    /// <returns>Base64-encoded image data.</returns>
    public async Task<(bool Success, string ImageData, string Message)> TakeScreenshotAsync()
    {
        if (!IsActive)
        {
            return (false, string.Empty, "No active debug session.");
        }

        // Try to find the debugged process if not already set
        if (_debuggedProcess == null)
        {
            await TryFindDebuggedProcessAsync();
        }

        return await _uiService.TakeScreenshotAsync();
    }

    /// <summary>
    /// Gets the UI control tree of the debugged application as XML.
    /// </summary>
    /// <param name="maxDepth">Maximum depth to traverse the control tree.</param>
    /// <returns>XML representation of the UI controls.</returns>
    public async Task<(bool Success, string Xml, string Message)> GetUiControlsAsync(int maxDepth = 10)
    {
        if (!IsActive)
        {
            return (false, string.Empty, "No active debug session.");
        }

        // Try to find the debugged process if not already set
        if (_debuggedProcess == null)
        {
            await TryFindDebuggedProcessAsync();
        }

        return await _uiService.GetControlsAsXmlAsync(maxDepth);
    }

    /// <summary>
    /// Sets the process ID of the debugged application for UI automation.
    /// </summary>
    public void SetDebuggedProcessId(int processId)
    {
        try
        {
            _debuggedProcess = Process.GetProcessById(processId);
            _uiService.SetTargetProcess(_debuggedProcess);
        }
        catch (Exception)
        {
            _debuggedProcess = null;
        }
    }

    #region UI Interaction Methods

    /// <summary>
    /// Clicks on a control by its handle.
    /// </summary>
    public async Task<(bool Success, string Message)> ClickControlAsync(long handle)
    {
        await EnsureProcessSetAsync();
        return await _uiService.ClickControlAsync(new IntPtr(handle));
    }

    /// <summary>
    /// Clicks at the specified screen coordinates.
    /// </summary>
    public async Task<(bool Success, string Message)> ClickAtAsync(int x, int y, string button = "left", int clickCount = 1)
    {
        await EnsureProcessSetAsync();
        return await _uiService.ClickAtAsync(x, y, button, clickCount);
    }

    /// <summary>
    /// Types text into the currently focused control or specified control.
    /// </summary>
    public async Task<(bool Success, string Message)> TypeTextAsync(string text, long? handle = null)
    {
        await EnsureProcessSetAsync();
        IntPtr? handlePtr = handle.HasValue ? new IntPtr(handle.Value) : null;
        return await _uiService.TypeTextAsync(text, handlePtr);
    }

    /// <summary>
    /// Sends keyboard keys (including special keys like Enter, Tab, Ctrl+C, etc.).
    /// </summary>
    public async Task<(bool Success, string Message)> SendKeysAsync(string keys)
    {
        await EnsureProcessSetAsync();
        return await _uiService.SendKeysAsync(keys);
    }

    /// <summary>
    /// Moves the mouse to the specified screen coordinates.
    /// </summary>
    public async Task<(bool Success, string Message)> MouseMoveAsync(int x, int y)
    {
        await EnsureProcessSetAsync();
        return await _uiService.MouseMoveAsync(x, y);
    }

    /// <summary>
    /// Performs a mouse drag from one point to another.
    /// </summary>
    public async Task<(bool Success, string Message)> MouseDragAsync(int startX, int startY, int endX, int endY, string button = "left")
    {
        await EnsureProcessSetAsync();
        return await _uiService.MouseDragAsync(startX, startY, endX, endY, button);
    }

    /// <summary>
    /// Sets focus to a control by its handle.
    /// </summary>
    public async Task<(bool Success, string Message)> FocusControlAsync(long handle)
    {
        await EnsureProcessSetAsync();
        return await _uiService.FocusControlAsync(new IntPtr(handle));
    }

    /// <summary>
    /// Scrolls the mouse wheel at the current or specified position.
    /// </summary>
    public async Task<(bool Success, string Message)> MouseScrollAsync(int delta, int? x = null, int? y = null)
    {
        await EnsureProcessSetAsync();
        return await _uiService.MouseScrollAsync(delta, x, y);
    }

    /// <summary>
    /// Finds a control by text and returns its handle.
    /// </summary>
    public async Task<(bool Success, long Handle, string Message)> FindControlByTextAsync(string text, bool exactMatch = false)
    {
        await EnsureProcessSetAsync();
        var (success, handle, message) = await _uiService.FindControlByTextAsync(text, exactMatch);
        return (success, handle.ToInt64(), message);
    }

    /// <summary>
    /// Ensures the target process is set for UI operations.
    /// </summary>
    private async Task EnsureProcessSetAsync()
    {
        if (_debuggedProcess == null)
        {
            await TryFindDebuggedProcessAsync();
        }
    }

    #endregion

    /// <summary>
    /// Tries to find the debugged process by looking for processes started after the debug session began.
    /// </summary>
    private async Task TryFindDebuggedProcessAsync()
    {
        if (string.IsNullOrEmpty(ProgramPath))
        {
            return;
        }

        try
        {
            var programName = Path.GetFileNameWithoutExtension(ProgramPath);

            // Look for processes with matching name
            var processes = Process.GetProcessesByName(programName);
            if (processes.Length > 0)
            {
                // Use the most recently started one
                _debuggedProcess = processes.OrderByDescending(p =>
                {
                    try { return p.StartTime; }
                    catch { return DateTime.MinValue; }
                }).FirstOrDefault();

                if (_debuggedProcess != null)
                {
                    _uiService.SetTargetProcess(_debuggedProcess);
                }
            }

            // Also try "dotnet" processes that might be running our DLL
            if (_debuggedProcess == null)
            {
                var dotnetProcesses = Process.GetProcessesByName("dotnet");
                foreach (var proc in dotnetProcesses)
                {
                    try
                    {
                        // Check if command line contains our program
                        // This is a heuristic and may not always work
                        _debuggedProcess = proc;
                        _uiService.SetTargetProcess(_debuggedProcess);
                        break;
                    }
                    catch
                    {
                        // Continue to next process
                    }
                }
            }
        }
        catch
        {
            // Ignore errors in process discovery
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Updates the session state - used by EnhancedDebugSession for VsDbg integration.
    /// </summary>
    internal void UpdateSessionState(bool isActive, string? programPath = null, bool isPaused = false)
    {
        lock (_lock)
        {
            IsActive = isActive;
            if (programPath != null)
            {
                ProgramPath = programPath;
            }
            IsPaused = isPaused;
        }
    }

    private void HandleStopped(StoppedEventBody body)
    {
        lock (_lock)
        {
            IsPaused = true;
            CurrentThreadId = body.ThreadId;
            StopReason = body.Reason;
        }

        // Get the current frame
        Task.Run(async () =>
        {
            try
            {
                var frames = await _dapClient.GetStackTraceAsync(body.ThreadId, 0, 1);
                if (frames.Length > 0)
                {
                    lock (_lock)
                    {
                        CurrentFrameId = frames[0].Id;
                    }
                }

                OnDebugEvent?.Invoke("stopped", new
                {
                    reason = body.Reason,
                    threadId = body.ThreadId,
                    description = body.Description,
                    location = frames.Length > 0 ? new
                    {
                        file = frames[0].Source?.Path,
                        line = frames[0].Line,
                        function = frames[0].Name
                    } : null
                });
            }
            catch
            {
                // Ignore errors getting frame info
            }
        });
    }

    private void HandleOutput(OutputEventBody body)
    {
        lock (_lock)
        {
            OutputMessages.Add($"[{body.Category}] {body.Output}");
        }

        OnDebugEvent?.Invoke("output", new
        {
            category = body.Category,
            output = body.Output
        });
    }

    private void HandleTerminated(TerminatedEventBody body)
    {
        lock (_lock)
        {
            IsActive = false;
            IsPaused = false;
        }

        OnDebugEvent?.Invoke("terminated", new { restart = body.Restart });
    }

    private void HandleInitialized()
    {
        OnDebugEvent?.Invoke("initialized", null);
    }
}
