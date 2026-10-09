using System.Text.Json;
using WinManager.Client;
using WinManager.Contracts;

namespace WinManager.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var options = Parse(args);
            if (options.Verb == "help") { Console.WriteLine(Help); return 0; }
            if (options.Verb == "install-startup")
            {
                var commandLine = StartupRegistration.Write(options.Host!, options.Config);
                Print(new { success = true, registrationName = StartupRegistration.DefaultRegistrationName, commandLine });
                return 0;
            }
            if (options.Verb == "uninstall-startup")
            {
                StartupRegistration.Remove();
                Print(new { success = true, registrationName = StartupRegistration.DefaultRegistrationName });
                return 0;
            }
            if (options.Verb == "watch")
            {
                await foreach (var envelope in WinManagerClient.WatchAsync(options.EventTypes, options.Pipe,
                    options.Reconnect, cancellationToken: cancellation.Token))
                    Print(envelope);
                return 0;
            }
            ManagerCommand? command = null;
            if (options.Verb == "command")
            {
                var json = options.CommandJson == "-"
                    ? await Console.In.ReadToEndAsync(cancellation.Token) : options.CommandJson!;
                command = JsonSerializer.Deserialize<ManagerCommand>(json, CommandJsonOptions)
                    ?? throw new ArgumentException("Command JSON must be an object.");
                if (!Enum.IsDefined(command.Kind)) throw new ArgumentException("Unknown command kind.");
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("kind", out _)) throw new ArgumentException("Command JSON requires 'kind'.");
            }
            await using var client = await WinManagerClient.ConnectAsync(options.Pipe, cancellationToken: cancellation.Token);
            if (command is null) Print(await client.QuerySnapshotAsync(cancellation.Token));
            else
            {
                var result = await client.ExecuteAsync(command, cancellation.Token);
                Print(result);
                if (!result.Success) return 3;
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            PrintError("usage", error.Message);
            return 2;
        }
        catch (WinManagerRemoteException error)
        {
            PrintError(error.ErrorCode, error.Message);
            return 3;
        }
        catch (Exception error) when (error is System.Security.SecurityException ||
            (args.FirstOrDefault() is "install-startup" or "uninstall-startup" &&
             error is IOException or UnauthorizedAccessException or PlatformNotSupportedException))
        {
            PrintError("startup_error", error.Message);
            return 4;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or
            WinManagerProtocolException or WinManagerConsumerLagException or PlatformNotSupportedException)
        {
            PrintError(error is WinManagerConsumerLagException ? "consumer_lag" :
                error is WinManagerProtocolException ? "protocol_error" : "connection_error", error.Message);
            return 4;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static readonly JsonSerializerOptions CommandJsonOptions = CreateCommandJsonOptions();

    private static JsonSerializerOptions CreateCommandJsonOptions()
    {
        var options = new JsonSerializerOptions(Protocol.JsonOptions)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        };
        return options;
    }

    private static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, Protocol.JsonOptions));
    private static void PrintError(string errorCode, string message) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(new { errorCode, message }, Protocol.JsonOptions));

    private sealed record Options(string Verb, string? Pipe, string? CommandJson, string[]? EventTypes, bool Reconnect,
        string? Host = null, string? Config = null);

    private static Options Parse(string[] args)
    {
        if (args.Length == 0) return new Options("help", null, null, null, false);
        if (args.Length == 1 && args[0] is "help" or "--help" or "-h") return new Options("help", null, null, null, false);
        var verb = args[0];
        if (verb is not ("snapshot" or "command" or "watch" or "install-startup" or "uninstall-startup"))
            throw new ArgumentException($"Unknown command '{verb}'. Use --help.");
        string? pipe = null;
        string? json = null;
        string[]? types = null;
        string? host = null;
        string? config = null;
        var reconnect = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host" when verb == "install-startup" && host is null:
                    host = TakeValue(args, ref i);
                    break;
                case "--config" when verb == "install-startup" && config is null:
                    config = TakeValue(args, ref i);
                    break;
                case "--pipe" when verb is "snapshot" or "command" or "watch" && pipe is null:
                    pipe = TakeValue(args, ref i);
                    ArgumentException.ThrowIfNullOrWhiteSpace(pipe);
                    break;
                case "--event-types" when verb == "watch" && types is null:
                    types = TakeValue(args, ref i).Split(',', StringSplitOptions.TrimEntries);
                    if (types.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Event types must be a nonempty comma-separated list.");
                    break;
                case "--reconnect" when verb == "watch" && !reconnect:
                    reconnect = true;
                    break;
                default:
                    if (verb == "command" && json is null && !args[i].StartsWith("--", StringComparison.Ordinal)) json = args[i];
                    else throw new ArgumentException($"Unexpected or duplicate argument '{args[i]}'. Use --help.");
                    break;
            }
        }
        if (verb == "command" && json is null) throw new ArgumentException("command requires one JSON ManagerCommand argument.");
        if (verb == "install-startup" && host is null) throw new ArgumentException("install-startup requires --host with an absolute existing .exe path.");
        return new Options(verb, pipe, json, types, reconnect, host, config);
    }

    private static string TakeValue(string[] args, ref int index)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Option requires a value.");
        return args[index];
    }

    private const string Help = """
        WinManager CLI
          snapshot [--pipe NAME]
          command JSON [--pipe NAME]
          watch [--event-types TYPE,TYPE] [--reconnect] [--pipe NAME]
          install-startup --host ABSOLUTE_EXE [--config ABSOLUTE_JSON]
          uninstall-startup
          help | --help | -h

        Outputs compact JSON; watch outputs one EventEnvelope per line (full snapshot payload).
        JSON commands use the ManagerCommand schema, e.g. {"kind":"pause","paused":true}.
        Use command - to read JSON from stdin (portable across PowerShell versions):
          '{"kind":"pause","paused":true}' | dotnet .\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll command -
        Startup is opt-in: writes only HKCU Software\Microsoft\Windows\CurrentVersion\Run\WinManager.
        Host and optional config must be existing absolute .exe/.json paths; no shell commands.
        No elevation, service, scheduled task, or automatic registration. Uninstall is idempotent.
        Example watch filter: --event-types pause.changed
        --reconnect retries transport disconnects with a fresh initial snapshot and instance ID.
        Protocol errors and consumer lag are fatal; events are never silently dropped.
        Exit codes: 0 success/help, 2 usage/JSON, 3 server rejection, 4 IPC/protocol/lag/startup failure, 130 Ctrl+C.
        """;
}