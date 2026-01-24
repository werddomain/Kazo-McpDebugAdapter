using System.Diagnostics;
using System.Text.Json;
using McpDebugAdapter.Dap;

namespace McpDebugAdapter;

/// <summary>
/// Enhanced DebugSession that supports multiple debugging approaches:
/// 1. Direct launch with vsdbg (better .NET 10+ support)
/// 2. Launch-then-attach mode for maximum compatibility
/// 3. Attach to existing processes
/// 
/// Uses composition instead of inheritance to avoid virtual method limitations.
/// </summary>
public class EnhancedDebugSession
{
    private readonly DebugSession _baseSession;
    private Process? _launchedProcess;
    private VsDbgClient? _activeDebugger;
    private bool IsVsDbgAttached = false;

    /// <summary>
    /// Delegate all base properties to the underlying session
    /// </summary>
    public bool IsActive => _baseSession.IsActive;
    public bool IsPaused => _baseSession.IsPaused;
    public int CurrentThreadId => _baseSession.CurrentThreadId;
    public int CurrentFrameId => _baseSession.CurrentFrameId;
    public string? StopReason => _baseSession.StopReason;
    public string? ProgramPath => _baseSession.ProgramPath;
    public Dictionary<string, List<int>> Breakpoints => _baseSession.Breakpoints;
    public List<string> OutputMessages => _baseSession.OutputMessages;
    public event Action<string, object?>? OnDebugEvent
    {
        add => _baseSession.OnDebugEvent += value;
        remove => _baseSession.OnDebugEvent -= value;
    }

    public EnhancedDebugSession()
    {
        _baseSession = new DebugSession();
    }

    /// <summary>
    /// Launch using the enhanced approach with better .NET 10+ support.
    /// </summary>
    /// <param name="programPath">Path to the program to debug</param>
    /// <param name="args">Command line arguments</param>
    /// <param name="stopAtEntry">Whether to stop at entry point</param>
    /// <param name="useVsDbg">Use vsdbg instead of netcoredbg</param>
    /// <param name="useLaunchThenAttach">Launch process first, then attach debugger</param>
    /// <returns>Success status and message</returns>
    public async Task<(bool Success, string Message)> LaunchEnhancedAsync(
        string programPath,
        string[]? args = null,
        bool stopAtEntry = false,
        bool useVsDbg = true,
        bool useLaunchThenAttach = false)
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

            DebugLogger.Log($"Starting enhanced debug session for: {programPath}");
            DebugLogger.Log($"Use vsdbg: {useVsDbg}, Launch-then-attach: {useLaunchThenAttach}");

