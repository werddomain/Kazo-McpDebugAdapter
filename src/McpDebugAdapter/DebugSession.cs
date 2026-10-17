using System.Diagnostics;
using McpDebugAdapter.Dap;
using McpDebugAdapter.Ui;

namespace McpDebugAdapter;

/// <summary>
/// Location where the debuggee is currently stopped.
/// </summary>
public record StopLocation(string Reason, int ThreadId, string? Description, string? Function, string? File, int Line);

/// <summary>
/// State of one breakpoint as last reported by the debugger.
/// </summary>
public class BreakpointInfo
{
    public string File { get; init; } = string.Empty;
    public int Line { get; init; }
    public string? Condition { get; set; }
    public int? Id { get; set; }
    public bool Verified { get; set; }
    public int? ActualLine { get; set; }
    public string? Message { get; set; }

    public string Describe()
    {
        var where = $"{Path.GetFileName(File)}:{Line}";
        if (Verified)
        {
            return ActualLine is { } actual && actual != Line
                ? $"{where} (bound to line {actual})"
                : $"{where} (bound)";
        }

        return $"{where} (pending: {Message ?? "will bind when the module is loaded"})";
    }
}

/// <summary>
/// Manages one debug session (netcoredbg via DAP) plus UI automation of the debuggee.
/// </summary>
public class DebugSession
{
    private const int MaxOutputMessages = 1000;
    private static readonly TimeSpan InitializedTimeout = TimeSpan.FromSeconds(30);

    private readonly EnhancedUiAutomationService _uiService = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, List<BreakpointInfo>> _breakpoints = new(PathComparer);
    private DapClient? _dapClient;
    private Process? _debuggedProcess;
    private bool _attached;
    private TaskCompletionSource<StopLocation> _stopSignal = NewStopSignal();

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
    /// Where the debuggee is stopped, when it is.
    /// </summary>
    public StopLocation? CurrentLocation { get; private set; }

    /// <summary>
    /// Path to the program being debugged.
    /// </summary>
    public string? ProgramPath { get; private set; }

    /// <summary>
    /// OS process id of the debuggee.
    /// </summary>
    public int? ProcessId { get; private set; }

    /// <summary>
    /// Exit code of the debuggee once it exited.
    /// </summary>
    public int? ExitCode { get; private set; }

