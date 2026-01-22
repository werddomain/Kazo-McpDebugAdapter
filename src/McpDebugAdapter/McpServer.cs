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
            Description = "Starts a new debug session for a .NET application. This launches netcoredbg and attaches it to the specified DLL.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["dllPath"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The full path to the .NET DLL file to debug."
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
                Required = ["dllPath"]
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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var line = await _input.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break; // End of stream
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                await ProcessMessageAsync(line, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await SendErrorAsync(null, -32603, $"Internal error: {ex.Message}");
            }
        }
    }

    private async Task ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            var request = JsonSerializer.Deserialize<JsonRpcRequest>(message);
            if (request == null)
            {
                await SendErrorAsync(null, -32600, "Invalid Request");
                return;
            }

            var result = await HandleRequestAsync(request, cancellationToken);

            if (request.Id != null)
            {
                await SendResponseAsync(request.Id, result);
            }
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, -32700, "Parse error");
        }
        catch (Exception ex)
        {
            await SendErrorAsync(null, -32603, $"Internal error: {ex.Message}");
        }
    }

    private async Task<object?> HandleRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
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
        return new InitializeResult
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
    }

    private object? HandleInitialized()
    {
        // Client has acknowledged initialization
        return null;
    }

    private object HandleToolsList()
    {
        return new ListToolsResult { Tools = AvailableTools };
    }

    private async Task<object> HandleToolsCallAsync(object? @params, CancellationToken cancellationToken)
    {
        if (@params == null)
        {
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
            return CreateErrorResult("Invalid tool parameters");
        }

        return toolParams.Name switch
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
            _ => CreateErrorResult($"Unknown tool: {toolParams.Name}")
        };
    }

    private async Task<CallToolResult> HandleDebugLaunchAsync(Dictionary<string, object>? args, CancellationToken cancellationToken)
    {
        if (args == null || !args.TryGetValue("dllPath", out var dllPathObj))
        {
            return CreateErrorResult("Missing required parameter: dllPath");
        }

        var dllPath = dllPathObj.ToString()!;
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

        var (success, message) = await _session.LaunchAsync(dllPath, programArgs, stopAtEntry);
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

    private object HandleShutdown()
    {
        // Cleanup and return acknowledgment
        return new { };
    }

    private void HandleDebugEvent(string eventName, object? data)
    {
        // Could send notifications to the client here
        // For now, we just log or store them for retrieval via debug_get_status
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

        await _output.WriteLineAsync(json);
        await _output.FlushAsync();
    }

    private async Task SendErrorAsync(object? id, int code, string message)
    {
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
