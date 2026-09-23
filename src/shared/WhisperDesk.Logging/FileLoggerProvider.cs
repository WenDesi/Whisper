using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Logging;

public sealed class FileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _filePath;
    private readonly LogLevel _minLevel;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Channel<string> _messages = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task _writerTask;
    private Exception? _writeFailure;
    private int _disposed;

    public FileLoggerProvider(string filePath, LogLevel minLevel = LogLevel.Debug)
    {
        _filePath = filePath;
        _minLevel = minLevel;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _writerTask = Task.Run(WriteMessagesAsync);
        _ = _writerTask.ContinueWith(
            task => System.Diagnostics.Trace.TraceError("WhisperDesk file logging failed: {0}", task.Exception),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        WriteToFile($"========== WhisperDesk started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
        WriteToFile($"Log file: {filePath}");
    }

    public static string GetLogPath(string component)
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesk", "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, $"{component}.log");
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this, _minLevel));
    }

    internal void WriteToFile(string message)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_messages.Writer.TryWrite(message))
        {
            throw new IOException("The WhisperDesk log writer is unavailable.", Volatile.Read(ref _writeFailure));
        }
    }

    private async Task WriteMessagesAsync()
    {
        try
        {
            while (await _messages.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new StringBuilder();
                for (var i = 0; i < 128 && _messages.Reader.TryRead(out var message); i++)
                    batch.AppendLine(message);
                await File.AppendAllTextAsync(_filePath, batch.ToString(), Utf8NoBom).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _writeFailure, ex);
            _messages.Writer.TryComplete(ex);
            await Console.Error.WriteLineAsync($"WhisperDesk could not write {_filePath}: {ex.Message}").ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _messages.Writer.TryComplete();
        _loggers.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _writerTask.ConfigureAwait(false);
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _provider;
        private readonly LogLevel _minLevel;

        public FileLogger(string category, FileLoggerProvider provider, LogLevel minLevel)
        {
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
