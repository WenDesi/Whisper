using System.Buffers.Binary;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Models;
using Xunit;

namespace WhisperDesk.Tests;

public class AudioLevelTests
{
    [Fact]
    public void SilenceDoesNotMoveTheWaveform()
    {
        var rms = PcmAudioLevel.CalculateRms(new byte[1600], 16);
        Assert.Equal(0, rms);
        var waveform = new AudioWaveform();
        for (var i = 0; i < 40; i++) waveform.Push(rms);
        Assert.All(waveform.Scales.ToArray(), scale => Assert.Equal(AudioWaveform.MinimumScale, scale));
    }

    [Fact]
    public void MeterUsesSignalEnergyWithoutCancellingOppositeChannels()
    {
        var pcm = new byte[1600];
        for (var i = 0; i < pcm.Length; i += 2)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)(i % 4 == 0 ? 16384 : -16384));
        Assert.Equal(0.5f, PcmAudioLevel.CalculateRms(pcm, 16), 5);
    }

    [Fact]
    public void SineWaveHasTheExpectedRms()
    {
        var pcm = new byte[1600];
        for (var i = 0; i < 800; i++)
        {
            var sample = (short)(32768 * 0.5 * Math.Sin(2 * Math.PI * i / 16));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample);
        }
        Assert.InRange(PcmAudioLevel.CalculateRms(pcm, 16), 0.3534f, 0.3537f);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void SupportsIntegerPcmFormats(int bits)
    {
        var sample = new byte[bits / 8];
        if (bits != 8) sample[^1] = 0x80;
        Assert.Equal(1, PcmAudioLevel.CalculateRms(sample, bits));
        Assert.Equal(0, PcmAudioLevel.CalculateRms([], bits));
        if (bits == 8) Assert.Equal(0, PcmAudioLevel.CalculateRms([128], bits));
    }

    [Fact]
    public void RejectsUnsupportedFormatsAndIncompleteSamples()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PcmAudioLevel.CalculateRms([], 12));
        Assert.Throws<ArgumentException>(() => PcmAudioLevel.CalculateRms([1], 16));
    }

    [Fact]
    public void WaveformTracksRecentAudioAndSettlesAfterSpeech()
    {
        var waveform = new AudioWaveform();
        waveform.Push(0.2f);
        var firstPeak = waveform.Scales[^1];
        Assert.True(firstPeak > AudioWaveform.MinimumScale);
        Assert.Equal(AudioWaveform.MinimumScale, waveform.Scales[0]);

        waveform.Push(0);
        Assert.Equal(firstPeak, waveform.Scales[^2]);
        Assert.True(waveform.Scales[^1] < firstPeak);

        for (var i = 0; i < 25; i++) waveform.Push(0);
        Assert.All(waveform.Scales.ToArray(), scale => Assert.Equal(AudioWaveform.MinimumScale, scale));

        waveform.Push(1);
        waveform.Reset();
        Assert.All(waveform.Scales.ToArray(), scale => Assert.Equal(AudioWaveform.MinimumScale, scale));
    }

    [Fact]
    public void WaveformStaysInsideItsFixedDrawingArea()
    {
        var waveform = new AudioWaveform();
        foreach (var level in new[] { 0, 0.0001f, 0.01f, 0.1f, 0.5f, 1, 2, -1 })
        {
            waveform.Push(level);
            Assert.All(waveform.Scales.ToArray(), scale => Assert.InRange(scale, AudioWaveform.MinimumScale, 1));
        }
    }
}
