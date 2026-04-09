using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Core.Diagnostics;

/// <summary>
/// Span-based trace logger. Called automatically by the [Trace] Metalama aspect.
/// Maintains trace/span/parent IDs via AsyncLocal for call chain tracking.
/// </summary>
public static class MethodTimeLogger
{
    private static readonly AsyncLocal<SpanContext?> _current = new();
    private static ILogger? _logger;
    private static long _spanCounter;

    public static void Initialize(ILogger logger) => _logger = logger;

    /// <summary>
    /// Push a new span onto the async-local call stack.
    /// Returns an IDisposable that pops the span and logs duration on dispose.
    /// </summary>
    public static IDisposable BeginSpan([CallerMemberName] string method = "", [CallerFilePath] string filePath = "")
    {
        var parent = _current.Value;
        var traceId = parent?.TraceId ?? GenerateId();
        var spanId = GenerateId();
        var className = Path.GetFileNameWithoutExtension(filePath);
        var ctx = new SpanContext(traceId, spanId, parent, className, method);
        _current.Value = ctx;
        return new SpanScope(ctx);
    }

    private static string GenerateId()
    {
        var id = Interlocked.Increment(ref _spanCounter);
        return id.ToString("x8");
    }

    private sealed class SpanContext
    {
        public string TraceId { get; }
        public string SpanId { get; }
        public SpanContext? Parent { get; }
        public string ClassName { get; }
        public string MethodName { get; }
        public long StartTimestamp { get; }

        public SpanContext(string traceId, string spanId, SpanContext? parent, string className, string methodName)
        {
            TraceId = traceId;
            SpanId = spanId;
            Parent = parent;
            ClassName = className;
            MethodName = methodName;
            StartTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private sealed class SpanScope(SpanContext context) : IDisposable
    {
        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(context.StartTimestamp);
            _current.Value = context.Parent;

            _logger?.LogInformation(
                "[TRACE] {Class}.{Method} trace={TraceId} span={SpanId} parent={ParentSpanId} thread={ThreadId} duration={Duration}ms",
                context.ClassName,
                context.MethodName,
                context.TraceId,
                context.SpanId,
                context.Parent?.SpanId ?? "-",
                Environment.CurrentManagedThreadId,
                elapsed.TotalMilliseconds.ToString("F1"));
        }
    }
}
