using System.Reflection;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Core.Diagnostics;

/// <summary>
/// Interceptor for MethodTimer.Fody + span tracking.
///
/// Usage: Add [Time] attribute to method, plus one line:
///   using var _span = MethodTimeLogger.BeginSpan();
///
/// The Fody weaver calls Log() at method exit with the elapsed time.
/// BeginSpan() pushes a trace/span context; Log() pops and logs with IDs.
/// </summary>
public static class MethodTimeLogger
{
    private static readonly AsyncLocal<SpanContext?> _current = new();
    private static ILogger? _logger;
    private static long _spanCounter;

    public static void Initialize(ILogger logger) => _logger = logger;

    /// <summary>
    /// Push a new span onto the async-local call stack.
    /// Call as: using var _span = MethodTimeLogger.BeginSpan();
    /// </summary>
    public static IDisposable BeginSpan()
    {
        var parent = _current.Value;
        var traceId = parent?.TraceId ?? GenerateId();
        var spanId = GenerateId();
        var ctx = new SpanContext(traceId, spanId, parent);
        _current.Value = ctx;
        return new SpanScope(ctx);
    }

    /// <summary>
    /// Called automatically by MethodTimer.Fody at method exit.
    /// </summary>
    public static void Log(MethodBase methodBase, TimeSpan elapsed, string message)
    {
        var ctx = _current.Value;
        var traceId = ctx?.TraceId ?? "-";
        var spanId = ctx?.SpanId ?? "-";
        var parentSpanId = ctx?.Parent?.SpanId ?? "-";
        var className = methodBase.DeclaringType?.Name ?? "?";

        _logger?.LogInformation(
            "[TRACE] {Class}.{Method} trace={TraceId} span={SpanId} parent={ParentSpanId} thread={ThreadId} duration={Duration}ms",
            className,
            methodBase.Name,
            traceId,
            spanId,
            parentSpanId,
            Environment.CurrentManagedThreadId,
            elapsed.TotalMilliseconds.ToString("F1"));
    }

    private static string GenerateId()
    {
        var id = Interlocked.Increment(ref _spanCounter);
        return id.ToString("x8");
    }

    private sealed class SpanContext(string traceId, string spanId, SpanContext? parent)
    {
        public string TraceId => traceId;
        public string SpanId => spanId;
        public SpanContext? Parent => parent;
    }

    private sealed class SpanScope(SpanContext context) : IDisposable
    {
        public void Dispose() => _current.Value = context.Parent;
    }
}
