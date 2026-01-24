using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace McpDebugAdapter.Ui;

/// <summary>
/// Provides UI automation capabilities for taking screenshots and inspecting UI controls.
/// Works by targeting the debugged application's process.
/// </summary>
public partial class UiAutomationService
{
    private Process? _targetProcess;

    // Regex to validate window IDs (typically numeric for X11)
    [GeneratedRegex(@"^\d+$")]
    private static partial Regex WindowIdRegex();

    /// <summary>
    /// Validates that a string contains only digits (valid window/process ID).
    /// </summary>
    private static bool IsValidNumericId(string id)
    {
        return !string.IsNullOrEmpty(id) && WindowIdRegex().IsMatch(id);
    }

    /// <summary>
    /// Validates that a process ID is positive.
    /// </summary>
    private static bool IsValidProcessId(int processId)
    {
        return processId > 0;
    }

    /// <summary>
    /// Sets the target process for UI automation operations.
    /// </summary>
    public void SetTargetProcess(Process? process)
    {
        _targetProcess = process;
    }

    /// <summary>
    /// Sets the target process by process ID.
    /// </summary>
    public bool SetTargetProcessById(int processId)
    {
        try
        {
            _targetProcess = Process.GetProcessById(processId);
            return true;
        }
        catch
        {
            _targetProcess = null;
            return false;
        }
    }

