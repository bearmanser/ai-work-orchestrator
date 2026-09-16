using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.AzureDevOps;

/// <summary>Configuration for the Azure DevOps pull request client.</summary>
public sealed class AzureDevOpsOptions
{
    public string BaseUrl { get; set; } = "https://dev.azure.com";

    public string Organization { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string RepositoryName { get; set; } = string.Empty;

    public SecretRef PatSecret { get; set; } = new SecretRef("azuredevops.pat");
}
