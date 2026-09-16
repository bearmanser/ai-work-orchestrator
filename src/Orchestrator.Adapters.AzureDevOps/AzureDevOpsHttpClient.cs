using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.AzureDevOps;

/// <summary>
/// Thin HTTP transport for Azure DevOps git APIs. Resolves the PAT per call from
/// <see cref="ISecretStore"/>, applies Basic auth per request (never on shared
/// headers), and throws status-plus-route errors that never carry headers or secrets.
/// Register the backing <see cref="HttpClient"/> with
/// <c>AddStandardResilienceHandler()</c> so 5xx/timeouts retry but 4xx do not.
/// </summary>
public sealed class AzureDevOpsHttpClient(
    HttpClient httpClient,
    ISecretStore secrets,
    AzureDevOpsOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken ct)
    {
        var pat = await secrets.GetAsync(options.PatSecret, ct).ConfigureAwait(false);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat));

        var baseUrl = options.BaseUrl.TrimEnd('/');
        var routePath = "/" + relativeUrl.TrimStart('/');
        using var request = new HttpRequestMessage(method, baseUrl + routePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new HttpRequestException(
                $"Azure DevOps {method} {routePath} failed with {status} {response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(payload);
    }

    public static string EnsureRefsHeads(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("Branch name is required.", nameof(branch));

        var trimmed = branch.Trim().TrimStart('/');
        return trimmed.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? trimmed
            : "refs/heads/" + trimmed;
    }

    public static string RepoBase(string organization, string project, string repositoryName) =>
        $"{organization}/{project}/_apis/git/repositories/{repositoryName}";
}
