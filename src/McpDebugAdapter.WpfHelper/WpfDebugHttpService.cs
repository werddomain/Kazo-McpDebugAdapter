using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace McpDebugAdapter.WpfHelper;

/// <summary>
/// HTTP service to expose WPF debug helper functionality
/// </summary>
public class WpfDebugHttpService : IDisposable
{
    private readonly IWpfDebugHelper _debugHelper;
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenerTask;
    private readonly int _port;

    public bool IsRunning { get; private set; }

    public WpfDebugHttpService(IWpfDebugHelper debugHelper, int port = 8899)
    {
        _debugHelper = debugHelper ?? throw new ArgumentNullException(nameof(debugHelper));
        _port = port;
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://localhost:{_port}/");
        
        try
        {
            _httpListener.Start();
            IsRunning = true;

            _cancellationTokenSource = new CancellationTokenSource();
            _listenerTask = Task.Run(() => ListenLoop(_cancellationTokenSource.Token));
            
            await Task.Delay(100); // Give time to start
        }
        catch (Exception)
        {
            IsRunning = false;
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        _cancellationTokenSource?.Cancel();
        _httpListener?.Stop();
        
        if (_listenerTask != null)
        {
            await _listenerTask;
        }
        
        IsRunning = false;
    }

    private async Task ListenLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _httpListener?.IsListening == true)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context), cancellationToken);
            }
            catch (HttpListenerException)
            {
                // Expected when stopping
                break;
            }
            catch (ObjectDisposedException)
            {
                // Expected when stopping
                break;
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            var path = request.Url?.AbsolutePath ?? "";
            var result = path switch
            {
                "/controls" => GetAllControls(),
                "/controls/by-name" => FindControlsByName(request),
                "/controls/by-type" => FindControlsByType(request),
                "/controls/by-text" => FindControlsByText(request),
                "/controls/by-automation-id" => FindControlsByAutomationId(request),
                "/controls/at-point" => GetControlAt(request),
                "/highlight" => HighlightControl(request),
                "/refresh" => RefreshControls(),
                _ => JsonSerializer.Serialize(new { error = "Endpoint not found" })
            };

            var buffer = Encoding.UTF8.GetBytes(result);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }
        catch (Exception ex)
        {
            try
            {
                var errorResult = JsonSerializer.Serialize(new { error = ex.Message });
                var buffer = Encoding.UTF8.GetBytes(errorResult);
                context.Response.ContentType = "application/json";
                context.Response.StatusCode = 500;
                await context.Response.OutputStream.WriteAsync(buffer);
                context.Response.Close();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private string GetAllControls()
    {
        var controls = _debugHelper.GetAllControls().ToList();
        return JsonSerializer.Serialize(new { controls });
    }

    private string FindControlsByName(HttpListenerRequest request)
    {
        var name = GetQueryParameter(request, "name");
        if (string.IsNullOrEmpty(name))
            return JsonSerializer.Serialize(new { error = "Name parameter required" });

        var controls = _debugHelper.FindControlsByName(name).ToList();
        return JsonSerializer.Serialize(new { controls });
    }

    private string FindControlsByType(HttpListenerRequest request)
    {
        var type = GetQueryParameter(request, "type");
        if (string.IsNullOrEmpty(type))
            return JsonSerializer.Serialize(new { error = "Type parameter required" });

        var controls = _debugHelper.FindControlsByType(type).ToList();
        return JsonSerializer.Serialize(new { controls });
    }

    private string FindControlsByText(HttpListenerRequest request)
    {
        var text = GetQueryParameter(request, "text");
        if (string.IsNullOrEmpty(text))
            return JsonSerializer.Serialize(new { error = "Text parameter required" });

        var controls = _debugHelper.FindControlsByText(text).ToList();
        return JsonSerializer.Serialize(new { controls });
    }

    private string FindControlsByAutomationId(HttpListenerRequest request)
    {
        var automationId = GetQueryParameter(request, "automationId");
        if (string.IsNullOrEmpty(automationId))
            return JsonSerializer.Serialize(new { error = "AutomationId parameter required" });

        var controls = _debugHelper.FindControlsByAutomationId(automationId).ToList();
        return JsonSerializer.Serialize(new { controls });
    }

    private string GetControlAt(HttpListenerRequest request)
    {
        var xStr = GetQueryParameter(request, "x");
        var yStr = GetQueryParameter(request, "y");
        
        if (!double.TryParse(xStr, out var x) || !double.TryParse(yStr, out var y))
            return JsonSerializer.Serialize(new { error = "X and Y parameters required" });

        var control = _debugHelper.GetControlAt(new Point(x, y));
        return JsonSerializer.Serialize(new { control });
    }

    private string HighlightControl(HttpListenerRequest request)
    {
        var name = GetQueryParameter(request, "name");
        var highlightStr = GetQueryParameter(request, "highlight") ?? "true";
        
        if (string.IsNullOrEmpty(name))
            return JsonSerializer.Serialize(new { error = "Name parameter required" });

        bool.TryParse(highlightStr, out var highlight);
        
        Application.Current.Dispatcher.Invoke(() =>
        {
            _debugHelper.HighlightControl(name, highlight);
        });
        
        return JsonSerializer.Serialize(new { success = true });
    }

    private string RefreshControls()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _debugHelper.RefreshControls();
        });
        
        return JsonSerializer.Serialize(new { success = true });
    }

    private static string? GetQueryParameter(HttpListenerRequest request, string parameterName)
    {
        return request.QueryString[parameterName];
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _httpListener?.Close();
        _cancellationTokenSource?.Dispose();
        _listenerTask?.Dispose();
    }
}