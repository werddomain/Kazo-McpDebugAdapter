using System;
using System.Threading.Tasks;
using McpDebugAdapter.Dap;

namespace McpDebugAdapter.Ui;

/// <summary>
/// Simple WPF control discovery using basic DAP expression evaluation
/// </summary>
public class SimpleWpfEvaluationService
{
    private readonly VsDbgClient _dapClient;

    public SimpleWpfEvaluationService(VsDbgClient dapClient)
    {
        _dapClient = dapClient ?? throw new ArgumentNullException(nameof(dapClient));
    }

    /// <summary>
    /// Gets the bounds of a control by name using simple expression evaluation
    /// </summary>
    public async Task<(bool Success, string Data, string Message)> GetControlBoundsByNameAsync(string controlName)
    {
        // First, verify this is a supported application type
        var appTypeCheck = await GetApplicationTypeAsync();
        if (!IsSupportedAppType(appTypeCheck))
        {
            return (false, "", $"Control bounds evaluation requires WPF, WinForms, WinUI 3, or MAUI application. Detected: {appTypeCheck}");
        }
        
        // Use appropriate implementation based on app type
        return appTypeCheck switch
        {   
            
            "WPF" => await GetWpfControlBoundsAsync(controlName),
            "WinForms" => await GetWinFormsControlBoundsAsync(controlName),
            "WinUI3" => await GetWinUI3ControlBoundsAsync(controlName),
            "MAUI" => await GetMauiControlBoundsAsync(controlName),
            _ => (false, "", $"Unsupported application type: {appTypeCheck}")
        };
    }

    private static bool IsSupportedAppType(string appType)
    {
        return appType is "WPF" or "WinForms" or "WinUI3" or "MAUI";
    }

