using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Zvt2SumUp.Core;

namespace Zvt2SumUp.Infrastructure;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string directory; private readonly string baseName; private readonly long maximumBytes;
    private readonly object writeLock = new(); private readonly ConcurrentDictionary<string, RollingFileLogger> loggers = new();
    private StreamWriter? writer; private string currentDate = string.Empty;
    public RollingFileLoggerProvider(string path, long maximumBytes = 10 * 1024 * 1024)
    { directory = System.IO.Path.GetDirectoryName(path) ?? AppPaths.Logs; baseName = System.IO.Path.GetFileNameWithoutExtension(path); this.maximumBytes = maximumBytes; Directory.CreateDirectory(directory); }
    public ILogger CreateLogger(string categoryName) => loggers.GetOrAdd(categoryName, name => new(this, name));
    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (writeLock)
        {
            EnsureWriter(); string safe = SensitiveDataRedactor.Redact(message + (exception is null ? string.Empty : " | " + exception));
            writer!.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level,-11}] {category} [{eventId.Id}]: {safe}"); writer.Flush();
        }
    }
    private void EnsureWriter()
    {
        string date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (writer is not null && currentDate == date && writer.BaseStream.Length < maximumBytes) return;
        writer?.Dispose(); currentDate = date; int suffix = 0; string file;
        do { file = System.IO.Path.Combine(directory, $"{baseName}-{date}{(suffix == 0 ? string.Empty : $"-{suffix}")}.log"); suffix++; }
        while (File.Exists(file) && new FileInfo(file).Length >= maximumBytes);
        writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }
    public void Dispose() { lock (writeLock) writer?.Dispose(); }
    private sealed class RollingFileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(logLevel)) owner.Write(category, logLevel, eventId, formatter(state, exception), exception); }
    }
}

public static class RuntimeLogging
{
    public static LogLevel ParseLevel(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToUpperInvariant() switch
        {
            "TRACE" => LogLevel.Trace,
            "DEBUG" => LogLevel.Debug,
            "WARNING" or "WARN" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            "CRITICAL" or "FATAL" => LogLevel.Critical,
            _ => LogLevel.Information
        };
    }
}
