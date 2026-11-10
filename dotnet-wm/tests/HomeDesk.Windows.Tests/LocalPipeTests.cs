using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace WinManager.Windows.Tests;

public sealed class LocalPipeTests
{
    [Fact]
    public async Task NativeServerAcceptsVerifiedLocalClientAndTransfersBytes()
    {
        var name = "WinManager.NativePipeTest." + Guid.NewGuid().ToString("N");
        using var server = LocalPipeServer.Create(name, 1);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accepting;
        Assert.True(server.IsAsync);
        Assert.Equal(PipeTransmissionMode.Byte, server.TransmissionMode);
        Assert.True(LocalPipeClient.IsCurrentSession(server.SafePipeHandle, out var detail), detail);

        await client.WriteAsync(new byte[] { 42 }, timeout.Token);
        var buffer = new byte[1];
        Assert.Equal(1, await server.ReadAsync(buffer, timeout.Token));
        Assert.Equal(42, buffer[0]);

        using var current = WindowsIdentity.GetCurrent();
        SecurityIdentifier? clientSid = null;
        server.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            clientSid = identity.User;
        });
        Assert.Equal(current.User, clientSid);

        await server.WriteAsync(new byte[] { 73 }, timeout.Token);
        Assert.Equal(1, await client.ReadAsync(buffer, timeout.Token));
        Assert.Equal(73, buffer[0]);
    }

    [Fact]
    public void NativeServerHasProtectedCurrentUserOnlyDacl()
    {
        using var server = LocalPipeServer.Create("WinManager.NativePipeTest." + Guid.NewGuid().ToString("N"), 1);
        using var current = WindowsIdentity.GetCurrent();
        var security = server.GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>());
        Assert.Equal(current.User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
        Assert.False(rule.IsInherited);
    }

    [Fact]
    public async Task UnconnectedPipeFailsClientValidationAndWaitCanBeCancelled()
    {
        using var server = LocalPipeServer.Create("WinManager.NativePipeTest." + Guid.NewGuid().ToString("N"), 1);
        Assert.False(LocalPipeClient.IsCurrentSession(server.SafePipeHandle));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.WaitForConnectionAsync(timeout.Token));
    }

    [Fact]
    public void NativeServerEnforcesInstanceLimitAndDisposalReleasesName()
    {
        var name = "WinManager.NativePipeTest." + Guid.NewGuid().ToString("N");
        var server = LocalPipeServer.Create(name, 1);
        var handle = server.SafePipeHandle;
        try { Assert.Throws<IOException>(() => LocalPipeServer.Create(name, 1)); }
        finally { server.Dispose(); }
        Assert.True(handle.IsClosed);
        using var replacement = LocalPipeServer.Create(name, 1);
        Assert.False(replacement.SafePipeHandle.IsInvalid);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(254)]
    public void NativeServerAllowsMultipleBoundedInstances(int maximumClients)
    {
        var name = "WinManager.NativePipeTest." + Guid.NewGuid().ToString("N");
        using var first = LocalPipeServer.Create(name, maximumClients);
        using var second = LocalPipeServer.Create(name, maximumClients);
        Assert.False(second.SafePipeHandle.IsInvalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nested\\pipe")]
    [InlineData("nested/pipe")]
    [InlineData("\\\\server\\pipe\\name")]
    [InlineData("name\0suffix")]
    [InlineData("name with spaces")]
    public void RejectsNonSimpleNames(string name) => Assert.Throws<ArgumentException>(() => LocalPipeServer.Create(name, 1));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(255)]
    public void RejectsUnboundedOrInvalidInstanceCounts(int maximumClients) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalPipeServer.Create("WinManager.Test", maximumClients));

    [Fact]
    public void RejectsNullOversizedNamesAndInvalidBuffers()
    {
        Assert.Throws<ArgumentNullException>(() => LocalPipeServer.Create(null!, 1));
        Assert.Throws<ArgumentException>(() => LocalPipeServer.Create(new string('a', 248), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalPipeServer.Create("WinManager.Test", 1, 0));
    }
}