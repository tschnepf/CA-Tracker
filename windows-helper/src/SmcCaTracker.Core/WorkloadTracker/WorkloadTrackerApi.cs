using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>A call's outcome: the HTTP status and body, or why the tracker couldn't be reached.</summary>
public sealed record ApiResult(int Status, string Body, string? NetworkError = null, string? ETag = null)
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
    private readonly TimeSpan retryDelay;

    /// <param name="retryDelay">Wait before repeating a call the connection dropped (default half a second).</param>
    public WorkloadTrackerApi(HttpClient http, TimeSpan? retryDelay = null)
    {
        this.http = http;
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);
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

    /// <summary>Statuses offered when binding: projects that are running (decision D3).</summary>
    public const string BindableStatuses = "active,active_ca";

    public const int SearchPageSize = 25;

    /// <summary>The tracker's largest page of assignments.</summary>
    public const int TeamPageSize = 200;

    public Task<ApiResult> PostObservationAsync(string address, string token, string clientId, CaObservationPartRequest part, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, address, "/api/ca/observations/", token, clientId, JsonSerializer.Serialize(part, ContractJson.Options), cancel);

    public Task<ApiResult> GetMeAsync(string address, string token, string clientId, CancellationToken cancel) =>
        SendAsync(HttpMethod.Get, address, "/api/auth/me/", token, clientId, null, cancel);

    public Task<ApiResult> RevokeSessionAsync(string address, string token, string clientId, int sessionId, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, address, $"/api/auth/sessions/{sessionId}/revoke/", token, clientId, null, cancel);

    /// <summary>Existing tracker projects matching <paramref name="query"/> (number, name or client), running ones only.</summary>
    public Task<ApiResult> SearchProjectsAsync(string address, string token, string clientId, string query, int page, CancellationToken cancel)
    {
        var text = query.Trim();
        var request = new ProjectSearchRequestRequest
        {
            Page = page,
            PageSize = SearchPageSize,
            StatusIn = BindableStatuses,
            SearchTokens = text.Length == 0 ? null : [new SearchTokenProjectRequest { Term = text, Op = OpEnum.And }]
        };
        return SendAsync(HttpMethod.Post, address, "/api/projects/search/", token, clientId, JsonSerializer.Serialize(request, ContractJson.Options), cancel);
    }

    /// <summary>One page of a tracker project's assignments: its team.</summary>
    public Task<ApiResult> GetTeamPageAsync(string address, string token, string clientId, int projectId, int page, CancellationToken cancel) =>
        SendAsync(HttpMethod.Get, address, string.Create(CultureInfo.InvariantCulture, $"/api/assignments/?project={projectId}&page={page}&page_size={TeamPageSize}"),
            token, clientId, null, cancel);

    public Task<ApiResult> PostAssignmentsAsync(string address, string token, string clientId, CaAssignmentBatchRequest batch, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, address, "/api/ca/assignments/", token, clientId, JsonSerializer.Serialize(batch, ContractJson.Options), cancel);

    /// <summary>Every item assignment on a tracker project; 304 when <paramref name="etag"/> still matches.</summary>
    public Task<ApiResult> GetAssignmentsAsync(string address, string token, string clientId, int projectId, string? etag, CancellationToken cancel) =>
        SendAsync(HttpMethod.Get, address, string.Create(CultureInfo.InvariantCulture, $"/api/ca/assignments/?project={projectId}"), token, clientId, null, cancel, etag);

    public Task<ApiResult> ResolvePeopleAsync(string address, string token, string clientId, CaPeopleResolveRequest request, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, address, "/api/ca/people/resolve/", token, clientId, JsonSerializer.Serialize(request, ContractJson.Options), cancel);

    public Task<ApiResult> LinkPeopleAsync(string address, string token, string clientId, CaPeopleLinkRequest request, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, address, "/api/ca/people/links/", token, clientId, JsonSerializer.Serialize(request, ContractJson.Options), cancel);

    /// <summary>People in Workload Tracker whose name matches <paramref name="query"/> (for someone not on a project's team).</summary>
    public Task<ApiResult> SearchPeopleAsync(string address, string token, string clientId, string query, CancellationToken cancel)
    {
        var text = query.Trim();
        var request = new PeopleSearchRequestRequest
        {
            Page = 1,
            PageSize = SearchPageSize,
            SearchTokens = text.Length == 0 ? null : [new SearchTokenPeopleRequest { Term = text, Op = OpEnum.And }]
        };
        return SendAsync(HttpMethod.Post, address, "/api/people/search/", token, clientId, JsonSerializer.Serialize(request, ContractJson.Options), cancel);
    }

    /// <summary>The active people in a people search result, read field by field like the team.</summary>
    public static List<TeamMember> ReadPeople(ApiResult result)
    {
        var people = new List<TeamMember>();
        if (!result.Ok || result.TryRead<JsonElement>() is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return people;
        }

        foreach (var row in rows.EnumerateArray())
        {
            var active = !row.TryGetProperty("isActive", out var flag) || flag.ValueKind != JsonValueKind.False;
            if (row.ValueKind == JsonValueKind.Object && active && Int(row, "id") is { } id && String(row, "name") is { Length: > 0 } name)
            {
                people.Add(new TeamMember(id, name, String(row, "departmentName"), String(row, "roleName")));
            }
        }

        return people;
    }

    /// <summary>
    /// The projects in a search result. Read field by field: the search returns the tracker's whole
    /// project row, of which only these fields are used (and checked against the pinned schema in tests).
    /// </summary>
    public static (List<TrackerProjectChoice> Projects, bool More) ReadProjectChoices(ApiResult result)
    {
        var projects = new List<TrackerProjectChoice>();
        if (!result.Ok || result.TryRead<JsonElement>() is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return (projects, false);
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object && Int(row, "id") is { } id && String(row, "name") is { Length: > 0 } name)
            {
                projects.Add(new TrackerProjectChoice(id, String(row, "projectNumber"), name, String(row, "status"), String(row, "client")));
            }
        }

        return (projects, String(root, "next") is { Length: > 0 });
    }

    /// <summary>The people on one page of a project's assignments (rows without a person are placeholders and left out).</summary>
    public static (List<TeamMember> Members, bool More) ReadTeamPage(ApiResult result)
    {
        var members = new List<TeamMember>();
        if (!result.Ok || result.TryRead<JsonElement>() is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return (members, false);
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object && Int(row, "person") is { } person)
            {
                members.Add(new TeamMember(person, String(row, "personName") ?? $"Person {person}", String(row, "personDepartmentName"), String(row, "roleName")));
            }
        }

        return (members, String(root, "next") is { Length: > 0 });
    }

    private static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? String(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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

    /// <summary>
    /// Sends one call. A call whose connection drops before any answer (a reused connection the server had
    /// just closed) is repeated once: every call here is safe to repeat, since observation parts are stored
    /// by id, assignments are decided by their time, and the rest only read.
    /// </summary>
    private async Task<ApiResult> SendAsync(HttpMethod method, string address, string path, string token, string clientId, string? json, CancellationToken cancel,
        string? ifNoneMatch = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, address.TrimEnd('/') + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation(ClientIdHeader, clientId);
            if (!string.IsNullOrEmpty(ifNoneMatch))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            }

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            try
            {
                using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                return new ApiResult((int)response.StatusCode, body, ETag: response.Headers.ETag?.ToString());
            }
            catch (HttpRequestException) when (attempt == 1 && !cancel.IsCancellationRequested)
            {
                await Task.Delay(retryDelay, cancel).ConfigureAwait(false);
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
}
