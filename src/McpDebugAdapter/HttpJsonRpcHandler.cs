using System.Text;

namespace McpDebugAdapter;

/// <summary>
/// Handles HTTP wrapper for JSON-RPC communication.
/// VS Code sends JSON-RPC messages wrapped in HTTP POST requests.
/// </summary>
public class HttpJsonRpcHandler
{
    /// <summary>
    /// Processes HTTP request and extracts the JSON-RPC message.
    /// </summary>
    /// <param name="reader">StreamReader for reading HTTP request</param>
    /// <returns>The JSON-RPC message from the HTTP body, or null if invalid</returns>
    public static async Task<string?> ReadHttpRequestAsync(StreamReader reader)
    {
        try
        {
            string? requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine) || !requestLine.StartsWith("POST"))
            {
                return null;
            }

            // Read headers until we find an empty line
            var headers = new Dictionary<string, string>();
            string? line;
            int contentLength = 0;
            
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrEmpty(line))
                {
                    // Empty line indicates end of headers
                    break;
                }
                
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    var headerName = line[..colonIndex].Trim().ToLowerInvariant();
                    var headerValue = line[(colonIndex + 1)..].Trim();
                    headers[headerName] = headerValue;
                    
                    if (headerName == "content-length")
                    {
                        int.TryParse(headerValue, out contentLength);
                    }
                }
            }

            // Read the body if there's content
            if (contentLength > 0)
            {
                var buffer = new char[contentLength];
                var totalRead = 0;
                
                while (totalRead < contentLength)
                {
                    var read = await reader.ReadAsync(buffer, totalRead, contentLength - totalRead);
                    if (read == 0)
                    {
                        // Connection closed before full content read
                        return null;
                    }
                    totalRead += read;
                }
                
                return new string(buffer);
            }
            
            return null;
        }
        catch (Exception ex)
        {
            DebugLogger.LogError("Error reading HTTP request", ex);
            return null;
        }
    }

    /// <summary>
    /// Sends JSON-RPC response wrapped in HTTP response.
    /// </summary>
    /// <param name="writer">StreamWriter for sending HTTP response</param>
    /// <param name="jsonRpcResponse">The JSON-RPC response to send</param>
    public static async Task SendHttpResponseAsync(StreamWriter writer, string jsonRpcResponse)
    {
        try
        {
            var content = Encoding.UTF8.GetBytes(jsonRpcResponse);
            
            await writer.WriteAsync("HTTP/1.1 200 OK\r\n");
            await writer.WriteAsync("Content-Type: application/json\r\n");
            await writer.WriteAsync("Access-Control-Allow-Origin: *\r\n");
            await writer.WriteAsync("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n");
            await writer.WriteAsync("Access-Control-Allow-Headers: Content-Type, Authorization\r\n");
            await writer.WriteAsync($"Content-Length: {content.Length}\r\n");
            await writer.WriteAsync("Connection: keep-alive\r\n");
            await writer.WriteAsync("\r\n");
            
            // Write the JSON content
            await writer.WriteAsync(jsonRpcResponse);
            await writer.FlushAsync();
        }
        catch (Exception ex)
        {
            DebugLogger.LogError("Error sending HTTP response", ex);
            throw;
        }
    }

    /// <summary>
    /// Handles HTTP OPTIONS request for CORS.
    /// </summary>
    /// <param name="writer">StreamWriter for sending HTTP response</param>
    public static async Task SendOptionsResponseAsync(StreamWriter writer)
    {
        try
        {
            await writer.WriteAsync("HTTP/1.1 200 OK\r\n");
            await writer.WriteAsync("Access-Control-Allow-Origin: *\r\n");
            await writer.WriteAsync("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n");
            await writer.WriteAsync("Access-Control-Allow-Headers: Content-Type, Authorization\r\n");
            await writer.WriteAsync("Content-Length: 0\r\n");
            await writer.WriteAsync("Connection: keep-alive\r\n");
            await writer.WriteAsync("\r\n");
            await writer.FlushAsync();
        }
        catch (Exception ex)
        {
            DebugLogger.LogError("Error sending OPTIONS response", ex);
            throw;
        }
    }
}