    /// <summary>
    /// Gets WPF control bounds using WPF-specific APIs
    /// </summary>
    private async Task<(bool Success, string Data, string Message)> GetWpfControlBoundsAsync(string controlName)
    {
        // Simple expression to get control bounds - use string concatenation to avoid escaping issues
        
        var expression = 
@"((System.Func<string>)(() => {
    try {
        var win = System.Windows.Application.Current.MainWindow;
        if (win == null) return ""No window found"";
        
        var ctrl = win.FindName(""" + controlName + @""") as System.Windows.FrameworkElement;
        if (ctrl == null) return ""Control not found"";
        
        var point = ctrl.PointToScreen(new System.Windows.Point(0, 0));
        var centerX = point.X + (ctrl.ActualWidth / 2);
        var centerY = point.Y + (ctrl.ActualHeight / 2);
        var e = new { name = controlName, x = point.X, y = point.Y, centerX = centerX, centerY = centerY, width = ctrl.ActualWidth, height = ctrl.ActualHeight };
        return Newtonsoft.Json.JsonConvert.SerializeObject(e); 
    }
    catch (System.Exception ex) {
        return Newtonsoft.Json.JsonConvert.SerializeObject( new {Error = ex.Message} );
    }
}))()";

        try
        {
            var (result, type) = await _dapClient.EvaluateAsync(expression);
            if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error:") && !result.Contains("not found"))
            {
                return (true, result, $"Control '{controlName}' bounds retrieved via DAP evaluation");
            }
            else
            {
                return (false, "", $"Failed to get control bounds: {result}");
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Error evaluating expression: {ex.Message}");
        }
    }

    /// <summary>
    /// Highlights a control by name
    /// </summary>
    public async Task<(bool Success, string Message)> HighlightControlAsync(string controlName, bool highlight = true)
    {
        // Verify this is a supported application type
        var appTypeCheck = await GetApplicationTypeAsync();
        if (!IsSupportedAppType(appTypeCheck))
        {
            return (false, $"Control highlighting requires WPF, WinForms, WinUI 3, or MAUI application. Detected: {appTypeCheck}");
        }
        
        // Use appropriate implementation based on app type
        return appTypeCheck switch
        {
            "WPF" => await HighlightWpfControlAsync(controlName, highlight),
            "WinForms" => await HighlightWinFormsControlAsync(controlName, highlight),
            "WinUI3" => await HighlightWinUI3ControlAsync(controlName, highlight),
            "MAUI" => await HighlightMauiControlAsync(controlName, highlight),
            _ => (false, $"Unsupported application type: {appTypeCheck}")
        };
    }

    /// <summary>
    /// Highlights a WPF control
    /// </summary>
    private async Task<(bool Success, string Message)> HighlightWpfControlAsync(string controlName, bool highlight = true)
    {
        var highlightValue = highlight.ToString().ToLower();
        var expression = 
@"((System.Func<string>)(() => {
    try {
        var win = System.Windows.Application.Current.MainWindow;
        if (win == null) return ""No window found"";
        
        var ctrl = win.FindName(""" + controlName + @""") as System.Windows.Controls.Control;
        if (ctrl == null) return ""Control not found"";
        
        if (" + highlightValue + @") {
            ctrl.BorderBrush = System.Windows.Media.Brushes.Red;
            ctrl.BorderThickness = new System.Windows.Thickness(3);
            return ""Highlighted"";
        } else {
            ctrl.BorderBrush = null;
            ctrl.BorderThickness = new System.Windows.Thickness(0);
            return ""Unhighlighted"";
        }
    }
    catch (System.Exception ex) {
        return ""Error: "" + ex.Message;
    }
}))()";

        try
        {
            var (result, type) = await _dapClient.EvaluateAsync(expression);
            if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error:"))
            {
                return (true, result);
            }
            else
            {
                return (false, $"Failed to highlight control: {result}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Error evaluating expression: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets WinForms control bounds using WinForms-specific APIs
    /// </summary>
    private async Task<(bool Success, string Data, string Message)> GetWinFormsControlBoundsAsync(string controlName)
    {
        var expression = 
@"((System.Func<string>)(() => {
    try {
        var forms = System.Windows.Forms.Application.OpenForms;
        if (forms.Count == 0) return ""No forms found"";
        
        // Look for the control in all open forms
        foreach (System.Windows.Forms.Form form in forms)
        {
            var ctrl = FindWinFormsControl(form, """ + controlName + @""");
            if (ctrl != null)
            {
                var screenPoint = ctrl.PointToScreen(new System.Drawing.Point(0, 0));
                var centerX = screenPoint.X + (ctrl.Width / 2);
                var centerY = screenPoint.Y + (ctrl.Height / 2);
                var e = new { name = controlName, x = screenPoint.X, y = screenPoint.Y, centerX = centerX, centerY = centerY, width = ctrl.Width, height = ctrl.Height, formTitle = form.Text };
                return Newtonsoft.Json.JsonConvert.SerializeObject(e);
            }
        }
        return ""Control not found in any form"";
    }
    catch (System.Exception ex) {
        return ""Error: "" + ex.Message;
    }
    
    System.Windows.Forms.Control FindWinFormsControl(System.Windows.Forms.Control parent, string name) {
        if (parent.Name == name) return parent;
        foreach (System.Windows.Forms.Control child in parent.Controls) {
            var found = FindWinFormsControl(child, name);
            if (found != null) return found;
        }
        return null;
    }
}))()";

        try
        {
            var (result, type) = await _dapClient.EvaluateAsync(expression);
            if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error:"))
            {
                return (true, result, "Success");
            }
            else
            {
                return (false, "", $"Failed to get control bounds: {result}");
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Error evaluating expression: {ex.Message}");
        }
    }

    /// <summary>
    /// Highlights a WinForms control
    /// </summary>
    private async Task<(bool Success, string Message)> HighlightWinFormsControlAsync(string controlName, bool highlight = true)
    {
        var highlightValue = highlight.ToString().ToLower();
        var expression = 
@"((System.Func<string>)(() => {
    try {
        var forms = System.Windows.Forms.Application.OpenForms;
        if (forms.Count == 0) return ""No forms found"";
        
        // Look for the control in all open forms
        foreach (System.Windows.Forms.Form form in forms)
        {
            var ctrl = FindWinFormsControl(form, """ + controlName + @""");
            if (ctrl != null)
            {
                if (" + highlightValue + @") {
                    // Store original color and set highlight
                    ctrl.Tag = ctrl.BackColor;
                    ctrl.BackColor = System.Drawing.Color.Red;
                } else {
                    // Restore original color if stored in Tag
                    if (ctrl.Tag is System.Drawing.Color originalColor) {
                        ctrl.BackColor = originalColor;
                        ctrl.Tag = null;
                    }
                }
                return ""Control highlighted: "" + controlName;
            }
        }
        return ""Control not found in any form"";
    }
    catch (System.Exception ex) {
        return ""Error: "" + ex.Message;
    }
    
    System.Windows.Forms.Control FindWinFormsControl(System.Windows.Forms.Control parent, string name) {
        if (parent.Name == name) return parent;
        foreach (System.Windows.Forms.Control child in parent.Controls) {
            var found = FindWinFormsControl(child, name);
            if (found != null) return found;
        }
        return null;
    }
}))()";

        try
        {
            var (result, type) = await _dapClient.EvaluateAsync(expression);
            if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error:"))
            {
                return (true, result);
            }
            else
            {
                return (false, $"Failed to highlight control: {result}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Error evaluating expression: {ex.Message}");
        }
    }

    /// <summary>
    /// Detects the type of application for appropriate UI automation strategy
    /// </summary>
    public async Task<string> GetApplicationTypeAsync()
    {
        try
        {
            // Check for MAUI first (as it might have multiple UI frameworks underneath)
            var mauiCheck = "Microsoft.Maui.Controls.Application.Current != null";
            var (mauiResult, mauiType) = await _dapClient.EvaluateAsync(mauiCheck);
            
            if (!string.IsNullOrEmpty(mauiResult) && mauiResult.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return "MAUI";
            }

            // Check for WinUI 3
            var winui3Check = "Microsoft.UI.Xaml.Application.Current != null";
            var (winui3Result, winui3Type) = await _dapClient.EvaluateAsync(winui3Check);
            
            if (!string.IsNullOrEmpty(winui3Result) && winui3Result.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return "WinUI3";
            }

            // Check for WPF
            var wpfCheck = "System.Windows.Application.Current != null";
            var (wpfResult, wpfType) = await _dapClient.EvaluateAsync(wpfCheck);
            
            if (!string.IsNullOrEmpty(wpfResult) && wpfResult.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return "WPF";
            }

            // Check for WinForms
            var winFormsCheck = "System.Windows.Forms.Application.OpenForms.Count > 0";
            var (winFormsResult, winFormsType) = await _dapClient.EvaluateAsync(winFormsCheck);
            
            if (!string.IsNullOrEmpty(winFormsResult) && 
                int.TryParse(winFormsResult, out int formCount) && formCount > 0)
            {
                return "WinForms";
            }

            // No recognized UI framework
            return "Console/Service";
        }
        catch (Exception ex)
        {
            return $"Detection failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Checks if the current application supports UI automation through expression evaluation
    /// </summary>
    public async Task<bool> IsUiAutomationSupportedAsync()
    {
        var appType = await GetApplicationTypeAsync();
        return IsSupportedAppType(appType);
    }

    /// <summary>
    /// Gets WinUI 3 control bounds using WinUI 3-specific APIs
    /// </summary>
    private async Task<(bool Success, string Data, string Message)> GetWinUI3ControlBoundsAsync(string controlName)
    {
        var expression = 
@"((System.Func<string>)(() => {
    try {
        var app = Microsoft.UI.Xaml.Application.Current;
        if (app == null) return ""No WinUI 3 application found"";
        
        // Get the main window - WinUI 3 uses Window instead of MainWindow
        var windows = Microsoft.UI.Xaml.Window.Current;
        if (windows?.Content == null) return ""No window content found"";
        
        var ctrl = windows.Content.FindName(""" + controlName + @""") as Microsoft.UI.Xaml.FrameworkElement;
        if (ctrl == null) return ""Control not found"";
        
        // Get the transform to get screen coordinates
        var transform = ctrl.TransformToVisual(null);
        var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var centerX = point.X + (ctrl.ActualWidth / 2);
        var centerY = point.Y + (ctrl.ActualHeight / 2);
        var e = new { name = controlName, x = point.X, y = point.Y, centerX = centerX, centerY = centerY, width = ctrl.ActualWidth, height = ctrl.ActualHeight };
        return Newtonsoft.Json.JsonConvert.SerializeObject(e);
    }
    catch (System.Exception ex) {
        return ""Error: "" + ex.Message;
    }
}))()";

        try
        {
            var (result, type) = await _dapClient.EvaluateAsync(expression);
            if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error:"))
            {
                return (true, result, "Success");
            }
            else
            {
                return (false, "", $"Failed to get control bounds: {result}");
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Error evaluating expression: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets MAUI control bounds using MAUI-specific APIs
    /// </summary>
    private async Task<(bool Success, string Data, string Message)> GetMauiControlBoundsAsync(string controlName)
    {
        var expression = 
@"((System.Func<string>)(() => {
    try {
        var app = Microsoft.Maui.Controls.Application.Current;
        if (app == null) return ""No MAUI application found"";
        
        // Get the main page
        var mainPage = app.MainPage;
        if (mainPage == null) return ""No main page found"";
        
        // Find the control recursively
        var ctrl = FindMauiElement(mainPage, """ + controlName + @""");
        if (ctrl == null) return ""Control not found"";
        
        // MAUI uses different coordinate system - might need platform-specific handling
        var bounds = ctrl.Bounds;
        var centerX = bounds.X + (bounds.Width / 2);
        var centerY = bounds.Y + (bounds.Height / 2);
        var e = new { name = controlName, x = bounds.X, y = bounds.Y, centerX = centerX, centerY = centerY, width = bounds.Width, height = bounds.Height, note = ""MAUI coordinates may be relative to parent"" };
        return Newtonsoft.Json.JsonConvert.SerializeObject(e);
    }
    catch (System.Exception ex) {
        return ""Error: "" + ex.Message;
    }
    
    Microsoft.Maui.Controls.Element FindMauiElement(Microsoft.Maui.Controls.Element parent, string name) {
        if (parent == null) return null;
        
        // Check StyleId (MAUI's equivalent to Name)
        if (parent.StyleId == name) return parent;
        
        // Check if it's a named element (for code-behind scenarios)
        if (parent.GetType().GetProperty(""Name"")?.GetValue(parent)?.ToString() == name) return parent;
        
        // Recursively search children based on parent type
        if (parent is Microsoft.Maui.Controls.Layout layout)
        {
            foreach (var child in layout.Children)
            {
                if (child is Microsoft.Maui.Controls.Element element)
                {
                    var found = FindMauiElement(element, name);
                    if (found != null) return found;
                }
            }
        }
        else if (parent is Microsoft.Maui.Controls.ContentView contentView && contentView.Content is Microsoft.Maui.Controls.Element content)
        {
            return FindMauiElement(content, name);
        }
        
        return null;
    }
}))()";

        try
        {
            var (result, type) = await _dapClient.EvaluateAsync(expression);
            if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error:"))
            {
                return (true, result, "Success");
            }
            else
            {
                return (false, "", $"Failed to get control bounds: {result}");
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Error evaluating expression: {ex.Message}");
        }
    }

    /// <summary>
    /// Highlights a WinUI 3 control
    /// </summary>
    private async Task<(bool Success, string Message)> HighlightWinUI3ControlAsync(string controlName, bool highlight = true)
    {
        var highlightValue = highlight.ToString().ToLower();
        var expression = 
@"((System.Func<string>)(() => {
    try {
        var app = Microsoft.UI.Xaml.Application.Current;
        if (app == null) return ""No WinUI 3 application found"";
        
        var windows = Microsoft.UI.Xaml.Window.Current;
        if (windows?.Content == null) return ""No window content found"";
        
        var ctrl = windows.Content.FindName(""" + controlName + @""") as Microsoft.UI.Xaml.Controls.Control;
        if (ctrl == null) return ""Control not found"";
        
        if (" + highlightValue + @") {
            // Store original background and set highlight
            ctrl.Tag = ctrl.Background;
            ctrl.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
        } else {
            // Restore original background if stored in Tag
            if (ctrl.Tag is Microsoft.UI.Xaml.Media.Brush originalBrush) {
                ctrl.Background = originalBrush;
                ctrl.Tag = null;
            }
        }
        return ""Control highlighted: "" + controlName;
    }
    catch (System.Exception ex) {
        return ""Error: "" + ex.Message;
    }
}))()";

        try
        {
            var (result, type) = await _dapClient.EvaluateAsync(expression);
            if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error:"))
            {
                return (true, result);
            }
            else
            {
                return (false, $"Failed to highlight control: {result}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Error evaluating expression: {ex.Message}");
        }
    }

    /// <summary>
    /// Highlights a MAUI control
    /// </summary>
    private async Task<(bool Success, string Message)> HighlightMauiControlAsync(string controlName, bool highlight = true)
    {
        var highlightValue = highlight.ToString().ToLower();
        var expression = 
@"((System.Func<string>)(() => {
    try {
        var app = Microsoft.Maui.Controls.Application.Current;
        if (app == null) return ""No MAUI application found"";
        
        var mainPage = app.MainPage;
        if (mainPage == null) return ""No main page found"";
        
        var ctrl = FindMauiElement(mainPage, """ + controlName + @""");
        if (ctrl == null) return ""Control not found"";
        
        // Try to highlight based on control type
        if (ctrl is Microsoft.Maui.Controls.VisualElement visualElement)
        {
            if (" + highlightValue + @") {
                visualElement.BackgroundColor = Microsoft.Maui.Graphics.Colors.Red;
            } else {
                visualElement.BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent;
            }
            return ""Visual element highlighted: "" + controlName;
        }
        else if (ctrl is Microsoft.Maui.Controls.View view)
        {
            if (" + highlightValue + @") {
                view.BackgroundColor = Microsoft.Maui.Graphics.Colors.Red;
            } else {
                view.BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent;
            }
            return ""View highlighted: "" + controlName;
        }
        
        return ""Control type does not support highlighting"";
    }
    catch (System.Exception ex) {
        return ""Error: "" + ex.Message;
    }
    
    Microsoft.Maui.Controls.Element FindMauiElement(Microsoft.Maui.Controls.Element parent, string name) {
        if (parent == null) return null;
        
        if (parent.StyleId == name) return parent;
        if (parent.GetType().GetProperty(""Name"")?.GetValue(parent)?.ToString() == name) return parent;
        
        if (parent is Microsoft.Maui.Controls.Layout layout)
        {
            foreach (var child in layout.Children)
            {
                if (child is Microsoft.Maui.Controls.Element element)
                {
                    var found = FindMauiElement(element, name);
                    if (found != null) return found;
                }
            }
        }
        else if (parent is Microsoft.Maui.Controls.ContentView contentView && contentView.Content is Microsoft.Maui.Controls.Element content)
        {
            return FindMauiElement(content, name);
        }
        
        return null;
    }
}))()";

        try
        {
            var (result, type) = await _dapClient.EvaluateAsync(expression);
            if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error:"))
            {
                return (true, result);
            }
            else
            {
                return (false, $"Failed to highlight control: {result}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Error evaluating expression: {ex.Message}");
        }
    }
}