using Microsoft.Win32;

namespace WinManager.Cli;

public static class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultRegistrationName = "WinManager";

    public static string Write(string hostPath, string? configPath = null,
        string registrationName = DefaultRegistrationName)
    {
        ValidateRegistrationName(registrationName);
        var host = ValidatePath(hostPath, ".exe", nameof(hostPath));
        var commandLine = $"\"{host}\"";
        if (configPath is not null)
            commandLine += $" --config \"{ValidatePath(configPath, ".json", nameof(configPath))}\"";
        if (commandLine.Length > 260)
            throw new ArgumentException("The Windows Run command line cannot exceed 260 characters.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(registrationName, commandLine, RegistryValueKind.String);
        return commandLine;
    }

    public static void Remove(string registrationName = DefaultRegistrationName)
    {
        ValidateRegistrationName(registrationName);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(registrationName, throwOnMissingValue: false);
    }

    private static string ValidatePath(string path, string extension, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Any(character => char.IsControl(character) || "\"<>|?*&;^%`".Contains(character)) ||
            !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute file path without quotes, control characters, wildcards, or shell metacharacters is required.", parameterName);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), extension, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
            throw new ArgumentException($"An existing {extension} file is required.", parameterName);
        return fullPath;
    }

    private static void ValidateRegistrationName(string registrationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationName);
        const string prefix = "WinManager.Test.";
        if (registrationName != DefaultRegistrationName &&
            !(registrationName.StartsWith(prefix, StringComparison.Ordinal) &&
              registrationName.Length == prefix.Length + 36 &&
              Guid.TryParseExact(registrationName[prefix.Length..], "D", out _)))
            throw new ArgumentException("Registration name must be WinManager or WinManager.Test.<guid>.", nameof(registrationName));
    }
}