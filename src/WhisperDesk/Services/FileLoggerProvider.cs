using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

/// <summary>
/// Simple file logger that writes all log messages to a fixed file.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly LogLevel _minLevel;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _writeLock = new();

    public FileLoggerProvider(string filePath, LogLevel minLevel = LogLevel.Debug)
    {
        _filePath = filePath;
        _minLevel = minLevel;

        // Ensure directory exists
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Write startup marker
        WriteToFile($"========== WhisperDesk started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
        WriteToFile($"Log file: {filePath}");
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this, _minLevel));
    }

    internal void WriteToFile(string message)
    {
        lock (_writeLock)
        {
            File.AppendAllText(_filePath, message + Environment.NewLine);
        }
    }

    public void Dispose()
    {
        _loggers.Clear();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _provider;
        private readonly LogLevel _minLevel;

        public FileLogger(string category, FileLoggerProvider provider, LogLevel minLevel)
        {
            // Shorten category name: "WhisperDesk.Services.AzureSpeechService" -> "AzureSpeechService"
            _category = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            _provider = provider;
            _minLevel = minLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };

            var message = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {_category}: {formatter(state, exception)}";
            if (exception != null)
                message += Environment.NewLine + exception;

            _provider.WriteToFile(message);
        }
    }
}
