using VsAcp.Core.Abstractions;
using VsAcp.Core.Acp;
using VsAcp.Core.Acp.Handlers;
using VsAcp.Core.Configuration;
using VsAcp.Core.IdeTools;
using VsAcp.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VsAcp.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVsAcpServices(
        this IServiceCollection services,
        Action<VsAcpOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<VsAcpOptions>(_ => { });

        services.AddSingleton<ISessionStore, JsonSessionStore>();
        services.AddSingleton<ICheckpointService, ShadowGitCheckpointService>();

        services.AddSingleton<IPermissionBroker, PermissionBroker>();
        services.AddSingleton<IElicitationBroker, ElicitationBroker>();
        services.AddSingleton<IDiffBroker, DiffBroker>();

        services.AddSingleton<AgentProcessHost>();
        services.AddSingleton<ToolCallAccumulator>();
        services.AddSingleton<ToolCallDiffNotifier>();
        services.AddSingleton<SessionUpdateAdapter>();
        services.AddSingleton<PermissionHandler>();
        services.AddSingleton<ElicitationHandler>();
        services.AddSingleton<FileSystemHandler>();
        services.AddSingleton<TerminalHandler>(sp =>
            new TerminalHandler(
                sp.GetRequiredService<ILogger<TerminalHandler>>(),
                sp.GetService<ITerminalOutputSink>()));
        services.AddSingleton<AcpClientHandler>();

        // Register only the concrete type. Resolving IChatService/IAcpSessionLifecycle via
        // factory forwarding causes MS DI to track the same IDisposable instance multiple times
        // and call Dispose() once per registration. Callers resolve AcpSessionService directly.
        services.AddSingleton<AcpSessionService>(sp =>
            new AcpSessionService(
                sp.GetRequiredService<AgentProcessHost>(),
                sp.GetRequiredService<AcpClientHandler>(),
                sp.GetRequiredService<SessionUpdateAdapter>(),
                sp.GetRequiredService<IOptions<VsAcpOptions>>(),
                sp.GetRequiredService<ILogger<AcpSessionService>>(),
                sp.GetService<IIdeToolsEndpoint>()));

        return services;
    }
}
