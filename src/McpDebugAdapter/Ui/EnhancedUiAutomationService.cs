using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using McpDebugAdapter.Dap;

namespace McpDebugAdapter.Ui;

/// <summary>
/// Enhanced UI automation service that can use WPF Helper HTTP API when available
/// </summary>
public partial class EnhancedUiAutomationService : UiAutomationService, IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _wpfHelperAvailable;
    private int _wpfHelperPort = 8899;
    private bool _disposed = false;
    private SimpleWpfEvaluationService? _evaluationService;

    public EnhancedUiAutomationService() : base()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Sets the DAP client for expression evaluation
    /// </summary>
    public void SetDapClient(object dapClient)
    {
        if (dapClient is VsDbgClient vsDbgClient)
        {
            _evaluationService = new SimpleWpfEvaluationService(vsDbgClient);
        }
        // DapClient doesn't have EvaluateAsync, so we'll only support VsDbgClient for now
    }

    /// <summary>
    /// Checks if WPF Helper API is available
    /// </summary>
    public async Task<bool> CheckWpfHelperAsync(int port = 8899)
    {
        try
        {
            _wpfHelperPort = port;
            var response = await _httpClient.GetAsync($"http://localhost:{port}/controls");
            _wpfHelperAvailable = response.IsSuccessStatusCode;
            return _wpfHelperAvailable;
        }
        catch
        {
            _wpfHelperAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Gets UI controls using WPF Helper API if available, fallback to base implementation
    /// </summary>
    public async Task<(bool Success, string Data, string Message)> GetControlsEnhancedAsync()
    {
        // Try WPF Helper API first
        if (_wpfHelperAvailable)
        {
            try
            {
                var response = await _httpClient.GetAsync($"http://localhost:{_wpfHelperPort}/controls");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return (true, content, "Controls retrieved via WPF Helper API");
                }
            }
            catch (Exception)
            {
                // Fallback to base implementation
            }
        }

        return await GetControlsAsXmlAsync();
    }

    /// <summary>
    /// Finds controls by name using WPF Helper API
    /// </summary>
    public async Task<(bool Success, string Data, string Message)> FindControlsByNameAsync(string name)
    {
        // First try DAP expression evaluation
        if (_evaluationService != null)
        {
            try
            {
                var result = await _evaluationService.GetControlBoundsByNameAsync(name);
                if (result.Success)
                {
                    // Wrap single control in controls array format
                    var wrappedData = $"{{\"controls\": [{result.Data}]}}";
                    return (true, wrappedData, result.Message);
                }
            }
            catch (Exception)
            {
                // Continue to fallback methods
            }
        }

        // Then try WPF Helper API
        if (_wpfHelperAvailable)
        {
            try
            {
                var encodedName = Uri.EscapeDataString(name);
                var response = await _httpClient.GetAsync($"http://localhost:{_wpfHelperPort}/controls/by-name?name={encodedName}");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return (true, content, $"Controls with name '{name}' found via WPF Helper API");
                }
            }
            catch (Exception ex)
            {
                return (false, "", $"Error finding controls by name: {ex.Message}");
            }
        }

        return (false, "", "No enhanced control discovery method available");
    }

    /// <summary>
    /// Finds controls by text using WPF Helper API
    /// </summary>
    public async Task<(bool Success, string Data, string Message)> FindControlsByTextAsync(string text)
    {
        // Try WPF Helper API
        if (_wpfHelperAvailable)
        {
            try
            {
                var encodedText = Uri.EscapeDataString(text);
                var response = await _httpClient.GetAsync($"http://localhost:{_wpfHelperPort}/controls/by-text?text={encodedText}");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return (true, content, $"Controls with text '{text}' found via WPF Helper API");
                }
            }
            catch (Exception ex)
            {
                return (false, "", $"Error finding controls by text: {ex.Message}");
            }
        }

        return (false, "", "No enhanced control discovery method available");
    }

    /// <summary>
    /// Highlights a control using WPF Helper API
    /// </summary>
    public async Task<(bool Success, string Message)> HighlightControlAsync(string name, bool highlight = true)
    {
        // First try DAP expression evaluation
        if (_evaluationService != null)
        {
            try
            {
                var result = await _evaluationService.HighlightControlAsync(name, highlight);
                if (result.Success)
                {
                    return result;
                }
            }
            catch (Exception)
            {
                // Continue to fallback methods
            }
        }

        // Then try WPF Helper API
        if (_wpfHelperAvailable)
        {
            try
            {
                var encodedName = Uri.EscapeDataString(name);
                var response = await _httpClient.PostAsync(
                    $"http://localhost:{_wpfHelperPort}/highlight?name={encodedName}&highlight={highlight.ToString().ToLower()}", 
                    null);
                
                if (response.IsSuccessStatusCode)
                {
                    return (true, $"Control '{name}' highlight {(highlight ? "enabled" : "disabled")}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Error highlighting control: {ex.Message}");
            }
        }

        return (false, "No enhanced control highlighting method available");
    }

    /// <summary>
    /// Clicks using precise coordinates from WPF Helper API
    /// </summary>
    public async Task<(bool Success, string Message)> ClickControlByNameAsync(string name, string button = "left", int clickCount = 1)
    {
        // First try DAP expression evaluation to get precise coordinates
        if (_evaluationService != null)
        {
            try
            {
                var (success, data, message) = await _evaluationService.GetControlBoundsByNameAsync(name);
                if (success)
                {
                    var controlData = JsonSerializer.Deserialize<JsonDocument>(data);
                    if (controlData?.RootElement.TryGetProperty("centerX", out var centerXProp) == true && 
                        controlData.RootElement.TryGetProperty("centerY", out var centerYProp))
                    {
                        var x = (int)centerXProp.GetDouble();
                        var y = (int)centerYProp.GetDouble();
                        
                        return await ClickAtAsync(x, y, button, clickCount);
                    }
                }
            }
            catch (Exception)
            {
                // Continue to fallback methods
            }
        }

        // Then try WPF Helper API
        if (_wpfHelperAvailable)
        {
            try
            {
                // First, find the control to get precise coordinates
                var (success, data, message) = await FindControlsByNameAsync(name);
                if (success)
                {
                    var controlData = JsonSerializer.Deserialize<JsonDocument>(data);
                    var controls = controlData?.RootElement.GetProperty("controls").EnumerateArray();
                    
                    if (controls != null)
                    {
                        foreach (var control in controls)
                        {
                            if (control.TryGetProperty("center", out var centerProp))
                            {
                                var x = (int)centerProp.GetProperty("x").GetDouble();
                                var y = (int)centerProp.GetProperty("y").GetDouble();
                                
                                // Use the enhanced click with precise coordinates
                                return await ClickAtAsync(x, y, button, clickCount);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, $"Error clicking control by name: {ex.Message}");
            }
        }

        return (false, "Enhanced control discovery not available or control not found");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _httpClient?.Dispose();
            _disposed = true;
        }
    }
}