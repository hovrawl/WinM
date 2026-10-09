using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Windows.Win32;

namespace WinManager.Windows;

[SupportedOSPlatform("windows10.0.15063")]
public static class WindowsRecovery
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || args[0].Length != "WinManager.Recovery.".Length + 32 ||
            !args[0].StartsWith("WinManager.Recovery.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[0]["WinManager.Recovery.".Length..], "N", out _) ||
            !uint.TryParse(args[1], out var pid) || pid == 0 || pid > int.MaxValue ||
            !ulong.TryParse(args[2], out var time)) return 2;
        var journal = new RecoveryJournal(pid, time, []);
        if (!RecoveryNative.HostAlive(journal)) return 2;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinManager", "Recovery");
        Directory.CreateDirectory(directory);
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var user = identity.User ?? throw new IOException("Cannot identify recovery journal owner.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        RecoverStale(directory);
        var path = Path.Combine(directory, args[0] + ".json");
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var death = new CancellationTokenSource();
        var monitor = MonitorHostAsync(journal, death);
        var completed = false;
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(death.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            if (!PInvoke.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != pid)
            {
                completed = true;
                return 2;
            }
            Save(path, journal);
            await RecoveryProtocol.WriteAsync(pipe, "READY", death.Token).ConfigureAwait(false);
            while (!death.IsCancellationRequested)
            {
                var text = await RecoveryProtocol.ReadAsync(pipe, death.Token).ConfigureAwait(false);
                var command = JsonSerializer.Deserialize<RecoveryCommand>(text) ?? throw new IOException("Invalid recovery command.");
                if (command.Kind == "register" && command.Record is { } record)
                {
                    if (journal.Records.Count >= 4096 || !ValidRecord(record) || !RecoveryNative.Matches(record)) throw new IOException("Invalid recovery target.");
                    if (!journal.Records.Contains(record)) journal.Records.Add(record);
                    Save(path, journal);
                }
                else if (command.Kind == "remove" && command.Record is { } removed)
                {
                    journal.Records.RemoveAll(record => record.Property == removed.Property);
                    Save(path, journal);
                }
                else if (command.Kind == "confirmed" && command.Record is { HideConfirmed: true } confirmed &&
                    journal.Records.FindIndex(record => record.Property == confirmed.Property &&
                        record with { HideConfirmed = true } == confirmed) is >= 0 and var index)
                {
                    journal.Records[index] = confirmed;
                    Save(path, journal);
                }
                else if (command.Kind == "complete" && journal.Records.Count == 0)
                {
                    File.Delete(path);
                    completed = true;
                }
                else throw new IOException("Invalid recovery command.");
                await RecoveryProtocol.WriteAsync(pipe, "ACK", death.Token).ConfigureAwait(false);
                if (completed) return 0;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) { }
        finally
        {
            if (completed)
            {
                death.Cancel();
                await monitor.ConfigureAwait(false);
                lease.Dispose();
                File.Delete(path + ".lock");
            }
        }
        if (journal.Records.Count == 0) death.Cancel();
        // A pipe disconnect is not host death. Keep protecting records until the exact host exits.
        await monitor.ConfigureAwait(false);
        for (var retry = 0; retry < 30 && journal.Records.Count != 0; retry++)
        {
            RestoreJournal(journal);
            Save(path, journal);
            if (journal.Records.Count != 0) await Task.Delay(1000).ConfigureAwait(false);
        }
        if (journal.Records.Count == 0)
        {
            File.Delete(path);
            lease.Dispose();
            File.Delete(path + ".lock");
        }
        return journal.Records.Count == 0 ? 0 : 1;
    }

    private static async Task MonitorHostAsync(RecoveryJournal journal, CancellationTokenSource death)
    {
        while (!death.IsCancellationRequested && RecoveryNative.HostAlive(journal))
            await Task.Delay(100).ConfigureAwait(false);
        death.Cancel();
    }

    private static void Save(string path, RecoveryJournal journal)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
    }

    private static void RestoreJournal(RecoveryJournal journal)
    {
        foreach (var record in journal.Records.ToArray())
        {
            if (!RecoveryNative.Restore(record)) continue;
            RecoveryNative.Unmark(record);
            journal.Records.Remove(record);
        }
    }

    private static bool ValidRecord(RecoveryRecord? record) => record is not null && record.ProcessId != 0 && record.ProcessId <= int.MaxValue &&
        record.Handle != 0 && record.CreationTime != 0 && record.ShowCommand is 3 or 4 or 7 &&
        record.Property is { Length: 52 } && record.Property.StartsWith("WinManager.Recovery.", StringComparison.Ordinal) &&
        Guid.TryParseExact(record.Property["WinManager.Recovery.".Length..], "N", out _);

    private static void RecoverStale(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "WinManager.Recovery.*.json").Take(1024))
        {
            try
            {
                using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (new FileInfo(path).Length > 2 * 1024 * 1024) continue;
                var journal = JsonSerializer.Deserialize<RecoveryJournal>(File.ReadAllBytes(path));
                if (journal is null || journal.HostId == 0 || journal.HostId > int.MaxValue || journal.HostCreationTime == 0 || journal.Records is null ||
                    journal.Records.Count > 4096 || journal.Records.Any(record => !ValidRecord(record)) ||
                    RecoveryNative.HostAlive(journal)) continue;
                RestoreJournal(journal);
                if (journal.Records.Count == 0)
                {
                    File.Delete(path);
                    lease.Dispose();
                    File.Delete(path + ".lock");
                }
                else Save(path, journal);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }
}