    /// <summary>
    /// Takes a screenshot of the target application's main window.
    /// Returns the screenshot as a base64-encoded BMP image.
    /// </summary>
    public async Task<(bool Success, string Data, string Message)> TakeScreenshotAsync()
    {
        if (_targetProcess == null)
        {
            return (false, string.Empty, "No target process set. Launch a debug session first.");
        }

        try
        {
            _targetProcess.Refresh();
            if (_targetProcess.HasExited)
            {
                return (false, string.Empty, "Target process has exited.");
            }

            var mainWindowHandle = _targetProcess.MainWindowHandle;
            if (mainWindowHandle == IntPtr.Zero)
            {
                return (false, string.Empty, "Target process has no visible main window.");
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var result = await CaptureWindowScreenshotWindowsAsync(mainWindowHandle);
                return result;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var result = await CaptureScreenshotLinuxAsync();
                return result;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var result = await CaptureScreenshotMacOSAsync();
                return result;
            }

            return (false, string.Empty, "Screenshot capture is not supported on this platform.");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"Failed to capture screenshot: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the UI control tree of the target application as XML.
    /// </summary>
    public async Task<(bool Success, string Xml, string Message)> GetControlsAsXmlAsync(int maxDepth = 10)
    {
        if (_targetProcess == null)
        {
            return (false, string.Empty, "No target process set. Launch a debug session first.");
        }

        try
        {
            _targetProcess.Refresh();
            if (_targetProcess.HasExited)
            {
                return (false, string.Empty, "Target process has exited.");
            }

            var mainWindowHandle = _targetProcess.MainWindowHandle;
            if (mainWindowHandle == IntPtr.Zero)
            {
                return (false, string.Empty, "Target process has no visible main window.");
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var result = await GetControlsWindowsAsync(mainWindowHandle, maxDepth);
                return result;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var result = await GetControlsLinuxAsync(maxDepth);
                return result;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var result = await GetControlsMacOSAsync(maxDepth);
                return result;
            }

            return (false, string.Empty, "UI control inspection is not supported on this platform.");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"Failed to get UI controls: {ex.Message}");
        }
    }

    #region Windows Implementation

    private async Task<(bool Success, string Data, string Message)> CaptureWindowScreenshotWindowsAsync(IntPtr hwnd)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Get window rectangle
                if (!GetWindowRect(hwnd, out RECT rect))
                {
                    return (false, string.Empty, "Failed to get window rectangle.");
                }

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (width <= 0 || height <= 0)
                {
                    return (false, string.Empty, "Window has invalid dimensions.");
                }

                // Create device contexts
                IntPtr hdcScreen = GetDC(IntPtr.Zero);
                IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
                IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
                IntPtr hOld = SelectObject(hdcMem, hBitmap);

                // Copy window content
                BitBlt(hdcMem, 0, 0, width, height, hdcScreen, rect.Left, rect.Top, SRCCOPY);

                // Create bitmap from handle
                SelectObject(hdcMem, hOld);

                // Convert to PNG and base64
                var bitmapData = GetBitmapData(hBitmap, width, height);

                // Cleanup
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
                ReleaseDC(IntPtr.Zero, hdcScreen);

                if (bitmapData == null)
                {
                    return (false, string.Empty, "Failed to create bitmap data.");
                }

                var base64 = Convert.ToBase64String(bitmapData);
                return (true, base64, $"Screenshot captured ({width}x{height})");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, $"Windows screenshot failed: {ex.Message}");
            }
        });
    }

    private static byte[]? GetBitmapData(IntPtr hBitmap, int width, int height)
    {
        // Get bitmap info
        BITMAPINFO bmi = new()
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // Top-down bitmap
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        // Allocate buffer for pixel data
        int stride = ((width * 32 + 31) / 32) * 4;
        byte[] pixels = new byte[stride * height];

        IntPtr hdc = GetDC(IntPtr.Zero);
        GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref bmi, 0);
        ReleaseDC(IntPtr.Zero, hdc);

        // Create a simple BMP file in memory
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // BMP Header
        writer.Write((ushort)0x4D42); // 'BM'
        writer.Write(54 + pixels.Length); // File size
        writer.Write(0); // Reserved
        writer.Write(54); // Pixel data offset

        // DIB Header (BITMAPINFOHEADER)
        writer.Write(40); // Header size
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1); // Planes
        writer.Write((ushort)32); // Bits per pixel
        writer.Write(0); // Compression (BI_RGB)
        writer.Write(pixels.Length); // Image size
        writer.Write(0); // X pixels per meter
        writer.Write(0); // Y pixels per meter
        writer.Write(0); // Colors used
        writer.Write(0); // Important colors

        // Flip the image vertically (BMP is bottom-up)
        for (int y = height - 1; y >= 0; y--)
        {
            writer.Write(pixels, y * stride, stride);
        }

        return ms.ToArray();
    }

    private async Task<(bool Success, string Xml, string Message)> GetControlsWindowsAsync(IntPtr hwnd, int maxDepth)
    {
        return await Task.Run(() =>
        {
            try
            {
                var sb = new StringBuilder();
                using var writer = XmlWriter.Create(sb, new XmlWriterSettings
                {
                    Indent = true,
                    OmitXmlDeclaration = false,
                    Encoding = Encoding.UTF8
                });

                writer.WriteStartDocument();
                writer.WriteStartElement("Window");

                // Get window info
                var windowText = GetWindowText(hwnd);
                var className = GetWindowClassName(hwnd);

                writer.WriteAttributeString("Title", windowText);
                writer.WriteAttributeString("ClassName", className);
                writer.WriteAttributeString("Handle", hwnd.ToString());

                if (GetWindowRect(hwnd, out RECT rect))
                {
                    writer.WriteAttributeString("X", rect.Left.ToString());
                    writer.WriteAttributeString("Y", rect.Top.ToString());
                    writer.WriteAttributeString("Width", (rect.Right - rect.Left).ToString());
                    writer.WriteAttributeString("Height", (rect.Bottom - rect.Top).ToString());
                }

                // Enumerate child windows
                EnumerateChildWindows(hwnd, writer, 1, maxDepth);

                writer.WriteEndElement(); // Window
                writer.WriteEndDocument();
                writer.Flush();

                return (true, sb.ToString(), "UI controls retrieved successfully.");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, $"Failed to enumerate Windows controls: {ex.Message}");
            }
        });
    }

    private static void EnumerateChildWindows(IntPtr parentHwnd, XmlWriter writer, int currentDepth, int maxDepth)
    {
        if (currentDepth >= maxDepth)
        {
            return;
        }

        EnumChildWindows(parentHwnd, (hwnd, lParam) =>
        {
            // Only process direct children
            if (GetParent(hwnd) != parentHwnd)
            {
                return true;
            }

            writer.WriteStartElement("Control");

            var text = GetWindowText(hwnd);
            var className = GetWindowClassName(hwnd);
            var controlType = MapClassNameToControlType(className);

            writer.WriteAttributeString("Type", controlType);
            writer.WriteAttributeString("ClassName", className);

            if (!string.IsNullOrEmpty(text))
            {
                writer.WriteAttributeString("Text", text);
            }

            writer.WriteAttributeString("Handle", hwnd.ToString());

            if (GetWindowRect(hwnd, out RECT rect))
            {
                writer.WriteAttributeString("X", rect.Left.ToString());
                writer.WriteAttributeString("Y", rect.Top.ToString());
                writer.WriteAttributeString("Width", (rect.Right - rect.Left).ToString());
                writer.WriteAttributeString("Height", (rect.Bottom - rect.Top).ToString());
            }

            bool isVisible = IsWindowVisible(hwnd);
            bool isEnabled = IsWindowEnabled(hwnd);
            writer.WriteAttributeString("IsVisible", isVisible.ToString().ToLower());
            writer.WriteAttributeString("IsEnabled", isEnabled.ToString().ToLower());

            // Recurse into children
            EnumerateChildWindows(hwnd, writer, currentDepth + 1, maxDepth);

            writer.WriteEndElement(); // Control
            return true;
        }, IntPtr.Zero);
    }

    private static string MapClassNameToControlType(string className)
    {
        return className.ToUpperInvariant() switch
        {
            "BUTTON" => "Button",
            "EDIT" => "TextBox",
            "STATIC" => "Label",
            "LISTBOX" => "ListBox",
            "COMBOBOX" => "ComboBox",
            "SCROLLBAR" => "ScrollBar",
            "MSCTLS_TRACKBAR32" => "Slider",
            "MSCTLS_PROGRESS32" => "ProgressBar",
            "SYSTREEVIEW32" => "TreeView",
            "SYSLISTVIEW32" => "ListView",
            "SYSTABCONTROL32" => "TabControl",
            "MSCTLS_STATUSBAR32" => "StatusBar",
            "TOOLTIPS_CLASS32" => "ToolTip",
            "RICHEDIT20W" or "RICHEDIT20A" or "RICHEDIT50W" => "RichTextBox",
            "#32770" => "Dialog",
            "MDICLIENT" => "MdiClient",
            _ when className.StartsWith("WINDOWS.UI", StringComparison.OrdinalIgnoreCase) => "ModernControl",
            _ when className.Contains("SCROLL", StringComparison.OrdinalIgnoreCase) => "ScrollBar",
            _ => "Custom"
        };
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    #endregion

    #region Linux Implementation

    private async Task<(bool Success, string Data, string Message)> CaptureScreenshotLinuxAsync()
    {
        try
        {
            // Use gnome-screenshot or scrot for Linux
            var tempFile = Path.Combine(Path.GetTempPath(), $"screenshot_{Guid.NewGuid()}.png");

            // Try xdotool + import (ImageMagick)
            var windowId = await GetLinuxWindowIdAsync();
            if (string.IsNullOrEmpty(windowId))
            {
                return (false, string.Empty, "Could not find window ID on Linux.");
            }

            // Validate window ID to prevent command injection
            if (!IsValidNumericId(windowId))
            {
                return (false, string.Empty, "Invalid window ID format.");
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "import",
                    Arguments = $"-window {windowId} {tempFile}",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && File.Exists(tempFile))
            {
                var bytes = await File.ReadAllBytesAsync(tempFile);
                File.Delete(tempFile);
                return (true, Convert.ToBase64String(bytes), "Screenshot captured on Linux.");
            }

            return (false, string.Empty, "Failed to capture screenshot on Linux. Ensure ImageMagick (import) is installed.");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"Linux screenshot failed: {ex.Message}");
        }
    }

    private async Task<string> GetLinuxWindowIdAsync()
    {
        if (_targetProcess == null) return string.Empty;

        try
        {
            var processId = _targetProcess.Id;

            // Validate process ID to prevent command injection
            if (!IsValidProcessId(processId))
            {
                return string.Empty;
            }

            // Use xdotool to find window by PID
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdotool",
                    Arguments = $"search --pid {processId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var windowIds = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // Validate the returned window ID
            var windowId = windowIds.FirstOrDefault() ?? string.Empty;
            return IsValidNumericId(windowId) ? windowId : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<(bool Success, string Xml, string Message)> GetControlsLinuxAsync(int maxDepth)
    {
        try
        {
            // Use AT-SPI2 via atspi command or dbus
            // For now, return a basic structure using xprop/xwininfo
            var windowId = await GetLinuxWindowIdAsync();
            if (string.IsNullOrEmpty(windowId))
            {
                return (false, string.Empty, "Could not find window ID on Linux.");
            }

            // Window ID is already validated in GetLinuxWindowIdAsync
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xwininfo",
                    Arguments = $"-id {windowId} -tree",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Convert xwininfo output to XML
            var xml = ConvertXwinfoToXml(output);
            return (true, xml, "UI controls retrieved on Linux (limited info via xwininfo).");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"Linux UI inspection failed: {ex.Message}. Ensure xwininfo is installed.");
        }
    }

    private static string ConvertXwinfoToXml(string xwinfoOutput)
    {
        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true });

        writer.WriteStartDocument();
        writer.WriteStartElement("Window");
        writer.WriteAttributeString("Source", "xwininfo");
        writer.WriteElementString("RawOutput", xwinfoOutput);
        writer.WriteEndElement();
        writer.WriteEndDocument();

        return sb.ToString();
    }

    #endregion

    #region macOS Implementation

    private async Task<(bool Success, string Data, string Message)> CaptureScreenshotMacOSAsync()
    {
        try
        {
            if (_targetProcess == null)
            {
                return (false, string.Empty, "No target process set.");
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"screenshot_{Guid.NewGuid()}.png");

            // Use screencapture with window ID
            var windowId = await GetMacOSWindowIdAsync();

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "screencapture",
                    Arguments = string.IsNullOrEmpty(windowId)
                        ? $"-x {tempFile}"
                        : $"-l {windowId} {tempFile}",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            if (File.Exists(tempFile))
            {
                var bytes = await File.ReadAllBytesAsync(tempFile);
                File.Delete(tempFile);
                return (true, Convert.ToBase64String(bytes), "Screenshot captured on macOS.");
            }

            return (false, string.Empty, "Failed to capture screenshot on macOS.");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"macOS screenshot failed: {ex.Message}");
        }
    }

    private async Task<string> GetMacOSWindowIdAsync()
    {
        if (_targetProcess == null) return string.Empty;

        try
        {
            var processId = _targetProcess.Id;

            // Validate process ID to prevent script injection
            if (!IsValidProcessId(processId))
            {
                return string.Empty;
            }

            // Use AppleScript to get window ID
            var script = $@"
                tell application ""System Events""
                    set targetProcess to first process whose unix id is {processId}
                    set frontWindow to first window of targetProcess
                    return id of frontWindow
                end tell
            ";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e '{script}'",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return output.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<(bool Success, string Xml, string Message)> GetControlsMacOSAsync(int maxDepth)
    {
        try
        {
            if (_targetProcess == null)
            {
                return (false, string.Empty, "No target process set.");
            }

            var processId = _targetProcess.Id;

            // Validate process ID to prevent script injection
            if (!IsValidProcessId(processId))
            {
                return (false, string.Empty, "Invalid process ID.");
            }

            // Use AppleScript to get UI elements
            var script = $@"
                tell application ""System Events""
                    set targetProcess to first process whose unix id is {processId}
                    set uiElements to entire contents of first window of targetProcess
                    set output to """"
                    repeat with elem in uiElements
                        try
                            set output to output & class of elem & "": "" & name of elem & linefeed
                        end try
                    end repeat
                    return output
                end tell
            ";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e '{script}'",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var xml = ConvertAppleScriptToXml(output);
            return (true, xml, "UI controls retrieved on macOS.");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"macOS UI inspection failed: {ex.Message}");
        }
    }

    private static string ConvertAppleScriptToXml(string output)
    {
        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true });

        writer.WriteStartDocument();
        writer.WriteStartElement("Window");
        writer.WriteAttributeString("Source", "AppleScript");

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2)
            {
                writer.WriteStartElement("Control");
                writer.WriteAttributeString("Type", parts[0].Trim());
                writer.WriteAttributeString("Name", parts[1].Trim());
                writer.WriteEndElement();
            }
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();

        return sb.ToString();
    }

    #endregion

    #region UI Interaction Methods

    /// <summary>
    /// Clicks on a control by its handle.
    /// </summary>
    public async Task<(bool Success, string Message)> ClickControlAsync(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return (false, "Invalid control handle.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await ClickControlWindowsAsync(handle);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await ClickControlLinuxAsync(handle);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await ClickControlMacOSAsync(handle);
        }

        return (false, "Click operation is not supported on this platform.");
    }

    /// <summary>
    /// Clicks at the specified screen coordinates.
    /// </summary>
    public async Task<(bool Success, string Message)> ClickAtAsync(int x, int y, string button = "left", int clickCount = 1)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await ClickAtWindowsAsync(x, y, button, clickCount);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await ClickAtLinuxAsync(x, y, button, clickCount);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await ClickAtMacOSAsync(x, y, button, clickCount);
        }

        return (false, "Click operation is not supported on this platform.");
    }

    /// <summary>
    /// Types text into the currently focused control or specified control.
    /// </summary>
    public async Task<(bool Success, string Message)> TypeTextAsync(string text, IntPtr? handle = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (false, "Text cannot be empty.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await TypeTextWindowsAsync(text, handle);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await TypeTextLinuxAsync(text);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await TypeTextMacOSAsync(text);
        }

        return (false, "Type text operation is not supported on this platform.");
    }

    /// <summary>
    /// Sends keyboard keys (including special keys like Enter, Tab, Ctrl+C, etc.).
    /// </summary>
    public async Task<(bool Success, string Message)> SendKeysAsync(string keys)
    {
        if (string.IsNullOrEmpty(keys))
        {
            return (false, "Keys cannot be empty.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await SendKeysWindowsAsync(keys);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await SendKeysLinuxAsync(keys);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await SendKeysMacOSAsync(keys);
        }

        return (false, "Send keys operation is not supported on this platform.");
    }

    /// <summary>
    /// Moves the mouse to the specified screen coordinates.
    /// </summary>
    public async Task<(bool Success, string Message)> MouseMoveAsync(int x, int y)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await MouseMoveWindowsAsync(x, y);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await MouseMoveLinuxAsync(x, y);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await MouseMoveMacOSAsync(x, y);
        }

        return (false, "Mouse move operation is not supported on this platform.");
    }

    /// <summary>
    /// Performs a mouse drag from one point to another.
    /// </summary>
    public async Task<(bool Success, string Message)> MouseDragAsync(int startX, int startY, int endX, int endY, string button = "left")
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await MouseDragWindowsAsync(startX, startY, endX, endY, button);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await MouseDragLinuxAsync(startX, startY, endX, endY, button);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await MouseDragMacOSAsync(startX, startY, endX, endY, button);
        }

        return (false, "Mouse drag operation is not supported on this platform.");
    }

    /// <summary>
    /// Sets focus to a control by its handle.
    /// </summary>
    public async Task<(bool Success, string Message)> FocusControlAsync(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return (false, "Invalid control handle.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await FocusControlWindowsAsync(handle);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await FocusControlLinuxAsync(handle);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await FocusControlMacOSAsync(handle);
        }

        return (false, "Focus operation is not supported on this platform.");
    }

    /// <summary>
    /// Scrolls the mouse wheel at the current or specified position.
    /// </summary>
    public async Task<(bool Success, string Message)> MouseScrollAsync(int delta, int? x = null, int? y = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await MouseScrollWindowsAsync(delta, x, y);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await MouseScrollLinuxAsync(delta, x, y);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await MouseScrollMacOSAsync(delta, x, y);
        }

        return (false, "Mouse scroll operation is not supported on this platform.");
    }

    /// <summary>
    /// Finds a control by text and returns its handle.
    /// </summary>
    public async Task<(bool Success, IntPtr Handle, string Message)> FindControlByTextAsync(string text, bool exactMatch = false)
    {
        if (_targetProcess == null)
        {
            return (false, IntPtr.Zero, "No target process set.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await FindControlByTextWindowsAsync(text, exactMatch);
        }

        return (false, IntPtr.Zero, "Find control operation is only supported on Windows.");
    }

    #endregion

    #region Windows UI Interaction Implementation

    private async Task<(bool Success, string Message)> ClickControlWindowsAsync(IntPtr handle)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Get the control's center coordinates
                if (!GetWindowRect(handle, out RECT rect))
                {
                    return (false, "Failed to get control rectangle.");
                }

                int centerX = (rect.Left + rect.Right) / 2;
                int centerY = (rect.Top + rect.Bottom) / 2;

                // Set cursor position and click
                SetCursorPos(centerX, centerY);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);

                return (true, $"Clicked at ({centerX}, {centerY})");
            }
            catch (Exception ex)
            {
                return (false, $"Click failed: {ex.Message}");
            }
        });
    }

    private async Task<(bool Success, string Message)> ClickAtWindowsAsync(int x, int y, string button, int clickCount)
    {
        return await Task.Run(() =>
        {
            try
            {
                SetCursorPos(x, y);

                uint downFlag, upFlag;
                switch (button.ToLower())
                {
                    case "right":
                        downFlag = MOUSEEVENTF_RIGHTDOWN;
                        upFlag = MOUSEEVENTF_RIGHTUP;
                        break;
                    case "middle":
                        downFlag = MOUSEEVENTF_MIDDLEDOWN;
                        upFlag = MOUSEEVENTF_MIDDLEUP;
                        break;
                    default:
                        downFlag = MOUSEEVENTF_LEFTDOWN;
                        upFlag = MOUSEEVENTF_LEFTUP;
                        break;
                }

                for (int i = 0; i < clickCount; i++)
                {
                    mouse_event(downFlag, 0, 0, 0, IntPtr.Zero);
                    mouse_event(upFlag, 0, 0, 0, IntPtr.Zero);
                    if (i < clickCount - 1)
                    {
                        Thread.Sleep(50); // Small delay between clicks
                    }
                }

                return (true, $"Clicked {button} button {clickCount} time(s) at ({x}, {y})");
            }
            catch (Exception ex)
            {
                return (false, $"Click failed: {ex.Message}");
            }
        });
    }

    private async Task<(bool Success, string Message)> TypeTextWindowsAsync(string text, IntPtr? handle)
    {
        return await Task.Run(() =>
        {
            try
            {
                // If a handle is specified, focus it first
                if (handle.HasValue && handle.Value != IntPtr.Zero)
                {
                    SetForegroundWindow(handle.Value);
                    SetFocus(handle.Value);
                    Thread.Sleep(100);
                }

                // Use SendInput for each character
                foreach (char c in text)
                {
                    // Create input for key down
                    var inputs = new INPUT[2];

                    inputs[0] = new INPUT
                    {
                        type = INPUT_KEYBOARD,
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KEYEVENTF_UNICODE,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    };

                    inputs[1] = new INPUT
                    {
                        type = INPUT_KEYBOARD,
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    };

                    SendInput(2, inputs, Marshal.SizeOf<INPUT>());
                }

                return (true, $"Typed {text.Length} characters");
            }
            catch (Exception ex)
            {
                return (false, $"Type text failed: {ex.Message}");
            }
        });
    }

    private async Task<(bool Success, string Message)> SendKeysWindowsAsync(string keys)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Parse keys string (e.g., "{Enter}", "{Tab}", "^c" for Ctrl+C)
                var keyActions = ParseKeyString(keys);

                foreach (var action in keyActions)
                {
                    ExecuteKeyAction(action);
                }

                return (true, $"Sent keys: {keys}");
            }
            catch (Exception ex)
            {
                return (false, $"Send keys failed: {ex.Message}");
            }
        });
    }

    private static List<KeyAction> ParseKeyString(string keys)
    {
        var actions = new List<KeyAction>();
        int i = 0;

        while (i < keys.Length)
        {
            if (keys[i] == '{')
            {
                // Special key
                int end = keys.IndexOf('}', i);
                if (end > i)
                {
                    string keyName = keys.Substring(i + 1, end - i - 1);
                    actions.Add(new KeyAction { IsSpecialKey = true, KeyName = keyName });
                    i = end + 1;
                    continue;
                }
            }
            else if (keys[i] == '^')
            {
                // Ctrl modifier
                if (i + 1 < keys.Length)
                {
                    actions.Add(new KeyAction { IsModifier = true, Modifier = "Ctrl", Character = keys[i + 1] });
                    i += 2;
                    continue;
                }
            }
            else if (keys[i] == '%')
            {
                // Alt modifier
                if (i + 1 < keys.Length)
                {
                    actions.Add(new KeyAction { IsModifier = true, Modifier = "Alt", Character = keys[i + 1] });
                    i += 2;
                    continue;
                }
            }
            else if (keys[i] == '+')
            {
                // Shift modifier
                if (i + 1 < keys.Length)
                {
                    actions.Add(new KeyAction { IsModifier = true, Modifier = "Shift", Character = keys[i + 1] });
                    i += 2;
                    continue;
                }
            }

            // Regular character
            actions.Add(new KeyAction { Character = keys[i] });
            i++;
        }

        return actions;
    }

    private static void ExecuteKeyAction(KeyAction action)
    {
        if (action.IsSpecialKey)
        {
            ushort vk = GetVirtualKeyCode(action.KeyName);
            if (vk != 0)
            {
                keybd_event((byte)vk, 0, 0, IntPtr.Zero);
                keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            }
        }
        else if (action.IsModifier)
        {
            ushort modifierVk = action.Modifier switch
            {
                "Ctrl" => VK_CONTROL,
                "Alt" => VK_MENU,
                "Shift" => VK_SHIFT,
                _ => 0
            };

            if (modifierVk != 0)
            {
                ushort charVk = (ushort)VkKeyScan(action.Character);
                keybd_event((byte)modifierVk, 0, 0, IntPtr.Zero);
                keybd_event((byte)charVk, 0, 0, IntPtr.Zero);
                keybd_event((byte)charVk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
                keybd_event((byte)modifierVk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            }
        }
        else
        {
            // Regular character
            var inputs = new INPUT[2];
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = action.Character,
                    dwFlags = KEYEVENTF_UNICODE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            };
            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = action.Character,
                    dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }
    }

    private static ushort GetVirtualKeyCode(string keyName)
    {
        return keyName.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => VK_RETURN,
            "TAB" => VK_TAB,
            "ESCAPE" or "ESC" => VK_ESCAPE,
            "BACKSPACE" or "BS" => VK_BACK,
            "DELETE" or "DEL" => VK_DELETE,
            "INSERT" or "INS" => VK_INSERT,
            "HOME" => VK_HOME,
            "END" => VK_END,
            "PAGEUP" or "PGUP" => VK_PRIOR,
            "PAGEDOWN" or "PGDN" => VK_NEXT,
            "UP" => VK_UP,
            "DOWN" => VK_DOWN,
            "LEFT" => VK_LEFT,
            "RIGHT" => VK_RIGHT,
            "F1" => VK_F1,
            "F2" => VK_F2,
            "F3" => VK_F3,
            "F4" => VK_F4,
            "F5" => VK_F5,
            "F6" => VK_F6,
            "F7" => VK_F7,
            "F8" => VK_F8,
            "F9" => VK_F9,
            "F10" => VK_F10,
            "F11" => VK_F11,
            "F12" => VK_F12,
            "SPACE" => VK_SPACE,
            "CTRL" or "CONTROL" => VK_CONTROL,
            "ALT" => VK_MENU,
            "SHIFT" => VK_SHIFT,
            "WIN" or "WINDOWS" => VK_LWIN,
            "CAPSLOCK" => VK_CAPITAL,
            "NUMLOCK" => VK_NUMLOCK,
            "SCROLLLOCK" => VK_SCROLL,
            "PRINTSCREEN" or "PRTSC" => VK_SNAPSHOT,
            "PAUSE" => VK_PAUSE,
            _ => 0
        };
    }

    private async Task<(bool Success, string Message)> MouseMoveWindowsAsync(int x, int y)
    {
        return await Task.Run(() =>
        {
            try
            {
                SetCursorPos(x, y);
                return (true, $"Mouse moved to ({x}, {y})");
            }
            catch (Exception ex)
            {
                return (false, $"Mouse move failed: {ex.Message}");
            }
        });
    }

    private async Task<(bool Success, string Message)> MouseDragWindowsAsync(int startX, int startY, int endX, int endY, string button)
    {
        return await Task.Run(() =>
        {
            try
            {
                uint downFlag, upFlag;
                switch (button.ToLower())
                {
                    case "right":
                        downFlag = MOUSEEVENTF_RIGHTDOWN;
                        upFlag = MOUSEEVENTF_RIGHTUP;
                        break;
                    case "middle":
                        downFlag = MOUSEEVENTF_MIDDLEDOWN;
                        upFlag = MOUSEEVENTF_MIDDLEUP;
                        break;
                    default:
                        downFlag = MOUSEEVENTF_LEFTDOWN;
                        upFlag = MOUSEEVENTF_LEFTUP;
                        break;
                }

                // Move to start position
                SetCursorPos(startX, startY);
                Thread.Sleep(50);

                // Press button
                mouse_event(downFlag, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(50);

                // Smooth drag with interpolation
                int steps = 20;
                for (int i = 1; i <= steps; i++)
                {
                    int x = startX + (endX - startX) * i / steps;
                    int y = startY + (endY - startY) * i / steps;
                    SetCursorPos(x, y);
                    Thread.Sleep(10);
                }

                // Release button
                mouse_event(upFlag, 0, 0, 0, IntPtr.Zero);

                return (true, $"Dragged from ({startX}, {startY}) to ({endX}, {endY})");
            }
            catch (Exception ex)
            {
                return (false, $"Mouse drag failed: {ex.Message}");
            }
        });
    }

    private async Task<(bool Success, string Message)> FocusControlWindowsAsync(IntPtr handle)
    {
        return await Task.Run(() =>
        {
            try
            {
                SetForegroundWindow(handle);
                SetFocus(handle);
                return (true, $"Focused control with handle {handle}");
            }
            catch (Exception ex)
            {
                return (false, $"Focus failed: {ex.Message}");
            }
        });
    }

    private async Task<(bool Success, string Message)> MouseScrollWindowsAsync(int delta, int? x, int? y)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (x.HasValue && y.HasValue)
                {
                    SetCursorPos(x.Value, y.Value);
                }

                // WHEEL_DELTA is 120 in Windows
                int wheelDelta = delta * 120;
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)wheelDelta, IntPtr.Zero);

                return (true, $"Scrolled {delta} clicks");
            }
            catch (Exception ex)
            {
                return (false, $"Mouse scroll failed: {ex.Message}");
            }
        });
    }

    private async Task<(bool Success, IntPtr Handle, string Message)> FindControlByTextWindowsAsync(string text, bool exactMatch)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (_targetProcess == null)
                {
                    return (false, IntPtr.Zero, "No target process set.");
                }

                var mainWindowHandle = _targetProcess.MainWindowHandle;
                if (mainWindowHandle == IntPtr.Zero)
                {
                    return (false, IntPtr.Zero, "Target process has no visible main window.");
                }

                IntPtr foundHandle = IntPtr.Zero;

                EnumChildWindows(mainWindowHandle, (hwnd, lParam) =>
                {
                    var windowText = GetWindowText(hwnd);
                    bool matches = exactMatch
                        ? windowText.Equals(text, StringComparison.OrdinalIgnoreCase)
                        : windowText.Contains(text, StringComparison.OrdinalIgnoreCase);

                    if (matches)
                    {
                        foundHandle = hwnd;
                        return false; // Stop enumeration
                    }
                    return true;
                }, IntPtr.Zero);

                if (foundHandle != IntPtr.Zero)
                {
                    return (true, foundHandle, $"Found control with text '{text}'");
                }

                return (false, IntPtr.Zero, $"No control found with text '{text}'");
            }
            catch (Exception ex)
            {
                return (false, IntPtr.Zero, $"Find control failed: {ex.Message}");
            }
        });
    }

    #endregion

    #region Linux UI Interaction Implementation

    private async Task<(bool Success, string Message)> ClickControlLinuxAsync(IntPtr handle)
    {
        // On Linux, use xdotool
        var windowId = handle.ToString();
        if (!IsValidNumericId(windowId))
        {
            return (false, "Invalid window ID.");
        }

        return await ExecuteXdotoolCommandAsync($"windowactivate --sync {windowId} click 1");
    }

    private async Task<(bool Success, string Message)> ClickAtLinuxAsync(int x, int y, string button, int clickCount)
    {
        int buttonNum = button.ToLower() switch
        {
            "right" => 3,
            "middle" => 2,
            _ => 1
        };

        var clickCmd = clickCount > 1 ? $"click --repeat {clickCount} {buttonNum}" : $"click {buttonNum}";
        return await ExecuteXdotoolCommandAsync($"mousemove {x} {y} {clickCmd}");
    }

    private async Task<(bool Success, string Message)> TypeTextLinuxAsync(string text)
    {
        // Escape special characters for xdotool
        var escapedText = text.Replace("'", "'\\''");
        return await ExecuteXdotoolCommandAsync($"type '{escapedText}'");
    }

    private async Task<(bool Success, string Message)> SendKeysLinuxAsync(string keys)
    {
        // Convert keys format to xdotool format
        var xdotoolKeys = ConvertKeysToXdotool(keys);
        return await ExecuteXdotoolCommandAsync($"key {xdotoolKeys}");
    }

    private async Task<(bool Success, string Message)> MouseMoveLinuxAsync(int x, int y)
    {
        return await ExecuteXdotoolCommandAsync($"mousemove {x} {y}");
    }

    private async Task<(bool Success, string Message)> MouseDragLinuxAsync(int startX, int startY, int endX, int endY, string button)
    {
        int buttonNum = button.ToLower() switch
        {
            "right" => 3,
            "middle" => 2,
            _ => 1
        };

        return await ExecuteXdotoolCommandAsync($"mousemove {startX} {startY} mousedown {buttonNum} mousemove {endX} {endY} mouseup {buttonNum}");
    }

    private async Task<(bool Success, string Message)> FocusControlLinuxAsync(IntPtr handle)
    {
        var windowId = handle.ToString();
        if (!IsValidNumericId(windowId))
        {
            return (false, "Invalid window ID.");
        }

        return await ExecuteXdotoolCommandAsync($"windowactivate --sync {windowId}");
    }

    private async Task<(bool Success, string Message)> MouseScrollLinuxAsync(int delta, int? x, int? y)
    {
        var moveCmd = x.HasValue && y.HasValue ? $"mousemove {x.Value} {y.Value} " : "";
        var direction = delta > 0 ? "4" : "5"; // 4 = scroll up, 5 = scroll down
        var count = Math.Abs(delta);
        return await ExecuteXdotoolCommandAsync($"{moveCmd}click --repeat {count} {direction}");
    }

    private static string ConvertKeysToXdotool(string keys)
    {
        // Convert SendKeys format to xdotool format
        return keys
            .Replace("{Enter}", "Return")
            .Replace("{Tab}", "Tab")
            .Replace("{Escape}", "Escape")
            .Replace("{Backspace}", "BackSpace")
            .Replace("{Delete}", "Delete")
            .Replace("{Up}", "Up")
            .Replace("{Down}", "Down")
            .Replace("{Left}", "Left")
            .Replace("{Right}", "Right")
            .Replace("^", "ctrl+")
            .Replace("%", "alt+")
            .Replace("+", "shift+");
    }

    private static async Task<(bool Success, string Message)> ExecuteXdotoolCommandAsync(string arguments)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdotool",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return (true, "Command executed successfully.");
            }

            return (false, $"xdotool failed: {error}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to execute xdotool: {ex.Message}. Ensure xdotool is installed.");
        }
    }

    #endregion

    #region macOS UI Interaction Implementation

    private async Task<(bool Success, string Message)> ClickControlMacOSAsync(IntPtr handle)
    {
        // On macOS, we'll use AppleScript with System Events
        return await ExecuteAppleScriptAsync(@"
            tell application ""System Events""
                click
            end tell
        ");
    }

    private async Task<(bool Success, string Message)> ClickAtMacOSAsync(int x, int y, string button, int clickCount)
    {
        // Use cliclick for reliable mouse operations on macOS
        var buttonArg = button.ToLower() switch
        {
            "right" => "rc",  // right click
            "middle" => "mc", // middle click (if supported)
            _ => "c"          // left click
        };

        var commands = new List<string>();
        commands.Add($"m:{x},{y}"); // move to position

        for (int i = 0; i < clickCount; i++)
        {
            commands.Add($"{buttonArg}:{x},{y}"); // click at position
        }

        return await ExecuteCliclickCommandAsync(string.Join(" ", commands));
    }

    private async Task<(bool Success, string Message)> TypeTextMacOSAsync(string text)
    {
        var escapedText = text.Replace("\"", "\\\"");
        return await ExecuteAppleScriptAsync($@"
            tell application ""System Events""
                keystroke ""{escapedText}""
            end tell
        ");
    }

    private async Task<(bool Success, string Message)> SendKeysMacOSAsync(string keys)
    {
        var appleScriptKeys = ConvertKeysToAppleScript(keys);
        return await ExecuteAppleScriptAsync($@"
            tell application ""System Events""
                {appleScriptKeys}
            end tell
        ");
    }

    private async Task<(bool Success, string Message)> MouseMoveMacOSAsync(int x, int y)
    {
        // macOS doesn't have a simple way to move mouse via AppleScript
        // We would need to use cliclick or similar tool
        return await ExecuteCliclickCommandAsync($"m:{x},{y}");
    }

    private async Task<(bool Success, string Message)> MouseDragMacOSAsync(int startX, int startY, int endX, int endY, string button)
    {
        return await ExecuteCliclickCommandAsync($"dd:{startX},{startY} du:{endX},{endY}");
    }

    private async Task<(bool Success, string Message)> FocusControlMacOSAsync(IntPtr handle)
    {
        if (_targetProcess == null)
        {
            return (false, "No target process set.");
        }

        var processId = _targetProcess.Id;
        if (!IsValidProcessId(processId))
        {
            return (false, "Invalid process ID.");
        }

        return await ExecuteAppleScriptAsync($@"
            tell application ""System Events""
                set frontmost of (first process whose unix id is {processId}) to true
            end tell
        ");
    }

    private async Task<(bool Success, string Message)> MouseScrollMacOSAsync(int delta, int? x, int? y)
    {
        if (x.HasValue && y.HasValue)
        {
            await ExecuteCliclickCommandAsync($"m:{x.Value},{y.Value}");
        }

        // Use cliclick for scrolling
        var direction = delta > 0 ? "u" : "d";
        var count = Math.Abs(delta);
        return await ExecuteCliclickCommandAsync($"s:{direction}:{count}");
    }

    private static string ConvertKeysToAppleScript(string keys)
    {
        var script = new StringBuilder();

        // Parse the keys and convert to AppleScript
        if (keys.Contains("{Enter}"))
        {
            script.AppendLine("key code 36"); // Return key
        }
        if (keys.Contains("{Tab}"))
        {
            script.AppendLine("key code 48"); // Tab key
        }
        if (keys.Contains("{Escape}"))
        {
            script.AppendLine("key code 53"); // Escape key
        }
        if (keys.Contains("^"))
        {
            // Ctrl modifier - extract the character after ^
            var match = System.Text.RegularExpressions.Regex.Match(keys, @"\^(.)");
            if (match.Success)
            {
                script.AppendLine($"keystroke \"{match.Groups[1].Value}\" using control down");
            }
        }
        if (keys.Contains("%"))
        {
            // Alt modifier
            var match = System.Text.RegularExpressions.Regex.Match(keys, @"%(.)");
            if (match.Success)
            {
                script.AppendLine($"keystroke \"{match.Groups[1].Value}\" using option down");
            }
        }

        return script.ToString();
    }

    private static async Task<(bool Success, string Message)> ExecuteAppleScriptAsync(string script)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e '{script}'",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return (true, "AppleScript executed successfully.");
            }

            return (false, $"AppleScript failed: {error}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to execute AppleScript: {ex.Message}");
        }
    }

    private static async Task<(bool Success, string Message)> ExecuteCliclickCommandAsync(string arguments)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cliclick",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return (true, "Command executed successfully.");
            }

            return (false, $"cliclick failed: {error}. Install with: brew install cliclick");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to execute cliclick: {ex.Message}. Install with: brew install cliclick");
        }
    }

    #endregion

    #region Helper Types

    private class KeyAction
    {
        public bool IsSpecialKey { get; set; }
        public bool IsModifier { get; set; }
        public string KeyName { get; set; } = string.Empty;
        public string Modifier { get; set; } = string.Empty;
        public char Character { get; set; }
    }

    #endregion

    #region Windows P/Invoke

    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public uint[] bmiColors;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    // Mouse event constants
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    // Keyboard constants
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Virtual key codes
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_INSERT = 0x2D;
    private const ushort VK_HOME = 0x24;
    private const ushort VK_END = 0x23;
    private const ushort VK_PRIOR = 0x21; // Page Up
    private const ushort VK_NEXT = 0x22;  // Page Down
    private const ushort VK_UP = 0x26;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_F1 = 0x70;
    private const ushort VK_F2 = 0x71;
    private const ushort VK_F3 = 0x72;
    private const ushort VK_F4 = 0x73;
    private const ushort VK_F5 = 0x74;
    private const ushort VK_F6 = 0x75;
    private const ushort VK_F7 = 0x76;
    private const ushort VK_F8 = 0x77;
    private const ushort VK_F9 = 0x78;
    private const ushort VK_F10 = 0x79;
    private const ushort VK_F11 = 0x7A;
    private const ushort VK_F12 = 0x7B;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;    // Alt
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_CAPITAL = 0x14; // Caps Lock
    private const ushort VK_NUMLOCK = 0x90;
    private const ushort VK_SCROLL = 0x91;  // Scroll Lock
    private const ushort VK_SNAPSHOT = 0x2C; // Print Screen
    private const ushort VK_PAUSE = 0x13;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;

        // Padding to match the size of the union in the native INPUT structure
        private readonly ulong padding;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    #endregion
}
