using Microsoft.VisualStudio.Shell;

// Binding redirects the package registers in its pkgdef. The extension runs inside devenv.exe (.NET Framework), whose
// only config is VS's own devenv.exe.config: a shipped dependency that is referenced at a version other than the one we
// ship, and that VS has no redirect for, fails to load (the CLR needs the exact version of a strong-named assembly).
// Check each one against devenv.exe.config of the oldest supported VS when a package is added or bumped.

// ModelContextProtocol.Core 1.2.0 is built against System.Net.ServerSentEvents 10.0.0.0, but the copy it resolves to and
// the VSIX ships is 10.0.0.5, and VS 18.10's devenv.exe.config has no redirect for it: every IDE tools request failed
// with FileNotFoundException when its transport was disposed. NewVersion, the upper bound and CodeBase must follow the
// version of the shipped System.Net.ServerSentEvents.dll.
[assembly: ProvideBindingRedirection(
    AssemblyName = "System.Net.ServerSentEvents",
    PublicKeyToken = "cc7b13ffcd2ddd51",
    OldVersionLowerBound = "10.0.0.0",
    OldVersionUpperBound = "10.0.0.5",
    NewVersion = "10.0.0.5",
    CodeBase = @"$PackageFolder$\System.Net.ServerSentEvents.dll")]
