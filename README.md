# MCP Debug Adapter for .NET

A Model Context Protocol (MCP) server that allows AI assistants to debug .NET applications by controlling `netcoredbg` via the Debug Adapter Protocol (DAP).

## Architecture

```
AI (MCP Client) <==[Stdio / JSON-RPC]==> MCP Debug Adapter <==[stdio / DAP]==> netcoredbg
        -- OR --
AI (MCP Client) <==[TCP / JSON-RPC]==> MCP Debug Adapter <==[stdio / DAP]==> netcoredbg
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
- 🌐 **TCP Server Mode** - Connect from VS Code via `localhost:5085`
- 📝 **Debug Logging** - Comprehensive logging for diagnostics

## Prerequisites

1. **.NET SDK 8, 9 or 10** - [Download](https://dotnet.microsoft.com/download). The adapter targets
   `net8.0` and rolls forward to the newest runtime installed. Programs you debug can target .NET 6 to 10.

2. **netcoredbg** - Samsung's open source (MIT) .NET debugger
   - Download the archive for your OS from [Samsung/netcoredbg releases](https://github.com/Samsung/netcoredbg/releases)
     (`netcoredbg-win64.zip` on Windows) and extract it. Version 3.1 or newer is needed for .NET 9/10 apps.
   - Then do one of the following:
     - add the extracted `netcoredbg` folder to your `PATH`,
     - set `NETCOREDBG_PATH` to the executable (e.g. `C:\tools\netcoredbg\netcoredbg.exe`),
     - or extract it into a `netcoredbg` folder next to `McpDebugAdapter.exe`.
   - There is no `dotnet tool` package for netcoredbg.

> **Why not vsdbg (the debugger of the VS Code C# extension)?** Its license only allows it to be used
> from Visual Studio, VS Code or Xamarin Studio, and it refuses to work with other clients. netcoredbg
> speaks the same Debug Adapter Protocol and has no such restriction.

## Building

```bash
cd src/McpDebugAdapter
dotnet build
```

For a self-contained executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained
# Or for Linux: dotnet publish -c Release -r linux-x64 --self-contained
# Or for macOS: dotnet publish -c Release -r osx-x64 --self-contained
```

## Checking that it works

`tests/smoke_test.py` talks to the adapter exactly like an MCP client would: it launches
`tests/SampleApp`, hits breakpoints (including a conditional one), steps, reads variables, then
attaches to a running process.

```bash
python tests/smoke_test.py --netcoredbg C:\tools\netcoredbg\netcoredbg.exe
```

It ends with `SMOKE TEST PASSED` when everything works.

## Running Modes

### Stdio Mode (Default)

The default mode reads from stdin and writes to stdout, suitable for MCP clients that spawn the server as a subprocess.

```bash
dotnet run --project src/McpDebugAdapter/McpDebugAdapter.csproj
```

### TCP Server Mode

Run as a TCP server to allow connections from VS Code or other MCP clients over the network:

```bash
# Start with default port (5085)
dotnet run --project src/McpDebugAdapter/McpDebugAdapter.csproj -- --tcp

# Start with custom port
dotnet run --project src/McpDebugAdapter/McpDebugAdapter.csproj -- --tcp --port 5085

# Enable debug logging
dotnet run --project src/McpDebugAdapter/McpDebugAdapter.csproj -- --tcp --port 5085 --debug
```

Then connect from VS Code using: `localhost:5085`

### Command Line Arguments

| Argument | Description |
|----------|-------------|
| `--tcp` | Run as TCP server instead of stdio |
| `--port <port>` | TCP port to listen on (default: 5085) |
| `--debug` | Enable debug logging to stderr and debugger output |

## Debug Logging

When debug logging is enabled (via `--debug` flag or when running in Visual Studio debugger), the adapter outputs detailed diagnostic information to:

- **stderr** - Console output that won't interfere with MCP protocol
- **Debug Output** - Visual Studio's Debug Output window
- **Custom log writers** - For TCP client notifications

