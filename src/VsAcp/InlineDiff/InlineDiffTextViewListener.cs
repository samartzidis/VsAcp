using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VsAcp.InlineDiff;

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class InlineDiffTextViewListener : IWpfTextViewCreationListener
{
#pragma warning disable CS0649 // Assigned by MEF composition, not in code.
    [Export]
    [Name(InlineDiffAdornment.LayerName)]
    [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
    internal AdornmentLayerDefinition? AdornmentLayer;

    [Export]
    [Name(InlineDiffAdornment.ToolbarLayerName)]
    [Order(After = PredefinedAdornmentLayers.Text)]
    internal AdornmentLayerDefinition? ToolbarLayer;
#pragma warning restore CS0649

    public void TextViewCreated(IWpfTextView textView)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var filePath = GetFilePath(textView);
        if (filePath is null) return;

        var manager = InlineDiffManager.GetManagerForFile(filePath);
        if (manager is null) return;

        var session = manager.GetSession(filePath);
        if (session is null) return;

        InlineDiffAdornment.Attach(textView, session, manager);
    }

    internal static string? GetFilePath(IWpfTextView textView)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (textView.TextBuffer.Properties.TryGetProperty(
                typeof(Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer), out object bufferAdapter)
            && bufferAdapter is Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer)
        {
            var persistFileFormat = bufferAdapter as Microsoft.VisualStudio.Shell.Interop.IPersistFileFormat;
            if (persistFileFormat != null)
            {
                persistFileFormat.GetCurFile(out string filePath, out _);
                return filePath;
            }
        }

        if (textView.TextDataModel?.DocumentBuffer?.Properties != null
            && textView.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                typeof(Microsoft.VisualStudio.Text.ITextDocument), out Microsoft.VisualStudio.Text.ITextDocument doc))
        {
            return doc.FilePath;
        }

        return null;
    }
}
