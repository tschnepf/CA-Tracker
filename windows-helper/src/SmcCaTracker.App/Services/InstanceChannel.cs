using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SmcCaTracker.Core.Diagnostics;

namespace SmcCaTracker.App.Services;

/// <summary>
/// One CA Tracker per data folder (see <see cref="SmcCaTracker.Core.Links.InstanceNames"/>): the running copy
/// listens on a named pipe that only the same Windows user can open, and a second start hands it what it was
/// started with (a clicked smc-ca-tracker:// link, or nothing: just come forward), then leaves. A message is
/// short text; the running copy reads a link again before acting on it.
/// </summary>
public sealed class InstanceChannel : IDisposable
{
    private const int MaxMessageBytes = 4096;
    private const int MaxFailures = 10;

    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();

    public InstanceChannel(string pipeName)
    {
        this.pipeName = pipeName;
    }

    /// <summary>Listens in the background; <paramref name="received"/> gets each message (on a background thread).</summary>
    public void Start(Action<string> received) => _ = Task.Run(() => ListenAsync(received));

    private async Task ListenAsync(Action<string> received)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                failures = 0;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (await ReadAsync(server, timeout.Token).ConfigureAwait(false) is { } message)
                {
                    received(message);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // A start that gave up half-way, or the name taken by something else: try again, but not forever.
                if (++failures >= MaxFailures)
                {
                    AppLog.Error("instance", "Stopped listening for links and second starts", error);
                    return;
                }

                AppLog.Warn("instance", $"A second start's message couldn't be read: {error.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(failures), stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Everything the other start wrote (it closes its end when done); null when too long or not text.</summary>
    private static async Task<string?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[MaxMessageBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancel).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaxMessageBytes)
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, length);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hands a message to the copy that is running. False when none answers in time (an earlier version that
    /// doesn't listen, or one that is closing).
    /// </summary>
    public static bool TrySend(string pipeName, string message, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);

            // Windows lets only the program in front bring a window forward: this start (just clicked) passes that on.
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var running))
            {
                AllowSetForegroundWindow(running);
            }

            var bytes = Encoding.UTF8.GetBytes(message);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }
}
