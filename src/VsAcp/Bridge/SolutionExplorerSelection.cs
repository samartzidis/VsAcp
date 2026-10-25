using System.IO;
using Microsoft.VisualStudio.Shell;

namespace VsAcp.Bridge;

/// <summary>
/// Files of the items selected in Solution Explorer. Used when a drop carries no file list: VS keeps the
/// dragged items selected for the duration of the drag, so the selection is what the user dragged.
/// </summary>
internal static class SolutionExplorerSelection
{
    internal static string[] Files()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE { SelectedItems: { } selected })
                return Array.Empty<string>();

            var list = new List<string>();
            foreach (EnvDTE.SelectedItem item in selected)
            {
                try
                {
                    var path = item.ProjectItem?.FileNames[1];
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        list.Add(path!);
                }
                catch
                {
                    // an item without a file (a solution folder, a project node): skip it
                }
            }
            return list.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
