// MCP Debug Adapter for .NET Applications
// =========================================
// This application is an MCP (Model Context Protocol) server that allows AI assistants
// to debug .NET applications by controlling netcoredbg via the DAP protocol.
//
// Architecture:
// AI (MCP Client) <==[Stdio / JSON-RPC]==> This App (MCP Server) <==[TCP / DAP]==> netcoredbg
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
// PREREQUISITES
// =============
// - .NET 9 SDK installed
// - netcoredbg installed and available in PATH
//   (Install via: dotnet tool install -g netcoredbg or from https://github.com/Samsung/netcoredbg)
//
// AVAILABLE TOOLS
// ===============
// - debug_launch(dllPath, args?, stopAtEntry?) - Start debugging a .NET DLL
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

// Create the debug session (state management)
var session = new DebugSession();

// Create and start the MCP server
var server = new McpServer(session);

// Setup cancellation for graceful shutdown
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Handle termination signals
AppDomain.CurrentDomain.ProcessExit += (s, e) =>
{
    cts.Cancel();
};

try
{
    // Run the MCP server (reads from stdin, writes to stdout)
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Normal shutdown
}
finally
{
    // Cleanup: stop any active debug session
    if (session.IsActive)
    {
        await session.StopAsync();
    }
}
