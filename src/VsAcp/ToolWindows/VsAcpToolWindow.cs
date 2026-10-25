using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.Shell;

namespace VsAcp.ToolWindows;

[Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d")]
public class VsAcpToolWindow : ToolWindowPane
{
    public TabbedChatControl TabbedChat { get; }

    public VsAcpToolWindow() : base(null)
    {
        Caption = $"ACP Agent v{GetPluginVersion()}";
        TabbedChat = new TabbedChatControl();
        Content = TabbedChat;
    }

    /// <summary>
    /// The VSIX manifest version is the single source of truth; the installed
    /// extension.vsixmanifest sits next to the assembly. Falls back to the assembly version.
    /// </summary>
    private static string GetPluginVersion()
    {
        var assembly = typeof(VsAcpToolWindow).Assembly;
        try
        {
            var dir = Path.GetDirectoryName(assembly.Location);
            var manifestPath = dir is null ? null : Path.Combine(dir, "extension.vsixmanifest");
            if (manifestPath is not null && File.Exists(manifestPath))
            {
                var version = XDocument.Load(manifestPath).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Identity")
                    ?.Attribute("Version")?.Value;
                if (!string.IsNullOrWhiteSpace(version))
                    return version!;
            }
        }
        catch
        {
            // fall through to the assembly version
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
