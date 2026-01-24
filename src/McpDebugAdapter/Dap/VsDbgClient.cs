using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.IO;

namespace McpDebugAdapter.Dap;

/// <summary>
/// Stream wrapper for process stdin/stdout communication.
/// </summary>
internal class ProcessStream : Stream
{
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly Stream _outputStream;

    public ProcessStream(StreamWriter input, StreamReader output)
    {
        _input = input;
        _output = output;
        _outputStream = output.BaseStream;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position 
    { 
        get => throw new NotSupportedException(); 
        set => throw new NotSupportedException(); 
    }

    public override void Flush() => _input.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _outputStream.Read(buffer, offset, count);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        return await _outputStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _input.BaseStream.Write(buffer, offset, count);
        _input.BaseStream.Flush();
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        await _input.BaseStream.WriteAsync(buffer, offset, count, cancellationToken);
        await _input.BaseStream.FlushAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _input?.Dispose();
            _output?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// DAP Client that communicates with vsdbg (Visual Studio Debugger) via stdin/stdout.
/// This provides better support for .NET 10+ applications compared to netcoredbg.
/// </summary>
public class VsDbgClient : IAsyncDisposable
{
    private Process? _vsdbgProcess;
    private Stream? _stream;
    private int _sequenceNumber = 0;
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
    public bool IsConnected => _stream != null && !_vsdbgProcess?.HasExited == true;

    /// <summary>
    /// Path to the vsdbg executable. Will auto-detect if not specified.
    /// </summary>
    public string? VsDbgPath { get; set; }

    /// <summary>
    /// Starts the vsdbg process and connects to it via TCP.
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

        // Auto-detect vsdbg path if not set
        if (string.IsNullOrEmpty(VsDbgPath))
        {
            VsDbgPath = await FindVsDbgAsync();
            if (string.IsNullOrEmpty(VsDbgPath))
            {
                throw new InvalidOperationException("Could not locate vsdbg. Please install VS Code or specify VsDbgPath manually.\n\n" +
                                                  "To fix this:\n" +
                                                  "1. Install VS Code (includes vsdbg)\n" +
                                                  "2. Set VSDBG_PATH environment variable\n" +
                                                  "3. Or use useVsDbg: false for basic debugging");
            }
        }

        // Start vsdbg process
        var startInfo = new ProcessStartInfo
        {
            FileName = VsDbgPath,
            Arguments = "--interpreter=vscode",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _vsdbgProcess = new Process { StartInfo = startInfo };

        _vsdbgProcess.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                DebugLogger.LogDebug($"vsdbg stdout: {e.Data}");
                OnOutput?.Invoke(new OutputEventBody { Category = "stdout", Output = e.Data + "\n" });
            }
        };

        _vsdbgProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                DebugLogger.LogDebug($"vsdbg stderr: {e.Data}");
                OnOutput?.Invoke(new OutputEventBody { Category = "stderr", Output = e.Data + "\n" });
            }
        };

        try
        {
            DebugLogger.LogDebug($"Starting vsdbg: {VsDbgPath} {startInfo.Arguments}");
            _vsdbgProcess.Start();
            
            DebugLogger.LogDebug($"vsdbg process started with PID: {_vsdbgProcess.Id}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start vsdbg process. Error: {ex.Message}", ex);
        }

        // Wait a bit for the process to initialize
        await Task.Delay(500, cancellationToken);
        
        // Check if process exited early
        if (_vsdbgProcess.HasExited)
        {
            var exitCode = _vsdbgProcess.ExitCode;
            throw new InvalidOperationException($"vsdbg process exited immediately with code {exitCode}. Check if the path is correct and the executable has proper permissions.");
        }

        // Use process stdin/stdout for communication with vsdbg
        _stream = new ProcessStream(_vsdbgProcess.StandardInput, _vsdbgProcess.StandardOutput);
        
        DebugLogger.LogDebug("Successfully connected to vsdbg via stdin/stdout");

        // Start the receive loop
        _receiveTask = ReceiveLoopAsync(_cancellationTokenSource.Token);
    }

    /// <summary>
    /// Auto-detect vsdbg path from common VS Code installation locations.
    /// </summary>
    private static async Task<string?> FindVsDbgAsync()
    {
        // First check environment variable
        var envPath = Environment.GetEnvironmentVariable("VSDBG_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            DebugLogger.Log($"Found vsdbg via VSDBG_PATH: {envPath}");
            return envPath;
        }
        
        var possiblePaths = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows paths
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            possiblePaths.AddRange([
                // Pattern from user's actual installation
                Path.Combine(userProfile, ".vscode", "extensions", "ms-dotnettools.csharp-*", ".debugger", "x86_64", "vsdbg.exe"),
                Path.Combine(userProfile, ".vscode", "extensions", "ms-dotnettools.csharp-*", "debugger", "x86_64", "vsdbg.exe"),
                
                // Original patterns
                Path.Combine(userProfile, ".vscode", "extensions", "ms-dotnettools.csharp-*", "debugger", "vsdbg", "bin", "vsdbg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "resources", "app", "extensions", "ms-dotnettools.csharp", "debugger", "vsdbg", "bin", "vsdbg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "resources", "app", "extensions", "ms-dotnettools.csharp", "debugger", "vsdbg", "bin", "vsdbg.exe")
            ]);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux paths
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/home";
            possiblePaths.AddRange([
                Path.Combine(home, ".vscode", "extensions", "ms-dotnettools.csharp-*", "debugger", "vsdbg", "bin", "vsdbg"),
                "/opt/microsoft/vscode/resources/app/extensions/ms-dotnettools.csharp/debugger/vsdbg/bin/vsdbg",
                "/usr/share/code/resources/app/extensions/ms-dotnettools.csharp/debugger/vsdbg/bin/vsdbg"
            ]);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS paths
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/Users";
            possiblePaths.AddRange([
                Path.Combine(home, ".vscode", "extensions", "ms-dotnettools.csharp-*", "debugger", "vsdbg", "bin", "vsdbg"),
                "/Applications/Visual Studio Code.app/Contents/Resources/app/extensions/ms-dotnettools.csharp/debugger/vsdbg/bin/vsdbg"
            ]);
        }

        // Look for wildcard patterns (for extension versions)
        var expandedPaths = new List<string>();
        foreach (var path in possiblePaths)
        {
            if (path.Contains('*'))
            {
                try
                {
                    // Handle wildcard in directory names (like ms-dotnettools.csharp-*)
                    var pathParts = path.Split(Path.DirectorySeparatorChar);
                    var wildcardIndex = Array.FindIndex(pathParts, part => part.Contains('*'));
                    
                    if (wildcardIndex >= 0)
                    {
                        // Build base path up to wildcard
                        var basePath = string.Join(Path.DirectorySeparatorChar.ToString(), pathParts.Take(wildcardIndex));
                        var wildcardPattern = pathParts[wildcardIndex];
                        var remainingPath = string.Join(Path.DirectorySeparatorChar.ToString(), pathParts.Skip(wildcardIndex + 1));
                        
                        if (Directory.Exists(basePath))
                        {
                            var matchingDirs = Directory.GetDirectories(basePath, wildcardPattern);
                            foreach (var matchingDir in matchingDirs)
                            {
                                var fullPath = Path.Combine(matchingDir, remainingPath);
                                expandedPaths.Add(fullPath);
                            }
                        }
                    }
                }
                catch
                {
                    // Fallback to simple wildcard handling
                    var directory = Path.GetDirectoryName(path);
                    var pattern = Path.GetFileName(path);
                    if (Directory.Exists(directory))
                    {
                        var files = Directory.GetFiles(directory, pattern);
                        expandedPaths.AddRange(files);
                    }
                }
            }
            else
            {
                expandedPaths.Add(path);
            }
        }

        // Find the first existing file
        foreach (var path in expandedPaths)
        {
            if (File.Exists(path))
            {
                DebugLogger.LogDebug($"Found vsdbg at: {path}");
                return path;
            }
        }

        // Try downloading vsdbg if not found
        DebugLogger.LogWarning("vsdbg not found in common locations. Attempting to download...");
        return await DownloadVsDbgAsync();
    }

    /// <summary>
    /// Download vsdbg using the same method VS Code uses.
    /// </summary>
    private static async Task<string?> DownloadVsDbgAsync()
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "vsdbg-download");
            Directory.CreateDirectory(tempDir);

            var vsdbgDir = Path.Combine(tempDir, "vsdbg");
            var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "vsdbg.exe" : "vsdbg";
            var vsdbgPath = Path.Combine(vsdbgDir, executable);

            if (File.Exists(vsdbgPath))
            {
                DebugLogger.LogDebug($"Using cached vsdbg at: {vsdbgPath}");
                return vsdbgPath;
            }

            // Download using curl or powershell
            var downloadCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
                ? $"powershell -Command \"Invoke-WebRequest -Uri 'https://github.com/OmniSharp/omnisharp-vscode/releases/latest/download/vsdbg-win32-x64.zip' -OutFile '{tempDir}\\vsdbg.zip'; Expand-Archive -Path '{tempDir}\\vsdbg.zip' -DestinationPath '{vsdbgDir}' -Force\""
                : $"curl -L https://github.com/OmniSharp/omnisharp-vscode/releases/latest/download/vsdbg-linux-x64.tar.gz | tar -xz -C {vsdbgDir}";

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash",
                Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"/c {downloadCmd}" : $"-c \"{downloadCmd}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            await process!.WaitForExitAsync();

            if (File.Exists(vsdbgPath))
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Make executable on Unix systems
                    Process.Start("chmod", $"+x {vsdbgPath}")?.WaitForExit();
                }
                DebugLogger.LogDebug($"Downloaded vsdbg to: {vsdbgPath}");
                return vsdbgPath;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.LogWarning($"Failed to download vsdbg: {ex.Message}");
        }

        return null;
    }

    // The rest of the methods can be identical to DapClient
    // I'll implement the key ones here and note that others should be copied

    /// <summary>
    /// Sends the initialize request to the debugger.
    /// </summary>
    public async Task<Capabilities?> InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DebugLogger.LogDebug("Sending initialize request to vsdbg...");
            var args = new InitializeRequestArguments();
            var response = await SendRequestAsync("initialize", args, cancellationToken);
            
            DebugLogger.LogDebug($"Initialize response - Success: {response.Success}");
            
            if (response.Success && response.Body is JsonElement bodyElement)
            {
                var capabilities = JsonSerializer.Deserialize<Capabilities>(bodyElement.GetRawText());
                return capabilities;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Initialize failed: {ex.Message}", ex);
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
                StopAtEntry = stopAtEntry,
                Type = "coreclr", // Important: specify coreclr type for .NET
                Request = "launch"
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
    /// Attaches to an existing process for debugging.
    /// </summary>
    public async Task<bool> AttachAsync(int processId, string? program = null, CancellationToken cancellationToken = default)
    {
        try
        {
            DebugLogger.LogDebug($"Sending attach request for process ID: {processId}");
            var attachArgs = new AttachRequestArguments
            {
                ProcessId = processId,
                Program = program,
                Type = "coreclr", // Important: specify coreclr type for .NET
                Request = "attach"
            };

            var response = await SendRequestAsync("attach", attachArgs, cancellationToken);
            DebugLogger.LogDebug($"Attach response - Success: {response.Success}");
            
            if (!response.Success)
            {
                DebugLogger.LogError($"Attach failed - Response: {JsonSerializer.Serialize(response)}");
            }
            
            return response.Success;
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Exception during attach: {ex.Message}", ex);
            throw;
        }
    }

    private int GetAvailablePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // TODO: Copy the remaining methods from DapClient.cs:
    // - SendRequestAsync
    // - ReceiveLoopAsync
    // - ConfigurationDoneAsync
    // - SetBreakpointsAsync
    // - ContinueAsync
    // - StepNextAsync
    // - StepInAsync
    // - EvaluateAsync
    // - GetStackTraceAsync
    // - GetVariablesAsync
    // - DisconnectAsync
    // - DisposeAsync
    // - All the private helper methods

    public async ValueTask DisposeAsync()
    {
        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask;
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        _stream?.Dispose();

        if (_vsdbgProcess != null && !_vsdbgProcess.HasExited)
        {
            try
            {
                _vsdbgProcess.Kill();
                await _vsdbgProcess.WaitForExitAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
            finally
            {
                _vsdbgProcess.Dispose();
            }
        }

        DebugLogger.LogDebug("VsDbgClient disposed");
    }

    // Placeholder for missing methods - copy from DapClient.cs
    private async Task<DapResponse> SendRequestAsync(string command, object arguments, CancellationToken cancellationToken)
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("Not connected to vsdbg");
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
        try
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
        catch (Exception ex)
        {
            DebugLogger.LogError($"Error processing response: {ex.Message}");
        }
    }

    private void ProcessEvent(string json)
    {
        try
        {
            var eventObj = JsonSerializer.Deserialize<DapEvent>(json);
            if (eventObj == null) return;

            switch (eventObj.Event)
            {
                case "stopped":
                    if (eventObj.Body is JsonElement bodyElement)
                    {
                        var stoppedEvent = JsonSerializer.Deserialize<StoppedEventBody>(bodyElement.GetRawText());
                        OnStopped?.Invoke(stoppedEvent);
                    }
                    break;
                case "output":
                    if (eventObj.Body is JsonElement outputElement)
                    {
                        var outputEvent = JsonSerializer.Deserialize<OutputEventBody>(outputElement.GetRawText());
                        OnOutput?.Invoke(outputEvent);
                    }
                    break;
                case "terminated":
                    if (eventObj.Body is JsonElement terminatedElement)
                    {
                        var terminatedEvent = JsonSerializer.Deserialize<TerminatedEventBody>(terminatedElement.GetRawText());
                        OnTerminated?.Invoke(terminatedEvent);
                    }
                    break;
                case "initialized":
                    OnInitialized?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Error processing event: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends configuration done to signal that the configuration phase is complete.
    /// </summary>
    public async Task<bool> ConfigurationDoneAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendRequestAsync("configurationDone", new { }, cancellationToken);
            return response.Success;
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"ConfigurationDone failed: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Sets breakpoints for a source file.
    /// </summary>
    public async Task<Breakpoint[]> SetBreakpointsAsync(string source, int[] lines, CancellationToken cancellationToken = default)
    {
        try
        {
            var breakpoints = lines.Select(line => new { line }).ToArray();
            var args = new
            {
                source = new { path = source },
                breakpoints
            };

            var response = await SendRequestAsync("setBreakpoints", args, cancellationToken);
            
            if (response.Success && response.Body is JsonElement bodyElement)
            {
                var result = JsonSerializer.Deserialize<SetBreakpointsResponseBody>(bodyElement.GetRawText());
                return result?.Breakpoints ?? [];
            }
            
            return [];
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"SetBreakpoints failed: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Continues execution.
    /// </summary>
    public async Task<bool> ContinueAsync(int threadId, CancellationToken cancellationToken = default)
    {
        var args = new { threadId };
        var response = await SendRequestAsync("continue", args, cancellationToken);
        return response.Success;
    }

    /// <summary>
    /// Steps to the next line (Step Over).
    /// </summary>
    public async Task<bool> NextAsync(int threadId, CancellationToken cancellationToken = default)
    {
        var args = new { threadId };
        var response = await SendRequestAsync("next", args, cancellationToken);
        return response.Success;
    }

    /// <summary>
    /// Steps into the next function call (Step In).
    /// </summary>
    public async Task<bool> StepInAsync(int threadId, CancellationToken cancellationToken = default)
    {
        var args = new { threadId };
        var response = await SendRequestAsync("stepIn", args, cancellationToken);
        return response.Success;
    }

    /// <summary>
    /// Evaluates an expression.
    /// </summary>
    public async Task<(string Result, string Type)> EvaluateAsync(string expression, string context = "repl", int? frameId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = new
            {
                expression,
                context,
                frameId
            };

            var response = await SendRequestAsync("evaluate", args, cancellationToken);
            
            if (response.Success && response.Body is JsonElement bodyElement)
            {
                var result = JsonSerializer.Deserialize<EvaluateResponseBody>(bodyElement.GetRawText());
                return (result?.Result ?? "", result?.Type ?? "");
            }
            
            return ("", "");
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Evaluate failed: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets the stack trace for a thread.
    /// </summary>
    public async Task<StackFrame[]> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = new
            {
                threadId,
                startFrame,
                levels
            };

            var response = await SendRequestAsync("stackTrace", args, cancellationToken);
            
            if (response.Success && response.Body is JsonElement bodyElement)
            {
                var result = JsonSerializer.Deserialize<StackTraceResponseBody>(bodyElement.GetRawText());
                return result?.StackFrames ?? [];
            }
            
            return [];
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"GetStackTrace failed: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets scopes for a stack frame.
    /// </summary>
    public async Task<Scope[]> GetScopesAsync(int frameId, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = new { frameId };
            var response = await SendRequestAsync("scopes", args, cancellationToken);
            
            if (response.Success && response.Body is JsonElement bodyElement)
            {
                var result = JsonSerializer.Deserialize<ScopesResponseBody>(bodyElement.GetRawText());
                return result?.Scopes ?? [];
            }
            
            return [];
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"GetScopes failed: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets variables for a scope.
    /// </summary>
    public async Task<Variable[]> GetVariablesAsync(int variablesReference, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = new { variablesReference };
            var response = await SendRequestAsync("variables", args, cancellationToken);
            
            if (response.Success && response.Body is JsonElement bodyElement)
            {
                var result = JsonSerializer.Deserialize<VariablesResponseBody>(bodyElement.GetRawText());
                return result?.Variables ?? [];
            }
            
            return [];
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"GetVariables failed: {ex.Message}", ex);
            throw;
        }
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
}