Log format:
```
[2024-01-20 10:30:45.123] [INFO] Message here
[2024-01-20 10:30:45.124] [DEBUG] Detailed debug info
[2024-01-20 10:30:45.125] [JSONRPC] RECV: {"jsonrpc":"2.0","method":"initialize",...}
[2024-01-20 10:30:45.126] [JSONRPC] SEND: {"jsonrpc":"2.0","result":{...}}
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

### VS Code with TCP Server

For TCP server mode, first start the server:

```bash
# In terminal or Visual Studio
dotnet run --project src/McpDebugAdapter/McpDebugAdapter.csproj -- --tcp --port 5085 --debug
```

Then configure VS Code to connect to the running server:

```json
{
  "mcp.servers": {
    "dotnet-debugger": {
      "url": "tcp://localhost:5085"
    }
  }
}
```

Or use it with httpL
```json
{
	"servers": {
		"dotnet-debugger": {
			"url": "http://localhost:5085",
			"type": "http"
		}
	},
	"inputs": []
}
```

**Exemple de prompt** :

> Lance `C:\...\KazoVault.UI.exe` avec dotnet-debugger, mets un breakpoint dans `KillSwitch_Activated`
> de MainWindow.xaml.cs, prends une capture d'écran, clique sur le bouton KillSwitch, puis attends que
> le breakpoint soit atteint et montre-moi les variables locales.

**Benefits of TCP/HTTP modes:**
- Run the MCP server in Visual Studio with debugger attached
- See all debug logs in Visual Studio's Output window
- Intercept and debug potential errors in the MCP server itself
- Keep the server running while restarting VS Code

## Available Tools

### `debug_launch`
Starts a new debug session for a .NET application (DLL or EXE). Breakpoints set beforehand are sent before the program starts.

**Parameters:**
- `programPath` (required): Full path to the .NET program to debug (DLL or EXE), built in Debug so its `.pdb` is next to it
- `args` (optional): Array of command-line arguments
- `cwd` (optional): Working directory (defaults to the program's folder)
- `stopAtEntry` (optional): If true, pause at program entry point
- `netcoredbgPath` (optional): Path to netcoredbg if it is not found automatically

**Example:**
```json
{
  "programPath": "C:\\path\\to\\MyApp.exe",
  "args": ["--config", "debug"]
}
```

> **Note:** The `dllPath` parameter is still supported for backwards compatibility but is deprecated. Use `programPath` instead.

### `debug_attach`
Attaches to a running .NET process.

**Parameters:**
- `processId` or `processName`: The process to attach to (see `debug_list_processes`)

### `debug_list_processes`
Lists .NET processes that can be attached to. Optional `filter` on the process name.

### `debug_stop`
Stops the current debug session and terminates the debugged program.

### `debug_set_breakpoint`
Sets a breakpoint at a specific line in a source file.

**Parameters:**
- `file` (required): Full path to the source file
- `line` (required): Line number (1-based)
- `condition` (optional): C# condition, e.g. `i == 3`

The answer says whether the breakpoint is **bound** or **pending**. Pending is normal until the
assembly containing the file is loaded; it binds by itself afterwards (see `debug_get_status`).

### `debug_remove_breakpoint`
Removes a breakpoint from a specific line.

**Parameters:**
- `file` (required): Full path to the source file
- `line` (required): Line number (1-based)

### `debug_step_next` / `debug_step_in` / `debug_step_out`
Step over, into, or out of the current function. They wait for the step to finish (`waitSeconds`, default 10) and return the new location.

### `debug_continue`
Continues execution. By default it returns immediately; pass `waitSeconds` to wait for the next stop.

### `debug_wait_for_stop`
Waits (`waitSeconds`, default 30) until a breakpoint, step or exception stops the program, or it exits, and returns where. Use it after `debug_continue` or after clicking in the UI.

### `debug_pause`
Pauses the running program.

### `debug_get_output`
Returns the latest console output of the program (`maxLines`, default 100).

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
- Stop reason and location
- Debuggee process id and exit code
- Every breakpoint with its bound/pending state

### `ui_take_screenshot`
Takes a screenshot of the debugged application's main window and returns it as a PNG image the AI can look at.
Pixel coordinates in this image are the coordinates the `ui_click` / `ui_mouse_*` tools expect.

**Note:** Requires the debugged application to have a visible window.

### `ui_get_controls`
Returns an XML representation of all UI controls in the debugged application's main window. Useful for understanding the UI structure and automating interactions.

**Parameters:**
- `maxDepth` (optional): Maximum depth to traverse the control tree (default: 10)

**Example output:**
```xml
<?xml version="1.0"?>
<Window Title="My Application" ClassName="WindowClass" ScreenX="100" ScreenY="100" Width="800" Height="600">
  <Control Type="Button" Text="Click Me" X="10" Y="10" Width="100" Height="30" IsVisible="true" IsEnabled="true"/>
  <Control Type="TextBox" Text="" X="10" Y="50" Width="200" Height="25" IsVisible="true" IsEnabled="true"/>
</Window>
```

Control positions are relative to the window (same origin as the screenshot). WPF controls are drawn
inside a single native window and have no handles of their own, so for WPF use coordinates read from
a screenshot, or the optional `McpDebugAdapter.WpfHelper` library.

### `ui_set_process_id`
Manually sets the process ID of the application to inspect for UI operations. Use this if automatic process detection fails.

**Parameters:**
- `processId` (required): The process ID (PID) of the application to inspect

### `ui_click`
Clicks on a control at specific coordinates or by handle. The window is brought to the front first.
The program must be running: while it is paused at a breakpoint its UI thread is frozen and cannot react.

**Parameters:**
- `x` (optional): X in screenshot pixels (relative to the window's top-left corner)
- `y` (optional): Y in screenshot pixels
- `screenCoordinates` (optional): If true, `x`/`y` are absolute screen pixels
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
Moves the mouse cursor (window-relative coordinates, or screen ones with `screenCoordinates: true`).

**Parameters:**
- `x` (required): X coordinate
- `y` (required): Y coordinate

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

- `initialize` - Protocol handshake (protocol versions 2024-11-05, 2025-03-26, 2025-06-18)
- `notifications/*` - Accepted and never answered, as JSON-RPC requires
- `tools/list` - List available debugging tools
- `tools/call` - Execute a debugging tool
- `ping` - Health check
- `shutdown` - Graceful shutdown

## Troubleshooting

### netcoredbg not found
The error message lists where the adapter looked. Put netcoredbg in your PATH, set `NETCOREDBG_PATH`,
or pass `netcoredbgPath` to `debug_launch`:
```bash
which netcoredbg  # Linux/macOS
where netcoredbg  # Windows
```

### Breakpoints not hitting
- Ensure the program was built with debug symbols (`Debug` configuration, `.pdb` next to the DLL)
- Use the full path of the source file the program was built from
- A breakpoint reported as **pending** binds once its assembly is loaded; check `debug_get_status`
- Run with `--debug` to see every DAP message exchanged with netcoredbg on stderr

### Clicks land in the wrong place
- Use coordinates from `ui_take_screenshot` (window-relative), or pass `screenCoordinates: true` for absolute ones
- Continue the program first: a paused program cannot process clicks

## License

MIT License - See [LICENSE](LICENSE) for details.
