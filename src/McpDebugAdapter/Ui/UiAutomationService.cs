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

    #endregion
}
