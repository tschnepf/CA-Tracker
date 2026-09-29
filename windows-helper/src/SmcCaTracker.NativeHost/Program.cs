using SmcCaTracker.Core.NativeMessaging;
using SmcCaTracker.Core.Storage;

// Chrome starts this process when the SMC-CA-Tracker extension sends a message and talks to
// it over stdin/stdout. Nothing else may be written to stdout.
var paths = AppPaths.Default;
var handler = new NativeHostHandler(paths);

try
{
    await using var input = Console.OpenStandardInput();
    await using var output = Console.OpenStandardOutput();
    while (true)
    {
        var request = await NativeMessageProtocol.ReadAsync(input, CancellationToken.None);
        if (request is null)
        {
            return 0;
        }

        await NativeMessageProtocol.WriteAsync(output, handler.Handle(request), CancellationToken.None);
    }
}
catch (Exception error)
{
    try
    {
        Directory.CreateDirectory(paths.Root);
        File.AppendAllText(paths.HostLogFile, $"{DateTimeOffset.Now:O} {error.GetType().Name}: {error.Message}{Environment.NewLine}");
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }

    return 1;
}
