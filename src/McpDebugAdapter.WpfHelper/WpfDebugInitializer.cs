using System;
using System.Threading.Tasks;
using System.Windows;

namespace McpDebugAdapter.WpfHelper;

/// <summary>
/// Static helper to easily initialize WPF debugging support
/// </summary>
public static class WpfDebugInitializer
{
    private static WpfDebugHttpService? _httpService;
    private static WpfDebugHelper? _debugHelper;

    /// <summary>
    /// Initializes WPF debugging support for the current application
    /// </summary>
    /// <param name="port">Port for the HTTP service (default: 8899)</param>
    /// <returns>True if initialized successfully</returns>
    public static async Task<bool> InitializeAsync(int port = 8899)
    {
        try
        {
            if (Application.Current == null)
                return false;

            _debugHelper = new WpfDebugHelper(Application.Current);
            _httpService = new WpfDebugHttpService(_debugHelper, port);
            
            await _httpService.StartAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Initializes WPF debugging support for the current application (synchronous)
    /// </summary>
    /// <param name="port">Port for the HTTP service (default: 8899)</param>
    /// <returns>True if initialized successfully</returns>
    public static bool Initialize(int port = 8899)
    {
        return InitializeAsync(port).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Shuts down the debugging support
    /// </summary>
    public static async Task ShutdownAsync()
    {
        if (_httpService != null)
        {
            await _httpService.StopAsync();
            _httpService.Dispose();
            _httpService = null;
        }
        _debugHelper = null;
    }

    /// <summary>
    /// Shuts down the debugging support (synchronous)
    /// </summary>
    public static void Shutdown()
    {
        ShutdownAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the current debug helper instance
    /// </summary>
    public static IWpfDebugHelper? GetDebugHelper() => _debugHelper;

    /// <summary>
    /// Checks if debugging support is running
    /// </summary>
    public static bool IsRunning => _httpService?.IsRunning == true;
}