using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpDebugAdapter.Dap;

/// <summary>
/// DAP Client that communicates with netcoredbg via TCP socket.
/// Handles launching the debugger process, sending requests, and receiving events.
/// </summary>
public class DapClient : IAsyncDisposable
{
    private Process? _netcoredbgProcess;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private int _sequenceNumber;
    private readonly Dictionary<int, TaskCompletionSource<DapResponse>> _pendingRequests = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveTask;
    private readonly object _lock = new();

    /// <summary>
    /// Event fired when the debugger stops (breakpoint hit, step complete, etc.).
    /// </summary>
    public event Action<StoppedEventBody>? OnStopped;

    /// <summary>
    /// Event fired when debugger output is received.
    /// </summary>
    public event Action<OutputEventBody>? OnOutput;

    /// <summary>
    /// Event fired when the debug session is terminated.
    /// </summary>
    public event Action<TerminatedEventBody>? OnTerminated;

    /// <summary>
    /// Event fired when the debugger is initialized.
    /// </summary>
    public event Action? OnInitialized;

    /// <summary>
    /// Event fired when configuration is done.
    /// </summary>
    public event Action? OnConfigurationDone;

    /// <summary>
    /// Indicates whether the client is currently connected to the debugger.
    /// </summary>
    public bool IsConnected => _tcpClient?.Connected == true;

    /// <summary>
    /// Path to the netcoredbg executable.
    /// </summary>
    public string NetCoreDbgPath { get; set; } = "netcoredbg";

