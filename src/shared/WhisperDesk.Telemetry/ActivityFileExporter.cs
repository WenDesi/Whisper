using System.Diagnostics;
using System.Text;
using OpenTelemetry;

namespace WhisperDesk.Telemetry;

internal sealed class ActivityFileExporter : BaseExporter<Activity>
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _filePath;
    private readonly object _writeLock = new();

    public ActivityFileExporter(string filePath)
    {
        _filePath = filePath;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        WriteLine($"========== Telemetry span log started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
        WriteLine($"Log file: {filePath}");
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            WriteActivity(activity);
        }

        return ExportResult.Success;
    }

    private void WriteActivity(Activity activity)
    {
        var sb = new StringBuilder(512);
        sb.AppendFormat(
            "{0:HH:mm:ss.fff} span name={1} kind={2} duration_ms={3:F3} trace_id={4} span_id={5} parent_span_id={6} status={7}",
            DateTime.Now,
            activity.OperationName,
            activity.Kind,
            activity.Duration.TotalMilliseconds,
            activity.TraceId,
            activity.SpanId,
            activity.ParentSpanId,
            activity.Status);

        if (!string.IsNullOrWhiteSpace(activity.StatusDescription))
        {
            sb.Append(" status_description=").Append(activity.StatusDescription);
        }

        if (!string.IsNullOrWhiteSpace(activity.DisplayName) &&
            !string.Equals(activity.DisplayName, activity.OperationName, StringComparison.Ordinal))
        {
            sb.Append(" display_name=").Append(activity.DisplayName);
        }

        WriteLine(sb.ToString());

        foreach (var tag in activity.TagObjects)
        {
            WriteLine($"  tag {tag.Key}={tag.Value}");
        }

        foreach (var activityEvent in activity.Events)
        {
            WriteLine($"  event name={activityEvent.Name} ts={activityEvent.Timestamp:O}");
            foreach (var tag in activityEvent.Tags)
            {
                WriteLine($"    event.tag {tag.Key}={tag.Value}");
            }
        }
    }

    private void WriteLine(string line)
    {
        lock (_writeLock)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine, Utf8NoBom);
        }
    }
}
