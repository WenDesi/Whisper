using Metalama.Framework.Aspects;

namespace WhisperDesk.Core.Diagnostics;

/// <summary>
/// Metalama aspect that automatically injects MethodTimeLogger.BeginSpan()
/// at method entry and disposes on exit. Works with sync and async methods.
///
/// Usage: Just add [Trace] to any method. No other code needed.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class TraceAttribute : OverrideMethodAspect
{
    public override dynamic? OverrideMethod()
    {
        using var _span = MethodTimeLogger.BeginSpan();
        return meta.Proceed();
    }
}
