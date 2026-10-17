using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpDebugAdapter.Dap;

/// <summary>
/// DAP client that drives netcoredbg (Samsung, MIT licensed) over its stdin/stdout.
/// Handles launching the debugger process, sending requests, and receiving events.
/// </summary>
public class DapClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private Process? _debuggerProcess;
    private Stream? _input;
    private Stream? _output;
    private int _sequenceNumber;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DapResponse>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveTask;
    private TaskCompletionSource _initializedEvent = NewSignal();

    /// <summary>
    /// Event fired when the debugger stops (breakpoint hit, step complete, etc.).
    /// </summary>
    public event Action<StoppedEventBody>? OnStopped;

    /// <summary>
    /// Event fired when the debuggee resumes execution.
    /// </summary>
    public event Action<int>? OnContinued;

    /// <summary>
    /// Event fired when debugger output is received.
    /// </summary>
    public event Action<OutputEventBody>? OnOutput;

    /// <summary>
    /// Event fired when the debug session is terminated.
    /// </summary>
    public event Action<TerminatedEventBody>? OnTerminated;

    /// <summary>
    /// Event fired when the debuggee exits.
    /// </summary>
    public event Action<int>? OnExited;

    /// <summary>
    /// Event fired when the debugger is initialized.
    /// </summary>
    public event Action? OnInitialized;

    /// <summary>
    /// Event fired when the debugger reports the debuggee process (carries its OS process id).
    /// </summary>
    public event Action<int>? OnProcess;

    /// <summary>
    /// Event fired when a breakpoint changes state (e.g. a pending breakpoint gets bound).
    /// </summary>
    public event Action<Breakpoint>? OnBreakpointChanged;

    /// <summary>
    /// Indicates whether the client is currently connected to the debugger.
    /// </summary>
    public bool IsConnected => _input != null && _debuggerProcess is { HasExited: false };

    /// <summary>
    /// Path to the netcoredbg executable. Resolved automatically when not set.
    /// </summary>
    public string? NetCoreDbgPath { get; set; }

    /// <summary>
    /// Timeout applied to each DAP request.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Starts the netcoredbg process and talks to it via stdin/stdout.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var path = NetCoreDbgLocator.Resolve(NetCoreDbgPath)
            ?? throw new InvalidOperationException(NetCoreDbgLocator.NotFoundMessage);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _initializedEvent = NewSignal();

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = "--interpreter=vscode",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _debuggerProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _debuggerProcess.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            DebugLogger.LogDebug($"netcoredbg stderr: {e.Data}");
            lock (_stderr)
            {
                if (_stderr.Length < 8192) _stderr.AppendLine(e.Data);
            }
        };

        try
        {
            DebugLogger.LogDebug($"Starting netcoredbg: {path} {startInfo.Arguments}");
            _debuggerProcess.Start();
            _debuggerProcess.BeginErrorReadLine();
            DebugLogger.LogDebug($"netcoredbg process started with PID: {_debuggerProcess.Id}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start netcoredbg at '{path}': {ex.Message}", ex);
        }

        _input = _debuggerProcess.StandardInput.BaseStream;
        _output = _debuggerProcess.StandardOutput.BaseStream;
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cancellationTokenSource.Token));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends the initialize request to the debugger.
    /// </summary>
    public async Task<Capabilities?> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync("initialize", new InitializeRequestArguments(), cancellationToken);
        EnsureSuccess(response);
        return response.Body == null ? new Capabilities() : Deserialize<Capabilities>(response.Body);
    }

    /// <summary>
    /// Sends the launch request. The response is awaited separately because some adapters
    /// only answer it after configurationDone.
    /// </summary>
    public Task<DapResponse> SendLaunchAsync(LaunchRequestArguments args, CancellationToken cancellationToken = default)
        => SendRequestAsync("launch", args, cancellationToken);

    /// <summary>
    /// Sends the attach request. The response is awaited separately (see <see cref="SendLaunchAsync"/>).
    /// </summary>
    public Task<DapResponse> SendAttachAsync(AttachRequestArguments args, CancellationToken cancellationToken = default)
        => SendRequestAsync("attach", args, cancellationToken);

    /// <summary>
    /// Waits for the "initialized" event, after which breakpoints can be configured.
    /// </summary>
    public async Task WaitForInitializedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _initializedEvent.Task.WaitAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Signals that configuration is done and the program can start.
    /// </summary>
    public async Task ConfigurationDoneAsync(CancellationToken cancellationToken = default)
    {
        EnsureSuccess(await SendRequestAsync("configurationDone", null, cancellationToken));
    }

    /// <summary>
    /// Configures which exceptions break into the debugger.
    /// </summary>
    public async Task SetExceptionBreakpointsAsync(string[] filters, CancellationToken cancellationToken = default)
    {
        EnsureSuccess(await SendRequestAsync("setExceptionBreakpoints", new { filters }, cancellationToken));
    }

    /// <summary>
    /// Sets breakpoints in a source file (replaces all breakpoints of that file).
    /// The returned array has one entry per requested breakpoint, in the same order.
    /// </summary>
    public async Task<Breakpoint[]> SetBreakpointsAsync(string filePath, SourceBreakpoint[] breakpoints, CancellationToken cancellationToken = default)
    {
        var args = new SetBreakpointsArguments
        {
            Source = new Source
            {
                Path = filePath,
                Name = Path.GetFileName(filePath)
            },
            Breakpoints = breakpoints
        };

        var response = await SendRequestAsync("setBreakpoints", args, cancellationToken);
        EnsureSuccess(response);
        return Deserialize<SetBreakpointsResponseBody>(response.Body)?.Breakpoints ?? [];
    }

    /// <summary>
    /// Gets the list of threads.
    /// </summary>
    public async Task<DapThread[]> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync("threads", null, cancellationToken);
        EnsureSuccess(response);
        return Deserialize<ThreadsResponseBody>(response.Body)?.Threads ?? [];
    }

    /// <summary>
    /// Gets the stack trace for a thread.
    /// </summary>
    public async Task<StackFrame[]> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 20, CancellationToken cancellationToken = default)
    {
        var args = new StackTraceArguments
        {
            ThreadId = threadId,
            StartFrame = startFrame,
            Levels = levels
        };

        var response = await SendRequestAsync("stackTrace", args, cancellationToken);
        EnsureSuccess(response);
        return Deserialize<StackTraceResponseBody>(response.Body)?.StackFrames ?? [];
    }

    /// <summary>
    /// Gets the scopes for a stack frame.
    /// </summary>
    public async Task<Scope[]> GetScopesAsync(int frameId, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync("scopes", new ScopesArguments { FrameId = frameId }, cancellationToken);
        EnsureSuccess(response);
        return Deserialize<ScopesResponseBody>(response.Body)?.Scopes ?? [];
    }

    /// <summary>
    /// Gets variables for a scope or a structured variable.
    /// </summary>
    public async Task<Variable[]> GetVariablesAsync(int variablesReference, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync("variables", new VariablesArguments { VariablesReference = variablesReference }, cancellationToken);
        EnsureSuccess(response);
        return Deserialize<VariablesResponseBody>(response.Body)?.Variables ?? [];
    }

    /// <summary>
    /// Evaluates an expression in the context of a stack frame.
    /// </summary>
    public async Task<EvaluateResponseBody> EvaluateAsync(string expression, int frameId, string context = "repl", CancellationToken cancellationToken = default)
    {
        var args = new EvaluateArguments
        {
            Expression = expression,
            FrameId = frameId,
            Context = context
        };

        var response = await SendRequestAsync("evaluate", args, cancellationToken);
        EnsureSuccess(response);
        return Deserialize<EvaluateResponseBody>(response.Body) ?? new EvaluateResponseBody();
    }

    /// <summary>
    /// Continues execution.
    /// </summary>
    public async Task ContinueAsync(int threadId, CancellationToken cancellationToken = default)
    {
        EnsureSuccess(await SendRequestAsync("continue", new ContinueArguments { ThreadId = threadId }, cancellationToken));
    }

    /// <summary>
    /// Pauses execution.
    /// </summary>
    public async Task PauseAsync(int threadId, CancellationToken cancellationToken = default)
    {
        EnsureSuccess(await SendRequestAsync("pause", new { threadId }, cancellationToken));
    }

    /// <summary>
    /// Steps over the next line (Step Over).
    /// </summary>
    public async Task NextAsync(int threadId, CancellationToken cancellationToken = default)
    {
        EnsureSuccess(await SendRequestAsync("next", new NextArguments { ThreadId = threadId }, cancellationToken));
    }

    /// <summary>
    /// Steps into the next function call (Step In).
    /// </summary>
    public async Task StepInAsync(int threadId, CancellationToken cancellationToken = default)
    {
        EnsureSuccess(await SendRequestAsync("stepIn", new StepInArguments { ThreadId = threadId }, cancellationToken));
    }

    /// <summary>
    /// Steps out of the current function (Step Out).
    /// </summary>
    public async Task StepOutAsync(int threadId, CancellationToken cancellationToken = default)
    {
        EnsureSuccess(await SendRequestAsync("stepOut", new { threadId }, cancellationToken));
    }

    /// <summary>
    /// Disconnects from the debugger.
    /// </summary>
    public async Task DisconnectAsync(bool terminateDebuggee = true, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await SendRequestAsync("disconnect", new { terminateDebuggee }, cts.Token);
        }
        catch
        {
            // Ignore errors during disconnect
        }
    }

    /// <summary>
    /// Returns what netcoredbg wrote to stderr so far (useful in error messages).
    /// </summary>
    public string GetDebuggerStderr()
    {
        lock (_stderr)
        {
            return _stderr.ToString().Trim();
        }
    }

    /// <summary>
    /// Throws an exception carrying the adapter's error message when a response failed.
    /// </summary>
    public static void EnsureSuccess(DapResponse response)
    {
        if (response.Success) return;
        throw new DapRequestException(response.Command, DescribeError(response));
    }

    private static string DescribeError(DapResponse response)
    {
        // DAP puts a short id in "message" and the readable text in body.error.format
        string? format = null;
        if (response.Body is JsonElement body && body.ValueKind == JsonValueKind.Object &&
            body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("format", out var formatElement))
        {
            format = formatElement.GetString();
        }

        return format ?? response.Message ?? "unknown error";
    }

    private async Task<DapResponse> SendRequestAsync(string command, object? arguments, CancellationToken cancellationToken)
    {
        if (_input == null || _debuggerProcess == null)
        {
            throw new InvalidOperationException("Not connected to debugger");
        }

        if (_debuggerProcess.HasExited)
        {
            throw new InvalidOperationException(DescribeDebuggerExit());
        }

        var seq = Interlocked.Increment(ref _sequenceNumber);
        // Continuations must not run on the receive loop, or a handler awaiting another response would stall it
        var tcs = new TaskCompletionSource<DapResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[seq] = tcs;

        var request = new DapRequest
        {
            Seq = seq,
            Command = command,
            Arguments = arguments
        };

        try
        {
            await WriteMessageAsync(JsonSerializer.Serialize(request, SerializerOptions), cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"netcoredbg did not answer '{command}' within {RequestTimeout.TotalSeconds:0}s.");
        }
        finally
        {
            _pendingRequests.TryRemove(seq, out _);
        }
    }

    private async Task WriteMessageAsync(string json, CancellationToken cancellationToken)
    {
        DebugLogger.LogDebug($"DAP SEND: {json}");
        // Content-Length counts bytes, not characters
        var content = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _input!.WriteAsync(header, cancellationToken);
            await _input.WriteAsync(content, cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var reader = new DapMessageReader(_output!);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await reader.ReadMessageAsync(cancellationToken);
                if (message == null)
                {
                    break;
                }

                DebugLogger.LogDebug($"DAP RECV: {message}");
                ProcessMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"DAP receive loop failed: {ex.Message}", ex);
        }

        // The debugger is gone: fail whatever is still waiting
        var reason = new InvalidOperationException(DescribeDebuggerExit());
        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetException(reason);
        }
        _initializedEvent.TrySetException(reason);
    }

    private string DescribeDebuggerExit()
    {
        var message = "netcoredbg stopped responding";
        try
        {
            if (_debuggerProcess is { HasExited: true })
            {
                message = $"netcoredbg exited with code {_debuggerProcess.ExitCode}";
            }
        }
        catch
        {
            // Process may be disposed
        }

        var stderr = GetDebuggerStderr();
        return string.IsNullOrEmpty(stderr) ? message + "." : $"{message}: {stderr}";
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node == null) return;

            switch (node["type"]?.GetValue<string>())
            {
                case "response":
                    var response = JsonSerializer.Deserialize<DapResponse>(json);
                    if (response != null && _pendingRequests.TryGetValue(response.RequestSeq, out var tcs))
                    {
                        tcs.TrySetResult(response);
                    }
                    break;
                case "event":
                    ProcessEvent(node);
                    break;
                case "request":
                    // Reverse request (e.g. runInTerminal): we advertise no support, so decline it
                    _ = RejectReverseRequestAsync(node);
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Error processing DAP message: {ex.Message}", ex);
        }
    }

    private async Task RejectReverseRequestAsync(JsonNode request)
    {
        var reply = new JsonObject
        {
            ["seq"] = Interlocked.Increment(ref _sequenceNumber),
            ["type"] = "response",
            ["request_seq"] = request["seq"]?.GetValue<int>() ?? 0,
            ["command"] = request["command"]?.GetValue<string>() ?? string.Empty,
            ["success"] = false,
            ["message"] = "Not supported by MCP Debug Adapter"
        };

        try
        {
            await WriteMessageAsync(reply.ToJsonString(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            DebugLogger.LogDebug($"Could not answer reverse request: {ex.Message}");
        }
    }

    private void ProcessEvent(JsonNode eventNode)
    {
        var body = eventNode["body"];

        switch (eventNode["event"]?.GetValue<string>())
        {
            case "initialized":
                _initializedEvent.TrySetResult();
                OnInitialized?.Invoke();
                break;

            case "stopped":
                var stopped = body?.Deserialize<StoppedEventBody>();
                if (stopped != null) OnStopped?.Invoke(stopped);
                break;

            case "continued":
                OnContinued?.Invoke(body?["threadId"]?.GetValue<int>() ?? 0);
                break;

            case "output":
                var output = body?.Deserialize<OutputEventBody>();
                if (output != null) OnOutput?.Invoke(output);
                break;

            case "breakpoint":
                var breakpoint = body?["breakpoint"]?.Deserialize<Breakpoint>();
                if (breakpoint != null) OnBreakpointChanged?.Invoke(breakpoint);
                break;

            case "process":
                var pid = body?["systemProcessId"]?.GetValue<int>();
                if (pid is > 0) OnProcess?.Invoke(pid.Value);
                break;

            case "exited":
                OnExited?.Invoke(body?["exitCode"]?.GetValue<int>() ?? 0);
                break;

            case "terminated":
                OnTerminated?.Invoke(body?.Deserialize<TerminatedEventBody>() ?? new TerminatedEventBody());
                break;
        }
    }

    private static T? Deserialize<T>(object? body)
    {
        return body switch
        {
            null => default,
            JsonElement element => element.Deserialize<T>(),
            _ => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(body))
        };
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        _cancellationTokenSource?.Cancel();

        if (_debuggerProcess != null)
        {
            try
            {
                if (!_debuggerProcess.HasExited)
                {
                    _debuggerProcess.Kill(entireProcessTree: true);
                    await _debuggerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
                // Ignore
            }
        }

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore
            }
        }

        _debuggerProcess?.Dispose();
        _debuggerProcess = null;
        _input = null;
        _output = null;
        _cancellationTokenSource?.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Raised when the debug adapter answers a request with success = false.
/// </summary>
public class DapRequestException(string command, string message)
    : Exception($"{command} failed: {message}")
{
    public string Command { get; } = command;
}

/// <summary>
/// Reads Content-Length framed DAP messages. Lengths are in bytes, so the payload is
/// decoded only once it is complete (multi-byte UTF-8 characters such as "é" are safe).
/// </summary>
public class DapMessageReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[16384];
    private int _start;
    private int _end;

    public async Task<string?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var headerEnd = IndexOfHeaderEnd();
            if (headerEnd >= 0)
            {
                var header = Encoding.ASCII.GetString(_buffer, _start, headerEnd - _start);
                var contentLength = ParseContentLength(header);
                _start = headerEnd + 4;

                var content = new byte[contentLength];
                var copied = 0;
                while (copied < contentLength)
                {
                    if (_start == _end && !await FillAsync(cancellationToken))
                    {
                        return null;
                    }

                    var count = Math.Min(contentLength - copied, _end - _start);
                    Buffer.BlockCopy(_buffer, _start, content, copied, count);
                    _start += count;
                    copied += count;
                }

                return Encoding.UTF8.GetString(content);
            }

            if (!await FillAsync(cancellationToken))
            {
                return null;
            }
        }
    }

    private static int ParseContentLength(string header)
    {
        foreach (var line in header.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(colon + 1)..].Trim(), out var length))
            {
                return length;
            }
        }

        throw new InvalidDataException($"DAP header without Content-Length: {header}");
    }

    private int IndexOfHeaderEnd()
    {
        for (var i = _start; i + 3 < _end; i++)
        {
            if (_buffer[i] == '\r' && _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            throw new InvalidDataException("DAP header too large");
        }

        var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken);
        if (read == 0)
        {
            return false;
        }

        _end += read;
        return true;
    }
}

