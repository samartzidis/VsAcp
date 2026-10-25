namespace VsAcp.Core.Abstractions;

public class AcpPermissionRequest
{
    public required string RequestId { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<AcpPermissionOption> Options { get; init; }
}

public class AcpPermissionOption
{
    public required string OptionId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
}

public interface IPermissionBroker
{
    event Action<AcpPermissionRequest>? PermissionRequested;

    /// <summary>
    /// When true, permission requests are granted without prompting. Per tab; starts off and is
    /// toggled at runtime.
    /// </summary>
    bool AutoApprove { get; }

    /// <summary>
    /// Turns auto-approve on or off. Turning it on also grants any prompt already waiting
    /// (one-time allow where offered). Returns how many pending prompts were granted.
    /// </summary>
    int SetAutoApprove(bool enabled);

    Task<string?> SubmitAsync(AcpPermissionRequest request, CancellationToken cancellationToken);
    void Resolve(string requestId, string? selectedOptionId);
    void CancelAll();
}
