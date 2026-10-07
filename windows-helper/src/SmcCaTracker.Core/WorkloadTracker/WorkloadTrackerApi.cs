using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>A call's outcome: the HTTP status and body, or why the tracker couldn't be reached.</summary>
public sealed record ApiResult(int Status, string Body, string? NetworkError = null)
{
    public bool Ok => NetworkError is null && Status is >= 200 and < 300;

    /// <summary>The tracker's error body (<c>{code, detail}</c>), when it sent one.</summary>
    public CaError? Error => Ok ? null : TryRead<CaError>();

    /// <summary>The tracker's explanation: its error detail, or DRF's <c>{detail}</c>.</summary>
    public string? Detail
    {
        get
        {
            if (Error?.Detail is { Length: > 0 } detail)
            {
                return detail;
            }

            return TryRead<JsonElement>() is { ValueKind: JsonValueKind.Object } element
                && element.TryGetProperty("detail", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
    }

    public T? TryRead<T>()
    {
        try
        {
            return string.IsNullOrWhiteSpace(Body) ? default : JsonSerializer.Deserialize<T>(Body, ContractJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

/// <summary>Who a Workload Tracker token belongs to (<c>GET /api/auth/me/</c>).</summary>
public sealed record TrackerIdentity(int UserId, string UserName, int? PersonId, string? PersonName);

/// <summary>
/// The Workload Tracker calls the desktop makes. Every call carries the token and this installation's
/// client id; the tracker binds the token to the first client id it sees.
/// </summary>
public sealed class WorkloadTrackerApi
{
    public const string ClientIdHeader = "X-CA-Client-Id";

    private readonly HttpClient http;

    public WorkloadTrackerApi(HttpClient http)
    {
        this.http = http;
    }

    /// <summary>
    /// The tracker's address as "https://host[:port]", or null when it isn't one. Plain http is allowed
    /// only for a tracker on this PC (development).
    /// </summary>
    public static string? NormalizeAddress(string? address)
    {
        var text = (address ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Host.Length == 0)
        {
            return null;
        }

        var local = uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && local))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public Task<ApiResult> PostObservationAsync(string address, string token, string clientId, CaObservationPartRequest part, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, address, "/api/ca/observations/", token, clientId, JsonSerializer.Serialize(part, ContractJson.Options), cancel);

    public Task<ApiResult> GetMeAsync(string address, string token, string clientId, CancellationToken cancel) =>
        SendAsync(HttpMethod.Get, address, "/api/auth/me/", token, clientId, null, cancel);

    public Task<ApiResult> RevokeSessionAsync(string address, string token, string clientId, int sessionId, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, address, $"/api/auth/sessions/{sessionId}/revoke/", token, clientId, null, cancel);

    /// <summary>Reads the person and Workload Tracker person from a <c>/api/auth/me/</c> body.</summary>
    public static TrackerIdentity? ReadIdentity(ApiResult result)
    {
        if (!result.Ok || result.TryRead<JsonElement>() is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("id", out var id) || !id.TryGetInt32(out var userId))
        {
            return null;
        }

        var userName = user.TryGetProperty("username", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
        int? personId = null;
        string? personName = null;
        if (root.TryGetProperty("person", out var person) && person.ValueKind == JsonValueKind.Object)
        {
            personId = person.TryGetProperty("id", out var pid) && pid.TryGetInt32(out var value) ? value : null;
            personName = person.TryGetProperty("name", out var pname) && pname.ValueKind == JsonValueKind.String ? pname.GetString() : null;
        }

        return new TrackerIdentity(userId, userName, personId, personName);
    }

    private async Task<ApiResult> SendAsync(HttpMethod method, string address, string path, string token, string clientId, string? json, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, address.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(ClientIdHeader, clientId);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return new ApiResult((int)response.StatusCode, body);
        }
        catch (HttpRequestException error)
        {
            return new ApiResult(0, "", error.Message);
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new ApiResult(0, "", "Workload Tracker didn't answer in time.");
        }
    }
}
