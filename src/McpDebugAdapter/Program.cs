// MCP Debug Adapter for .NET Applications
// =========================================
// This application is an MCP (Model Context Protocol) server that allows AI assistants
// to debug .NET applications by controlling netcoredbg via the DAP protocol.
//
// Architecture:
// AI (MCP Client) <==[Stdio / JSON-RPC]==> This App (MCP Server) <==[TCP / DAP]==> netcoredbg
//          -- OR --
// AI (MCP Client) <==[TCP / JSON-RPC]==> This App (MCP Server) <==[TCP / DAP]==> netcoredbg
//
// CONFIGURATION
// =============
//
// For Claude Desktop (claude_desktop_config.json):
// {
//   "mcpServers": {
//     "dotnet-debugger": {
//       "command": "dotnet",
//       "args": ["run", "--project", "/path/to/McpDebugAdapter.csproj"]
//     }
//   }
// }
//
// Or with a published executable:
// {
//   "mcpServers": {
//     "dotnet-debugger": {
//       "command": "/path/to/McpDebugAdapter"
//     }
//   }
// }
//
// For VS Code Extension (settings.json or extension config):
// {
//   "mcp.servers": {
//     "dotnet-debugger": {
//       "command": "dotnet",
//       "args": ["run", "--project", "/path/to/McpDebugAdapter.csproj"]
//     }
//   }
// }
//
// TCP SERVER MODE
// ===============
// Run as a TCP server to allow connections from VS Code or other MCP clients:
//   dotnet run --project McpDebugAdapter.csproj -- --tcp --port 5085
// 
// Then connect from VS Code using: localhost:5085
//
// PREREQUISITES
// =============
// - .NET 8 runtime or newer to run the adapter (it targets net8.0 and rolls forward)
// - netcoredbg (Samsung, MIT): https://github.com/Samsung/netcoredbg/releases
//   Put it in PATH, set NETCOREDBG_PATH, or extract it into a "netcoredbg" folder next to this executable.
//
// AVAILABLE TOOLS
// ===============
// - debug_launch(programPath, args?, cwd?, stopAtEntry?, netcoredbgPath?) - Start debugging a .NET program (DLL or EXE)
// - debug_attach(processId?, processName?) - Attach to an existing .NET process
// - debug_list_processes(filter?) - List .NET processes that can be attached to
// - debug_stop() - Stop the debug session
// - debug_set_breakpoint(file, line, condition?) / debug_remove_breakpoint(file, line)
// - debug_continue(waitSeconds?) / debug_pause() / debug_wait_for_stop(waitSeconds?)
// - debug_step_next() / debug_step_in() / debug_step_out()
// - debug_evaluate(expression) / debug_get_stack() / debug_get_variables()
// - debug_get_status() / debug_get_output(maxLines?)
// - ui_* tools - screenshot, control tree, click, type, keys, mouse (Windows)

using McpDebugAdapter;

// Parse command line arguments
var useTcp = args.Contains("--tcp");
var port = 5085; // Default port
var debugLogging = args.Contains("--debug");

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length)
    {
        if (int.TryParse(args[i + 1], out var parsedPort))
        {
            port = parsedPort;
        }
    }
}

// Enable debug logging when:
// 1. --debug flag is explicitly passed, OR
// 2. Running under a debugger (e.g., Visual Studio F5 debugging)
// This allows users to get diagnostics when needed without affecting normal operation
DebugLogger.Enabled = debugLogging || System.Diagnostics.Debugger.IsAttached;

DebugLogger.Log("=== MCP Debug Adapter Starting ===");
DebugLogger.Log($"Mode: {(useTcp ? "TCP" : "Stdio")}");
if (useTcp)
{
    DebugLogger.Log($"Port: {port}");
}
DebugLogger.Log($"Debug Logging: {DebugLogger.Enabled}");
DebugLogger.Log($"Process ID: {Environment.ProcessId}");

// Coordinates from screenshots and window rectangles must be real pixels, not DPI-virtualized ones
if (OperatingSystem.IsWindows())
{
    NativeDpi.EnablePerMonitorDpiAwareness();
}

var session = new DebugSession();

// Setup cancellation for graceful shutdown
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    DebugLogger.Log("Received Ctrl+C, shutting down...");
    e.Cancel = true;
    cts.Cancel();
};

// Handle termination signals
AppDomain.CurrentDomain.ProcessExit += (s, e) =>
{
    DebugLogger.Log("Process exit signal received");
    try
    {
        if (!cts.IsCancellationRequested)
        {
            cts.Cancel();
        }
    }
    catch (ObjectDisposedException)
    {
        // Already disposed, ignore
    }
};

try
{
    if (useTcp)
    {
        // Run as TCP server
        DebugLogger.Log($"Starting TCP MCP Server on port {port}...");
        await using var tcpServer = new TcpMcpServer(port, session);
        await tcpServer.RunAsync(cts.Token);
    }
    else
    {
        // Run as stdio server (default mode)
        DebugLogger.Log("Starting Stdio MCP Server...");
        var server = new McpServer(session);
        await server.RunAsync(cts.Token);
    }
}
catch (OperationCanceledException)
{
    DebugLogger.Log("Server shutdown requested");
}
catch (Exception ex)
{
    DebugLogger.LogError("Fatal error", ex);
    throw;
}
finally
{
    // Cleanup: stop any active debug session
    if (session.IsActive)
    {
        DebugLogger.Log("Stopping active debug session...");
        await session.StopAsync();
    }
    DebugLogger.Log("=== MCP Debug Adapter Stopped ===");
}

internal static class NativeDpi
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        }
        catch (Exception ex)
        {
            DebugLogger.LogDebug($"Could not enable DPI awareness: {ex.Message}");
        }
    }
}
