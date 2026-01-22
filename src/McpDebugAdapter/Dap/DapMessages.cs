using System.Text.Json.Serialization;

namespace McpDebugAdapter.Dap;

/// <summary>
/// Base class for all DAP protocol messages.
/// DAP uses a simple message format with a header and JSON body.
/// </summary>
public abstract class DapMessage
{
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

/// <summary>
/// DAP Request message sent from client to debug adapter.
/// </summary>
public class DapRequest : DapMessage
{
    [JsonPropertyName("type")]
    public override string Type => "request";

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Arguments { get; set; }
}

/// <summary>
/// DAP Response message sent from debug adapter to client.
/// </summary>
public class DapResponse : DapMessage
{
    [JsonPropertyName("type")]
    public override string Type => "response";

    [JsonPropertyName("request_seq")]
    public int RequestSeq { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; set; }
}

/// <summary>
/// DAP Event message sent from debug adapter to client.
/// </summary>
public class DapEvent : DapMessage
{
    [JsonPropertyName("type")]
    public override string Type => "event";

    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; set; }
}

#region Initialize Request/Response

public class InitializeRequestArguments
{
    [JsonPropertyName("clientID")]
    public string ClientId { get; set; } = "mcp-debug-adapter";

    [JsonPropertyName("clientName")]
    public string ClientName { get; set; } = "MCP Debug Adapter";

    [JsonPropertyName("adapterID")]
    public string AdapterId { get; set; } = "coreclr";

    [JsonPropertyName("pathFormat")]
    public string PathFormat { get; set; } = "path";

    [JsonPropertyName("linesStartAt1")]
    public bool LinesStartAt1 { get; set; } = true;

    [JsonPropertyName("columnsStartAt1")]
    public bool ColumnsStartAt1 { get; set; } = true;

    [JsonPropertyName("supportsVariableType")]
    public bool SupportsVariableType { get; set; } = true;

    [JsonPropertyName("supportsVariablePaging")]
    public bool SupportsVariablePaging { get; set; } = false;

    [JsonPropertyName("supportsRunInTerminalRequest")]
    public bool SupportsRunInTerminalRequest { get; set; } = false;

    [JsonPropertyName("locale")]
    public string Locale { get; set; } = "en-US";
}

public class Capabilities
{
    [JsonPropertyName("supportsConfigurationDoneRequest")]
    public bool SupportsConfigurationDoneRequest { get; set; }

    [JsonPropertyName("supportsFunctionBreakpoints")]
    public bool SupportsFunctionBreakpoints { get; set; }

    [JsonPropertyName("supportsConditionalBreakpoints")]
    public bool SupportsConditionalBreakpoints { get; set; }

    [JsonPropertyName("supportsEvaluateForHovers")]
    public bool SupportsEvaluateForHovers { get; set; }

    [JsonPropertyName("supportsSetVariable")]
    public bool SupportsSetVariable { get; set; }
}

#endregion

#region Launch Request

public class LaunchRequestArguments
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = ".NET Core Launch";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "coreclr";

    [JsonPropertyName("request")]
    public string Request { get; set; } = "launch";

    [JsonPropertyName("program")]
    public string Program { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Args { get; set; }

    [JsonPropertyName("cwd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cwd { get; set; }

    [JsonPropertyName("stopAtEntry")]
    public bool StopAtEntry { get; set; } = false;

    [JsonPropertyName("console")]
    public string Console { get; set; } = "internalConsole";

    [JsonPropertyName("justMyCode")]
    public bool JustMyCode { get; set; } = true;
}

#endregion

#region Breakpoints

public class SetBreakpointsArguments
{
    [JsonPropertyName("source")]
    public Source Source { get; set; } = new();

    [JsonPropertyName("breakpoints")]
    public SourceBreakpoint[] Breakpoints { get; set; } = [];
}

public class Source
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

public class SourceBreakpoint
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Column { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }
}

public class SetBreakpointsResponseBody
{
    [JsonPropertyName("breakpoints")]
    public Breakpoint[] Breakpoints { get; set; } = [];
}

public class Breakpoint
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Source? Source { get; set; }
}

#endregion

#region Threads

public class ThreadsResponseBody
{
    [JsonPropertyName("threads")]
    public DapThread[] Threads { get; set; } = [];
}

public class DapThread
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

#endregion

#region StackTrace

public class StackTraceArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; } = 0;

    [JsonPropertyName("levels")]
    public int Levels { get; set; } = 20;
}

public class StackTraceResponseBody
{
    [JsonPropertyName("stackFrames")]
    public StackFrame[] StackFrames { get; set; } = [];

    [JsonPropertyName("totalFrames")]
    public int TotalFrames { get; set; }
}

public class StackFrame
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Source? Source { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

#endregion

#region Evaluate

public class EvaluateArguments
{
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = string.Empty;

    [JsonPropertyName("frameId")]
    public int FrameId { get; set; }

    [JsonPropertyName("context")]
    public string Context { get; set; } = "repl";
}

public class EvaluateResponseBody
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}

#endregion

#region Step/Continue

public class ContinueArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("singleThread")]
    public bool SingleThread { get; set; } = false;
}

public class NextArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("singleThread")]
    public bool SingleThread { get; set; } = false;

    [JsonPropertyName("granularity")]
    public string Granularity { get; set; } = "statement";
}

public class StepInArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("singleThread")]
    public bool SingleThread { get; set; } = false;

    [JsonPropertyName("granularity")]
    public string Granularity { get; set; } = "statement";
}

#endregion

#region Events

public class StoppedEventBody
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("allThreadsStopped")]
    public bool AllThreadsStopped { get; set; }
}

public class OutputEventBody
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = "console";

    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;
}

public class TerminatedEventBody
{
    [JsonPropertyName("restart")]
    public bool Restart { get; set; } = false;
}

#endregion

#region Scopes and Variables

public class ScopesArguments
{
    [JsonPropertyName("frameId")]
    public int FrameId { get; set; }
}

public class ScopesResponseBody
{
    [JsonPropertyName("scopes")]
    public Scope[] Scopes { get; set; } = [];
}

public class Scope
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }

    [JsonPropertyName("expensive")]
    public bool Expensive { get; set; }

    [JsonPropertyName("namedVariables")]
    public int NamedVariables { get; set; }
}

public class VariablesArguments
{
    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }

    [JsonPropertyName("start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Start { get; set; }

    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; set; }
}

public class VariablesResponseBody
{
    [JsonPropertyName("variables")]
    public Variable[] Variables { get; set; } = [];
}

public class Variable
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}

#endregion
