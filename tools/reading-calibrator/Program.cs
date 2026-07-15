using System.Diagnostics;
using System.Globalization;
using System.Text;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var samples = new[]
{
    new ReadingSample(
        "短句",
        "这是短句校准，用来测试你看到一小段文字以后，大概需要多久才能确认内容没有问题。"),
    new ReadingSample(
        "普通说明",
        "这是普通长度的阅读校准文本。你需要像平时看 WhisperDesk 预览一样读完它，确认意思完整，然后按下回车结束这一轮计时。"),
    new ReadingSample(
        "口语表达",
        "这段话会更接近日常讨论的语气，中间有一些补充和转折，你可以按照自然阅读速度看完，不需要刻意加快，也不需要故意放慢。"),
    new ReadingSample(
        "技术术语",
        "这段文本包含 pipeline、draft preview、reading units per second、final text 这些英文术语，用来校准中英文混合内容的阅读时间。"),
    new ReadingSample(
        "较长说明",
        "这是较长一点的校准文本，用来模拟一次语音输入之后生成的预览内容。你需要判断这段话是否已经表达清楚、有没有明显错误、是否需要在自动提交之前进行修正。"),
    new ReadingSample(
        "复杂长句",
        "这段校准文本故意写得更复杂一些，里面包含多个从句、停顿、条件和结论，用来观察你在阅读比较绕的内容时，进度条应该停留多久才不会让你觉得紧张。"),
    new ReadingSample(
        "列表感文本",
        "这段话包含几个需要检查的点：第一，文字是否完整；第二，英文术语是否识别正确；第三，标点是否影响理解；第四，自动提交时间是否足够。"),
    new ReadingSample(
        "长技术段落",
        "我们现在做一个更接近真实使用场景的阅读测试。假设这是一段 WhisperDesk 刚刚生成的转写结果，里面有中文说明，也有 bigmodel nostream、websocket、result type full 这些英文词，你需要快速扫一遍，确认它是否可以直接提交。"),
    new ReadingSample(
        "压力测试",
        "这是最后一段压力测试文本，长度会稍微更长一些。你可以按照平时真正使用时的节奏阅读，不要为了测试而刻意表现得更快。我们的目标不是得到一个好看的数字，而是得到一个能够让进度条更贴近你实际阅读感受的参数。")
};

var results = new List<ReadingResult>();

PrintHeader();

