using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fabric.Deployment.Core.Auth;
using Fabric.Deployment.Core.Logging;

namespace Fabric.Deployment.Core.Api;

public sealed class FabricApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }
    public string? RequestId { get; }

    public FabricApiException(HttpStatusCode status, string message, string? errorCode = null, string? requestId = null)
        : base(message)
    {
        StatusCode = status; ErrorCode = errorCode; RequestId = requestId;
    }
}

/// <summary>
/// Thin, resilient wrapper over the Fabric REST API:
///  * bearer auth via <see cref="ITokenProvider"/>
///  * retries on 429 / 5xx honouring Retry-After (Fabric throttles aggressively)
///  * continuation-token paging
///  * long running operation (LRO) start + polling
/// </summary>
public sealed class FabricApiClient
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokens;
    private readonly IDeployLogger _log;
    private readonly string _baseUrl;
    private readonly int _maxRetries;

    public FabricApiClient(HttpClient http, ITokenProvider tokens, IDeployLogger log,
        string baseUrl = "https://api.fabric.microsoft.com/v1", int maxRetries = 6)
    {
        _http = http; _tokens = tokens; _log = log;
        _baseUrl = baseUrl.TrimEnd('/');
        _maxRetries = maxRetries;
    }

    // ------------------------------------------------------------------ basic verbs

    public async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        var (_, body, _) = await SendAsync(HttpMethod.Get, path, null, ct);
        return Deserialize<T>(body, path);
    }

    public async Task<T> PostAsync<T>(string path, object? payload, CancellationToken ct)
    {
        var (_, body, _) = await SendAsync(HttpMethod.Post, path, payload, ct);
        return Deserialize<T>(body, path);
    }

    public async Task PostAsync(string path, object? payload, CancellationToken ct)
        => await SendAsync(HttpMethod.Post, path, payload, ct);

    public async Task DeleteAsync(string path, CancellationToken ct)
        => await SendAsync(HttpMethod.Delete, path, null, ct);

    /// <summary>
    /// Runs a POST that may be long running and returns the final JSON body:
    /// the immediate 200 body, or GET /operations/{id}/result once the LRO succeeds.
    /// </summary>
    /// <remarks>Only use for calls that are safe to repeat (they are retried on network errors).</remarks>
    public async Task<T?> PostAndWaitAsync<T>(string path, object? payload, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var handle = await StartLongRunningAsync(path, payload, ct, idempotent: true);
        if (handle.IsCompletedImmediately)
            return string.IsNullOrWhiteSpace(handle.ImmediateBody) ? null : JsonSerializer.Deserialize<T>(handle.ImmediateBody, Json);

        var state = await WaitForOperationAsync(handle.OperationId!, timeout, handle.RetryAfterSeconds, null, ct);
        if (state.Status != OperationStatus.Succeeded)
            throw new FabricApiException(HttpStatusCode.InternalServerError,
                $"POST {path} operation {state.Status}: {state.Error?.ErrorCode} {state.Error?.Message}", state.Error?.ErrorCode);
        if (typeof(T) == typeof(object)) return null;
        try { return await GetOperationResultAsync<T>(handle.OperationId!, ct); }
        catch (FabricApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>Follows continuationToken until all pages are read.</summary>
    public async Task<List<T>> GetAllPagesAsync<T>(string path, CancellationToken ct)
    {
        var all = new List<T>();
        string? token = null;
        do
        {
            var sep = path.Contains('?') ? '&' : '?';
            var url = token is null ? path : $"{path}{sep}continuationToken={Uri.EscapeDataString(token)}";
            var page = await GetAsync<PagedResponse<T>>(url, ct);
            all.AddRange(page.Value);
            token = string.IsNullOrEmpty(page.ContinuationToken) ? null : Uri.UnescapeDataString(page.ContinuationToken);
        } while (token is not null);
        return all;
    }

    // ------------------------------------------------------------------ long running operations

    /// <summary>POSTs a request that may return 202 + operation headers.</summary>
    public async Task<LroHandle> StartLongRunningAsync(string path, object? payload, CancellationToken ct, bool idempotent = false)
    {
        var (status, body, headers) = await SendAsync(HttpMethod.Post, path, payload, ct, idempotent);
        if (status != HttpStatusCode.Accepted)
            return new LroHandle { ImmediateBody = body };

        headers.TryGetValues("x-ms-operation-id", out var opIds);
        headers.TryGetValues("Location", out var locs);
        headers.TryGetValues("deployment-id", out var depIds);
        return new LroHandle
        {
            OperationId = opIds?.FirstOrDefault() ?? ExtractOperationId(locs?.FirstOrDefault()),
            Location = locs?.FirstOrDefault(),
            DeploymentId = depIds?.FirstOrDefault(),
            RetryAfterSeconds = ReadRetryAfter(headers) ?? 10
        };
    }

    /// <summary>Polls GET /operations/{id} until terminal state or timeout.</summary>
    public async Task<OperationState> WaitForOperationAsync(string operationId, TimeSpan timeout,
        int pollSeconds, Action<OperationState>? onProgress, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var delay = Math.Max(1, pollSeconds);
        while (true)
        {
            var (_, body, headers) = await SendAsync(HttpMethod.Get, $"operations/{operationId}", null, ct);
            var state = Deserialize<OperationState>(body, "operations");
            onProgress?.Invoke(state);
            if (OperationStatus.IsTerminal(state.Status)) return state;

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Operation {operationId} did not finish within {timeout}. Last status: {state.Status}");

            var wait = ReadRetryAfter(headers) ?? delay;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(wait, delay)), ct);
        }
    }

    /// <summary>GET /operations/{id}/result (available for 24h after completion).</summary>
    public Task<T> GetOperationResultAsync<T>(string operationId, CancellationToken ct)
        => GetAsync<T>($"operations/{operationId}/result", ct);

    // ------------------------------------------------------------------ core send with retry

    /// <param name="idempotent">
    /// Whether the call is safe to repeat. Network errors and 5xx are retried only for idempotent calls
    /// (GET, definition reads/writes); a deploy is never re-sent blindly. 429 is always retried
    /// because the request was rejected before doing anything.
    /// </param>
    private async Task<(HttpStatusCode, string, HttpResponseHeaders)> SendAsync(
        HttpMethod method, string path, object? payload, CancellationToken ct, bool? idempotent = null)
    {
        var safeToRepeat = idempotent ?? (method == HttpMethod.Get || method == HttpMethod.Delete);
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{_baseUrl}/{path.TrimStart('/')}";
        string? json = payload is null ? null : JsonSerializer.Serialize(payload, Json);

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(ct));
            if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            else if (method == HttpMethod.Post) req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            _log.Debug($"{method} {url}");
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries && safeToRepeat)
            {
                var backoff = Backoff(attempt);
                _log.Warn($"Network error calling {method} {path}: {ex.Message}. Retrying in {backoff}s");
                await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
                continue;
            }

            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var code = (int)resp.StatusCode;

                if (resp.IsSuccessStatusCode) return (resp.StatusCode, body, resp.Headers);

                var retryable = code == 429 || (code >= 500 && safeToRepeat);
                if (retryable && attempt < _maxRetries)
                {
                    var wait = ReadRetryAfter(resp.Headers) ?? Backoff(attempt);
                    _log.Warn($"{method} {path} returned {code}; retry {attempt + 1}/{_maxRetries} in {wait}s");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    continue;
                }

                FabricErrorResponse? err = null;
                try { err = JsonSerializer.Deserialize<FabricErrorResponse>(body, Json); } catch { /* non-JSON error */ }
                var details = err?.MoreDetails is { Count: > 0 }
                    ? " | " + string.Join("; ", err.MoreDetails.Select(d => $"{d.ErrorCode}: {d.Message}"))
                    : "";
                throw new FabricApiException(resp.StatusCode,
                    $"{method} {path} failed with {code} {err?.ErrorCode}: {err?.Message ?? body}{details}",
                    err?.ErrorCode, err?.RequestId);
            }
        }
    }

    private static int Backoff(int attempt) => Math.Min(60, (int)Math.Pow(2, attempt + 1));

    private static int? ReadRetryAfter(HttpResponseHeaders headers)
    {
        if (headers.RetryAfter?.Delta is { } d) return (int)Math.Ceiling(d.TotalSeconds);
        if (headers.RetryAfter?.Date is { } date) return Math.Max(1, (int)(date - DateTimeOffset.UtcNow).TotalSeconds);
        if (headers.TryGetValues("Retry-After", out var v) && int.TryParse(v.FirstOrDefault(), out var s)) return s;
        return null;
    }

    private static string? ExtractOperationId(string? location)
        => string.IsNullOrEmpty(location) ? null : location.TrimEnd('/').Split('/').Last();

    private static T Deserialize<T>(string body, string context)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            if (typeof(T) == typeof(object)) return default!;
            throw new InvalidOperationException($"Empty response body from {context}");
        }
        return JsonSerializer.Deserialize<T>(body, Json)
               ?? throw new InvalidOperationException($"Could not parse response from {context}");
    }
}
