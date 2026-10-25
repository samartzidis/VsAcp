using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsAcp.Bridge;

/// <summary>The Visual Studio folder browser (the same dialog VS uses for its own folder settings).</summary>
internal static class FolderPicker
{
    private const int MaxPathChars = 1024;

    /// <summary>The folder the user chose, or null when they cancelled or the dialog is unavailable. UI thread only.</summary>
    internal static string? Pick(string title, string? initialDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Package.GetGlobalService(typeof(SVsUIShell)) is not IVsUIShell uiShell)
            return null;

        uiShell.GetDialogOwnerHwnd(out var owner);

        var buffer = Marshal.AllocCoTaskMem((MaxPathChars + 1) * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);

            var info = new VSBROWSEINFOW[1];
            info[0].lStructSize = (uint)Marshal.SizeOf(typeof(VSBROWSEINFOW));
            info[0].pwzDlgTitle = title;
            info[0].pwzInitialDir = initialDirectory;
            info[0].pwzDirName = buffer;
            info[0].nMaxDirName = MaxPathChars;
            info[0].hwndOwner = owner;

            // S_OK = a folder was chosen; anything else (S_FALSE, cancelled) = nothing to add.
            var hr = uiShell.GetDirectoryViaBrowseDlg(info);
            if (hr != Microsoft.VisualStudio.VSConstants.S_OK) return null;

            var chosen = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(chosen) ? null : chosen;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}