/// <summary>
/// Finds the netcoredbg executable.
/// </summary>
public static class NetCoreDbgLocator
{
    public const string NotFoundMessage =
        "netcoredbg was not found. Download it from https://github.com/Samsung/netcoredbg/releases " +
        "(netcoredbg-win64.zip on Windows), extract it, then either add its folder to PATH, " +
        "set the NETCOREDBG_PATH environment variable to the executable, or pass netcoredbgPath to debug_launch.";

    public static string? Resolve(string? explicitPath)
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "netcoredbg.exe" : "netcoredbg";

        foreach (var candidate in Candidates(explicitPath, exeName))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            var path = candidate.Trim().Trim('"');
            if (Directory.Exists(path))
            {
                path = Path.Combine(path, exeName);
            }

            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    private static IEnumerable<string?> Candidates(string? explicitPath, string exeName)
    {
        yield return explicitPath;
        yield return Environment.GetEnvironmentVariable("NETCOREDBG_PATH");

        // Shipped next to the adapter (e.g. <adapter folder>/netcoredbg/netcoredbg.exe)
        yield return Path.Combine(AppContext.BaseDirectory, "netcoredbg", exeName);
        yield return Path.Combine(AppContext.BaseDirectory, exeName);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                yield return Path.Combine(dir.Trim('"'), exeName);
            }
        }
    }
}
