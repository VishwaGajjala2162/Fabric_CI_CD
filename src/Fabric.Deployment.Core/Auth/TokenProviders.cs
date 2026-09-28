using System.Text.Json;

namespace Fabric.Deployment.Core.Auth;

/// <summary>Supplies bearer tokens for https://api.fabric.microsoft.com.</summary>
public interface ITokenProvider
{
    Task<string> GetTokenAsync(CancellationToken ct);
}

/// <summary>
/// Service principal (client credentials) flow against Microsoft Entra ID — no SDK dependency.
/// The service principal must be allowed to use Fabric APIs (tenant setting
/// "Service principals can use Fabric APIs") and be Admin on the deployment pipeline and
/// Contributor (or higher) on every stage workspace.
/// </summary>
public sealed class ClientSecretTokenProvider : ITokenProvider
{
    public const string FabricScope = "https://api.fabric.microsoft.com/.default";

    private readonly HttpClient _http;
    private readonly string _tenantId, _clientId, _clientSecret, _authorityHost;
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ClientSecretTokenProvider(string tenantId, string clientId, string clientSecret,
        HttpClient? http = null, string authorityHost = "https://login.microsoftonline.com")
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("tenantId is required");
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("clientId is required");
        if (string.IsNullOrWhiteSpace(clientSecret)) throw new ArgumentException("client secret is required");
        _tenantId = tenantId; _clientId = clientId; _clientSecret = clientSecret;
        _authorityHost = authorityHost.TrimEnd('/');
        _http = http ?? new HttpClient();
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_authorityHost}/{_tenantId}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret,
                    ["scope"] = FabricScope
                })
            };
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token request failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e)
                ? (e.ValueKind == JsonValueKind.Number ? e.GetInt32() : int.Parse(e.GetString()!))
                : 3600;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 300); // refresh 5 min early
            return _token;
        }
        finally { _lock.Release(); }
    }
}

/// <summary>
/// Uses a pre-acquired token from an environment variable, e.g. in a pipeline:
///   export FABRIC_ACCESS_TOKEN=$(az account get-access-token --resource https://api.fabric.microsoft.com --query accessToken -o tsv)
/// Handy for user identities / federated (OIDC) logins done by the CI tool.
/// </summary>
public sealed class EnvironmentTokenProvider : ITokenProvider
{
    private readonly string _variable;
    public EnvironmentTokenProvider(string variable = "FABRIC_ACCESS_TOKEN") => _variable = variable;

    public Task<string> GetTokenAsync(CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable(_variable);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Environment variable '{_variable}' is empty. Acquire a Fabric token first.");
        return Task.FromResult(token.Trim());
    }
}

/// <summary>Adapter so any delegate (e.g. Azure.Identity TokenCredential) can be plugged in.</summary>
public sealed class DelegateTokenProvider : ITokenProvider
{
    private readonly Func<CancellationToken, Task<string>> _factory;
    public DelegateTokenProvider(Func<CancellationToken, Task<string>> factory) => _factory = factory;
    public Task<string> GetTokenAsync(CancellationToken ct) => _factory(ct);
}
