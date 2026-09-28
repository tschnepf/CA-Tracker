using System.Text;
using System.Text.Json;
using SmcCaTracker.Core.Models;
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

/// <summary>Handles messages from the extension: "ping" and "snapshot".</summary>
public sealed class NativeHostHandler
{
    private readonly SnapshotStore store;

    public NativeHostHandler(SnapshotStore store)
    {
        this.store = store;
    }

    public string Handle(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            switch (type)
            {
                case "ping":
                    return Respond(new { ok = true, version = TrackerVersion.Current });
                case "snapshot":
                    if (!root.TryGetProperty("snapshot", out var snapshotElement))
                    {
                        return Error("The snapshot message has no snapshot.");
                    }

                    var snapshot = snapshotElement.Deserialize<ProviderSnapshot>(TrackerJson.Options)
                        ?? throw new InvalidDataException("The snapshot was empty.");
                    var merged = store.Save(snapshot);
                    return Respond(new { ok = true, version = TrackerVersion.Current, items = merged.Items.Count });
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

    private static string Error(string message) =>
        Respond(new { ok = false, version = TrackerVersion.Current, error = message });

    private static string Respond(object value) => JsonSerializer.Serialize(value);
}
