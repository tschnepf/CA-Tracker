using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Refresh;

public enum RefreshStatus
{
    Pending,
    Running,
    Complete,
    Failed,
    Cancelled
}

/// <summary>
/// A refresh the app asked for. The app writes it and opens the built-in browser window, which
/// claims it when the project page opens, records a snapshot per step, and marks it done. The main
/// window follows the file to show progress, and cancels it if you press Cancel.
/// </summary>
public sealed class RefreshRequest
{
    public string Id { get; set; } = "";
    public string TrackerProjectId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProviderProjectId { get; set; } = "";
    public string? AccountId { get; set; }
    public string ProjectName { get; set; } = "";
    public List<RefreshStep> Steps { get; set; } = new();
    public int CompletedSteps { get; set; }
    public int ItemCount { get; set; }
    public RefreshStatus Status { get; set; } = RefreshStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>How long each stage may take: waiting for the page to open, and each page's scan.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    public bool IsFinished => Status is RefreshStatus.Complete or RefreshStatus.Failed or RefreshStatus.Cancelled;

    public string UrlForStep(int step) => Steps[step].Url;
}

public sealed record ClaimResult(RefreshRequest? Request, string? Error);

public sealed record StepResult(RefreshRequest Request, string? NextUrl, bool Done);

public sealed partial class RefreshRequestStore
{
    private readonly AppPaths paths;

    public RefreshRequestStore(AppPaths paths)
    {
        this.paths = paths;
    }

    public string Directory => paths.RefreshDirectory;

    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public RefreshRequest Create(TrackerProject project, BrowserSettings browser, DateTimeOffset now)
    {
        var connection = project.Connection
            ?? throw new InvalidOperationException("Connect this project to Helix or Procore before refreshing.");
        var request = new RefreshRequest
        {
            Id = RandomNumberGenerator.GetHexString(32, lowercase: true),
            TrackerProjectId = project.Id,
            Provider = connection.Provider,
            ProviderProjectId = connection.ProjectId,
            AccountId = connection.AccountId,
            ProjectName = connection.ProjectName,
            Steps = ProviderLinks.RefreshSteps(connection),
            CreatedAt = now,
            TimeoutSeconds = Math.Clamp(browser.TimeoutSeconds, 30, 1800),
            ExpiresAt = now.AddSeconds(Math.Clamp(browser.TimeoutSeconds, 30, 1800))
        };
        Save(request);
        return request;
    }

    public RefreshRequest? Load(string? id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        try
        {
            var json = LocalFiles.ReadAllTextWithRetry(PathFor(id!));
            return json is null ? null : JsonSerializer.Deserialize<RefreshRequest>(json, TrackerJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(RefreshRequest request) =>
        LocalFiles.WriteAllTextAtomic(PathFor(request.Id), JsonSerializer.Serialize(request, TrackerJson.Options));

    /// <summary>Called when the built-in browser reaches the project page and starts reading it.</summary>
    public ClaimResult Claim(string? id, string provider, DateTimeOffset now)
    {
        var request = Load(id);
        if (request is null)
        {
            return new ClaimResult(null, "This refresh no longer exists.");
        }

        if (request.IsFinished)
        {
            return new ClaimResult(null, "This refresh has already finished.");
        }

        if (now > request.ExpiresAt)
        {
            Finish(request, RefreshStatus.Failed, "The refresh took too long and was stopped.", now);
            return new ClaimResult(null, request.Error);
        }

        if (!string.Equals(request.Provider, provider, StringComparison.OrdinalIgnoreCase))
        {
            return new ClaimResult(null, "This refresh is for a different site.");
        }

        request.Status = RefreshStatus.Running;
        request.ClaimedAt ??= now;
        // Once the page is open, give the scan its own full time budget.
        request.ExpiresAt = now.AddSeconds(request.TimeoutSeconds);
        Save(request);
        return new ClaimResult(request, null);
    }

    /// <summary>Records a delivered snapshot for a step and says what to open next, if anything.</summary>
    public StepResult? CompleteStep(string? id, int step, ProviderSnapshot snapshot, DateTimeOffset now)
    {
        var request = Load(id);
        if (request is null || request.IsFinished)
        {
            return null;
        }

        if (!string.Equals(request.Provider, snapshot.Provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(request.ProviderProjectId, snapshot.ProjectId, StringComparison.Ordinal))
        {
            Finish(request, RefreshStatus.Failed, "A different project opened than the one requested.", now);
            return new StepResult(request, null, true);
        }

        if (step < 0 || step >= request.Steps.Count)
        {
            Finish(request, RefreshStatus.Failed, "The refresh step was not recognised.", now);
            return new StepResult(request, null, true);
        }

        if (step >= request.CompletedSteps)
        {
            request.ItemCount += snapshot.Items.Count;
        }

        request.CompletedSteps = Math.Max(request.CompletedSteps, step + 1);
        if (request.CompletedSteps >= request.Steps.Count)
        {
            Finish(request, RefreshStatus.Complete, null, now);
            return new StepResult(request, null, true);
        }

        request.ExpiresAt = now.AddSeconds(request.TimeoutSeconds);
        Save(request);
        return new StepResult(request, request.UrlForStep(request.CompletedSteps), false);
    }

    /// <summary>Pushes the deadline out while someone is signing in inside the built-in browser.</summary>
    public RefreshRequest? KeepAlive(string? id, DateTimeOffset now)
    {
        var request = Load(id);
        if (request is null || request.IsFinished)
        {
            return request;
        }

        request.ExpiresAt = now.AddSeconds(request.TimeoutSeconds);
        Save(request);
        return request;
    }

    public RefreshRequest? Fail(string? id, string error, DateTimeOffset now)
    {
        var request = Load(id);
        if (request is null || request.IsFinished)
        {
            return request;
        }

        Finish(request, RefreshStatus.Failed, error, now);
        return request;
    }

    public RefreshRequest? Cancel(string? id, DateTimeOffset now)
    {
        var request = Load(id);
        if (request is null || request.IsFinished)
        {
            return request;
        }

        Finish(request, RefreshStatus.Cancelled, "Cancelled in the app.", now);
        return request;
    }

    /// <summary>Removes finished or expired request files older than a day.</summary>
    public void Cleanup(DateTimeOffset now)
    {
        if (!System.IO.Directory.Exists(paths.RefreshDirectory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(paths.RefreshDirectory, "*.json"))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
                {
                    File.Delete(file);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void Finish(RefreshRequest request, RefreshStatus status, string? error, DateTimeOffset now)
    {
        request.Status = status;
        request.Error = error;
        request.FinishedAt = now;
        Save(request);
    }

    private string PathFor(string id) => Path.Combine(paths.RefreshDirectory, $"{id}.json");
}
