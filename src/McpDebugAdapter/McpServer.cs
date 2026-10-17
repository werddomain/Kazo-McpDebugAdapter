using System.Text.Json;
using System.Text.Json.Nodes;
using McpDebugAdapter.Dap;
using McpDebugAdapter.Mcp;

namespace McpDebugAdapter;

/// <summary>
/// MCP Server that exposes debug tools to AI clients.
/// Handles JSON-RPC messages via stdio and routes commands to the debug session.
/// </summary>
public class McpServer
{
    private readonly DebugSession _session;
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly bool _useHttpWrapper;

    private static readonly Tool[] AvailableTools =
    [
        new Tool
        {
            Name = "debug_launch",
            Description = "Starts a new debug session for a .NET application (DLL or EXE, .NET 6 to 10) under netcoredbg. Breakpoints set beforehand are applied before the program starts.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["programPath"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The full path to the .NET program to debug (DLL or EXE file), built in Debug so that .pdb files are next to it."
                    },
                    ["dllPath"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "[Deprecated: use programPath instead] The full path to the .NET DLL file to debug."
                    },
                    ["args"] = new ToolProperty
                    {
                        Type = "array",
                        Description = "Optional command line arguments to pass to the program.",
                        Items = new ToolPropertyItems { Type = "string" }
                    },
                    ["cwd"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Optional working directory. Defaults to the program's folder."
                    },
                    ["stopAtEntry"] = new ToolProperty
                    {
                        Type = "boolean",
                        Description = "If true, the debugger will pause at the entry point of the program."
                    },
                    ["netcoredbgPath"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Optional path to netcoredbg(.exe). Defaults to NETCOREDBG_PATH, a netcoredbg folder next to the adapter, then PATH."
                    }
                },
                Required = []
            }
        },
        new Tool
        {
            Name = "debug_attach",
            Description = "Attaches the debugger to an already running .NET process by process ID or name (use debug_list_processes to find it).",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["processId"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The process ID (PID) to attach to."
                    },
                    ["processName"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The process name to search for and attach to."
                    },
                    ["netcoredbgPath"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Optional path to netcoredbg(.exe)."
                    }
                },
                Required = []
            }
        },
        new Tool
        {
            Name = "debug_list_processes",
            Description = "Lists available .NET processes that can be debugged.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["filter"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Optional filter to search for specific process names."
                    }
                },
                Required = []
            }
        },
        new Tool
        {
            Name = "debug_stop",
            Description = "Stops the current debug session and terminates the debugged program.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>()
            }
        },
        new Tool
        {
            Name = "debug_set_breakpoint",
            Description = "Sets a breakpoint at a specific line in a source file. The breakpoint will be hit when execution reaches that line.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["file"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The full path to the source file."
                    },
                    ["line"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The line number where the breakpoint should be set (1-based)."
                    },
                    ["condition"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Optional C# condition; the breakpoint only stops when it is true (e.g. 'i == 3')."
                    }
                },
                Required = ["file", "line"]
            }
        },
        new Tool
        {
            Name = "debug_remove_breakpoint",
            Description = "Removes a breakpoint from a specific line in a source file.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["file"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The full path to the source file."
                    },
                    ["line"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The line number where the breakpoint should be removed (1-based)."
                    }
                },
                Required = ["file", "line"]
            }
        },
        new Tool
        {
            Name = "debug_step_next",
            Description = "Steps to the next line of code (Step Over). If the current line contains a function call, it executes the entire function.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["waitSeconds"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "How long to wait for the step to complete and report the new location (default 10)."
                    }
                }
            }
        },
        new Tool
        {
            Name = "debug_step_in",
            Description = "Steps into the function call on the current line (Step In). If there's no function call, behaves like step_next.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["waitSeconds"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "How long to wait for the step to complete and report the new location (default 10)."
                    }
                }
            }
        },
        new Tool
        {
            Name = "debug_continue",
            Description = "Continues execution until the next breakpoint is hit or the program terminates.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["waitSeconds"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Optional: wait up to this many seconds for the next stop and report where (default 0, return immediately)."
                    }
                }
            }
        },
        new Tool
        {
            Name = "debug_step_out",
            Description = "Runs until the current function returns (Step Out).",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["waitSeconds"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "How long to wait for the step to complete (default 10)."
                    }
                }
            }
        },
        new Tool
        {
            Name = "debug_pause",
            Description = "Pauses the running program wherever it is.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>()
            }
        },
        new Tool
        {
            Name = "debug_wait_for_stop",
            Description = "Waits until the program stops (breakpoint, step, exception) or exits, and returns where it stopped. Use after debug_continue or after triggering an action in the UI.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["waitSeconds"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Maximum time to wait (default 30)."
                    }
                }
            }
        },
        new Tool
        {
            Name = "debug_get_output",
            Description = "Returns the latest console output of the debugged program and of the debugger.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["maxLines"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Number of lines to return (default 100)."
                    }
                }
            }
        },
        new Tool
        {
            Name = "debug_evaluate",
            Description = "Evaluates an expression in the current debug context. Can be used to inspect variables or execute expressions.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["expression"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The expression to evaluate (e.g., variable name, method call, or arithmetic expression)."
                    }
                },
                Required = ["expression"]
            }
        },
        new Tool
        {
            Name = "debug_get_stack",
            Description = "Returns the current call stack showing the chain of function calls that led to the current execution point.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>()
            }
        },
        new Tool
        {
            Name = "debug_get_variables",
            Description = "Returns the local variables in the current stack frame.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>()
            }
        },
        new Tool
        {
            Name = "debug_get_status",
            Description = "Returns the current status of the debug session (active, paused, current location, etc.).",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>()
            }
        },
        new Tool
        {
            Name = "ui_take_screenshot",
            Description = "Takes a screenshot of the debugged application's main window (PNG image). Pixel coordinates in the image are the ones ui_click, ui_mouse_move, ui_mouse_drag and ui_mouse_scroll expect.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>()
            }
        },
        new Tool
        {
            Name = "ui_get_controls",
            Description = "Returns an XML representation of the UI controls in the debugged application's main window (type, text, position, hierarchy). Positions are relative to the window, like screenshot pixels. WPF controls have no native handles, so for WPF prefer coordinates from a screenshot.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["maxDepth"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Maximum depth to traverse the control tree (default: 10)."
                    }
                }
            }
        },
        new Tool
        {
            Name = "ui_set_process_id",
            Description = "Manually sets the process ID of the application to inspect for UI operations. Use this if automatic process detection fails.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["processId"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The process ID (PID) of the application to inspect."
                    }
                },
                Required = ["processId"]
            }
        },
        new Tool
        {
            Name = "ui_click",
            Description = "Clicks in the debugged application, at coordinates or on a control handle. x/y are pixels of ui_take_screenshot (relative to the window's top-left corner). The program must be running (not paused at a breakpoint) for the UI to react.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["x"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "X in screenshot pixels (relative to the window)."
                    },
                    ["y"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Y in screenshot pixels (relative to the window)."
                    },
                    ["handle"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The window handle of the control to click (alternative to coordinates)."
                    },
                    ["button"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Mouse button to click: 'left' (default), 'right', or 'middle'."
                    },
                    ["screenCoordinates"] = new ToolProperty
                    {
                        Type = "boolean",
                        Description = "If true, coordinates are absolute screen pixels instead of window-relative."
                    },
                    ["clickCount"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Number of clicks (1 for single click, 2 for double click). Default is 1."
                    }
                }
            }
        },
        new Tool
        {
            Name = "ui_type_text",
            Description = "Types text into the currently focused control or a specific control. Works with WPF, WinForms, WinUI, and MAUI applications.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["text"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The text to type."
                    },
                    ["handle"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Optional: The window handle of the control to type into. If not specified, types into the focused control."
                    }
                },
                Required = ["text"]
            }
        },
        new Tool
        {
            Name = "ui_send_keys",
            Description = "Sends keyboard input including special keys and shortcuts. Use {Enter}, {Tab}, {Escape}, {Backspace}, {Delete}, {Up}, {Down}, {Left}, {Right}, {F1}-{F12}. For modifiers: ^ = Ctrl, % = Alt, + = Shift (e.g., '^c' for Ctrl+C, '^s' for Ctrl+S).",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["keys"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The keys to send. Examples: '{Enter}', '{Tab}', '^c' (Ctrl+C), '%{F4}' (Alt+F4)."
                    }
                },
                Required = ["keys"]
            }
        },
        new Tool
        {
            Name = "ui_mouse_move",
            Description = "Moves the mouse cursor. x/y are screenshot pixels (relative to the window) unless screenCoordinates is true.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["x"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "X in screenshot pixels."
                    },
                    ["y"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Y in screenshot pixels."
                    },
                    ["screenCoordinates"] = new ToolProperty
                    {
                        Type = "boolean",
                        Description = "If true, coordinates are absolute screen pixels instead of window-relative."
                    }
                },
                Required = ["x", "y"]
            }
        },
        new Tool
        {
            Name = "ui_mouse_drag",
            Description = "Performs a mouse drag operation from one point to another. Useful for drag-and-drop interactions.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["startX"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The starting X coordinate."
                    },
                    ["startY"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The starting Y coordinate."
                    },
                    ["endX"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The ending X coordinate."
                    },
                    ["endY"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The ending Y coordinate."
                    },
                    ["button"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Mouse button to use: 'left' (default), 'right', or 'middle'."
                    },
                    ["screenCoordinates"] = new ToolProperty
                    {
                        Type = "boolean",
                        Description = "If true, coordinates are absolute screen pixels instead of window-relative."
                    }
                },
                Required = ["startX", "startY", "endX", "endY"]
            }
        },
        new Tool
        {
            Name = "ui_mouse_scroll",
            Description = "Scrolls the mouse wheel at the current or specified position. Positive delta scrolls up, negative scrolls down.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["delta"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The scroll amount. Positive values scroll up, negative values scroll down."
                    },
                    ["x"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Optional: X coordinate to scroll at."
                    },
                    ["y"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Optional: Y coordinate to scroll at."
                    },
                    ["screenCoordinates"] = new ToolProperty
                    {
                        Type = "boolean",
                        Description = "If true, x/y are absolute screen pixels instead of window-relative."
                    }
                },
                Required = ["delta"]
            }
        },
        new Tool
        {
            Name = "ui_focus_control",
            Description = "Sets focus to a specific control by its handle. The control will receive keyboard input.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["handle"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The window handle of the control to focus."
                    }
                },
                Required = ["handle"]
            }
        },
        new Tool
        {
            Name = "ui_find_control",
            Description = "Finds a control by its text content and returns its handle. Useful for locating buttons, labels, or other controls.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["text"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The text to search for."
                    },
                    ["exactMatch"] = new ToolProperty
                    {
                        Type = "boolean",
                        Description = "If true, requires exact text match. If false (default), partial match is allowed."
                    }
                },
                Required = ["text"]
            }
        }
    ];

    public McpServer(DebugSession session, TextWriter? output = null, TextReader? input = null, bool useHttpWrapper = false)
    {
        _session = session;
        _output = output ?? Console.Out;
        _input = input ?? Console.In;
        _useHttpWrapper = useHttpWrapper;

        // Subscribe to debug events to potentially send notifications
        _session.OnDebugEvent += HandleDebugEvent;
    }

    /// <summary>
    /// Starts the MCP server and begins processing messages from stdin or HTTP.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        DebugLogger.Log("MCP Server RunAsync started - waiting for messages...");
        
        if (_useHttpWrapper)
        {
            await RunHttpModeAsync(cancellationToken);
        }
        else
        {
            await RunStdioModeAsync(cancellationToken);
        }
        
        DebugLogger.Log("MCP Server RunAsync completed");
    }

    private async Task RunHttpModeAsync(CancellationToken cancellationToken)
    {
        var reader = _input as StreamReader;
        var writer = _output as StreamWriter;

        if (reader == null || writer == null)
        {
            throw new InvalidOperationException("HTTP mode requires StreamReader/StreamWriter");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Read HTTP request and extract JSON-RPC message
                var jsonRpcMessage = await HttpJsonRpcHandler.ReadHttpRequestAsync(reader);
                
                if (jsonRpcMessage == null)
                {
                    // Invalid or non-POST request, close connection
                    break;
                }

                if (string.IsNullOrWhiteSpace(jsonRpcMessage))
                {
                    // Handle OPTIONS request for CORS
                    await HttpJsonRpcHandler.SendOptionsResponseAsync(writer);
                    continue;
                }

                DebugLogger.LogJsonRpc("RECV", jsonRpcMessage);
                
                // Process the JSON-RPC message
                var response = await ProcessMessageForResponseAsync(jsonRpcMessage, cancellationToken);
                
                if (!string.IsNullOrEmpty(response))
                {
                    DebugLogger.LogJsonRpc("SEND", response);
                    await HttpJsonRpcHandler.SendHttpResponseAsync(writer, response);
                }
            }
            catch (OperationCanceledException)
            {
                DebugLogger.Log("MCP Server HTTP mode cancelled");
                break;
            }
            catch (Exception ex)
            {
                DebugLogger.LogError("Error processing HTTP message", ex);
                try
                {
                    var errorResponse = JsonSerializer.Serialize(new JsonRpcResponse
                    {
                        JsonRpc = "2.0",
                        Id = null,
                        Error = new JsonRpcError
                        {
                            Code = -32603,
                            Message = $"Internal error: {ex.Message}"
                        }
                    });
                    await HttpJsonRpcHandler.SendHttpResponseAsync(writer, errorResponse);
                }
                catch
                {
                    // If we can't send error response, break the connection
                    break;
                }
            }
        }
    }

    private async Task RunStdioModeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var line = await _input.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    DebugLogger.Log("End of input stream detected");
                    break; // End of stream
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                DebugLogger.LogJsonRpc("RECV", line);
                await ProcessMessageAsync(line, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                DebugLogger.Log("MCP Server operation cancelled");
                break;
            }
            catch (Exception ex)
            {
                DebugLogger.LogError("Error processing message", ex);
            }
        }
    }

    private async Task ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        var response = await ProcessMessageForResponseAsync(message, cancellationToken);
        if (response != null)
        {
            DebugLogger.LogJsonRpc("SEND", response);
            await _output.WriteLineAsync(response);
            await _output.FlushAsync();
        }
    }

    /// <summary>
    /// Handles one JSON-RPC message and returns the serialized response, or null for notifications.
    /// </summary>
    private async Task<string?> ProcessMessageForResponseAsync(string message, CancellationToken cancellationToken)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(message);
        }
        catch (JsonException ex)
        {
            DebugLogger.LogError("JSON parse error", ex);
            return SerializeError(null, -32700, "Parse error");
        }

        if (request == null || string.IsNullOrEmpty(request.Method))
        {
            // Not a request (e.g. a response to something we never sent): nothing to answer
            return request?.Id == null ? null : SerializeError(request.Id, -32600, "Invalid Request");
        }

        // Notifications (no id) never get a response, even when unknown or failing
        var isNotification = request.Id == null;

        try
        {
            DebugLogger.LogDebug($"Processing request: method={request.Method}, id={request.Id}");
            var result = await HandleRequestAsync(request, cancellationToken);
            if (isNotification)
            {
                return null;
            }

            return JsonSerializer.Serialize(new JsonRpcResponse
            {
                JsonRpc = "2.0",
                Id = request.Id,
                Result = result ?? new { }
            }, ResponseSerializerOptions);
        }
        catch (MethodNotFoundException)
        {
            DebugLogger.LogDebug($"Method not found: {request.Method}");
            return isNotification ? null : SerializeError(request.Id, -32601, $"Method not found: {request.Method}");
        }
        catch (Exception ex)
        {
            DebugLogger.LogError("Error handling request", ex);
            return isNotification ? null : SerializeError(request.Id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions ResponseSerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string SerializeError(object? id, int code, string message)
    {
        return JsonSerializer.Serialize(new JsonRpcResponse
        {
            JsonRpc = "2.0",
            Id = id,
            Error = new JsonRpcError
            {
                Code = code,
                Message = message
            }
        });
    }

    private sealed class MethodNotFoundException(string method) : Exception(method);

    private async Task<object?> HandleRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        DebugLogger.LogDebug($"Handling method: {request.Method}");

        if (request.Method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            DebugLogger.LogDebug($"Notification received: {request.Method}");
            return null;
        }

        return request.Method switch
        {
            "initialize" => HandleInitialize(request.Params),
            "initialized" => HandleInitialized(),
            "logging/setLevel" => HandleLoggingSetLevel(request.Params),
            "tools/list" => HandleToolsList(),
            "tools/call" => await HandleToolsCallAsync(request.Params, cancellationToken),
            "resources/list" => new { resources = Array.Empty<object>() },
            "prompts/list" => new { prompts = Array.Empty<object>() },
            "ping" => new { },
            "shutdown" => HandleShutdown(),
            _ => throw new MethodNotFoundException(request.Method)
        };
    }

    private object HandleInitialize(object? @params)
    {
        DebugLogger.Log("MCP Initialize request received");
        
        // Answer with the client's version when we know it (we only use features common to all of them)
        string[] supported = ["2025-06-18", "2025-03-26", "2024-11-05"];
        var requested = @params is JsonElement { ValueKind: JsonValueKind.Object } p &&
                        p.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

        var result = new InitializeResult
        {
            ProtocolVersion = requested != null && supported.Contains(requested) ? requested : supported[^1],
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
                Logging = new { }
            },
            ServerInfo = new ServerInfo
            {
                Name = "mcp-debug-adapter",
                Version = "1.0.0"
            }
        };
        
        DebugLogger.Log($"MCP Initialize response: protocol={result.ProtocolVersion}, server={result.ServerInfo.Name} v{result.ServerInfo.Version}");
        return result;
    }

    private object? HandleInitialized()
    {
        // Client has acknowledged initialization
        DebugLogger.Log("MCP Initialized notification received - client ready");
        return null;
    }

    private object HandleLoggingSetLevel(object? @params)
    {
        // Client is setting log level
        DebugLogger.Log("MCP logging/setLevel received");
        if (@params != null)
        {
            try
            {
                var logLevelParams = JsonSerializer.Deserialize<Dictionary<string, object>>(@params.ToString() ?? "{}");
                if (logLevelParams?.ContainsKey("level") == true)
                {
                    DebugLogger.Log($"Log level set to: {logLevelParams["level"]}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Could not parse log level params: {ex.Message}");
            }
        }
        return new { };
    }

    private object HandleToolsList()
    {
        DebugLogger.LogDebug($"Tools list requested - returning {AvailableTools.Length} tools");
        return new ListToolsResult { Tools = AvailableTools };
    }

    private async Task<object> HandleToolsCallAsync(object? @params, CancellationToken cancellationToken)
    {
        if (@params == null)
        {
            DebugLogger.LogWarning("tools/call called with missing parameters");
            return CreateErrorResult("Missing parameters");
        }

        CallToolParams? toolParams;
        if (@params is JsonElement element)
        {
            toolParams = JsonSerializer.Deserialize<CallToolParams>(element.GetRawText());
        }
        else
        {
            var json = JsonSerializer.Serialize(@params);
            toolParams = JsonSerializer.Deserialize<CallToolParams>(json);
        }

        if (toolParams == null)
        {
            DebugLogger.LogWarning("tools/call called with invalid tool parameters");
            return CreateErrorResult("Invalid tool parameters");
        }

        DebugLogger.Log($"Tool call: {toolParams.Name}");
        if (toolParams.Arguments != null && toolParams.Arguments.Count > 0)
        {
            DebugLogger.LogDebug($"Tool arguments: {JsonSerializer.Serialize(toolParams.Arguments)}");
        }

        var result = toolParams.Name switch
        {
            "debug_launch" => await HandleDebugLaunchAsync(toolParams.Arguments, cancellationToken),
            "debug_attach" => await HandleDebugAttachAsync(toolParams.Arguments, cancellationToken),
            "debug_list_processes" => HandleListProcessesAsync(toolParams.Arguments),
            "debug_stop" => await HandleDebugStopAsync(cancellationToken),
            "debug_set_breakpoint" => await HandleSetBreakpointAsync(toolParams.Arguments, cancellationToken),
            "debug_remove_breakpoint" => await HandleRemoveBreakpointAsync(toolParams.Arguments, cancellationToken),
            "debug_step_next" => ToResult(await _session.StepNextAsync(GetWait(toolParams.Arguments, 10))),
            "debug_step_in" => ToResult(await _session.StepInAsync(GetWait(toolParams.Arguments, 10))),
            "debug_step_out" => ToResult(await _session.StepOutAsync(GetWait(toolParams.Arguments, 10))),
            "debug_continue" => ToResult(await _session.ContinueAsync(GetWait(toolParams.Arguments, 0))),
            "debug_pause" => ToResult(await _session.PauseAsync()),
            "debug_wait_for_stop" => ToResult(await _session.WaitForStopAsync(GetWait(toolParams.Arguments, 30))),
            "debug_get_output" => HandleGetOutput(toolParams.Arguments),
            "debug_evaluate" => await HandleEvaluateAsync(toolParams.Arguments, cancellationToken),
            "debug_get_stack" => await HandleGetStackAsync(cancellationToken),
            "debug_get_variables" => await HandleGetVariablesAsync(cancellationToken),
            "debug_get_status" => HandleGetStatus(),
            "ui_take_screenshot" => await HandleTakeScreenshotAsync(cancellationToken),
            "ui_get_controls" => await HandleGetUiControlsAsync(toolParams.Arguments, cancellationToken),
            "ui_set_process_id" => HandleSetProcessId(toolParams.Arguments),
            "ui_click" => await HandleUiClickAsync(toolParams.Arguments, cancellationToken),
            "ui_type_text" => await HandleUiTypeTextAsync(toolParams.Arguments, cancellationToken),
            "ui_send_keys" => await HandleUiSendKeysAsync(toolParams.Arguments, cancellationToken),
            "ui_mouse_move" => await HandleUiMouseMoveAsync(toolParams.Arguments, cancellationToken),
            "ui_mouse_drag" => await HandleUiMouseDragAsync(toolParams.Arguments, cancellationToken),
            "ui_mouse_scroll" => await HandleUiMouseScrollAsync(toolParams.Arguments, cancellationToken),
            "ui_focus_control" => await HandleUiFocusControlAsync(toolParams.Arguments, cancellationToken),
            "ui_find_control" => await HandleUiFindControlAsync(toolParams.Arguments, cancellationToken),
            _ => CreateErrorResult($"Unknown tool: {toolParams.Name}")
        };

        DebugLogger.LogDebug($"Tool {toolParams.Name} completed - isError={result.IsError}");
        return result;
    }

    private async Task<CallToolResult> HandleDebugLaunchAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null)
        {
            return CreateErrorResult("Missing required parameter: programPath (or dllPath)");
        }

        // Accept either 'programPath' (preferred) or 'dllPath' (deprecated, for backwards compatibility)
        string? programPath = null;
        
        if (args.TryGetValue("programPath", out var programPathObj))
        {
            programPath = programPathObj.ToString();
        }
        else if (args.TryGetValue("dllPath", out var dllPathObj))
        {
            programPath = dllPathObj.ToString();
        }
        
        if (string.IsNullOrEmpty(programPath))
        {
            return CreateErrorResult("Missing required parameter: programPath (or dllPath)");
        }

        string[]? programArgs = null;
        if (args.TryGetValue("args", out var argsObj) && argsObj is JsonElement { ValueKind: JsonValueKind.Array } argsElement)
        {
            programArgs = argsElement.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText())
                .ToArray();
        }

        var stopAtEntry = GetBool(args, "stopAtEntry") ?? false;
        var cwd = GetString(args, "cwd");
        var netcoredbgPath = GetString(args, "netcoredbgPath");

        var (success, message) = await _session.LaunchAsync(programPath, programArgs, stopAtEntry, netcoredbgPath, cwd);
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleDebugAttachAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        int? processId = null;
        string? processName = null;

        if (args?.TryGetValue("processId", out var processIdObj) == true)
        {
            if (processIdObj is JsonElement intElement)
            {
                processId = intElement.GetInt32();
            }
            else if (processIdObj is int i)
            {
                processId = i;
            }
            else if (int.TryParse(processIdObj.ToString(), out var parsed))
            {
                processId = parsed;
            }
        }

        if (args?.TryGetValue("processName", out var processNameObj) == true)
        {
            processName = processNameObj.ToString();
        }

        if (!processId.HasValue && string.IsNullOrEmpty(processName))
        {
            return CreateErrorResult("Either processId or processName must be specified.");
        }

        var (success, message) = await _session.AttachAsync(processId, processName, GetString(args, "netcoredbgPath"));
        return CreateResult(message, !success);
    }

    private CallToolResult HandleListProcessesAsync(Dictionary<string, object>? args)
    {
        string? filter = null;
        if (args?.TryGetValue("filter", out var filterObj) == true)
        {
            filter = filterObj.ToString();
        }

        var (success, processes, message) = _session.ListDebuggableProcesses(filter);
        
        if (!success)
        {
            return CreateErrorResult(message);
        }

        var processInfo = processes.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            title = p.MainWindowTitle,
            startTime = p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
            hasExited = p.HasExited
        }).ToList();

        var fullMessage = message;
        if (processes.Any())
        {
            fullMessage += ":\n" + string.Join("\n", processes.Select(p => $"  {p}"));
        }

        return CreateResult(fullMessage, false);
    }

    private async Task<CallToolResult> HandleDebugStopAsync(CancellationToken cancellationToken)
    {
        var (success, message) = await _session.StopAsync();
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleSetBreakpointAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null)
        {
            return CreateErrorResult("Missing parameters");
        }

        if (!args.TryGetValue("file", out var fileObj))
        {
            return CreateErrorResult("Missing required parameter: file");
        }

        if (!args.TryGetValue("line", out var lineObj))
        {
            return CreateErrorResult("Missing required parameter: line");
        }

        var file = fileObj.ToString()!;
        int line;

        if (lineObj is JsonElement lineElement)
        {
            line = lineElement.GetInt32();
        }
        else if (lineObj is int i)
        {
            line = i;
        }
        else if (!int.TryParse(lineObj.ToString(), out line))
        {
            return CreateErrorResult("Invalid line number");
        }

        var (success, message) = await _session.SetBreakpointAsync(file, line, GetString(args, "condition"));
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleRemoveBreakpointAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null)
        {
            return CreateErrorResult("Missing parameters");
        }

        if (!args.TryGetValue("file", out var fileObj))
        {
            return CreateErrorResult("Missing required parameter: file");
        }

        if (!args.TryGetValue("line", out var lineObj))
        {
            return CreateErrorResult("Missing required parameter: line");
        }

        var file = fileObj.ToString()!;
        int line;

        if (lineObj is JsonElement lineElement)
        {
            line = lineElement.GetInt32();
        }
        else if (lineObj is int i)
        {
            line = i;
        }
        else if (!int.TryParse(lineObj.ToString(), out line))
        {
            return CreateErrorResult("Invalid line number");
        }

        var (success, message) = await _session.RemoveBreakpointAsync(file, line);
        return CreateResult(message, !success);
    }

    private CallToolResult HandleGetOutput(Dictionary<string, object>? args)
    {
        var maxLines = 100;
        if (args != null && args.TryGetValue("maxLines", out var maxLinesObj))
        {
            maxLines = Math.Max(1, GetInt32Value(maxLinesObj));
        }

        var lines = _session.GetRecentOutput(maxLines);
        return CreateResult(lines.Length == 0 ? "No output yet." : string.Join("\n", lines), false);
    }

    private async Task<CallToolResult> HandleEvaluateAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null || !args.TryGetValue("expression", out var exprObj))
        {
            return CreateErrorResult("Missing required parameter: expression");
        }

        var expression = exprObj.ToString()!;
        var (success, result, type) = await _session.EvaluateAsync(expression);

        if (success)
        {
            var output = type != null
                ? $"{expression} = {result} ({type})"
                : $"{expression} = {result}";
            return CreateResult(output, false);
        }

        return CreateResult(result, true);
    }

    private async Task<CallToolResult> HandleGetStackAsync(CancellationToken cancellationToken)
    {
        var (success, frames, message) = await _session.GetStackTraceAsync();

        if (!success)
        {
            return CreateResult(message, true);
        }

        var output = "Stack Trace:\n" + string.Join("\n", frames.Select((f, i) =>
            $"  [{i}] {f.Name} at {f.Source?.Path ?? "unknown"}:{f.Line}"));

        return CreateResult(output, false);
    }

    private async Task<CallToolResult> HandleGetVariablesAsync(CancellationToken cancellationToken)
    {
        var (success, variables, message) = await _session.GetLocalVariablesAsync();

        if (!success)
        {
            return CreateResult(message, true);
        }

        if (variables.Length == 0)
        {
            return CreateResult("No local variables in current scope.", false);
        }

        var output = "Local Variables:\n" + string.Join("\n", variables.Select(v =>
            $"  {v.Name}: {v.Value}" + (v.Type != null ? $" ({v.Type})" : "")));

        return CreateResult(output, false);
    }

    private CallToolResult HandleGetStatus()
    {
        var status = new
        {
            active = _session.IsActive,
            paused = _session.IsPaused,
            program = _session.ProgramPath,
            threadId = _session.CurrentThreadId,
            frameId = _session.CurrentFrameId,
            stopReason = _session.StopReason,
            location = _session.CurrentLocation,
            processId = _session.ProcessId,
            exitCode = _session.ExitCode,
            breakpoints = _session.BreakpointDetails.Select(b => new
            {
                file = b.File,
                line = b.Line,
                condition = b.Condition,
                verified = b.Verified,
                boundLine = b.ActualLine,
                message = b.Message
            }).ToArray()
        };

        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
        return CreateResult($"Debug Session Status:\n{json}", false);
    }

    private async Task<CallToolResult> HandleTakeScreenshotAsync(CancellationToken cancellationToken)
    {
        var (success, imageData, message) = await _session.TakeScreenshotAsync();

        if (!success)
        {
            return CreateResult(message, true);
        }

        return new CallToolResult
        {
            Content =
            [
                new ContentItem { Type = "image", Data = imageData, MimeType = "image/png" },
                new ContentItem { Type = "text", Text = message }
            ],
            IsError = false
        };
    }

    private async Task<CallToolResult> HandleGetUiControlsAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        var maxDepth = 10;

        if (args != null && args.TryGetValue("maxDepth", out var maxDepthObj))
        {
            if (maxDepthObj is JsonElement depthElement)
            {
                maxDepth = depthElement.GetInt32();
            }
            else if (maxDepthObj is int i)
            {
                maxDepth = i;
            }
            else if (int.TryParse(maxDepthObj.ToString(), out var parsed))
            {
                maxDepth = parsed;
            }
        }

        var (success, xml, message) = await _session.GetUiControlsAsync(maxDepth);

        if (!success)
        {
            return CreateResult(message, true);
        }

        return CreateResult($"UI Controls (XML):\n\n{xml}", false);
    }

    private CallToolResult HandleSetProcessId(Dictionary<string, object>? args)
    {
        if (args == null || !args.TryGetValue("processId", out var processIdObj))
        {
            return CreateErrorResult("Missing required parameter: processId");
        }

        int processId;
        if (processIdObj is JsonElement pidElement)
        {
            processId = pidElement.GetInt32();
        }
        else if (processIdObj is int i)
        {
            processId = i;
        }
        else if (!int.TryParse(processIdObj.ToString(), out processId))
        {
            return CreateErrorResult("Invalid process ID");
        }

        return _session.SetDebuggedProcessId(processId)
            ? CreateResult($"Target process set to PID {processId}", false)
            : CreateErrorResult($"Process {processId} not found.");
    }

    private async Task<CallToolResult> HandleUiClickAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        // Check if we have a handle to click
        if (args != null && args.TryGetValue("handle", out var handleObj))
        {
            long handle = GetInt64Value(handleObj);
            var (success, message) = await _session.ClickControlAsync(handle);
            return CreateResult(message, !success);
        }

        // Otherwise use coordinates
        if (args == null || !args.TryGetValue("x", out var xObj) || !args.TryGetValue("y", out var yObj))
        {
            return CreateErrorResult("Missing required parameters: x and y coordinates, or handle");
        }

        int x = GetInt32Value(xObj);
        int y = GetInt32Value(yObj);

        string button = "left";
        if (args.TryGetValue("button", out var buttonObj))
        {
            button = buttonObj.ToString() ?? "left";
        }

        int clickCount = 1;
        if (args.TryGetValue("clickCount", out var clickCountObj))
        {
            clickCount = GetInt32Value(clickCountObj);
        }

        var (clickSuccess, clickMessage) = await _session.ClickAtAsync(x, y, button, clickCount, GetBool(args, "screenCoordinates") ?? false);
        return CreateResult(clickMessage, !clickSuccess);
    }

    private async Task<CallToolResult> HandleUiTypeTextAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null || !args.TryGetValue("text", out var textObj))
        {
            return CreateErrorResult("Missing required parameter: text");
        }

        var text = textObj.ToString() ?? string.Empty;
        long? handle = null;

        if (args.TryGetValue("handle", out var handleObj))
        {
            handle = GetInt64Value(handleObj);
        }

        var (success, message) = await _session.TypeTextAsync(text, handle);
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleUiSendKeysAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null || !args.TryGetValue("keys", out var keysObj))
        {
            return CreateErrorResult("Missing required parameter: keys");
        }

        var keys = keysObj.ToString() ?? string.Empty;
        var (success, message) = await _session.SendKeysAsync(keys);
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleUiMouseMoveAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null || !args.TryGetValue("x", out var xObj) || !args.TryGetValue("y", out var yObj))
        {
            return CreateErrorResult("Missing required parameters: x and y");
        }

        int x = GetInt32Value(xObj);
        int y = GetInt32Value(yObj);

        var (success, message) = await _session.MouseMoveAsync(x, y, GetBool(args, "screenCoordinates") ?? false);
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleUiMouseDragAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null ||
            !args.TryGetValue("startX", out var startXObj) ||
            !args.TryGetValue("startY", out var startYObj) ||
            !args.TryGetValue("endX", out var endXObj) ||
            !args.TryGetValue("endY", out var endYObj))
        {
            return CreateErrorResult("Missing required parameters: startX, startY, endX, endY");
        }

        int startX = GetInt32Value(startXObj);
        int startY = GetInt32Value(startYObj);
        int endX = GetInt32Value(endXObj);
        int endY = GetInt32Value(endYObj);

        string button = "left";
        if (args.TryGetValue("button", out var buttonObj))
        {
            button = buttonObj.ToString() ?? "left";
        }

        var (success, message) = await _session.MouseDragAsync(startX, startY, endX, endY, button, GetBool(args, "screenCoordinates") ?? false);
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleUiMouseScrollAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null || !args.TryGetValue("delta", out var deltaObj))
        {
            return CreateErrorResult("Missing required parameter: delta");
        }

        int delta = GetInt32Value(deltaObj);
        int? x = null;
        int? y = null;

        if (args.TryGetValue("x", out var xObj) && args.TryGetValue("y", out var yObj))
        {
            x = GetInt32Value(xObj);
            y = GetInt32Value(yObj);
        }

        var (success, message) = await _session.MouseScrollAsync(delta, x, y, GetBool(args, "screenCoordinates") ?? false);
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleUiFocusControlAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null || !args.TryGetValue("handle", out var handleObj))
        {
            return CreateErrorResult("Missing required parameter: handle");
        }

        long handle = GetInt64Value(handleObj);
        var (success, message) = await _session.FocusControlAsync(handle);
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleUiFindControlAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null || !args.TryGetValue("text", out var textObj))
        {
            return CreateErrorResult("Missing required parameter: text");
        }

        var text = textObj.ToString() ?? string.Empty;
        bool exactMatch = false;

        if (args.TryGetValue("exactMatch", out var exactMatchObj))
        {
            if (exactMatchObj is JsonElement boolElement)
            {
                exactMatch = boolElement.GetBoolean();
            }
            else if (exactMatchObj is bool b)
            {
                exactMatch = b;
            }
        }

        var (success, handle, message) = await _session.FindControlByTextAsync(text, exactMatch);

        if (success)
        {
            return CreateResult($"Found control with handle: {handle}\n{message}", false);
        }

        return CreateResult(message, true);
    }

    private static CallToolResult ToResult((bool Success, string Message) result)
        => CreateResult(result.Message, !result.Success);

    private static TimeSpan GetWait(Dictionary<string, object>? args, int defaultSeconds)
    {
        var seconds = defaultSeconds;
        if (args != null && args.TryGetValue("waitSeconds", out var value))
        {
            seconds = GetInt32Value(value);
        }

        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 600));
    }

    private static string? GetString(Dictionary<string, object>? args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var value) || value == null)
        {
            return null;
        }

        var text = value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool? GetBool(Dictionary<string, object>? args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => bool.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static int GetInt32Value(object obj)
    {
        if (obj is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number ? (int)element.GetDouble()
                : int.TryParse(element.ToString(), out var fromText) ? fromText : 0;
        }
        else if (obj is int i)
        {
            return i;
        }
        else if (int.TryParse(obj.ToString(), out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static long GetInt64Value(object obj)
    {
        if (obj is JsonElement element)
        {
            return element.GetInt64();
        }
        else if (obj is long l)
        {
            return l;
        }
        else if (obj is int i)
        {
            return i;
        }
        else if (long.TryParse(obj.ToString(), out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private object HandleShutdown()
    {
        // Cleanup and return acknowledgment
        DebugLogger.Log("MCP Shutdown request received");
        return new { };
    }

    private void HandleDebugEvent(string eventName, object? data)
    {
        // Could send notifications to the client here
        // For now, we just log or store them for retrieval via debug_get_status
        DebugLogger.LogDebug($"Debug event: {eventName}");
    }

    private static CallToolResult CreateResult(string text, bool isError)
    {
        return new CallToolResult
        {
            Content = [new ContentItem { Type = "text", Text = text }],
            IsError = isError
        };
    }

    private static CallToolResult CreateErrorResult(string message)
    {
        return CreateResult(message, true);
    }
}
