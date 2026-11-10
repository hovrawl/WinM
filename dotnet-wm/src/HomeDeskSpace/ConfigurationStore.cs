using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinManager.Contracts;

namespace WinManager.Host;

public sealed class ConfigurationConflictException(string message) : Exception(message);

public sealed class ConfigurationStore
{
    public const int MaximumIpcConfigurationBytes = Protocol.MaximumConfigurationContentBytes;

    private readonly string path;
    private readonly object sync = new();

    public ConfigurationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public ConfigurationDocument Read()
    {
        lock (sync)
        {
            var bytes = ReadBytes();
            var content = Encoding.UTF8.GetString(bytes);
            var isValid = true;
            string? validationError = null;
            try
            {
                _ = ManagerConfiguration.Parse(content);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
            {
                isValid = false;
                validationError = error.Message;
            }

            return new ConfigurationDocument(path, content, Revision(bytes), isValid, validationError);
        }
    }

    public ConfigurationDocument Save(string expectedRevision, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        ArgumentNullException.ThrowIfNull(content);
        var contentBytes = new UTF8Encoding(false).GetBytes(content);
        if (contentBytes.Length > MaximumIpcConfigurationBytes)
            throw new InvalidDataException($"Configuration must be no larger than {MaximumIpcConfigurationBytes} UTF-8 bytes for editing over IPC.");
        _ = ManagerConfiguration.Parse(content);

        lock (sync)
        {
            var currentBytes = ReadBytes();
            if (!string.Equals(Revision(currentBytes), expectedRevision, StringComparison.Ordinal))
                throw new ConfigurationConflictException("The configuration file changed since it was loaded. Reload before saving.");

            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    stream.Write(contentBytes);
                    stream.Flush(flushToDisk: true);
                }

                // Recheck immediately before replacement to catch external edits during the write.
                currentBytes = ReadBytes();
                if (!string.Equals(Revision(currentBytes), expectedRevision, StringComparison.Ordinal))
                    throw new ConfigurationConflictException("The configuration file changed while saving. Reload before saving.");
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        return Read();
    }

    private byte[] ReadBytes()
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumIpcConfigurationBytes)
            throw new InvalidDataException($"Configuration exceeds the {MaximumIpcConfigurationBytes}-byte IPC editing limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
