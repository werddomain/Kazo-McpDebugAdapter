using McpDebugAdapter.Dap;

namespace McpDebugAdapter;

/// <summary>
/// Manages the state of a debug session including threads, frames, and breakpoints.
/// </summary>
public class DebugSession
{
    private readonly DapClient _dapClient;
    private readonly object _lock = new();

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

        _dapClient.OnStopped += HandleStopped;
        _dapClient.OnOutput += HandleOutput;
        _dapClient.OnTerminated += HandleTerminated;
        _dapClient.OnInitialized += HandleInitialized;
    }

    /// <summary>
    /// Starts a new debug session.
    /// </summary>
    /// <param name="dllPath">Path to the DLL to debug.</param>
    /// <param name="args">Command line arguments for the program.</param>
    /// <param name="stopAtEntry">Whether to stop at entry point.</param>
    /// <param name="netcoredbgPath">Path to netcoredbg executable.</param>
    public async Task<(bool Success, string Message)> LaunchAsync(
        string dllPath,
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

            if (!File.Exists(dllPath))
            {
                return (false, $"File not found: {dllPath}");
            }

            if (!string.IsNullOrEmpty(netcoredbgPath))
            {
                _dapClient.NetCoreDbgPath = netcoredbgPath;
            }

            // Start the DAP client
            await _dapClient.StartAsync();

            // Initialize
            var capabilities = await _dapClient.InitializeAsync();
            if (capabilities == null)
            {
                return (false, "Failed to initialize debugger.");
            }

            // Launch
            var launched = await _dapClient.LaunchAsync(dllPath, args, Path.GetDirectoryName(dllPath), stopAtEntry);
            if (!launched)
            {
                return (false, "Failed to launch program.");
            }

            // Set any pending breakpoints
            foreach (var (file, lines) in Breakpoints)
            {
                await _dapClient.SetBreakpointsAsync(file, [.. lines]);
            }

            // Configuration done
            await _dapClient.ConfigurationDoneAsync();

            lock (_lock)
            {
                IsActive = true;
                IsPaused = stopAtEntry;
                ProgramPath = dllPath;
            }

            return (true, $"Debug session started for {Path.GetFileName(dllPath)}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start debug session: {ex.Message}");
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
    public async Task<(bool Success, StackFrame[] Frames, string Message)> GetStackTraceAsync()
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
            }

            return (true, "Debug session stopped.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to stop debug session: {ex.Message}");
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
