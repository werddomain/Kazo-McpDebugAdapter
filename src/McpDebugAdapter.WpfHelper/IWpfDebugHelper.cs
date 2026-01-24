using System.Collections.Generic;
using System.Windows;

namespace McpDebugAdapter.WpfHelper;

/// <summary>
/// Information about a WPF control for debugging purposes
/// </summary>
public class ControlInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public Rect Bounds { get; set; }
    public Point Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
    public bool IsVisible { get; set; }
    public bool IsEnabled { get; set; }
    public string AutomationId { get; set; } = string.Empty;
    public int ZIndex { get; set; }
    public object? Tag { get; set; }
    public string ToolTip { get; set; } = string.Empty;
}

/// <summary>
/// Interface for WPF control discovery and interaction
/// </summary>
public interface IWpfDebugHelper
{
    /// <summary>
    /// Gets all discoverable controls in the application
    /// </summary>
    IEnumerable<ControlInfo> GetAllControls();
    
    /// <summary>
    /// Finds controls by name
    /// </summary>
    IEnumerable<ControlInfo> FindControlsByName(string name);
    
    /// <summary>
    /// Finds controls by type
    /// </summary>
    IEnumerable<ControlInfo> FindControlsByType(string typeName);
    
    /// <summary>
    /// Finds controls by text content
    /// </summary>
    IEnumerable<ControlInfo> FindControlsByText(string text);
    
    /// <summary>
    /// Finds controls by AutomationId
    /// </summary>
    IEnumerable<ControlInfo> FindControlsByAutomationId(string automationId);
    
    /// <summary>
    /// Finds the control at specific coordinates
    /// </summary>
    ControlInfo? GetControlAt(Point point);
    
    /// <summary>
    /// Highlights a control visually for debugging
    /// </summary>
    void HighlightControl(string name, bool highlight = true);
    
    /// <summary>
    /// Refreshes the control cache
    /// </summary>
    void RefreshControls();
}