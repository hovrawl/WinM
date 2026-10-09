using Microsoft.Win32;
using WinManager.Cli;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void InstallUpdateAndIdempotentUninstallAffectOnlyDisposableValue()
    {
        using var fixture = new RegistrationFixture();
        var otherName = $"WinManager.Test.{Guid.NewGuid():D}";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(StartupRegistration.RunKeyPath))
                key.SetValue(otherName, "untouched", RegistryValueKind.String);

            var expected = $"\"{fixture.Host}\" --config \"{fixture.Config}\"";
            Assert.Equal(expected, StartupRegistration.Write(fixture.Host, fixture.Config, fixture.Name));
            Assert.Equal(expected, fixture.Read());
            using (var key = Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKeyPath))
                Assert.Equal(RegistryValueKind.String, key!.GetValueKind(fixture.Name));

            Assert.Equal(expected, StartupRegistration.Write(fixture.Host, fixture.Config, fixture.Name));
            var updated = $"\"{fixture.Host}\"";
            Assert.Equal(updated, StartupRegistration.Write(fixture.Host, registrationName: fixture.Name));
            Assert.Equal(updated, fixture.Read());
            StartupRegistration.Remove(fixture.Name);
            StartupRegistration.Remove(fixture.Name);
            Assert.Null(fixture.Read());
            using var remaining = Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKeyPath);
            Assert.Equal("untouched", remaining!.GetValue(otherName));
        }
        finally { StartupRegistration.Remove(otherName); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("host.exe")]
    [InlineData(@"\host.exe")]
    [InlineData(@"C:host.exe")]
    [InlineData(@"C:\missing\host.exe")]
    [InlineData("C:\\host.exe\" --config evil.json")]
    [InlineData("C:\\host.exe\0")]
    [InlineData("C:\\host.exe\n")]
    [InlineData(@"C:\host&evil.exe")]
    [InlineData(@"C:\host;evil.exe")]
    [InlineData(@"C:\%TEMP%\host.exe")]
    [InlineData(@"C:\host|evil.exe")]
    [InlineData(@"C:\host^evil.exe")]
    [InlineData(@"C:\host*.exe")]
    public void InvalidHostCannotOverwriteExistingRegistration(string? host)
    {
        using var fixture = new RegistrationFixture();
        var before = StartupRegistration.Write(fixture.Host, registrationName: fixture.Name);
        Assert.ThrowsAny<ArgumentException>(() => StartupRegistration.Write(host!, registrationName: fixture.Name));
        Assert.Equal(before, fixture.Read());
    }

    [Theory]
    [InlineData("")]
    [InlineData("config.json")]
    [InlineData(@"C:\missing\config.json")]
    [InlineData("C:\\config.json\" & evil.exe")]
    [InlineData("C:\\config\0.json")]
    [InlineData("C:\\config\r.json")]
    [InlineData(@"C:\config;evil.json")]
    public void InvalidConfigCannotCreateRegistration(string config)
    {
        using var fixture = new RegistrationFixture();
        Assert.ThrowsAny<ArgumentException>(() => StartupRegistration.Write(fixture.Host, config, fixture.Name));
        Assert.Null(fixture.Read());
    }

    [Theory]
    [InlineData('&')]
    [InlineData(';')]
    [InlineData('^')]
    [InlineData('%')]
    [InlineData('`')]
    public void ShellMetacharactersAreRejectedEvenInExistingFiles(char character)
    {
        using var fixture = new RegistrationFixture();
        var directory = Path.GetDirectoryName(fixture.Host)!;
        var host = Path.Combine(directory, $"host{character}app.exe");
        var config = Path.Combine(directory, $"config{character}file.json");
        File.WriteAllText(host, "not executed");
        File.WriteAllText(config, "{}");
        Assert.Throws<ArgumentException>(() => StartupRegistration.Write(host, registrationName: fixture.Name));
        Assert.Throws<ArgumentException>(() => StartupRegistration.Write(fixture.Host, config, fixture.Name));
        Assert.Null(fixture.Read());
    }

    [Fact]
    public void ExistingWrongExtensionsAndOverlongCommandAreRejected()
    {
        using var fixture = new RegistrationFixture();
        Assert.Throws<ArgumentException>(() => StartupRegistration.Write(fixture.Config, registrationName: fixture.Name));
        Assert.Throws<ArgumentException>(() => StartupRegistration.Write(fixture.Host, fixture.Host, fixture.Name));
        var directory = Path.Combine(Path.GetDirectoryName(fixture.Host)!, new string('a', 65));
        Directory.CreateDirectory(directory);
        var host = Path.Combine(directory, "host.exe");
        var config = Path.Combine(directory, "config.json");
        File.WriteAllText(host, "not executed");
        File.WriteAllText(config, "{}");
        Assert.True($"\"{host}\" --config \"{config}\"".Length > 260);
        Assert.Throws<ArgumentException>(() => StartupRegistration.Write(host, config, fixture.Name));
        Assert.Null(fixture.Read());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OtherApplication")]
    [InlineData(@"WinManager\Other")]
    [InlineData("WinManager.Test.not-a-guid")]
    [InlineData("WinManager.Test.00000000-0000-0000-0000-000000000000.extra")]
    public void RegistrationNamesAreBounded(string? name)
    {
        using var fixture = new RegistrationFixture();
        Assert.ThrowsAny<ArgumentException>(() => StartupRegistration.Write(fixture.Host, registrationName: name!));
        Assert.ThrowsAny<ArgumentException>(() => StartupRegistration.Remove(name!));
        Assert.Null(fixture.Read());
    }

    [Fact]
    public void DefaultsAreNormalPerUserRegistration()
    {
        Assert.Equal("WinManager", StartupRegistration.DefaultRegistrationName);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunKeyPath);
        Assert.Equal("WinManager", typeof(StartupRegistration).GetMethod("Write")!.GetParameters()[2].DefaultValue);
        Assert.Equal("WinManager", typeof(StartupRegistration).GetMethod("Remove")!.GetParameters()[0].DefaultValue);
    }

    [Theory]
    [InlineData("install-startup")]
    [InlineData("install-startup", "--host", "relative.exe")]
    [InlineData("install-startup", "--host", "relative.exe", "--host", "other.exe")]
    [InlineData("install-startup", "--host", "relative.exe", "--pipe", "other")]
    [InlineData("uninstall-startup", "--host", "relative.exe")]
    [InlineData("uninstall-startup", "--registration-name", "Other")]
    public async Task CliRejectsInvalidStartupArgumentsWithoutConnectingOrRegistering(params string[] args)
    {
        Assert.Equal(2, await Program.Main(args));
    }

    private sealed class RegistrationFixture : IDisposable
    {
        private readonly string directory = CreateDirectoryPath();
        public string Name { get; } = $"WinManager.Test.{Guid.NewGuid():D}";
        public string Host => Path.Combine(directory, "host app.exe");
        public string Config => Path.Combine(directory, "config file.json");

        private static string CreateDirectoryPath()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
                root = root.Parent ?? throw new InvalidOperationException("Project root was not found.");
            return Path.Combine(root.FullName, "src", "WinManager.Cli", $"startup {Guid.NewGuid():N}");
        }

        public RegistrationFixture()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Host, "not executed");
            File.WriteAllText(Config, "{}");
        }

        public object? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKeyPath);
            return key?.GetValue(Name);
        }

        public void Dispose()
        {
            try { StartupRegistration.Remove(Name); }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }
}