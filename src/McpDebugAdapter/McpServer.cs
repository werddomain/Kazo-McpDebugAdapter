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

    private static readonly Tool[] AvailableTools =
    [
        new Tool
        {
            Name = "debug_launch",
            Description = "Starts a new debug session for a .NET application. This launches netcoredbg and attaches it to the specified program (DLL or EXE).",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["programPath"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The full path to the .NET program to debug (DLL or EXE file)."
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
                    ["stopAtEntry"] = new ToolProperty
                    {
                        Type = "boolean",
                        Description = "If true, the debugger will pause at the entry point of the program."
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
                Properties = new Dictionary<string, ToolProperty>()
            }
        },
        new Tool
        {
            Name = "debug_step_in",
            Description = "Steps into the function call on the current line (Step In). If there's no function call, behaves like step_next.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>()
            }
        },
        new Tool
        {
            Name = "debug_continue",
            Description = "Continues execution until the next breakpoint is hit or the program terminates.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>()
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
            Description = "Takes a screenshot of the debugged application's main window. Returns a base64-encoded BMP image. Useful for visually inspecting the application state during debugging.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>()
            }
        },
        new Tool
        {
            Name = "ui_get_controls",
            Description = "Returns an XML representation of all UI controls in the debugged application's main window. Includes control type, text, position, and hierarchy. Useful for understanding the UI structure and automating interactions.",
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
            Description = "Clicks on a control at specific coordinates or by handle. Works with WPF, WinForms, WinUI, and MAUI applications.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["x"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The X coordinate (screen position) to click."
                    },
                    ["y"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The Y coordinate (screen position) to click."
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
            Description = "Moves the mouse cursor to the specified screen coordinates.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["x"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The X coordinate (screen position)."
                    },
                    ["y"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The Y coordinate (screen position)."
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

    public McpServer(DebugSession session, TextWriter? output = null, TextReader? input = null)
    {
        _session = session;
        _output = output ?? Console.Out;
        _input = input ?? Console.In;

        // Subscribe to debug events to potentially send notifications
        _session.OnDebugEvent += HandleDebugEvent;
    }

    /// <summary>
    /// Starts the MCP server and begins processing messages from stdin.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        DebugLogger.Log("MCP Server RunAsync started - waiting for messages...");
        
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
                await SendErrorAsync(null, -32603, $"Internal error: {ex.Message}");
            }
        }
        
        DebugLogger.Log("MCP Server RunAsync completed");
    }

    private async Task ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            var request = JsonSerializer.Deserialize<JsonRpcRequest>(message);
            if (request == null)
            {
                DebugLogger.LogWarning("Received invalid JSON-RPC request (null after deserialization)");
                await SendErrorAsync(null, -32600, "Invalid Request");
                return;
            }

            DebugLogger.LogDebug($"Processing request: method={request.Method}, id={request.Id}");
            var result = await HandleRequestAsync(request, cancellationToken);

            if (request.Id != null)
            {
                await SendResponseAsync(request.Id, result);
            }
        }
        catch (JsonException ex)
        {
            DebugLogger.LogError("JSON parse error", ex);
            await SendErrorAsync(null, -32700, "Parse error");
        }
        catch (Exception ex)
        {
            DebugLogger.LogError("Error handling request", ex);
            await SendErrorAsync(null, -32603, $"Internal error: {ex.Message}");
        }
    }

    private async Task<object?> HandleRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        DebugLogger.LogDebug($"Handling method: {request.Method}");
        
        return request.Method switch
        {
            "initialize" => HandleInitialize(request.Params),
            "initialized" => HandleInitialized(),
            "tools/list" => HandleToolsList(),
            "tools/call" => await HandleToolsCallAsync(request.Params, cancellationToken),
            "ping" => new { },
            "shutdown" => HandleShutdown(),
            _ => throw new InvalidOperationException($"Unknown method: {request.Method}")
        };
    }

    private object HandleInitialize(object? @params)
    {
        DebugLogger.Log("MCP Initialize request received");
        
        var result = new InitializeResult
        {
            ProtocolVersion = "2024-11-05",
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
            "debug_stop" => await HandleDebugStopAsync(cancellationToken),
            "debug_set_breakpoint" => await HandleSetBreakpointAsync(toolParams.Arguments, cancellationToken),
            "debug_remove_breakpoint" => await HandleRemoveBreakpointAsync(toolParams.Arguments, cancellationToken),
            "debug_step_next" => await HandleStepNextAsync(cancellationToken),
            "debug_step_in" => await HandleStepInAsync(cancellationToken),
            "debug_continue" => await HandleContinueAsync(cancellationToken),
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
        var stopAtEntry = false;

        if (args.TryGetValue("args", out var argsObj) && argsObj is JsonElement argsElement)
        {
            programArgs = argsElement.EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .ToArray();
        }

        if (args.TryGetValue("stopAtEntry", out var stopAtEntryObj))
        {
            if (stopAtEntryObj is JsonElement boolElement)
            {
                stopAtEntry = boolElement.GetBoolean();
            }
            else if (stopAtEntryObj is bool b)
            {
                stopAtEntry = b;
            }
        }

        var (success, message) = await _session.LaunchAsync(programPath, programArgs, stopAtEntry);
        return CreateResult(message, !success);
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

        var (success, message) = await _session.SetBreakpointAsync(file, line);
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

    private async Task<CallToolResult> HandleStepNextAsync(CancellationToken cancellationToken)
    {
        var (success, message) = await _session.StepNextAsync();
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleStepInAsync(CancellationToken cancellationToken)
    {
        var (success, message) = await _session.StepInAsync();
        return CreateResult(message, !success);
    }

    private async Task<CallToolResult> HandleContinueAsync(CancellationToken cancellationToken)
    {
        var (success, message) = await _session.ContinueAsync();
        return CreateResult(message, !success);
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
            breakpoints = _session.Breakpoints.SelectMany(kvp =>
                kvp.Value.Select(line => new { file = kvp.Key, line })).ToArray()
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

        // Return the image as base64 with a data URI prefix for easy display
        var output = $"Screenshot captured successfully.\n\nBase64 Image Data (BMP format):\ndata:image/bmp;base64,{imageData}";
        return CreateResult(output, false);
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

        _session.SetDebuggedProcessId(processId);
        return CreateResult($"Target process set to PID {processId}", false);
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

        var (clickSuccess, clickMessage) = await _session.ClickAtAsync(x, y, button, clickCount);
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

        var (success, message) = await _session.MouseMoveAsync(x, y);
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

        var (success, message) = await _session.MouseDragAsync(startX, startY, endX, endY, button);
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

        var (success, message) = await _session.MouseScrollAsync(delta, x, y);
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

    private static int GetInt32Value(object obj)
    {
        if (obj is JsonElement element)
        {
            return element.GetInt32();
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

    private async Task SendResponseAsync(object id, object? result)
    {
        var response = new JsonRpcResponse
        {
            Id = id,
            Result = result
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        DebugLogger.LogJsonRpc("SEND", json);
        await _output.WriteLineAsync(json);
        await _output.FlushAsync();
    }

    private async Task SendErrorAsync(object? id, int code, string message)
    {
        DebugLogger.LogError($"Sending error response: code={code}, message={message}");
        
        var response = new JsonRpcResponse
        {
            Id = id,
            Error = new JsonRpcError
            {
                Code = code,
                Message = message
            }
        };

        var json = JsonSerializer.Serialize(response);
        DebugLogger.LogJsonRpc("SEND", json);
        await _output.WriteLineAsync(json);
        await _output.FlushAsync();
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