for (var index = 0; index < samples.Length; index++)
{
    var sample = samples[index];

    Console.WriteLine();
    Console.WriteLine($"[{index + 1}/{samples.Length}] {sample.Label}");
    Console.WriteLine("按 Enter 显示文本并开始计时。按 Q 再按 Enter 退出。");

    var command = Console.ReadLine();
    if (string.Equals(command, "q", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    Console.WriteLine();
    WriteWrapped(sample.Text);
    Console.WriteLine();
    Console.WriteLine("读完后按 Enter 结束计时。");

    var stopwatch = Stopwatch.StartNew();
    Console.ReadLine();
    stopwatch.Stop();

    var metrics = TextMetrics.From(sample.Text);
    var result = new ReadingResult(sample, metrics, stopwatch.Elapsed);
    results.Add(result);

    Console.WriteLine(FormatResult(result));
}

PrintSummary(results);

static void PrintHeader()
{
    Console.Clear();
    Console.WriteLine("WhisperDesk Reading Calibrator");
    Console.WriteLine("================================");
    Console.WriteLine("每轮：按 Enter 显示文本并开始计时，读完再按 Enter。");
    Console.WriteLine("请按真实使用时的速度阅读，不要刻意加快或放慢。");
}

static void PrintSummary(IReadOnlyList<ReadingResult> results)
{
    Console.WriteLine();
    Console.WriteLine("Summary");
    Console.WriteLine("=======");

    if (results.Count == 0)
    {
        Console.WriteLine("没有记录。再次运行工具开始校准。");
        return;
    }

    foreach (var result in results)
    {
        Console.WriteLine(FormatResult(result));
    }

    var unitsPerSecond = results.Select(r => r.UnitsPerSecond).OrderBy(x => x).ToArray();
    var charsPerSecond = results.Select(r => r.CharsPerSecond).OrderBy(x => x).ToArray();
    var medianUnitsPerSecond = Percentile(unitsPerSecond, 0.5);
    var lowerUnitsPerSecond = Percentile(unitsPerSecond, 0.25);
    var medianCharsPerSecond = Percentile(charsPerSecond, 0.5);
    var recommendedUnitsPerSecond = Math.Max(1.0, lowerUnitsPerSecond);

    Console.WriteLine();
    Console.WriteLine($"Median reading units / sec: {medianUnitsPerSecond:F2}");
    Console.WriteLine($"25th percentile units / sec: {lowerUnitsPerSecond:F2}");
    Console.WriteLine($"Median chars / sec: {medianCharsPerSecond:F2}");
    Console.WriteLine();
    Console.WriteLine("Recommended DraftPreview settings");
    Console.WriteLine("---------------------------------");
    Console.WriteLine($"ReadingUnitsPerSecond: {recommendedUnitsPerSecond:F1}");
    Console.WriteLine("BaseDelayMs: 1000");
    Console.WriteLine("MinimumDelayMs: 2500");
    Console.WriteLine("MaximumDelayMs: 18000");
    Console.WriteLine();
    Console.WriteLine("如果你希望进度条更从容，把 ReadingUnitsPerSecond 再降低 10% 到 20%。");
}

static string FormatResult(ReadingResult result)
{
    return string.Create(
        CultureInfo.InvariantCulture,
        $"{result.Sample.Label}: {result.Elapsed.TotalSeconds:F2}s, chars={result.Metrics.Characters}, units={result.Metrics.ReadingUnits:F1}, units/s={result.UnitsPerSecond:F2}, chars/s={result.CharsPerSecond:F2}");
}

static double Percentile(double[] sortedValues, double percentile)
{
    if (sortedValues.Length == 0)
    {
        return 0;
    }

    if (sortedValues.Length == 1)
    {
        return sortedValues[0];
    }

    var position = (sortedValues.Length - 1) * percentile;
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    if (lower == upper)
    {
        return sortedValues[lower];
    }

    var weight = position - lower;
    return sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight;
}

static void WriteWrapped(string text)
{
    var width = Math.Clamp(Console.WindowWidth - 4, 40, 100);
    var current = 0;
    foreach (var rune in text.EnumerateRunes())
    {
        var value = rune.ToString();
        var size = IsWide(rune) ? 2 : 1;
        if (current + size > width)
        {
            Console.WriteLine();
            current = 0;
        }

        Console.Write(value);
        current += size;
    }
    Console.WriteLine();
}

static bool IsWide(Rune rune)
{
    var value = rune.Value;
    return value is >= 0x1100 and <= 0x11FF
        or >= 0x2E80 and <= 0xA4CF
        or >= 0xAC00 and <= 0xD7AF
        or >= 0xF900 and <= 0xFAFF
        or >= 0xFE10 and <= 0xFE6F
        or >= 0xFF00 and <= 0xFFEF;
}

sealed record ReadingSample(string Label, string Text);

sealed record ReadingResult(ReadingSample Sample, TextMetrics Metrics, TimeSpan Elapsed)
{
    public double UnitsPerSecond => Metrics.ReadingUnits / Math.Max(0.001, Elapsed.TotalSeconds);
    public double CharsPerSecond => Metrics.Characters / Math.Max(0.001, Elapsed.TotalSeconds);
}

sealed record TextMetrics(int Characters, double ReadingUnits)
{
    public static TextMetrics From(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TextMetrics(0, 0);
        }

        var trimmed = text.Trim();
        var characters = trimmed.EnumerateRunes().Count(rune => !Rune.IsWhiteSpace(rune));
        var units = 0.0;
        var index = 0;
        while (index < trimmed.Length)
        {
            var rune = Rune.GetRuneAt(trimmed, index);
            if (Rune.IsWhiteSpace(rune))
            {
                index += rune.Utf16SequenceLength;
                continue;
            }

            if (IsAsciiLetter(rune))
            {
                index += rune.Utf16SequenceLength;
                while (index < trimmed.Length)
                {
                    var next = Rune.GetRuneAt(trimmed, index);
                    if (!IsAsciiLetter(next) && next.Value != '-' && next.Value != '_')
                    {
                        break;
                    }
                    index += next.Utf16SequenceLength;
                }
                units += 2.0;
                continue;
            }

            if (Rune.IsDigit(rune))
            {
                index += rune.Utf16SequenceLength;
                while (index < trimmed.Length)
                {
                    var next = Rune.GetRuneAt(trimmed, index);
                    if (!Rune.IsDigit(next) && next.Value != '.' && next.Value != ',')
                    {
                        break;
                    }
                    index += next.Utf16SequenceLength;
                }
                units += 1.5;
                continue;
            }

            units += Rune.IsPunctuation(rune) || Rune.IsSymbol(rune) ? 0.25 : 1.0;
            index += rune.Utf16SequenceLength;
        }

        return new TextMetrics(characters, units);
    }

    private static bool IsAsciiLetter(Rune rune)
    {
        return rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }
}