using System;

namespace EchoReplay;

// Absolute timeline pages distinguish old ring contents from actual silence.
// Separate WASAPI sources write against the same QPC clock, including after reconnects.
public sealed class TimelineBuffer
{
    public const int SampleRate = 48000;
    private const int PageFrames = 480;
    private readonly object gate = new();
    private readonly short[] samples;
    private readonly long[] pageNumbers;
    public int Channels { get; }
    public int CapacityFrames { get; }

    public TimelineBuffer(int seconds, int channels)
    {
        if (seconds <= 0 || channels is < 1 or > 2) throw new ArgumentOutOfRangeException();
        Channels = channels;
        CapacityFrames = checked(seconds * SampleRate);
        samples = new short[checked(CapacityFrames * channels)];
        pageNumbers = new long[CapacityFrames / PageFrames];
        Array.Fill(pageNumbers, -1L);
    }

    public void Write(long startFrame, float[] source, int frames)
    {
        if (frames < 0 || source.Length < frames * Channels) throw new ArgumentException("Invalid packet size");
        lock (gate)
        {
            int consumed = (int)Math.Min(frames, Math.Max(0L, -startFrame));
            while (consumed < frames)
            {
                long absolute = startFrame + consumed;
                long page = absolute / PageFrames;
                int slot = (int)(page % pageNumbers.Length);
                int within = (int)(absolute % PageFrames);
                int count = Math.Min(PageFrames - within, frames - consumed);
                // A delayed packet must not overwrite a newer generation of the ring.
                if (pageNumbers[slot] <= page)
                {
                    if (pageNumbers[slot] != page)
                    {
                        Array.Clear(samples, slot * PageFrames * Channels, PageFrames * Channels);
                        pageNumbers[slot] = page;
                    }
                    int destination = (slot * PageFrames + within) * Channels;
                    for (int i = 0; i < count * Channels; i++)
                    {
                        float value = source[consumed * Channels + i];
                        samples[destination + i] = float.IsFinite(value)
                            ? (short)Math.Clamp((int)MathF.Round(value * 32768f), -32768, 32767) : (short)0;
                    }
                }
                consumed += count;
            }
        }
    }

    public short[] Snapshot(long startFrame, int frames)
    {
        if (startFrame < 0 || frames < 0 || frames > CapacityFrames) throw new ArgumentOutOfRangeException();
        short[] result = new short[checked(frames * Channels)];
        lock (gate)
        {
            int copied = 0;
            while (copied < frames)
            {
                long absolute = startFrame + copied;
                long page = absolute / PageFrames;
                int slot = (int)(page % pageNumbers.Length);
                int within = (int)(absolute % PageFrames);
                int count = Math.Min(PageFrames - within, frames - copied);
                if (pageNumbers[slot] == page)
                    Array.Copy(samples, (slot * PageFrames + within) * Channels, result, copied * Channels, count * Channels);
                copied += count;
            }
        }
        return result;
    }
}
