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
- 📸 **Take screenshots** of the debugged application
- 🖼️ **Get UI control tree** as XML representation
- 🖱️ **Click controls** - Click at coordinates or by handle
- ⌨️ **Type text** - Enter text into controls
- 🎹 **Send keyboard input** - Special keys and shortcuts (Ctrl+C, Enter, Tab, etc.)
- 🔄 **Mouse operations** - Move, drag, scroll
- 🎯 **Find controls** - Locate controls by text content
- ✅ **Compatible with WPF, WinForms, WinUI, and MAUI**

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
Starts a new debug session for a .NET application (DLL or EXE).

**Parameters:**
- `programPath` (required): Full path to the .NET program to debug (DLL or EXE)
- `args` (optional): Array of command-line arguments
- `stopAtEntry` (optional): If true, pause at program entry point

**Example with DLL:**
```json
{
  "programPath": "/path/to/MyApp.dll",
  "args": ["--config", "debug"],
  "stopAtEntry": true
}
```

**Example with EXE:**
```json
{
  "programPath": "C:\\path\\to\\MyApp.exe",
  "stopAtEntry": true
}
```

> **Note:** The `dllPath` parameter is still supported for backwards compatibility but is deprecated. Use `programPath` instead.

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

### `ui_take_screenshot`
Takes a screenshot of the debugged application's main window. Returns a base64-encoded BMP image that can be used to visually inspect the application state during debugging.

**Note:** Requires the debugged application to have a visible window.

### `ui_get_controls`
Returns an XML representation of all UI controls in the debugged application's main window. Useful for understanding the UI structure and automating interactions.

**Parameters:**
- `maxDepth` (optional): Maximum depth to traverse the control tree (default: 10)

**Example output:**
```xml
<?xml version="1.0"?>
<Window Title="My Application" ClassName="WindowClass" X="100" Y="100" Width="800" Height="600">
  <Control Type="Button" Text="Click Me" X="10" Y="10" Width="100" Height="30" IsVisible="true" IsEnabled="true"/>
  <Control Type="TextBox" Text="" X="10" Y="50" Width="200" Height="25" IsVisible="true" IsEnabled="true"/>
</Window>
```

### `ui_set_process_id`
Manually sets the process ID of the application to inspect for UI operations. Use this if automatic process detection fails.

**Parameters:**
- `processId` (required): The process ID (PID) of the application to inspect

### `ui_click`
Clicks on a control at specific coordinates or by handle. Works with WPF, WinForms, WinUI, and MAUI applications.

**Parameters:**
- `x` (optional): X screen coordinate to click
- `y` (optional): Y screen coordinate to click
- `handle` (optional): Window handle of the control to click (alternative to coordinates)
- `button` (optional): Mouse button - 'left', 'right', or 'middle' (default: 'left')
- `clickCount` (optional): Number of clicks (default: 1, use 2 for double-click)

### `ui_type_text`
Types text into the currently focused control or a specific control.

**Parameters:**
- `text` (required): The text to type
- `handle` (optional): Window handle of the control to type into

### `ui_send_keys`
Sends keyboard input including special keys and shortcuts.

**Parameters:**
- `keys` (required): Keys to send using the format:
  - Special keys: `{Enter}`, `{Tab}`, `{Escape}`, `{Backspace}`, `{Delete}`, `{Up}`, `{Down}`, `{Left}`, `{Right}`, `{F1}`-`{F12}`
  - Modifiers: `^` = Ctrl, `%` = Alt, `+` = Shift
  - Examples: `{Enter}`, `^c` (Ctrl+C), `%{F4}` (Alt+F4), `^+s` (Ctrl+Shift+S)

### `ui_mouse_move`
Moves the mouse cursor to specified screen coordinates.

**Parameters:**
- `x` (required): X screen coordinate
- `y` (required): Y screen coordinate

### `ui_mouse_drag`
Performs a mouse drag operation from one point to another.

**Parameters:**
- `startX` (required): Starting X coordinate
- `startY` (required): Starting Y coordinate
- `endX` (required): Ending X coordinate
- `endY` (required): Ending Y coordinate
- `button` (optional): Mouse button to use (default: 'left')

### `ui_mouse_scroll`
Scrolls the mouse wheel at the current or specified position.

**Parameters:**
- `delta` (required): Scroll amount (positive = up, negative = down)
- `x` (optional): X coordinate to scroll at
- `y` (optional): Y coordinate to scroll at

### `ui_focus_control`
Sets focus to a specific control by its handle.

**Parameters:**
- `handle` (required): Window handle of the control to focus

### `ui_find_control`
Finds a control by its text content and returns its handle.

**Parameters:**
- `text` (required): Text to search for
- `exactMatch` (optional): If true, requires exact match; if false, partial match allowed (default: false)

## Supported UI Frameworks

The UI automation tools are compatible with:
- **WPF** (Windows Presentation Foundation)
- **WinForms** (Windows Forms)
- **WinUI** (Windows UI Library)
- **MAUI** (Multi-platform App UI) - Windows target

## Example Usage

Here's an example conversation with Claude:

```
User: Debug my .NET application at /home/user/MyApp/bin/Debug/net9.0/MyApp.dll

Claude: I'll start a debug session for your application.
[Calls debug_launch with programPath="/home/user/MyApp/bin/Debug/net9.0/MyApp.dll"]

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