            if (useLaunchThenAttach)
            {
                return await LaunchThenAttachAsync(programPath, args, stopAtEntry, useVsDbg);
            }
            else
            {
                return await DirectLaunchAsync(programPath, args, stopAtEntry, useVsDbg);
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to start enhanced debug session: {ex.Message}";
            DebugLogger.LogError(errorMsg, ex);
            return (false, errorMsg);
        }
    }

    /// <summary>
    /// Direct launch approach (existing behavior but with vsdbg option).
    /// </summary>
    private async Task<(bool Success, string Message)> DirectLaunchAsync(
        string programPath, 
        string[]? args, 
        bool stopAtEntry, 
        bool useVsDbg)
    {
        if (useVsDbg)
        {
            // Try to use VsDbgClient for better .NET 10+ support
            try
            {
                DebugLogger.Log("Attempting to use vsdbg for .NET 10+ compatibility...");
                return await LaunchWithVsDbgAsync(programPath, args, stopAtEntry);
            }
            catch (Exception ex)
            {
                DebugLogger.LogWarning($"VsDbg failed ({ex.Message}), falling back to netcoredbg");
                return await _baseSession.LaunchAsync(programPath, args, stopAtEntry);
            }
        }
        else
        {
            return await _baseSession.LaunchAsync(programPath, args, stopAtEntry);
        }
    }

    /// <summary>
    /// Launch using VsDbgClient directly for better .NET 10+ support.
    /// </summary>
    private async Task<(bool Success, string Message)> LaunchWithVsDbgAsync(
        string programPath, 
        string[]? args, 
        bool stopAtEntry)
    {
        var vsdbgClient = new VsDbgClient();
        
        try
        {
            // Use CancellationTokenSource with timeout for better control
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var cancellationToken = timeoutCts.Token;

            // Start the VsDbg client
            DebugLogger.LogDebug("Starting VsDbg client...");
            await vsdbgClient.StartAsync(cancellationToken: cancellationToken);

            // Initialize
            DebugLogger.LogDebug("Initializing vsdbg debugger...");
            var capabilities = await vsdbgClient.InitializeAsync(cancellationToken);
            if (capabilities == null)
            {
                return (false, "Failed to initialize vsdbg - no capabilities returned.");
            }

            // Launch
            DebugLogger.LogDebug($"Launching program with vsdbg: {programPath}");
            var launched = await vsdbgClient.LaunchAsync(programPath, args, Path.GetDirectoryName(programPath), stopAtEntry, cancellationToken);
            if (!launched)
            {
                return (false, "Failed to launch program with vsdbg - launch request failed.");
            }

            // Set any pending breakpoints
            if (_baseSession.Breakpoints.Any())
            {
                DebugLogger.LogDebug($"Setting {_baseSession.Breakpoints.Sum(bp => bp.Value.Count)} pending breakpoints...");
                foreach (var (file, lines) in _baseSession.Breakpoints)
                {
                    await vsdbgClient.SetBreakpointsAsync(file, [.. lines], cancellationToken);
                }
            }

            // Configuration done
            DebugLogger.LogDebug("Sending configuration done to vsdbg...");
            await vsdbgClient.ConfigurationDoneAsync(cancellationToken);

            var message = $"Debug session started with vsdbg for {Path.GetFileName(programPath)}";
            DebugLogger.Log(message);
            
            // Store the active VsDbg client and wire up events
            _activeDebugger = vsdbgClient;
            IsVsDbgAttached = true;
            
            // Wire up VsDbg events to update session state
            _activeDebugger.OnStopped += (stoppedEvent) => 
            {
                _baseSession.UpdateSessionState(true, programPath, true); // paused = true
                DebugLogger.LogDebug($"VsDbg stopped: {stoppedEvent.Reason}");
            };
            
            _activeDebugger.OnTerminated += (terminatedEvent) =>
            {
                _baseSession.UpdateSessionState(false); // session ended
                IsVsDbgAttached = false;
                _activeDebugger = null;
                DebugLogger.LogDebug("VsDbg session terminated");
            };
            
            // Update base session state to reflect active debugging
            _baseSession.UpdateSessionState(true, programPath);
            
            return (true, message);
        }
        catch (Exception ex)
        {
            await vsdbgClient.DisposeAsync();
            throw new Exception($"VsDbg launch failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Delegate all base debug methods to the underlying session
    /// </summary>
    public async Task<(bool Success, string Message)> LaunchAsync(string programPath, string[]? args = null, bool stopAtEntry = false, string? netcoredbgPath = null)
        => await _baseSession.LaunchAsync(programPath, args, stopAtEntry, netcoredbgPath);

    public async Task<(bool Success, string Message)> SetBreakpointAsync(string filePath, int line)
    {
        if (IsVsDbgAttached && _activeDebugger != null)
        {
            try
            {
                var breakpoints = await _activeDebugger.SetBreakpointsAsync(filePath, [line]);
                if (breakpoints.Any())
                {
                    // Also update base session breakpoints for tracking
                    await _baseSession.SetBreakpointAsync(filePath, line);
                    return (true, $"Breakpoint set with VsDbg at {Path.GetFileName(filePath)}:{line}");
                }
                return (false, "VsDbg failed to set breakpoint");
            }
            catch (Exception ex)
            {
                return (false, $"VsDbg set breakpoint failed: {ex.Message}");
            }
        }
        return await _baseSession.SetBreakpointAsync(filePath, line);
    }

    public async Task<(bool Success, string Message)> RemoveBreakpointAsync(string filePath, int line)
        => await _baseSession.RemoveBreakpointAsync(filePath, line);

    public async Task<(bool Success, string Message)> ContinueAsync()
    {
        if (IsVsDbgAttached && _activeDebugger != null)
        {
            // Use VsDbg for continue operation  
            try
            {
                var success = await _activeDebugger.ContinueAsync(CurrentThreadId);
                return (success, success ? "Continue sent to VsDbg" : "Continue failed with VsDbg");
            }
            catch (Exception ex)
            {
                return (false, $"VsDbg continue failed: {ex.Message}");
            }
        }
        return await _baseSession.ContinueAsync();
    }

    public async Task<(bool Success, string Message)> StepNextAsync()
    {
        if (IsVsDbgAttached && _activeDebugger != null)
        {
            try
            {
                var success = await _activeDebugger.NextAsync(CurrentThreadId);
                return (success, success ? "Step next sent to VsDbg" : "Step next failed with VsDbg");
            }
            catch (Exception ex)
            {
                return (false, $"VsDbg step next failed: {ex.Message}");
            }
        }
        return await _baseSession.StepNextAsync();
    }

    public async Task<(bool Success, string Message)> StepInAsync()
    {
        if (IsVsDbgAttached && _activeDebugger != null)
        {
            try
            {
                var success = await _activeDebugger.StepInAsync(CurrentThreadId);
                return (success, success ? "Step in sent to VsDbg" : "Step in failed with VsDbg");
            }
            catch (Exception ex)
            {
                return (false, $"VsDbg step in failed: {ex.Message}");
            }
        }
        return await _baseSession.StepInAsync();
    }

    public async Task<(bool Success, string Message, object?)> EvaluateAsync(string expression)
    {
        if (IsVsDbgAttached && _activeDebugger != null)
        {
            try
            {
                var (result, type) = await _activeDebugger.EvaluateAsync(expression, "repl", CurrentFrameId);
                return (true, $"Evaluation result: {result} (type: {type})", result);
            }
            catch (Exception ex)
            {
                return (false, $"VsDbg evaluation failed: {ex.Message}", null);
            }
        }
        return await _baseSession.EvaluateAsync(expression);
    }

    public async Task<(bool Success, Dap.StackFrame[] Frames, string Message)> GetStackTraceAsync()
        => await _baseSession.GetStackTraceAsync();

    public async Task<(bool Success, Variable[] Variables, string Message)> GetLocalVariablesAsync()
        => await _baseSession.GetLocalVariablesAsync();

    // UI automation methods - delegate to base session
    public async Task<(bool Success, string ImageData, string Message)> TakeScreenshotAsync()
        => await _baseSession.TakeScreenshotAsync();

    public async Task<(bool Success, string Xml, string Message)> GetUiControlsAsync(int maxDepth = 10)
        => await _baseSession.GetUiControlsAsync(maxDepth);

    public void SetDebuggedProcessId(int processId) 
        => _baseSession.SetDebuggedProcessId(processId);

    public async Task<(bool Success, string Message)> ClickControlAsync(long handle)
        => await _baseSession.ClickControlAsync(handle);

    public async Task<(bool Success, string Message)> ClickAtAsync(int x, int y, string button = "left", int clickCount = 1)
        => await _baseSession.ClickAtAsync(x, y, button, clickCount);

    public async Task<(bool Success, string Message)> TypeTextAsync(string text, long? handle = null)
        => await _baseSession.TypeTextAsync(text, handle);

    public async Task<(bool Success, string Message)> SendKeysAsync(string keys)
        => await _baseSession.SendKeysAsync(keys);

    public async Task<(bool Success, string Message)> MouseMoveAsync(int x, int y)
        => await _baseSession.MouseMoveAsync(x, y);

    public async Task<(bool Success, string Message)> MouseDragAsync(int fromX, int fromY, int toX, int toY, string button = "left")
        => await _baseSession.MouseDragAsync(fromX, fromY, toX, toY, button);

    public async Task<(bool Success, string Message)> MouseScrollAsync(int delta, int? x = null, int? y = null)
        => await _baseSession.MouseScrollAsync(delta, x, y);

    public async Task<(bool Success, string Message)> FocusControlAsync(long handle)
        => await _baseSession.FocusControlAsync(handle);

    public async Task<(bool Success, long Handle, string Message)> FindControlByTextAsync(string text, bool exactMatch = false)
        => await _baseSession.FindControlByTextAsync(text, exactMatch);

    /// <summary>
    /// Launch-then-attach approach for maximum compatibility.
    /// </summary>
    private async Task<(bool Success, string Message)> LaunchThenAttachAsync(
        string programPath, 
        string[]? args, 
        bool stopAtEntry, 
        bool useVsDbg)
    {
        // Step 1: Launch the process
        DebugLogger.LogDebug("Step 1: Launching target process...");
        var launchResult = await ProcessLaunchManager.LaunchForDebuggingAsync(
            programPath, 
            args, 
            Path.GetDirectoryName(programPath),
            waitForDebugger: stopAtEntry);

        if (!launchResult.Success || launchResult.Process == null)
        {
            return (false, $"Failed to launch process: {launchResult.Message}");
        }

        _launchedProcess = launchResult.Process;
        DebugLogger.Log($"Process launched with PID: {_launchedProcess.Id}");

        // Step 2: Start the debugger
        DebugLogger.LogDebug("Step 2: Starting debugger...");
        try
        {
            if (useVsDbg)
            {
                // Use VsDbgClient for better .NET 10+ support
                DebugLogger.Log("Using VsDbg for .NET 10+ compatibility...");
                try
                {
                    return await AttachWithVsDbgAsync(_launchedProcess.Id, stopAtEntry);
                }
                catch (Exception ex)
                {
                    DebugLogger.LogWarning($"VsDbg failed ({ex.Message}), falling back to netcoredbg");
                    return await AttachWithNetCoreDbgAsync(_launchedProcess.Id, stopAtEntry);
                }
            }
            else
            {
                // Use traditional netcoredbg approach
                DebugLogger.Log("Using netcoredbg for debugging...");
                return await AttachWithNetCoreDbgAsync(_launchedProcess.Id, stopAtEntry);
            }
        }
        catch (Exception ex)
        {
            // Clean up the launched process if debugger attachment failed
            if (_launchedProcess != null && !_launchedProcess.HasExited)
            {
                try
                {
                    _launchedProcess.Kill();
                }
                catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Attach to an existing process by PID using the DAP client directly.
    /// </summary>
    /// <param name="processId">Process ID to attach to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    private async Task<bool> AttachToProcessAsync(int processId, CancellationToken cancellationToken = default)
    {
        try
        {
            // For now, we'll use the launch approach and note that proper attach
            // support requires extending the DapClient to support attach requests
            DebugLogger.LogWarning($"Direct attach to process {processId} not fully implemented. Using launch approach instead.");
            
            // TODO: Implement proper DAP attach request
            // This would involve:
            // 1. Adding AttachAsync method to DapClient
            // 2. Creating AttachRequestArguments class
            // 3. Sending "attach" DAP request instead of "launch"
            
            return false; // For now, indicate that direct attach isn't implemented
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Failed to attach to process {processId}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Attach to an existing process by name or window title.
    /// </summary>
    public async Task<(bool Success, string Message)> AttachToProcessAsync(
        string? processName = null, 
        int? processId = null)
    {
        try
        {
            if (IsActive)
            {
                return (false, "A debug session is already active. Stop it first.");
            }

            Process? targetProcess = null;

            if (processId.HasValue)
            {
                // Attach by PID
                try
                {
                    targetProcess = Process.GetProcessById(processId.Value);
                }
                catch (ArgumentException)
                {
                    return (false, $"Process with ID {processId} not found.");
                }
            }
            else if (!string.IsNullOrEmpty(processName))
            {
                // Find by name
                var processes = ProcessLaunchManager.FindDebuggableProcesses(processName);
                if (!processes.Any())
                {
                    return (false, $"No debuggable processes found matching '{processName}'.");
                }

                if (processes.Count > 1)
                {
                    var processNames = string.Join("\n", processes.Select(p => p.ToString()));
                    return (false, $"Multiple processes found matching '{processName}':\n{processNames}\nSpecify a process ID instead.");
                }

                targetProcess = Process.GetProcessById(processes.First().Id);
            }
            else
            {
                return (false, "Either processName or processId must be specified.");
            }

            if (targetProcess == null)
            {
                return (false, "Could not find target process.");
            }

            DebugLogger.Log($"Attaching to process: {targetProcess.ProcessName} (PID: {targetProcess.Id})");

            // Start debugger and attach
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var cancellationToken = timeoutCts.Token;

            await _baseSession.DapClient.StartAsync(cancellationToken: cancellationToken);
            
            var capabilities = await _baseSession.DapClient.InitializeAsync(cancellationToken);
            if (capabilities == null)
            {
                return (false, "Failed to initialize debugger.");
            }

            var attached = await AttachToProcessAsync(targetProcess.Id, cancellationToken);
            if (!attached)
            {
                return (false, "Failed to attach to process.");
            }

            await _baseSession.DapClient.ConfigurationDoneAsync(cancellationToken);

            var message = $"Attached to process {targetProcess.ProcessName} (PID: {targetProcess.Id})";
            DebugLogger.Log(message);
            return (true, message);
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to attach to process: {ex.Message}";
            DebugLogger.LogError(errorMsg, ex);
            return (false, errorMsg);
        }
    }

    /// <summary>
    /// List available processes for debugging.
    /// </summary>
    public (bool Success, List<ProcessInfo> Processes, string Message) ListDebuggableProcesses(string? filter = null)
    {
        try
        {
            var processes = ProcessLaunchManager.FindDebuggableProcesses(filter);
            var message = $"Found {processes.Count} debuggable process{(processes.Count == 1 ? "" : "es")}";
            
            if (!string.IsNullOrEmpty(filter))
            {
                message += $" matching '{filter}'";
            }

            return (true, processes, message);
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to list processes: {ex.Message}";
            DebugLogger.LogError(errorMsg, ex);
            return (false, new List<ProcessInfo>(), errorMsg);
        }
    }

    public async Task<(bool Success, string Message)> StopAsync()
    {
        try
        {
            var result = await _baseSession.StopAsync();

            // Clean up launched process if we own it
            if (_launchedProcess != null && !_launchedProcess.HasExited)
            {
                try
                {
                    DebugLogger.LogDebug($"Terminating launched process {_launchedProcess.Id}");
                    _launchedProcess.Kill();
                    await _launchedProcess.WaitForExitAsync();
                }
                catch (Exception ex)
                {
                    DebugLogger.LogWarning($"Failed to clean up launched process: {ex.Message}");
                }
                finally
                {
                    _launchedProcess.Dispose();
                    _launchedProcess = null;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to stop session: {ex.Message}";
            DebugLogger.LogError(errorMsg, ex);
            return (false, errorMsg);
        }
    }

    /// <summary>
    /// Attach using VsDbgClient for better .NET 10+ support.
    /// </summary>
    private async Task<(bool Success, string Message)> AttachWithVsDbgAsync(
        int processId, 
        bool stopAtEntry)
    {
        var vsdbgClient = new VsDbgClient();
        
        try
        {
            // Use CancellationTokenSource with timeout for better control
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var cancellationToken = timeoutCts.Token;

            // Start the VsDbg client
            DebugLogger.LogDebug("Starting VsDbg client...");
            await vsdbgClient.StartAsync(cancellationToken: cancellationToken);

            // Initialize
            DebugLogger.LogDebug("Initializing vsdbg debugger...");
            var capabilities = await vsdbgClient.InitializeAsync(cancellationToken);
            if (capabilities == null)
            {
                return (false, "Failed to initialize vsdbg - no capabilities returned.");
            }

            // For attach, we need to use the DAP attach request
            DebugLogger.LogDebug($"Attaching to process {processId} with VsDbg...");

            // Send attach request to vsdbg
            var attachSuccess = await vsdbgClient.AttachAsync(processId, cancellationToken: cancellationToken);
            
            if (!attachSuccess)
            {
                await vsdbgClient.DisposeAsync();
                return (false, "VsDbg attach request failed");
            }

            DebugLogger.LogDebug("VsDbg attach succeeded, waiting for debugger to stabilize...");
            await Task.Delay(500, cancellationToken); // Give the debugger time to attach properly

            // Configure the session  
            DebugLogger.LogDebug("Sending configurationDone to vsdbg...");
            try
            {
                var configSuccess = await vsdbgClient.ConfigurationDoneAsync(cancellationToken);
                if (!configSuccess)
                {
                    DebugLogger.LogWarning("ConfigurationDone returned false, but continuing with attach (some debuggers don't require this for attach)");
                }
                else
                {
                    DebugLogger.LogDebug("ConfigurationDone succeeded");
                }
            }
            catch (Exception configEx)
            {
                DebugLogger.LogWarning($"ConfigurationDone failed: {configEx.Message}, but continuing with attach (this is often not critical for attach scenarios)");
            }

            DebugLogger.Log("Successfully attached VsDbg to process");
            
            // Store the client for this session and wire up events
            _activeDebugger = vsdbgClient;
            IsVsDbgAttached = true;
            
            // Wire up VsDbg events to update session state
            _activeDebugger.OnStopped += (stoppedEvent) => 
            {
                _baseSession.UpdateSessionState(true, null, true); // paused = true
                DebugLogger.LogDebug($"VsDbg stopped: {stoppedEvent.Reason}");
            };
            
            _activeDebugger.OnTerminated += (terminatedEvent) =>
            {
                _baseSession.UpdateSessionState(false); // session ended
                IsVsDbgAttached = false;
                _activeDebugger = null;
                DebugLogger.LogDebug("VsDbg session terminated");
            };
            
            // Update base session state to reflect active debugging  
            _baseSession.UpdateSessionState(true, null, false);
            
            return (true, "Successfully attached with VsDbg");
        }
        catch (Exception ex)
        {
            await vsdbgClient.DisposeAsync();
            // Return failure instead of throwing to allow fallback
            var message = $"VsDbg failed: {ex.Message}";
            if (ex.Message.Contains("Could not locate vsdbg"))
            {
                message += "\n\nTo enable better .NET 10+ debugging:\n" +
                          "1. Install VS Code (includes vsdbg)\n" +
                          "2. Set VSDBG_PATH environment variable\n" +
                          "3. Or use useVsDbg: false for basic debugging";
            }
            return (false, message);
        }
    }

    /// <summary>
    /// Attach using traditional netcoredbg approach.
    /// </summary>
    private async Task<(bool Success, string Message)> AttachWithNetCoreDbgAsync(
        int processId, 
        bool stopAtEntry)
    {
        try
        {
            // Use CancellationTokenSource with timeout for better control
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var cancellationToken = timeoutCts.Token;

            // Start the DAP client (with netcoredbg)
            await _baseSession.DapClient.StartAsync(cancellationToken: cancellationToken);

            // Initialize
            DebugLogger.LogDebug("Initializing netcoredbg debugger...");
            var capabilities = await _baseSession.DapClient.InitializeAsync(cancellationToken);
            if (capabilities == null)
            {
                _launchedProcess?.Kill();
                return (false, "Failed to initialize netcoredbg - no capabilities returned.");
            }

            // Step 3: Attach to the process
            DebugLogger.LogDebug($"Step 3: Attaching to process {processId}...");
            var attached = await AttachToProcessAsync(processId, cancellationToken);
            if (!attached)
            {
                _launchedProcess?.Kill();
                return (false, "Failed to attach to process.");
            }

            // Set any pending breakpoints
            if (_baseSession.Breakpoints.Any())
            {
                DebugLogger.LogDebug($"Setting {_baseSession.Breakpoints.Sum(bp => bp.Value.Count)} pending breakpoints...");
                foreach (var (file, lines) in _baseSession.Breakpoints)
                {
                    await _baseSession.DapClient.SetBreakpointsAsync(file, [.. lines], cancellationToken);
                }
            }

            // Configuration done
            DebugLogger.LogDebug("Sending configuration done...");
            await _baseSession.DapClient.ConfigurationDoneAsync(cancellationToken);

            var message = $"Debug session attached to process {processId} using netcoredbg";
            DebugLogger.Log(message);
            return (true, message);
        }
        catch (Exception ex)
        {
            _launchedProcess?.Kill();
            
            // Provide more helpful error messages for .NET 10+ compatibility issues
            var errorMessage = $"NetCoreDbg attach failed: {ex.Message}";
            if (ex.Message.Contains("task was canceled") || ex.Message.Contains("timeout"))
            {
                errorMessage += "\n\nThis appears to be a .NET 10+ compatibility issue with NetCoreDbg.\n" +
                              "Recommended solutions:\n" +
                              "1. Install VS Code to enable VsDbg (better .NET 10+ support)\n" +
                              "2. Build your app targeting net8.0 or net9.0 for better compatibility\n" +
                              "3. Use Visual Studio or VS Code's built-in debugger for .NET 10+ apps";
            }
            
            return (false, errorMessage);
        }
    }
}