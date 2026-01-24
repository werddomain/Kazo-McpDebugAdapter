using System.Net;
using System.Net.Sockets;

namespace McpDebugAdapter;

/// <summary>
/// TCP server that allows MCP clients to connect over a network socket.
/// This enables connections from VS Code via URL like 'localhost:5085'.
/// </summary>
public class TcpMcpServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly EnhancedDebugSession _session;
    private TcpListener? _listener;
    private readonly List<TcpClient> _clients = [];
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new TCP MCP server.
    /// </summary>
    /// <param name="port">The port to listen on (e.g., 5085).</param>
    /// <param name="session">The debug session to use.</param>
    public TcpMcpServer(int port, EnhancedDebugSession session)
    {
        _port = port;
        _session = session;
    }

    /// <summary>
    /// Starts the TCP server and listens for incoming connections.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        DebugLogger.Log($"TCP MCP Server listening on port {_port}");
        DebugLogger.Log($"Connect from VS Code using: localhost:{_port}");
        
        // Also write to stdout for visibility when running from command line
        Console.WriteLine($"TCP MCP Server listening on port {_port}");
        Console.WriteLine($"Connect from VS Code using: localhost:{_port}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    DebugLogger.Log($"Client connected from {client.Client.RemoteEndPoint}");
                    
                    lock (_lock)
                    {
                        _clients.Add(client);
                    }

                    // Handle client in background
                    _ = HandleClientAsync(client, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DebugLogger.LogError("Error accepting TCP client", ex);
                }
            }
        }
        finally
        {
            _listener.Stop();
            DebugLogger.Log("TCP MCP Server stopped");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };

            DebugLogger.Log($"Starting MCP session for client {endpoint}");

            // Create an MCP server instance for this client with HTTP wrapper enabled
            var mcpServer = new McpServer(_session, writer, reader, useHttpWrapper: true);
            
            // Run the MCP server for this client
            await mcpServer.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            DebugLogger.LogDebug($"Client {endpoint} session cancelled");
        }
        catch (IOException ex)
        {
            DebugLogger.LogDebug($"Client {endpoint} disconnected: {ex.Message}");
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"Error handling client {endpoint}", ex);
        }
        finally
        {
            lock (_lock)
            {
                _clients.Remove(client);
            }

            try
            {
                client.Close();
            }
            catch
            {
                // Ignore close errors
            }

            DebugLogger.Log($"Client {endpoint} disconnected");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Stop();

        List<TcpClient> clientsCopy;
        lock (_lock)
        {
            clientsCopy = [.. _clients];
            _clients.Clear();
        }

        foreach (var client in clientsCopy)
        {
            try
            {
                client.Close();
            }
            catch
            {
                // Ignore close errors
            }
        }

        await Task.CompletedTask;
    }
}
