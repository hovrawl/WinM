using System.Runtime.Versioning;
using Xunit;

namespace WinManager.Windows.Tests;

[SupportedOSPlatform("windows10.0.15063")]
public sealed class RecoveryProtocolTests
{
    [Theory]
    [InlineData("WinManager.Recovery...\\outside", "1", "1")]
    [InlineData("WinManager.Recovery.00000000000000000000000000000000", "0", "1")]
    [InlineData("WinManager.Recovery.00000000000000000000000000000000", "4294967295", "1")]
    [InlineData("WinManager.Recovery.00000000000000000000000000000000", "1", "invalid")]
    public async Task RejectsInvalidHelperArgumentsBeforeTouchingDesktop(string pipe, string pid, string creationTime) =>
        Assert.Equal(2, await WindowsRecovery.RunAsync([pipe, pid, creationTime]));

    [Fact]
    public async Task PipeMessagesAreBoundedAndTruncatedInputIsRejected()
    {
        using var oversized = new MemoryStream(new byte[65536]);
        await Assert.ThrowsAsync<IOException>(() => RecoveryProtocol.ReadAsync(oversized, CancellationToken.None));
        using var truncated = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("ACK"));
        await Assert.ThrowsAsync<EndOfStreamException>(() => RecoveryProtocol.ReadAsync(truncated, CancellationToken.None));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => RecoveryProtocol.WriteAsync(output, new string('x', 65536), CancellationToken.None));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task PipeRoundTripPreservesMessages()
    {
        using var stream = new MemoryStream();
        await RecoveryProtocol.WriteAsync(stream, "ACK", CancellationToken.None);
        stream.Position = 0;
        Assert.Equal("ACK", await RecoveryProtocol.ReadAsync(stream, CancellationToken.None));
    }
}