    /// <summary>
    /// Map of file paths to their breakpoint lines.
    /// </summary>
    public Dictionary<string, List<int>> Breakpoints
    {
        get
        {
            lock (_lock)
            {
                return _breakpoints.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Select(b => b.Line).ToList());
            }
        }
    }

    /// <summary>
    /// Detailed state of every breakpoint.
    /// </summary>
    public IReadOnlyList<BreakpointInfo> BreakpointDetails
    {
        get
        {
            lock (_lock)
            {
                return _breakpoints.Values.SelectMany(list => list).ToList();
            }
        }
    }

    /// <summary>
    /// Output messages from the debugger and the debuggee (most recent last).
    /// </summary>
    public List<string> OutputMessages { get; } = new();

    /// <summary>
    /// Event fired when a debug event occurs (for notification to MCP client).
    /// </summary>
    public event Action<string, object?>? OnDebugEvent;

    /// <summary>
    /// Starts a new debug session by launching a program under netcoredbg.
    /// </summary>
    /// <param name="programPath">Path to the program (DLL or EXE) to debug.</param>
    /// <param name="args">Command line arguments for the program.</param>
    /// <param name="stopAtEntry">Whether to stop at entry point.</param>
    /// <param name="netcoredbgPath">Path to netcoredbg executable (optional).</param>
    /// <param name="cwd">Working directory (defaults to the program's folder).</param>
    public async Task<(bool Success, string Message)> LaunchAsync(
        string programPath,
        string[]? args = null,
        bool stopAtEntry = false,
        string? netcoredbgPath = null,
        string? cwd = null)
    {
        if (IsActive)
        {
            return (false, "A debug session is already active. Stop it first.");
        }

        programPath = Path.GetFullPath(programPath);
        if (!File.Exists(programPath))
        {
            return (false, $"File not found: {programPath}");
        }

        var launchArgs = new LaunchRequestArguments
        {
            Program = programPath,
            Args = args ?? [],
            Cwd = cwd ?? Path.GetDirectoryName(programPath),
            StopAtEntry = stopAtEntry
        };

        DebugLogger.Log($"Launching {programPath} under netcoredbg (stopAtEntry={stopAtEntry})");
        _attached = false;
        return await StartSessionAsync(
            client => client.SendLaunchAsync(launchArgs),
            programPath,
            netcoredbgPath,
            $"Debug session started for {Path.GetFileName(programPath)}");
    }

    /// <summary>
    /// Attaches the debugger to an already running .NET process.
    /// </summary>
    public async Task<(bool Success, string Message)> AttachAsync(int? processId, string? processName, string? netcoredbgPath = null)
    {
        if (IsActive)
        {
            return (false, "A debug session is already active. Stop it first.");
        }

        if (processId == null)
        {
            if (string.IsNullOrEmpty(processName))
            {
                return (false, "Either processId or processName must be specified.");
            }

            var matches = ProcessLaunchManager.FindDebuggableProcesses(processName);
            if (matches.Count == 0)
            {
                return (false, $"No .NET process found matching '{processName}'.");
            }

            if (matches.Count > 1)
            {
                return (false, $"Several processes match '{processName}', pass processId instead:\n" +
                               string.Join("\n", matches.Select(p => $"  {p}")));
            }

            processId = matches[0].Id;
        }

        string? program;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            program = process.MainModule?.FileName;
        }
        catch (ArgumentException)
        {
            return (false, $"Process with ID {processId} not found.");
        }
        catch
        {
            program = null;
        }

        var attachArgs = new AttachRequestArguments { ProcessId = processId.Value };

        DebugLogger.Log($"Attaching netcoredbg to process {processId}");
        _attached = true;
        var result = await StartSessionAsync(
            client => client.SendAttachAsync(attachArgs),
            program,
            netcoredbgPath,
            $"Attached to process {processId}");

        if (result.Success)
        {
            SetDebuggedProcessId(processId.Value);
        }

        return result;
    }

    /// <summary>
    /// Runs the DAP start-up sequence: initialize, launch/attach, wait for "initialized",
    /// send breakpoints, configurationDone, then wait for the launch/attach response.
    /// </summary>
    private async Task<(bool Success, string Message)> StartSessionAsync(
        Func<DapClient, Task<DapResponse>> sendStartRequest,
        string? programPath,
        string? netcoredbgPath,
        string successMessage)
    {
        var client = new DapClient { NetCoreDbgPath = netcoredbgPath };
        Subscribe(client);

        lock (_lock)
        {
            _dapClient = client;
            IsPaused = false;
            CurrentThreadId = 0;
            CurrentFrameId = 0;
            CurrentLocation = null;
            StopReason = null;
            ProcessId = null;
            ExitCode = null;
            ProgramPath = programPath;
            _stopSignal = NewStopSignal();
            OutputMessages.Clear();
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var cancellationToken = timeoutCts.Token;

            await client.StartAsync(cancellationToken);
            await client.InitializeAsync(cancellationToken);

            var startResponse = sendStartRequest(client);

            // netcoredbg answers launch right away, other adapters only after configurationDone:
            // wait for whichever comes first between "initialized" and a failed response.
            var initialized = client.WaitForInitializedAsync(InitializedTimeout, cancellationToken);
            var first = await Task.WhenAny(initialized, startResponse);
            if (first == startResponse)
            {
                DapClient.EnsureSuccess(await startResponse);
            }
            await initialized;

            lock (_lock)
            {
                IsActive = true;
            }

            await SendAllBreakpointsAsync(client, cancellationToken);
            await client.SetExceptionBreakpointsAsync([], cancellationToken);
            await client.ConfigurationDoneAsync(cancellationToken);
            DapClient.EnsureSuccess(await startResponse);

            StartWpfHelperProbe();

            var message = successMessage;
            var breakpoints = BreakpointDetails;
            if (breakpoints.Count > 0)
            {
                message += "\nBreakpoints:\n" + string.Join("\n", breakpoints.Select(b => "  " + b.Describe()));
            }

            DebugLogger.Log(message);
            return (true, message);
        }
        catch (Exception ex)
        {
            var details = ex is TimeoutException or OperationCanceledException
                ? "timed out waiting for netcoredbg"
                : ex.Message;
            var stderr = client.GetDebuggerStderr();
            if (!string.IsNullOrEmpty(stderr) && !details.Contains(stderr))
            {
                details += $"\nnetcoredbg stderr:\n{stderr}";
            }

            DebugLogger.LogError($"Failed to start debug session: {details}", ex);
            await TearDownAsync(terminateDebuggee: !_attached);
            return (false, $"Failed to start debug session: {details}");
        }
    }

    private void Subscribe(DapClient client)
    {
        client.OnStopped += body => HandleStopped(client, body);
        client.OnContinued += _ => HandleContinued(client);
        client.OnOutput += HandleOutput;
        client.OnTerminated += body => HandleTerminated(client, body);
        client.OnExited += code =>
        {
            lock (_lock)
            {
                if (_dapClient == client) ExitCode = code;
            }
        };
        client.OnProcess += pid =>
        {
            lock (_lock)
            {
                if (_dapClient != client) return;
                ProcessId = pid;
            }
            SetDebuggedProcessId(pid);
        };
        client.OnBreakpointChanged += HandleBreakpointChanged;
    }

    private async Task SendAllBreakpointsAsync(DapClient client, CancellationToken cancellationToken)
    {
        List<string> files;
        lock (_lock)
        {
            files = _breakpoints.Keys.ToList();
        }

        foreach (var file in files)
        {
            await SendFileBreakpointsAsync(client, file, cancellationToken);
        }
    }

    /// <summary>
    /// Sends every breakpoint of a file (DAP replaces the whole set per file) and records
    /// the debugger's answer, which comes back in the same order as the request.
    /// </summary>
    private async Task SendFileBreakpointsAsync(DapClient client, string file, CancellationToken cancellationToken = default)
    {
        List<BreakpointInfo> entries;
        lock (_lock)
        {
            entries = _breakpoints.TryGetValue(file, out var list) ? list.ToList() : [];
        }

        var requested = entries.Select(b => new SourceBreakpoint { Line = b.Line, Condition = b.Condition }).ToArray();
        var result = await client.SetBreakpointsAsync(file, requested, cancellationToken);

        lock (_lock)
        {
            for (var i = 0; i < entries.Count && i < result.Length; i++)
            {
                entries[i].Id = result[i].Id;
                entries[i].Verified = result[i].Verified;
                entries[i].ActualLine = result[i].Line > 0 ? result[i].Line : null;
                entries[i].Message = result[i].Message;
            }
        }
    }

    /// <summary>
    /// Sets a breakpoint at the specified file and line.
    /// </summary>
    public async Task<(bool Success, string Message)> SetBreakpointAsync(string filePath, int line, string? condition = null)
    {
        try
        {
            filePath = Path.GetFullPath(filePath);
            if (!File.Exists(filePath))
            {
                return (false, $"Source file not found: {filePath}");
            }

            BreakpointInfo entry;
            lock (_lock)
            {
                if (!_breakpoints.TryGetValue(filePath, out var list))
                {
                    list = [];
                    _breakpoints[filePath] = list;
                }

                entry = list.FirstOrDefault(b => b.Line == line) ?? new BreakpointInfo { File = filePath, Line = line };
                entry.Condition = condition;
                if (!list.Contains(entry)) list.Add(entry);
            }

            var client = _dapClient;
            if (!IsActive || client == null)
            {
                return (true, $"Breakpoint queued at {Path.GetFileName(filePath)}:{line} (sent when the session starts)");
            }

            await SendFileBreakpointsAsync(client, filePath);
            return (true, "Breakpoint set at " + entry.Describe());
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

            bool removed;
            lock (_lock)
            {
                removed = _breakpoints.TryGetValue(filePath, out var list) && list.RemoveAll(b => b.Line == line) > 0;
            }

            if (!removed)
            {
                return (false, $"No breakpoint at {Path.GetFileName(filePath)}:{line}");
            }

            var client = _dapClient;
            if (IsActive && client != null)
            {
                await SendFileBreakpointsAsync(client, filePath);
            }

            lock (_lock)
            {
                if (_breakpoints.TryGetValue(filePath, out var list) && list.Count == 0)
                {
                    _breakpoints.Remove(filePath);
                }
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
    public Task<(bool Success, string Message)> StepNextAsync(TimeSpan? wait = null)
        => ResumeAsync("Step over", (client, thread) => client.NextAsync(thread), wait ?? TimeSpan.FromSeconds(10));

    /// <summary>
    /// Steps into the current function call.
    /// </summary>
    public Task<(bool Success, string Message)> StepInAsync(TimeSpan? wait = null)
        => ResumeAsync("Step in", (client, thread) => client.StepInAsync(thread), wait ?? TimeSpan.FromSeconds(10));

    /// <summary>
    /// Steps out of the current function.
    /// </summary>
    public Task<(bool Success, string Message)> StepOutAsync(TimeSpan? wait = null)
        => ResumeAsync("Step out", (client, thread) => client.StepOutAsync(thread), wait ?? TimeSpan.FromSeconds(10));

    /// <summary>
    /// Continues execution until the next breakpoint.
    /// </summary>
    public Task<(bool Success, string Message)> ContinueAsync(TimeSpan? wait = null)
        => ResumeAsync("Continue", (client, thread) => client.ContinueAsync(thread), wait ?? TimeSpan.Zero);

    private async Task<(bool Success, string Message)> ResumeAsync(string action, Func<DapClient, int, Task> send, TimeSpan wait)
    {
        var client = _dapClient;
        if (!IsActive || client == null)
        {
            return (false, "No active debug session.");
        }

        if (!IsPaused)
        {
            return (false, "Program is not paused.");
        }

        int threadId;
        Task<StopLocation> nextStop;
        lock (_lock)
        {
            threadId = CurrentThreadId;
            // Mark running before sending: the next "stopped" event can arrive before the response
            MarkRunning();
            nextStop = _stopSignal.Task;
        }

        try
        {
            await send(client, threadId);
        }
        catch (Exception ex)
        {
            return (false, $"{action} failed: {ex.Message}");
        }

        if (wait <= TimeSpan.Zero)
        {
            return (true, $"{action}: program is running. Use debug_wait_for_stop to wait for the next breakpoint.");
        }

        return await WaitForStopCoreAsync(nextStop, wait, $"{action} done");
    }

    /// <summary>
    /// Pauses the running program.
    /// </summary>
    public async Task<(bool Success, string Message)> PauseAsync()
    {
        var client = _dapClient;
        if (!IsActive || client == null)
        {
            return (false, "No active debug session.");
        }

        if (IsPaused)
        {
            return (true, "Program is already paused.");
        }

        Task<StopLocation> nextStop;
        lock (_lock)
        {
            nextStop = _stopSignal.Task;
        }

        try
        {
            // netcoredbg rejects threadId 0, so name a real thread (it pauses all of them anyway)
            var threadId = CurrentThreadId;
            if (threadId == 0)
            {
                threadId = (await client.GetThreadsAsync()).FirstOrDefault()?.Id ?? 0;
            }
            await client.PauseAsync(threadId);
        }
        catch (Exception ex)
        {
            return (false, $"Pause failed: {ex.Message}");
        }

        return await WaitForStopCoreAsync(nextStop, TimeSpan.FromSeconds(10), "Paused");
    }

    /// <summary>
    /// Waits until the program stops (breakpoint, step, exception, pause) or exits.
    /// </summary>
    public async Task<(bool Success, string Message)> WaitForStopAsync(TimeSpan timeout)
    {
        if (!IsActive)
        {
            return (false, DescribeEnded("No active debug session."));
        }

        Task<StopLocation> nextStop;
        lock (_lock)
        {
            if (IsPaused && CurrentLocation != null)
            {
                return (true, "Already stopped: " + FormatLocation(CurrentLocation));
            }

            nextStop = _stopSignal.Task;
        }

        return await WaitForStopCoreAsync(nextStop, timeout, "Stopped");
    }

    private async Task<(bool Success, string Message)> WaitForStopCoreAsync(Task<StopLocation> stop, TimeSpan timeout, string prefix)
    {
        try
        {
            var location = await stop.WaitAsync(timeout);
            return (true, $"{prefix}: {FormatLocation(location)}");
        }
        catch (TimeoutException)
        {
            return (true, $"Program still running after {timeout.TotalSeconds:0}s (no breakpoint hit yet).");
        }
        catch (Exception)
        {
            return (true, DescribeEnded("Program ended."));
        }
    }

    private string DescribeEnded(string message)
    {
        return ExitCode is { } code ? $"{message} Exit code: {code}." : message;
    }

    private static string FormatLocation(StopLocation location)
    {
        var where = location.File != null
            ? $"{location.Function} at {location.File}:{location.Line}"
            : location.Function ?? "unknown location";
        var reason = string.IsNullOrEmpty(location.Description) ? location.Reason : $"{location.Reason}, {location.Description}";
        return $"{where} (reason: {reason}, thread {location.ThreadId})";
    }

    /// <summary>
    /// Evaluates an expression in the current context.
    /// </summary>
    public async Task<(bool Success, string Result, string? Type)> EvaluateAsync(string expression)
    {
        var client = _dapClient;
        if (!IsActive || client == null)
        {
            return (false, "No active debug session.", null);
        }

        if (!IsPaused)
        {
            return (false, "Program is not paused. Cannot evaluate expressions while running.", null);
        }

        try
        {
            var result = await client.EvaluateAsync(expression, CurrentFrameId);
            return (true, result.Result, result.Type);
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
        var client = _dapClient;
        if (!IsActive || client == null)
        {
            return (false, [], "No active debug session.");
        }

        if (!IsPaused)
        {
            return (false, [], "Program is not paused.");
        }

        try
        {
            var frames = await client.GetStackTraceAsync(CurrentThreadId);
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
        var client = _dapClient;
        if (!IsActive || client == null)
        {
            return (false, [], "No active debug session.");
        }

        if (!IsPaused)
        {
            return (false, [], "Program is not paused.");
        }

        try
        {
            var scopes = await client.GetScopesAsync(CurrentFrameId);
            var variables = new List<Variable>();
            foreach (var scope in scopes.Where(s => s.VariablesReference > 0 && !s.Expensive))
            {
                variables.AddRange(await client.GetVariablesAsync(scope.VariablesReference));
            }

            return (true, [.. variables], $"Retrieved {variables.Count} local variables.");
        }
        catch (Exception ex)
        {
            return (false, [], $"Failed to get local variables: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the last lines of program/debugger output.
    /// </summary>
    public string[] GetRecentOutput(int maxLines)
    {
        lock (_lock)
        {
            return OutputMessages.Skip(Math.Max(0, OutputMessages.Count - maxLines)).ToArray();
        }
    }

    /// <summary>
    /// Stops the current debug session.
    /// </summary>
    public async Task<(bool Success, string Message)> StopAsync()
    {
        if (_dapClient == null)
        {
            return (false, "No active debug session.");
        }

        // A process we attached to keeps running; one we launched is terminated
        var detach = _attached;
        await TearDownAsync(terminateDebuggee: !detach);
        return (true, detach ? "Detached; the process keeps running." : "Debug session stopped.");
    }

    private async Task TearDownAsync(bool terminateDebuggee)
    {
        DapClient? client;
        lock (_lock)
        {
            client = _dapClient;
            _dapClient = null;
            IsActive = false;
            MarkRunning();
            _stopSignal.TrySetException(new InvalidOperationException("Session ended"));
            _debuggedProcess = null;
        }

        if (client != null)
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(terminateDebuggee);
            }
            await client.DisposeAsync();
        }
    }

    /// <summary>
    /// Takes a screenshot of the debugged application's main window (PNG, base64).
    /// </summary>
    public async Task<(bool Success, string ImageData, string Message)> TakeScreenshotAsync()
    {
        if (!EnsureProcessSet())
        {
            return (false, string.Empty, "No debugged process to capture. Start a session or call ui_set_process_id.");
        }

        return await _uiService.TakeScreenshotAsync();
    }

    /// <summary>
    /// Gets the UI control tree of the debugged application as XML.
    /// </summary>
    public async Task<(bool Success, string Xml, string Message)> GetUiControlsAsync(int maxDepth = 10)
    {
        if (!EnsureProcessSet())
        {
            return (false, string.Empty, "No debugged process to inspect. Start a session or call ui_set_process_id.");
        }

        return await _uiService.GetControlsEnhancedAsync(maxDepth);
    }

    /// <summary>
    /// Sets the process ID of the debugged application for UI automation.
    /// </summary>
    public bool SetDebuggedProcessId(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            lock (_lock)
            {
                _debuggedProcess = process;
            }
            _uiService.SetTargetProcess(process);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    #region UI Interaction Methods

    public async Task<(bool Success, string Message)> ClickControlAsync(long handle)
    {
        EnsureProcessSet();
        return await _uiService.ClickControlAsync(new IntPtr(handle));
    }

    public async Task<(bool Success, string Message)> ClickAtAsync(int x, int y, string button = "left", int clickCount = 1, bool screenCoordinates = false)
    {
        if (!EnsureProcessSet())
        {
            return (false, "No debugged process to click in.");
        }

        if (IsPaused)
        {
            return (false, "The program is paused in the debugger, so its UI cannot react. Continue first (debug_continue).");
        }

        return await _uiService.ClickAtAsync(x, y, button, clickCount, screenCoordinates);
    }

    public async Task<(bool Success, string Message)> TypeTextAsync(string text, long? handle = null)
    {
        EnsureProcessSet();
        IntPtr? handlePtr = handle.HasValue ? new IntPtr(handle.Value) : null;
        return await _uiService.TypeTextAsync(text, handlePtr);
    }

    public async Task<(bool Success, string Message)> SendKeysAsync(string keys)
    {
        EnsureProcessSet();
        return await _uiService.SendKeysAsync(keys);
    }

    public async Task<(bool Success, string Message)> MouseMoveAsync(int x, int y, bool screenCoordinates = false)
    {
        EnsureProcessSet();
        return await _uiService.MouseMoveAsync(x, y, screenCoordinates);
    }

    public async Task<(bool Success, string Message)> MouseDragAsync(int startX, int startY, int endX, int endY, string button = "left", bool screenCoordinates = false)
    {
        EnsureProcessSet();
        return await _uiService.MouseDragAsync(startX, startY, endX, endY, button, screenCoordinates);
    }

    public async Task<(bool Success, string Message)> FocusControlAsync(long handle)
    {
        EnsureProcessSet();
        return await _uiService.FocusControlAsync(new IntPtr(handle));
    }

    public async Task<(bool Success, string Message)> MouseScrollAsync(int delta, int? x = null, int? y = null, bool screenCoordinates = false)
    {
        EnsureProcessSet();
        return await _uiService.MouseScrollAsync(delta, x, y, screenCoordinates);
    }

    public async Task<(bool Success, long Handle, string Message)> FindControlByTextAsync(string text, bool exactMatch = false)
    {
        EnsureProcessSet();
        var (success, handle, message) = await _uiService.FindControlByTextAsync(text, exactMatch);
        return (success, handle.ToInt64(), message);
    }

    /// <summary>
    /// Makes sure UI automation targets the debuggee (pid reported by the debugger).
    /// </summary>
    private bool EnsureProcessSet()
    {
        lock (_lock)
        {
            if (_debuggedProcess != null)
            {
                try
                {
                    _debuggedProcess.Refresh();
                    if (!_debuggedProcess.HasExited) return true;
                }
                catch
                {
                    // Fall through to re-resolve
                }
            }
        }

        return ProcessId is { } pid && SetDebuggedProcessId(pid);
    }

    #endregion

    /// <summary>
    /// Lists .NET processes that can be attached to.
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
            return (false, [], $"Failed to list processes: {ex.Message}");
        }
    }

    private void StartWpfHelperProbe()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000);
                if (await _uiService.CheckWpfHelperAsync())
                {
                    DebugLogger.Log("WPF Helper API detected - Enhanced UI automation available");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error checking WPF Helper API: {ex.Message}");
            }
        });
    }

    private void MarkRunning()
    {
        IsPaused = false;
        CurrentLocation = null;
        StopReason = null;
        if (_stopSignal.Task.IsCompleted)
        {
            _stopSignal = NewStopSignal();
        }
    }

    private void HandleStopped(DapClient client, StoppedEventBody body)
    {
        lock (_lock)
        {
            if (_dapClient != client) return;
            IsPaused = true;
            CurrentThreadId = body.ThreadId;
            StopReason = body.Reason;
        }

        // Fetch the top frame off the receive loop, then publish the stop
        _ = Task.Run(async () =>
        {
            Dap.StackFrame? top = null;
            try
            {
                var frames = await client.GetStackTraceAsync(body.ThreadId, 0, 1);
                top = frames.FirstOrDefault();
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Could not read stack after stop: {ex.Message}");
            }

            var location = new StopLocation(body.Reason, body.ThreadId, body.Description, top?.Name, top?.Source?.Path, top?.Line ?? 0);
            lock (_lock)
            {
                if (_dapClient != client || !IsPaused) return;
                CurrentFrameId = top?.Id ?? 0;
                CurrentLocation = location;
                _stopSignal.TrySetResult(location);
            }

            OnDebugEvent?.Invoke("stopped", location);
        });
    }

    private void HandleContinued(DapClient client)
    {
        lock (_lock)
        {
            if (_dapClient == client) MarkRunning();
        }
    }

    private void HandleBreakpointChanged(Breakpoint breakpoint)
    {
        lock (_lock)
        {
            var entry = _breakpoints.Values.SelectMany(list => list).FirstOrDefault(b => b.Id == breakpoint.Id);
            if (entry == null) return;
            entry.Verified = breakpoint.Verified;
            entry.ActualLine = breakpoint.Line > 0 ? breakpoint.Line : entry.ActualLine;
            entry.Message = breakpoint.Message;
        }
    }

    private void HandleOutput(OutputEventBody body)
    {
        lock (_lock)
        {
            OutputMessages.Add($"[{body.Category}] {body.Output.TrimEnd('\r', '\n')}");
            if (OutputMessages.Count > MaxOutputMessages)
            {
                OutputMessages.RemoveRange(0, OutputMessages.Count - MaxOutputMessages);
            }
        }

        OnDebugEvent?.Invoke("output", body);
    }

    private void HandleTerminated(DapClient client, TerminatedEventBody body)
    {
        lock (_lock)
        {
            if (_dapClient != client) return;
            IsActive = false;
            MarkRunning();
            _stopSignal.TrySetException(new InvalidOperationException("Program terminated"));
        }

        OnDebugEvent?.Invoke("terminated", body);

        // Release netcoredbg; the session object stays usable for a new launch
        _ = Task.Run(async () =>
        {
            lock (_lock)
            {
                if (_dapClient == client) _dapClient = null;
            }
            await client.DisposeAsync();
        });
    }

    private static TaskCompletionSource<StopLocation> NewStopSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
