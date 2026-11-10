using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Pipes;

namespace WinManager.Windows;

[SupportedOSPlatform("windows6.0.6000")]
public static class LocalPipeServer
{
    public static unsafe NamedPipeServerStream Create(string pipeName, int maximumClients, int bufferSize = 65536)
    {
        ArgumentNullException.ThrowIfNull(pipeName);
        if (pipeName.Length is 0 or > 247 || pipeName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new ArgumentException("Pipe name must be a simple ASCII name without path separators.", nameof(pipeName));
        if (maximumClients is < 1 or > 254)
            throw new ArgumentOutOfRangeException(nameof(maximumClients));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Current user SID is unavailable.");
        var descriptor = new RawSecurityDescriptor($"O:{sid.Value}D:P(A;;0x{(int)PipeAccessRights.FullControl:X};;;{sid.Value})");
        var security = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(security, 0);
        var pipe = new SafePipeHandle(IntPtr.Zero, ownsHandle: true);
        try
        {
            fixed (byte* securityPointer = security)
            fixed (char* name = @"\\.\pipe\" + pipeName)
            {
                var attributes = new SECURITY_ATTRIBUTES
                {
                    nLength = (uint)sizeof(SECURITY_ATTRIBUTES),
                    lpSecurityDescriptor = securityPointer,
                    bInheritHandle = false,
                };
                var handle = PInvoke.CreateNamedPipe(name,
                    FILE_FLAGS_AND_ATTRIBUTES.PIPE_ACCESS_DUPLEX | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
                    NAMED_PIPE_MODE.PIPE_REJECT_REMOTE_CLIENTS | NAMED_PIPE_MODE.PIPE_TYPE_BYTE |
                    NAMED_PIPE_MODE.PIPE_READMODE_BYTE | NAMED_PIPE_MODE.PIPE_WAIT,
                    (uint)maximumClients, (uint)bufferSize, (uint)bufferSize, 0, &attributes);
                Marshal.InitHandle(pipe, (IntPtr)handle.Value);
                if (pipe.IsInvalid)
                    throw new IOException("Cannot create private local pipe.");
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }
}