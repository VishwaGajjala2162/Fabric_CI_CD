using Fabric.Deployment.Core.Api;
using Fabric.Deployment.Core.Auth;
using Fabric.Deployment.Core.Configuration;
using Fabric.Deployment.Core.Engine;
using Fabric.Deployment.Core.Logging;
using Fabric.Deployment.Core.Services;
using Fabric.Deployment.Core.Steps;

namespace Fabric.Deployment.Core;

/// <summary>
/// Entry point for hosts (CLI, Azure Function, ASP.NET API, test harness).
/// <code>
/// var settings = DeploymentSettings.Load("deploysettings.json");
/// var fabric   = FabricDeploymentFactory.CreateServices(settings, logger);
/// var ctx      = await FabricDeploymentFactory.DeployAsync(settings, fabric, logger, ct);
/// </code>
/// </summary>
public static class FabricDeploymentFactory
{
    public static ITokenProvider CreateTokenProvider(DeploymentSettings s) => s.Fabric.Auth.Mode switch
    {
        AuthMode.EnvironmentToken => new EnvironmentTokenProvider(s.Fabric.Auth.TokenEnvVar),
        _ => new ClientSecretTokenProvider(
            s.Fabric.Auth.TenantId,
            s.Fabric.Auth.ClientId,
            Environment.GetEnvironmentVariable(s.Fabric.Auth.ClientSecretEnvVar)
                ?? throw new InvalidOperationException($"Environment variable '{s.Fabric.Auth.ClientSecretEnvVar}' is not set"))
    };

    public static FabricServices CreateServices(DeploymentSettings s, IDeployLogger log,
        ITokenProvider? tokens = null, HttpClient? http = null)
    {
        http ??= new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var api = new FabricApiClient(http, tokens ?? CreateTokenProvider(s), log, s.Fabric.BaseUrl);
        return new FabricServices(api);
    }

    public static Task<DeploymentContext> DeployAsync(DeploymentSettings s, FabricServices fabric, IDeployLogger log,
        CancellationToken ct, IApprovalGate? gate = null)
        => DeploymentOrchestrator.CreateDefault(gate ?? ApprovalStep.CreateGate(s.Approval))
            .RunAsync(new DeploymentContext(s, fabric, log, ct));

    public static Task<DeploymentContext> PlanAsync(DeploymentSettings s, FabricServices fabric, IDeployLogger log, CancellationToken ct)
        => DeploymentOrchestrator.CreatePlanOnly().RunAsync(new DeploymentContext(s, fabric, log, ct));
}
