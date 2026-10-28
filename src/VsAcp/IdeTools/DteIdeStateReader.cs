using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.OperationProgress;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using VsAcp.Core.IdeTools;

namespace VsAcp.IdeTools;

/// <summary>
/// Reads <see cref="IdeState"/> through DTE for <c>get_ide_state</c> (ADR-061), on the UI thread. Each part is read on
/// its own, so a part Visual Studio can't answer right now (a document whose load is deferred, a solution without a
/// build configuration) is left out rather than failing the call. Changes nothing.
/// </summary>
internal sealed class DteIdeStateReader : IIdeStateReader
{
    readonly JoinableTaskFactory _jtf;
    readonly IAsyncServiceProvider _services;

    public DteIdeStateReader(JoinableTaskFactory jtf, IAsyncServiceProvider services)
    {
        _jtf = jtf;
        _services = services;
    }

    public async Task<IdeState> ReadAsync(CancellationToken cancellationToken)
    {
        var progress = await _services.GetServiceAsync(typeof(SVsOperationProgressStatusService)) as IVsOperationProgressStatusService;
        await _jtf.SwitchToMainThreadAsync(cancellationToken);
        return Capture(progress);
    }

    static IdeState Capture(IVsOperationProgressStatusService? progress)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(DTE)) is not DTE2 dte)
            return new IdeState();

        var solution = OpenSolution(dte);
        var build = solution is null ? null : BuildOf(solution);
        var (configuration, platform) = build is null ? (null, null) : ConfigurationOf(build);

        return new IdeState
        {
            SolutionPath = solution is null ? null : PathOf(solution),
            Configuration = configuration,
            Platform = platform,
            StartupProjects = build is null ? [] : StartupProjects(build),
            Debugger = DebuggerModeOf(dte),
            Building = build is not null && IsBuilding(build),
            SolutionLoading = solution is not null && IsLoading(progress),
            ActiveWindow = ActiveWindowOf(dte),
            ActiveDocument = ActiveDocumentOf(dte),
            OpenDocuments = OpenDocuments(dte)
        };
    }

    static Solution? OpenSolution(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return dte.Solution is { IsOpen: true } solution ? solution : null; }
        catch (COMException) { return null; }
    }

    static string? PathOf(Solution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return NullIfEmpty(solution.FullName); }
        catch (COMException) { return null; }
    }

    static SolutionBuild? BuildOf(Solution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return solution.SolutionBuild; }
        catch (COMException) { return null; }
    }

    static (string? Configuration, string? Platform) ConfigurationOf(SolutionBuild build)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return build.ActiveConfiguration is SolutionConfiguration2 configuration
                ? (NullIfEmpty(configuration.Name), NullIfEmpty(configuration.PlatformName))
                : (null, null);
        }
        catch (COMException) { return (null, null); }
    }

    static string[] StartupProjects(SolutionBuild build)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return build.StartupProjects switch
            {
                string single when single.Length > 0 => [single],
                object[] many => many.OfType<string>().Where(p => p.Length > 0).ToArray(),
                _ => []
            };
        }
        catch (COMException) { return []; }
    }

    static bool IsBuilding(SolutionBuild build)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return build.BuildState == vsBuildState.vsBuildStateInProgress; }
        catch (COMException) { return false; }
    }

    // The solution, or IntelliSense's processing of it, is still in progress: the operation-progress service's
    // IntelliSense stage, correlated with solution load. Reported as not loading when the service is unavailable.
    static bool IsLoading(IVsOperationProgressStatusService? progress)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return progress?.GetStageStatusForSolutionLoad(CommonOperationProgressStageIds.Intellisense)?.IsInProgress == true; }
        catch (COMException) { return false; }
    }

    static DebuggerMode DebuggerModeOf(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return dte.Debugger.CurrentMode switch
            {
                dbgDebugMode.dbgRunMode => DebuggerMode.Running,
                dbgDebugMode.dbgBreakMode => DebuggerMode.Break,
                _ => DebuggerMode.NotRunning
            };
        }
        catch (COMException) { return DebuggerMode.NotRunning; }
    }

    static string? ActiveWindowOf(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return NullIfEmpty(dte.ActiveWindow?.Caption); }
        catch (COMException) { return null; }
    }

    static ActiveDocument? ActiveDocumentOf(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (dte.ActiveDocument is not { } document || NullIfEmpty(document.FullName) is not { } path)
                return null;

            if (document.Object("TextDocument") is not TextDocument { Selection: { } selection })
                return new ActiveDocument(path, 1, 1, null, null);

            var empty = selection.IsEmpty;
            return new ActiveDocument(path, selection.ActivePoint.Line, selection.ActivePoint.LineCharOffset,
                empty ? null : selection.TopPoint.Line, empty ? null : selection.BottomPoint.Line);
        }
        catch (Exception ex) when (IsUnavailable(ex)) { return null; }
    }

    static string[] OpenDocuments(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var paths = new List<string>();
        try
        {
            foreach (Document document in dte.Documents)
            {
                try
                {
                    if (NullIfEmpty(document.FullName) is { } path)
                        paths.Add(path);
                }
                catch (Exception ex) when (IsUnavailable(ex)) { /* this document only */ }
            }
        }
        catch (COMException) { /* what was read so far */ }
        return paths.ToArray();
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // DTE throws COMException for a part it can't answer now, and an InternalErrorException for a document whose load
    // is deferred (opened in an earlier session, not shown since).
    static bool IsUnavailable(Exception ex) => ex is COMException || ex.GetType().Name == "InternalErrorException";
}
