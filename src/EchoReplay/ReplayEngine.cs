using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace EchoReplay;

public sealed class ReplayEngine : IDisposable
{
    private TimelineBuffer? systemBuffer;
    private TimelineBuffer? micBuffer;
    private long origin;
    private long stoppedFrame;
    private int minutes = 5;
    private int saving;
    public CaptureSource? SystemSource { get; private set; }
    public CaptureSource? MicrophoneSource { get; private set; }
    public bool Running { get; private set; }
    public bool Saving => Volatile.Read(ref saving) != 0;
    public int BufferMinutes => minutes;
    public long EndFrame => systemBuffer is null ? 0 : Running ? CaptureSource.CurrentFrame(origin) : stoppedFrame;
    public double AvailableSeconds => Math.Min(EndFrame / (double)TimelineBuffer.SampleRate, minutes * 60);

    public void Start(AppSettings settings)
    {
        if (Saving) throw new InvalidOperationException("請等候目前的音檔儲存完成。");
        Stop();
        minutes = settings.Minutes;
        systemBuffer = new TimelineBuffer(minutes * 60 + 2, 2);
        micBuffer = settings.CaptureMicrophone ? new TimelineBuffer(minutes * 60 + 2, 1) : null;
        origin = Stopwatch.GetTimestamp();
        stoppedFrame = 0;
        Running = true;
        SystemSource = new(systemBuffer, origin, true, settings.OutputDeviceId);
        MicrophoneSource = micBuffer is null ? null : new(micBuffer, origin, false, settings.MicrophoneDeviceId);
    }
    public void Stop()
    {
        if (!Running) return;
        stoppedFrame = EndFrame;
        Running = false;
        SystemSource?.Dispose();
        MicrophoneSource?.Dispose();
        SystemSource = null;
        MicrophoneSource = null;
    }

    public async Task<string> SaveAsync(AppSettings settings)
    {
        if (Interlocked.CompareExchange(ref saving, 1, 0) != 0) throw new InvalidOperationException("上一份音檔仍在儲存。");
        try
        {
            var sys = systemBuffer ?? throw new InvalidOperationException("請先開始錄音。");
            var mic = micBuffer;
            long end = EndFrame;
            int frames = (int)Math.Min(end, minutes * 60L * TimelineBuffer.SampleRate);
            if (frames < TimelineBuffer.SampleRate / 2) throw new InvalidOperationException("請先累積至少半秒的錄音。");
            long start = end - frames;
            var state = new
            {
                SavedAt = DateTimeOffset.Now,
                DurationSeconds = frames / (double)TimelineBuffer.SampleRate,
                SampleRate = TimelineBuffer.SampleRate,
                settings.SystemGain,
                settings.MicrophoneGain,
                SystemStatus = SystemSource?.Status ?? "已暫停",
                MicrophoneStatus = MicrophoneSource?.Status ?? "未啟用／已暫停",
                SystemDiscontinuities = SystemSource?.Discontinuities ?? 0,
                MicrophoneDiscontinuities = MicrophoneSource?.Discontinuities ?? 0
            };
            // Let packets containing the instant of the key press arrive before copying.
            await Task.Delay(150);
            return await Task.Run(() =>
            {
                short[] system = sys.Snapshot(start, frames);
                short[]? microphone = mic?.Snapshot(start, frames);
                string root = Path.GetFullPath(settings.OutputFolder);
                Directory.CreateDirectory(root);
                string name = "Replay_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" + Guid.NewGuid().ToString("N")[..6];
                string temporary = Path.Combine(root, "." + name + ".partial");
                string destination = Path.Combine(root, name);
                Directory.CreateDirectory(temporary);
                try
                {
                    WaveExporter.WriteMix(Path.Combine(temporary, "混音.wav"), system, microphone, settings.SystemGain, settings.MicrophoneGain);
                    if (settings.SaveSeparateTracks)
                    {
                        WaveExporter.WritePcm(Path.Combine(temporary, "電腦聲音.wav"), system, 2);
                        if (microphone is not null) WaveExporter.WritePcm(Path.Combine(temporary, "麥克風.wav"), microphone, 1);
                    }
                    File.WriteAllText(Path.Combine(temporary, "錄音資訊.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                    Directory.Move(temporary, destination);
                }
                catch
                {
                    // Only remove this export's newly created temporary directory.
                    try { Directory.Delete(temporary, true); } catch { }
                    throw;
                }
                return destination;
            });
        }
        finally { Volatile.Write(ref saving, 0); }
    }
    public void Dispose() => Stop();
}

public static class WaveExporter
{
    public static void WritePcm(string path, short[] samples, int channels)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(TimelineBuffer.SampleRate, 16, channels));
        byte[] bytes = new byte[16384];
        for (int offset = 0; offset < samples.Length; offset += bytes.Length / 2)
        {
            int count = Math.Min(bytes.Length / 2, samples.Length - offset);
            Buffer.BlockCopy(samples, offset * 2, bytes, 0, count * 2);
            writer.Write(bytes, 0, count * 2);
        }
    }
    public static void WriteMix(string path, short[] system, short[]? microphone, double systemGain, double micGain)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(TimelineBuffer.SampleRate, 16, 2));
        byte[] bytes = new byte[16384];
        for (int offset = 0; offset < system.Length; offset += bytes.Length / 2)
        {
            int count = Math.Min(bytes.Length / 2, system.Length - offset);
            for (int i = 0; i < count; i++)
            {
                int position = offset + i;
                double value = system[position] / 32768d * systemGain;
                if (microphone is not null) value += microphone[position / 2] / 32768d * micGain;
                // Soft-limit only the top 20% of the range to avoid harsh clipping on overlap.
                double magnitude = Math.Abs(value);
                if (magnitude > 0.8) value = Math.Sign(value) * (0.8 + 0.2 * Math.Tanh((magnitude - 0.8) / 0.2));
                short pcm = (short)Math.Clamp((int)Math.Round(value * 32768), -32768, 32767);
                bytes[i * 2] = (byte)(pcm & 255);
                bytes[i * 2 + 1] = (byte)(pcm >> 8);
            }
            writer.Write(bytes, 0, count * 2);
        }
    }
}
