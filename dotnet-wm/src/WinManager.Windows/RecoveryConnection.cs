using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using Windows.Win32;

namespace WinManager.Windows;

[SupportedOSPlatform("windows10.0.15063")]
internal sealed class RecoveryConnection : IDisposable
{
    private NamedPipeServerStream? pipe;
    private Process? helper;
    private bool failed;
    internal bool Healthy => !failed && helper is { HasExited: false } && pipe is { IsConnected: true };
    internal int? HelperProcessId => helper?.Id;

    internal void Start(string executable)
    {
        if (failed) throw new IOException("Recovery protection has failed for this observer session.");
        if (Healthy) return;
        try
        {
            if (helper is not null) throw new IOException("Recovery helper exited.");
            var name = "WinManager.Recovery." + Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
            if (!RecoveryNative.TryProcessTime((uint)Environment.ProcessId, out var time)) throw new IOException("Cannot identify recovery host.");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(name);
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add(time.ToString(System.Globalization.CultureInfo.InvariantCulture));
            helper = Process.Start(info) ?? throw new IOException("Cannot start recovery helper.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            if (!PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != helper.Id)
                throw new IOException("Recovery peer identity mismatch.");
            if (RecoveryProtocol.ReadAsync(pipe, timeout.Token).GetAwaiter().GetResult() != "READY")
                throw new IOException("Recovery helper did not initialize.");
        }
        catch
        {
            failed = true;
            pipe?.Dispose();
            // Startup cannot have registered any new hides. Do not leave an uninitialized child behind.
            try
            {
                if (helper is { HasExited: false })
                {
                    helper.Kill();
                    helper.WaitForExit(1000);
                }
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            throw;
        }
    }

    internal void Send(RecoveryCommand command)
    {
        try
        {
            if (!Healthy) throw new IOException("Recovery helper is unavailable.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            RecoveryProtocol.WriteAsync(pipe!, JsonSerializer.Serialize(command), timeout.Token).GetAwaiter().GetResult();
            if (RecoveryProtocol.ReadAsync(pipe!, timeout.Token).GetAwaiter().GetResult() != "ACK")
                throw new IOException("Recovery registration was not acknowledged.");
        }
        catch { failed = true; pipe?.Dispose(); throw; }
    }

    internal void Complete()
    {
        Send(new RecoveryCommand("complete"));
        if (helper is not null && !helper.WaitForExit(5000))
        {
            failed = true;
            throw new IOException("Recovery helper did not complete gracefully.");
        }
        pipe?.Dispose();
        helper?.Dispose();
        pipe = null;
        helper = null;
    }

    public void Dispose() { pipe?.Dispose(); helper?.Dispose(); }
}

internal static class RecoveryProtocol
{
    internal static async Task<string> ReadAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var next = new byte[1];
        while (bytes.Count < 65536)
        {
            if (await stream.ReadAsync(next, token).ConfigureAwait(false) == 0) throw new EndOfStreamException();
            if (next[0] == 10) return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(next[0]);
        }
        throw new IOException("Recovery message exceeds limit.");
    }

    internal static async Task WriteAsync(Stream stream, string message, CancellationToken token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message + "\n");
        if (bytes.Length > 65536) throw new IOException("Recovery message exceeds limit.");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}