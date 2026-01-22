# MCP Debug Adapter for .NET

A Model Context Protocol (MCP) server that allows AI assistants to debug .NET applications by controlling `netcoredbg` via the Debug Adapter Protocol (DAP).

## Architecture

```
AI (MCP Client) <==[Stdio / JSON-RPC]==> MCP Debug Adapter <==[TCP / DAP]==> netcoredbg
```

This application acts as a bridge between AI assistants (like Claude) and the .NET debugging ecosystem, enabling interactive debugging sessions through natural language.

## Features

- 🚀 **Launch debugging sessions** for .NET applications
- 🔴 **Set and remove breakpoints** at specific lines
- ➡️ **Step through code** (step over, step into)
- ▶️ **Continue execution** until breakpoints
- 🔍 **Evaluate expressions** in the current context
- 📚 **View stack traces** and call chains
- 📊 **Inspect local variables**
- 📋 **Monitor session status**

## Prerequisites

1. **.NET 9 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/9.0)

2. **netcoredbg** - Samsung's .NET Core debugger
   - Install globally: `dotnet tool install -g netcoredbg`
   - Or download from [Samsung/netcoredbg](https://github.com/Samsung/netcoredbg/releases)
   - Ensure it's available in your PATH

## Building

```bash
cd src/McpDebugAdapter
dotnet build
```

For a self-contained executable:

```bash
dotnet publish -c Release -r linux-x64 --self-contained
# Or for Windows: dotnet publish -c Release -r win-x64 --self-contained
# Or for macOS: dotnet publish -c Release -r osx-x64 --self-contained
```

## Configuration

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "dotnet-debugger": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/src/McpDebugAdapter/McpDebugAdapter.csproj"]
    }
  }
}
```

Or with a published executable:

```json
{
  "mcpServers": {
    "dotnet-debugger": {
      "command": "/absolute/path/to/McpDebugAdapter"
    }
  }
}
```

### VS Code Extension

If using an MCP-compatible VS Code extension, configure in your settings:

```json
{
  "mcp.servers": {
    "dotnet-debugger": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/src/McpDebugAdapter/McpDebugAdapter.csproj"]
    }
  }
}
```

## Available Tools

### `debug_launch`
Starts a new debug session for a .NET application.

**Parameters:**
- `dllPath` (required): Full path to the .NET DLL to debug
- `args` (optional): Array of command-line arguments
- `stopAtEntry` (optional): If true, pause at program entry point

**Example:**
```json
{
  "dllPath": "/path/to/MyApp.dll",
  "args": ["--config", "debug"],
  "stopAtEntry": true
}
```

### `debug_stop`
Stops the current debug session and terminates the debugged program.

### `debug_set_breakpoint`
Sets a breakpoint at a specific line in a source file.

**Parameters:**
- `file` (required): Full path to the source file
- `line` (required): Line number (1-based)

### `debug_remove_breakpoint`
Removes a breakpoint from a specific line.

**Parameters:**
- `file` (required): Full path to the source file
- `line` (required): Line number (1-based)

### `debug_step_next`
Steps to the next line (Step Over). Executes the current line, stepping over any function calls.

### `debug_step_in`
Steps into the function call on the current line (Step In).

### `debug_continue`
Continues execution until the next breakpoint or program termination.

### `debug_evaluate`
Evaluates an expression in the current debug context.

**Parameters:**
- `expression` (required): Expression to evaluate (variable name, method call, etc.)

**Example:**
```json
{
  "expression": "myVariable.Count"
}
```

### `debug_get_stack`
Returns the current call stack showing the chain of function calls.

### `debug_get_variables`
Returns local variables in the current stack frame.

### `debug_get_status`
Returns the current status of the debug session including:
- Whether a session is active
- Whether the program is paused
- Current thread and frame IDs
- Stop reason
- All set breakpoints

## Example Usage

Here's an example conversation with Claude:

```
User: Debug my .NET application at /home/user/MyApp/bin/Debug/net9.0/MyApp.dll

Claude: I'll start a debug session for your application.
[Calls debug_launch with dllPath="/home/user/MyApp/bin/Debug/net9.0/MyApp.dll"]

User: Set a breakpoint at line 42 in Program.cs

Claude: I'll set a breakpoint at that location.
[Calls debug_set_breakpoint with file="/home/user/MyApp/Program.cs", line=42]

User: Continue execution

Claude: Continuing...
[Calls debug_continue]
The program has stopped at your breakpoint on line 42.

User: What's the value of the 'result' variable?

Claude: Let me check that for you.
[Calls debug_evaluate with expression="result"]
result = 42 (int)

User: Show me the stack trace

Claude: [Calls debug_get_stack]
Stack Trace:
  [0] MyApp.Program.Calculate() at /home/user/MyApp/Program.cs:42
  [1] MyApp.Program.Main(string[]) at /home/user/MyApp/Program.cs:15
```

## Protocol Details

This server implements the MCP (Model Context Protocol) specification over stdio using JSON-RPC 2.0.

### Supported MCP Methods

- `initialize` - Protocol handshake
- `initialized` - Initialization acknowledgment
- `tools/list` - List available debugging tools
- `tools/call` - Execute a debugging tool
- `ping` - Health check
- `shutdown` - Graceful shutdown

## Troubleshooting

### netcoredbg not found
Ensure netcoredbg is installed and in your PATH:
```bash
which netcoredbg  # Linux/macOS
where netcoredbg  # Windows
```

### Connection timeout
The adapter waits up to 2 seconds for netcoredbg to start. If your system is slow, you may need to increase the delay in `DapClient.cs`.

### Breakpoints not hitting
- Ensure the DLL was built with debug symbols (`Debug` configuration)
- Verify the file path matches exactly (case-sensitive on Linux)
- Check that the line number is valid for the source file

## License

MIT License - See [LICENSE](LICENSE) for details.
