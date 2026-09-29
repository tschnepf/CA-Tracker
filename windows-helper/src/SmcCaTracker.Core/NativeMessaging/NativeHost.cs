using System.Text;
using System.Text.Json;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.NativeMessaging;

/// <summary>
/// Chrome native messaging framing: a 4-byte length in native byte order (little-endian on
/// Windows) followed by UTF-8 JSON. Chrome allows 64 MiB to the host and 1 MB back.
/// </summary>
public static class NativeMessageProtocol
{
    public const int MaxIncomingBytes = 16 * 1024 * 1024;
    public const int MaxOutgoingBytes = 1024 * 1024;

    public static async Task<string?> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(input, header, cancellationToken))
        {
            return null;
        }

        var length = BitConverter.ToInt32(header, 0);
        if (length < 0 || length > MaxIncomingBytes)
        {
            throw new InvalidDataException($"Native message length {length} is outside the allowed range.");
        }

        var body = new byte[length];
        if (length > 0 && !await ReadExactlyOrEndAsync(input, body, cancellationToken))
        {
            throw new EndOfStreamException("Chrome closed the connection in the middle of a message.");
        }

        return Encoding.UTF8.GetString(body);
    }

    public static async Task WriteAsync(Stream output, string json, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxOutgoingBytes)
        {
            throw new InvalidDataException("Native message responses must be smaller than 1 MB.");
        }

        await output.WriteAsync(BitConverter.GetBytes(body.Length), cancellationToken);
        await output.WriteAsync(body, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await input.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Chrome closed the connection in the middle of a message.");
            }

            read += count;
        }

        return true;
    }
}

/// <summary>
/// Handles messages from the extension: "ping", "snapshot" (optionally completing a refresh step),
/// "claimRefresh" and "refreshFailed".
/// </summary>
public sealed class NativeHostHandler
{
    private readonly AppPaths paths;
    private readonly SnapshotStore store;
    private readonly RefreshRequestStore refreshes;
    private readonly ProjectCatalogStore catalog;
    private readonly Func<DateTimeOffset> clock;

    public NativeHostHandler(AppPaths paths, Func<DateTimeOffset>? clock = null)
    {
        this.paths = paths;
        store = new SnapshotStore(paths);
        refreshes = new RefreshRequestStore(paths);
        catalog = new ProjectCatalogStore(paths);
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string Handle(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            var now = clock();
            ExtensionContact.Record(paths, type ?? "unknown", now);
            switch (type)
            {
                case "ping":
                    return Respond(new { ok = true, version = TrackerVersion.Current });
                case "snapshot":
                    return HandleSnapshot(root, now);
                case "claimRefresh":
                    return HandleClaim(root, now);
                case "refreshFailed":
                {
                    var id = OptionalString(root, "id");
                    var error = OptionalString(root, "error") ?? "The extension could not finish the refresh.";
                    refreshes.Fail(id, error.Length > 500 ? error[..500] : error, now);
                    return Respond(new { ok = true, version = TrackerVersion.Current });
                }
                default:
                    return Error("Unsupported message type.");
            }
        }
        catch (Exception error)
        {
            // Every failure is reported back to the extension instead of ending the host.
            return Error(error.Message);
        }
    }

    private string HandleSnapshot(JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("snapshot", out var snapshotElement))
        {
            return Error("The snapshot message has no snapshot.");
        }

        var snapshot = snapshotElement.Deserialize<ProviderSnapshot>(TrackerJson.Options)
            ?? throw new InvalidDataException("The snapshot was empty.");
        var merged = store.Save(snapshot);

        if (root.TryGetProperty("catalog", out var catalogElement) && catalogElement.ValueKind == JsonValueKind.Object)
        {
            catalog.Merge(catalogElement.Deserialize<CatalogMessage>(TrackerJson.Options), now);
        }

        object? refresh = null;
        if (root.TryGetProperty("refresh", out var refreshElement) && refreshElement.ValueKind == JsonValueKind.Object)
        {
            var id = OptionalString(refreshElement, "id");
            var step = refreshElement.TryGetProperty("step", out var stepElement)
                && stepElement.ValueKind == JsonValueKind.Number
                && stepElement.TryGetInt32(out var value) ? value : 0;
            var result = refreshes.CompleteStep(id, step, snapshot, now);
            if (result is not null)
            {
                refresh = new
                {
                    done = result.Done,
                    nextUrl = result.NextUrl,
                    closeTab = result.Done && result.Request.CloseTabWhenDone && result.Request.Status == RefreshStatus.Complete,
                    status = result.Request.Status.ToString(),
                    error = result.Request.Error
                };
            }
        }

        return Respond(new { ok = true, version = TrackerVersion.Current, items = merged.Items.Count, refresh });
    }

    private string HandleClaim(JsonElement root, DateTimeOffset now)
    {
        var provider = OptionalString(root, "provider") ?? "";
        var claim = refreshes.Claim(OptionalString(root, "id"), provider, now);
        if (claim.Request is null)
        {
            return Error(claim.Error ?? "The refresh could not be claimed.");
        }

        var request = claim.Request;
        var step = Math.Min(request.CompletedSteps, request.Steps.Count - 1);
        return Respond(new
        {
            ok = true,
            version = TrackerVersion.Current,
            request = new
            {
                id = request.Id,
                provider = request.Provider,
                projectId = request.ProviderProjectId,
                accountId = request.AccountId,
                projectName = request.ProjectName,
                step,
                stepCount = request.Steps.Count,
                itemTypes = request.Steps[step].ItemTypes
            }
        });
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Error(string message) =>
        Respond(new { ok = false, version = TrackerVersion.Current, error = message });

    private static string Respond(object value) => JsonSerializer.Serialize(value);
}
