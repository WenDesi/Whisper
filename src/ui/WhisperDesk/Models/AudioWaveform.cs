namespace WhisperDesk.Models;

public sealed class AudioWaveform
{
    public const int BarCount = 5;
    public const double MinimumScale = 4.0 / 26;
    private readonly double[] _scales = new double[BarCount];
    private double _smoothedLevel;

    public ReadOnlySpan<double> Scales => _scales;

    public AudioWaveform() => Reset();

    public void Push(float rms)
    {
        var level = rms > 0
            ? Math.Clamp((20 * Math.Log10(Math.Min(rms, 1)) + 60) / 60, 0, 1)
            : 0;
        _smoothedLevel += (level - _smoothedLevel) * (level > _smoothedLevel ? 0.8 : 0.5);
        if (level == 0 && _smoothedLevel < 0.003)
            _smoothedLevel = 0;

        Array.Copy(_scales, 1, _scales, 0, BarCount - 1);
        _scales[^1] = MinimumScale + (1 - MinimumScale) * _smoothedLevel;
    }

    public void Reset()
    {
        _smoothedLevel = 0;
        Array.Fill(_scales, MinimumScale);
    }
}