    /// <summary>
    /// Starts the netcoredbg process and connects to it via TCP.
    /// </summary>
    /// <param name="port">The TCP port to use for communication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task StartAsync(int port = 0, CancellationToken cancellationToken = default)
    {
        if (port == 0)
        {
            port = GetAvailablePort();
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start netcoredbg process
        var startInfo = new ProcessStartInfo
        {
            FileName = NetCoreDbgPath,
            Arguments = $"--interpreter=vscode --server={port}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _netcoredbgProcess = new Process { StartInfo = startInfo };

        _netcoredbgProcess.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                DebugLogger.LogDebug($"netcoredbg stdout: {e.Data}");
                OnOutput?.Invoke(new OutputEventBody { Category = "stdout", Output = e.Data + "\n" });
            }
        };

        _netcoredbgProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                DebugLogger.LogDebug($"netcoredbg stderr: {e.Data}");
                OnOutput?.Invoke(new OutputEventBody { Category = "stderr", Output = e.Data + "\n" });
            }
        };

        try
        {
            DebugLogger.LogDebug($"Starting netcoredbg: {NetCoreDbgPath} {startInfo.Arguments}");
            _netcoredbgProcess.Start();
            _netcoredbgProcess.BeginOutputReadLine();
            _netcoredbgProcess.BeginErrorReadLine();
            
            DebugLogger.LogDebug($"netcoredbg process started with PID: {_netcoredbgProcess.Id}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start netcoredbg process. Make sure netcoredbg is installed and in PATH. Error: {ex.Message}", ex);
        }

        // Wait a bit for the server to start
        await Task.Delay(500, cancellationToken);
        
        // Check if process exited early
        if (_netcoredbgProcess.HasExited)
        {
            var exitCode = _netcoredbgProcess.ExitCode;
            var errorMessage = $"netcoredbg process exited immediately with code {exitCode}.";
            
            if (exitCode == -1073741515) // 0xC0000135 - DLL not found
            {
                errorMessage += " This usually means .NET Core runtime is not installed or netcoredbg is not compatible with your system.";
            }
            else if (exitCode != 0)
            {
                errorMessage += " Check if netcoredbg is compatible with your system and .NET version.";
            }
            
            throw new InvalidOperationException(errorMessage);
        }

        // Connect via TCP
        _tcpClient = new TcpClient();

        var connectAttempts = 0;
        const int maxAttempts = 10;
        Exception? lastException = null;

        DebugLogger.LogDebug($"Attempting to connect to netcoredbg on port {port}...");
        
        while (connectAttempts < maxAttempts)
        {
            try
            {
                await _tcpClient.ConnectAsync("127.0.0.1", port, cancellationToken);
                break;
            }
            catch (SocketException ex) when (connectAttempts < maxAttempts - 1)
            {
                lastException = ex;
                connectAttempts++;
                DebugLogger.LogDebug($"Connection attempt {connectAttempts}/{maxAttempts} failed: {ex.Message}");
                await Task.Delay(200, cancellationToken);
            }
        }

        if (!_tcpClient.Connected)
        {
            var errorMsg = $"Failed to connect to netcoredbg on port {port} after {maxAttempts} attempts.";
            if (lastException != null)
            {
                errorMsg += $" Last error: {lastException.Message}";
            }
            if (!_netcoredbgProcess.HasExited)
            {
                errorMsg += " The netcoredbg process is running but not accepting connections.";
            }
            else
            {
                errorMsg += $" The netcoredbg process exited with code {_netcoredbgProcess.ExitCode}.";
            }
            throw new InvalidOperationException(errorMsg);
        }

        _stream = _tcpClient.GetStream();
        DebugLogger.LogDebug("Successfully connected to netcoredbg");

        // Start the receive loop
        _receiveTask = ReceiveLoopAsync(_cancellationTokenSource.Token);
    }

    /// <summary>
    /// Sends the initialize request to the debugger.
    /// </summary>
    public async Task<Capabilities?> InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DebugLogger.LogDebug("Sending initialize request to debugger...");
            var args = new InitializeRequestArguments();
            var response = await SendRequestAsync("initialize", args, cancellationToken);
            
            DebugLogger.LogDebug($"Initialize response - Success: {response.Success}");

            if (response.Success && response.Body != null)
            {
                var json = JsonSerializer.Serialize(response.Body);
                var capabilities = JsonSerializer.Deserialize<Capabilities>(json);
                DebugLogger.LogDebug("Debugger initialization completed successfully");
                return capabilities;
            }

            DebugLogger.LogError($"Initialize failed - Success: {response.Success}, Body: {response.Body}");
            return null;
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Exception during initialize: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Launches a program for debugging.
    /// </summary>
    public async Task<bool> LaunchAsync(string program, string[]? args = null, string? cwd = null, bool stopAtEntry = false, CancellationToken cancellationToken = default)
    {
        try
        {
            DebugLogger.LogDebug($"Sending launch request for program: {program}");
            var launchArgs = new LaunchRequestArguments
            {
                Program = program,
                Args = args,
                Cwd = cwd ?? Path.GetDirectoryName(program),
                StopAtEntry = stopAtEntry
            };

            var response = await SendRequestAsync("launch", launchArgs, cancellationToken);
            DebugLogger.LogDebug($"Launch response - Success: {response.Success}");
            
            if (!response.Success)
            {
                DebugLogger.LogError($"Launch failed - Response: {JsonSerializer.Serialize(response)}");
            }
            
            return response.Success;
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Exception during launch: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Signals that configuration is done and the program can start.
    /// </summary>
    public async Task<bool> ConfigurationDoneAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync("configurationDone", null, cancellationToken);
        return response.Success;
    }

    /// <summary>
    /// Sets breakpoints in a source file.
    /// </summary>
    public async Task<Breakpoint[]> SetBreakpointsAsync(string filePath, int[] lines, CancellationToken cancellationToken = default)
    {
        var args = new SetBreakpointsArguments
        {
            Source = new Source
            {
                Path = filePath,
                Name = Path.GetFileName(filePath)
            },
            Breakpoints = lines.Select(line => new SourceBreakpoint { Line = line }).ToArray()
        };

        var response = await SendRequestAsync("setBreakpoints", args, cancellationToken);

        if (response.Success && response.Body != null)
        {
            var json = JsonSerializer.Serialize(response.Body);
            var body = JsonSerializer.Deserialize<SetBreakpointsResponseBody>(json);
            return body?.Breakpoints ?? [];
        }

        return [];
    }

    /// <summary>
    /// Gets the list of threads.
    /// </summary>
    public async Task<DapThread[]> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync("threads", null, cancellationToken);

        if (response.Success && response.Body != null)
        {
            var json = JsonSerializer.Serialize(response.Body);
            var body = JsonSerializer.Deserialize<ThreadsResponseBody>(json);
            return body?.Threads ?? [];
        }

        return [];
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

        if (response.Success && response.Body != null)
        {
            var json = JsonSerializer.Serialize(response.Body);
            var body = JsonSerializer.Deserialize<StackTraceResponseBody>(json);
            return body?.StackFrames ?? [];
        }

        return [];
    }

    /// <summary>
    /// Gets the scopes for a stack frame.
    /// </summary>
    public async Task<Scope[]> GetScopesAsync(int frameId, CancellationToken cancellationToken = default)
    {
        var args = new ScopesArguments { FrameId = frameId };
        var response = await SendRequestAsync("scopes", args, cancellationToken);

        if (response.Success && response.Body != null)
        {
            var json = JsonSerializer.Serialize(response.Body);
            var body = JsonSerializer.Deserialize<ScopesResponseBody>(json);
            return body?.Scopes ?? [];
        }

        return [];
    }

    /// <summary>
    /// Gets variables for a scope.
    /// </summary>
    public async Task<Variable[]> GetVariablesAsync(int variablesReference, CancellationToken cancellationToken = default)
    {
        var args = new VariablesArguments { VariablesReference = variablesReference };
        var response = await SendRequestAsync("variables", args, cancellationToken);

        if (response.Success && response.Body != null)
        {
            var json = JsonSerializer.Serialize(response.Body);
            var body = JsonSerializer.Deserialize<VariablesResponseBody>(json);
            return body?.Variables ?? [];
        }

        return [];
    }

    /// <summary>
    /// Evaluates an expression in the context of a stack frame.
    /// </summary>
    public async Task<EvaluateResponseBody?> EvaluateAsync(string expression, int frameId, string context = "repl", CancellationToken cancellationToken = default)
    {
        var args = new EvaluateArguments
        {
            Expression = expression,
            FrameId = frameId,
            Context = context
        };

        var response = await SendRequestAsync("evaluate", args, cancellationToken);

        if (response.Success && response.Body != null)
        {
            var json = JsonSerializer.Serialize(response.Body);
            return JsonSerializer.Deserialize<EvaluateResponseBody>(json);
        }

        return null;
    }

    /// <summary>
    /// Continues execution of a thread.
    /// </summary>
    public async Task<bool> ContinueAsync(int threadId, CancellationToken cancellationToken = default)
    {
        var args = new ContinueArguments { ThreadId = threadId };
        var response = await SendRequestAsync("continue", args, cancellationToken);
        return response.Success;
    }

    /// <summary>
    /// Steps over the next line (Step Over).
    /// </summary>
    public async Task<bool> NextAsync(int threadId, CancellationToken cancellationToken = default)
    {
        var args = new NextArguments { ThreadId = threadId };
        var response = await SendRequestAsync("next", args, cancellationToken);
        return response.Success;
    }

    /// <summary>
    /// Steps into the next function call (Step In).
    /// </summary>
    public async Task<bool> StepInAsync(int threadId, CancellationToken cancellationToken = default)
    {
        var args = new StepInArguments { ThreadId = threadId };
        var response = await SendRequestAsync("stepIn", args, cancellationToken);
        return response.Success;
    }

    /// <summary>
    /// Disconnects from the debugger.
    /// </summary>
    public async Task DisconnectAsync(bool terminateDebuggee = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = new { terminateDebuggee };
            await SendRequestAsync("disconnect", args, cancellationToken);
        }
        catch
        {
            // Ignore errors during disconnect
        }
    }

    private async Task<DapResponse> SendRequestAsync(string command, object? arguments, CancellationToken cancellationToken)
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("Not connected to debugger");
        }

        int seq;
        TaskCompletionSource<DapResponse> tcs;

        lock (_lock)
        {
            seq = ++_sequenceNumber;
            tcs = new TaskCompletionSource<DapResponse>();
            _pendingRequests[seq] = tcs;
        }

        var request = new DapRequest
        {
            Seq = seq,
            Command = command,
            Arguments = arguments
        };

        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var content = Encoding.UTF8.GetBytes(json);
        var header = Encoding.UTF8.GetBytes($"Content-Length: {content.Length}\r\n\r\n");

        await _stream.WriteAsync(header, cancellationToken);
        await _stream.WriteAsync(content, cancellationToken);
        await _stream.FlushAsync(cancellationToken);

        // Wait for response with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _pendingRequests.Remove(seq);
            }
            throw;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var messageBuffer = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested && _stream != null)
        {
            try
            {
                var bytesRead = await _stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                // Process complete messages
                while (TryParseMessage(messageBuffer, out var message))
                {
                    ProcessMessage(message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnOutput?.Invoke(new OutputEventBody
                {
                    Category = "stderr",
                    Output = $"Error receiving DAP message: {ex.Message}\n"
                });
            }
        }
    }

    private static bool TryParseMessage(StringBuilder buffer, out string message)
    {
        message = string.Empty;
        var content = buffer.ToString();

        // Look for Content-Length header
        var headerEnd = content.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            return false;
        }

        var header = content[..headerEnd];
        const string contentLengthPrefix = "Content-Length: ";

        if (!header.StartsWith(contentLengthPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lengthStr = header[contentLengthPrefix.Length..];
        var newlineIndex = lengthStr.IndexOf('\r');
        if (newlineIndex >= 0)
        {
            lengthStr = lengthStr[..newlineIndex];
        }

        if (!int.TryParse(lengthStr, out var contentLength))
        {
            return false;
        }

        var messageStart = headerEnd + 4;
        var totalLength = messageStart + contentLength;

        if (content.Length < totalLength)
        {
            return false;
        }

        message = content.Substring(messageStart, contentLength);
        buffer.Remove(0, totalLength);
        return true;
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node == null) return;

            var type = node["type"]?.GetValue<string>();

            switch (type)
            {
                case "response":
                    ProcessResponse(json);
                    break;
                case "event":
                    ProcessEvent(json);
                    break;
            }
        }
        catch (Exception ex)
        {
            OnOutput?.Invoke(new OutputEventBody
            {
                Category = "stderr",
                Output = $"Error processing DAP message: {ex.Message}\n"
            });
        }
    }

    private void ProcessResponse(string json)
    {
        var response = JsonSerializer.Deserialize<DapResponse>(json);
        if (response == null) return;

        lock (_lock)
        {
            if (_pendingRequests.TryGetValue(response.RequestSeq, out var tcs))
            {
                _pendingRequests.Remove(response.RequestSeq);
                tcs.SetResult(response);
            }
        }
    }

    private void ProcessEvent(string json)
    {
        var eventNode = JsonNode.Parse(json);
        if (eventNode == null) return;

        var eventName = eventNode["event"]?.GetValue<string>();
        var body = eventNode["body"];

        switch (eventName)
        {
            case "stopped":
                if (body != null)
                {
                    var stoppedBody = JsonSerializer.Deserialize<StoppedEventBody>(body.ToJsonString());
                    if (stoppedBody != null)
                    {
                        OnStopped?.Invoke(stoppedBody);
                    }
                }
                break;

            case "output":
                if (body != null)
                {
                    var outputBody = JsonSerializer.Deserialize<OutputEventBody>(body.ToJsonString());
                    if (outputBody != null)
                    {
                        OnOutput?.Invoke(outputBody);
                    }
                }
                break;

            case "terminated":
                var terminatedBody = body != null
                    ? JsonSerializer.Deserialize<TerminatedEventBody>(body.ToJsonString())
                    : new TerminatedEventBody();
                OnTerminated?.Invoke(terminatedBody ?? new TerminatedEventBody());
                break;

            case "initialized":
                OnInitialized?.Invoke();
                break;

            case "configurationDone":
                OnConfigurationDone?.Invoke();
                break;
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cancellationTokenSource?.Cancel();

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

        _stream?.Dispose();
        _tcpClient?.Dispose();

        if (_netcoredbgProcess != null && !_netcoredbgProcess.HasExited)
        {
            try
            {
                _netcoredbgProcess.Kill();
                await _netcoredbgProcess.WaitForExitAsync();
            }
            catch
            {
                // Ignore
            }
        }

        _netcoredbgProcess?.Dispose();
        _cancellationTokenSource?.Dispose();

        GC.SuppressFinalize(this);
    }
}
