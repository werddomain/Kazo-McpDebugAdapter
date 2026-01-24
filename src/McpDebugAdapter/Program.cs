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
// - .NET 9 SDK installed
// - netcoredbg installed and available in PATH
//   (Install via: dotnet tool install -g netcoredbg or from https://github.com/Samsung/netcoredbg)
//
// AVAILABLE TOOLS
// ===============
// - debug_launch(programPath, args?, stopAtEntry?) - Start debugging a .NET program (DLL or EXE)
// - debug_stop() - Stop the debug session
// - debug_set_breakpoint(file, line) - Set a breakpoint
// - debug_remove_breakpoint(file, line) - Remove a breakpoint
// - debug_step_next() - Step over (next line)
// - debug_step_in() - Step into function
// - debug_continue() - Continue execution
// - debug_evaluate(expression) - Evaluate an expression
// - debug_get_stack() - Get current stack trace
// - debug_get_variables() - Get local variables
// - debug_get_status() - Get debug session status

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

// Enable debug logging based on command line or when running in debugger
DebugLogger.Enabled = debugLogging || System.Diagnostics.Debugger.IsAttached;

DebugLogger.Log("=== MCP Debug Adapter Starting ===");
DebugLogger.Log($"Mode: {(useTcp ? "TCP" : "Stdio")}");
if (useTcp)
{
    DebugLogger.Log($"Port: {port}");
}
DebugLogger.Log($"Debug Logging: {DebugLogger.Enabled}");
DebugLogger.Log($"Process ID: {Environment.ProcessId}");

// Create the debug session (state management)
var session = new DebugSession();
DebugLogger.LogDebug("Debug session created");

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
