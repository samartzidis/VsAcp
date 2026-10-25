using System.Windows.Media;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;

namespace VsAcp.Controls;

/// <summary>
/// Resolves editor Plain Text colors from the active VS color theme so the chat
/// WebView background matches the code editor (dark or light), similar to Cursor.
/// </summary>
internal static class ChatThemeColors
{
    public static (Color Background, Color Foreground) GetEditorColors()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (TryGetClassificationEditorColors(out var background, out var foreground))
            return (background, foreground);

        var bg = VSColorTheme.GetThemedColor(EnvironmentColors.EnvironmentBackgroundColorKey);
        var fg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
        return (ToWpf(bg), ToWpf(fg));
    }

    static bool TryGetClassificationEditorColors(out Color background, out Color foreground)
    {
        background = default;
        foreground = default;

        var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
        var formatMapService = componentModel?.GetService<IClassificationFormatMapService>();
        var formatMap = formatMapService?.GetClassificationFormatMap("text");
        if (formatMap is null)
            return false;

        var props = formatMap.DefaultTextProperties;
        if (!TryBrushColor(props.BackgroundBrush, out background))
            return false;
        if (!TryBrushColor(props.ForegroundBrush, out foreground))
            return false;

        return true;
    }

    static bool TryBrushColor(System.Windows.Media.Brush? brush, out Color color)
    {
        color = default;
        if (brush is not SolidColorBrush solid || solid.Color.A == 0)
            return false;
        color = solid.Color;
        return true;
    }

    static Color ToWpf(System.Drawing.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
