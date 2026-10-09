using System.Text;
using Microsoft.Extensions.Logging;

namespace WinManager.Host;

public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private readonly object gate = new();
    private readonly string path;
    private readonly int maximumBytes;
    private readonly int fileCount;
    private bool disposed;

    public RotatingFileLoggerProvider(string directory, int maximumBytes, int fileCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(fileCount, 2);
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "manager.log");
        this.maximumBytes = maximumBytes;
        this.fileCount = fileCount;
    }
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { lock (gate) disposed = true; }

    private void Write(string category, LogLevel level, string message, Exception? error)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {level} {category} {message.Replace('\r', ' ').Replace('\n', ' ')} {error}{Environment.NewLine}";
        if (Encoding.UTF8.GetByteCount(line) > maximumBytes / 2)
            line = line[..Math.Min(line.Length, maximumBytes / 8)] + " [truncated]" + Environment.NewLine;
        lock (gate)
        {
            if (disposed) return;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > maximumBytes)
                {
                    for (var index = fileCount - 1; index >= 1; index--)
                    {
                        var source = index == 1 ? path : path + "." + (index - 1);
                        var destination = path + "." + index;
                        if (File.Exists(source)) File.Move(source, destination, overwrite: true);
                    }
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FileLogger(RotatingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}