using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Shapes;

namespace McpDebugAdapter.WpfHelper;

/// <summary>
/// WPF Debug Helper implementation for control discovery and interaction
/// </summary>
public class WpfDebugHelper : IWpfDebugHelper
{
    private readonly Application _application;
    private readonly List<ControlInfo> _controlCache = new();
    private readonly Dictionary<string, UIElement> _highlightOverlays = new();

    public WpfDebugHelper(Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        RefreshControls();
    }

    /// <summary>
    /// Gets all discoverable controls in the application
    /// </summary>
    public IEnumerable<ControlInfo> GetAllControls()
    {
        RefreshControls();
        return _controlCache.ToList();
    }

    /// <summary>
    /// Finds controls by name
    /// </summary>
    public IEnumerable<ControlInfo> FindControlsByName(string name)
    {
        return _controlCache.Where(c => 
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds controls by type
    /// </summary>
    public IEnumerable<ControlInfo> FindControlsByType(string typeName)
    {
        return _controlCache.Where(c => 
            c.Type.Contains(typeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds controls by text content
    /// </summary>
    public IEnumerable<ControlInfo> FindControlsByText(string text)
    {
        return _controlCache.Where(c => 
            c.Text.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            c.ToolTip.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds controls by AutomationId
    /// </summary>
    public IEnumerable<ControlInfo> FindControlsByAutomationId(string automationId)
    {
        return _controlCache.Where(c => 
            string.Equals(c.AutomationId, automationId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the control at specific coordinates
    /// </summary>
    public ControlInfo? GetControlAt(Point point)
    {
        return _controlCache
            .Where(c => c.Bounds.Contains(point) && c.IsVisible)
            .OrderByDescending(c => c.ZIndex)
            .FirstOrDefault();
    }

    /// <summary>
    /// Highlights a control visually for debugging
    /// </summary>
    public void HighlightControl(string name, bool highlight = true)
    {
        if (!highlight)
        {
            RemoveHighlight(name);
            return;
        }

        var control = FindControlsByName(name).FirstOrDefault();
        if (control == null) return;

        var window = GetMainWindow();
        if (window == null) return;

        RemoveHighlight(name);

        // Create a new popup window for highlighting
        var highlightWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0)),
            ShowInTaskbar = false,
            Topmost = true,
            IsHitTestVisible = false,
            Left = control.Bounds.X,
            Top = control.Bounds.Y,
            Width = control.Bounds.Width,
            Height = control.Bounds.Height
        };

        var border = new Border
        {
            BorderBrush = Brushes.Red,
            BorderThickness = new Thickness(2)
        };

        highlightWindow.Content = border;
        highlightWindow.Show();

        _highlightOverlays[name] = highlightWindow;
    }

    /// <summary>
    /// Refreshes the control cache
    /// </summary>
    public void RefreshControls()
    {
        _controlCache.Clear();

        foreach (Window window in _application.Windows)
        {
            if (window.IsVisible)
            {
                ScanElement(window, window);
            }
        }
    }

    private void ScanElement(UIElement element, Window parentWindow)
    {
        if (element == null) return;

        var controlInfo = CreateControlInfo(element, parentWindow);
        if (controlInfo != null)
        {
            _controlCache.Add(controlInfo);
        }

        // Scan child elements
        if (element is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                ScanElement(child, parentWindow);
            }
        }
        else if (element is ContentControl contentControl && contentControl.Content is UIElement content)
        {
            ScanElement(content, parentWindow);
        }
        else if (element is Decorator decorator && decorator.Child != null)
        {
            ScanElement(decorator.Child, parentWindow);
        }
        else
        {
            // Use visual tree for other controls
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i) as UIElement;
                if (child != null)
                {
                    ScanElement(child, parentWindow);
                }
            }
        }
    }

    private ControlInfo? CreateControlInfo(UIElement element, Window parentWindow)
    {
        try
        {
            var bounds = GetElementBounds(element, parentWindow);
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            var name = "";
            if (element is FrameworkElement fe)
            {
                name = fe.Name;
            }

            var text = GetElementText(element);
            var automationId = AutomationProperties.GetAutomationId(element);
            var toolTip = GetToolTip(element);

            return new ControlInfo
            {
                Name = name ?? "",
                Type = element.GetType().Name,
                Text = text ?? "",
                Bounds = bounds,
                IsVisible = element.IsVisible,
                IsEnabled = element.IsEnabled,
                AutomationId = automationId ?? "",
                ZIndex = Panel.GetZIndex(element),
                Tag = (element as FrameworkElement)?.Tag,
                ToolTip = toolTip ?? ""
            };
        }
        catch
        {
            return null;
        }
    }

    private Rect GetElementBounds(UIElement element, Window parentWindow)
    {
        try
        {
            var transform = element.TransformToAncestor(parentWindow);
            var bounds = transform.TransformBounds(new Rect(element.RenderSize));
            
            // Convert to screen coordinates
            var windowPoint = parentWindow.PointToScreen(bounds.Location);
            return new Rect(windowPoint, bounds.Size);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private static string? GetElementText(UIElement element)
    {
        return element switch
        {
            TextBlock textBlock => textBlock.Text,
            Button button => button.Content?.ToString(),
            Label label => label.Content?.ToString(),
            TextBox textBox => textBox.Text,
            CheckBox checkBox => checkBox.Content?.ToString(),
            RadioButton radioButton => radioButton.Content?.ToString(),
            MenuItem menuItem => menuItem.Header?.ToString(),
            _ => null
        };
    }

    private static string? GetToolTip(UIElement element)
    {
        var toolTip = (element as FrameworkElement)?.ToolTip;
        return toolTip?.ToString();
    }

    private Window? GetMainWindow()
    {
        return _application.MainWindow ?? _application.Windows.OfType<Window>().FirstOrDefault();
    }

    private void RemoveHighlight(string name)
    {
        if (_highlightOverlays.TryGetValue(name, out var overlay))
        {
            if (overlay is Window highlightWindow)
            {
                highlightWindow.Close();
            }
            _highlightOverlays.Remove(name);
        }
    }
}