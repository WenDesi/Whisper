using System.Reflection;
using CoreLogger = WhisperDesk.Core.Diagnostics.MethodTimeLogger;

namespace WhisperDesk.Diagnostics;

/// <summary>
/// Assembly-local interceptor for MethodTimer.Fody (WPF project).
/// Fody requires MethodTimeLogger in the same assembly.
/// Delegates to the Core implementation for span tracking.
/// </summary>
public static class MethodTimeLogger
{
    public static void Log(MethodBase methodBase, TimeSpan elapsed, string message)
    {
        CoreLogger.Log(methodBase, elapsed, message);
    }
}
