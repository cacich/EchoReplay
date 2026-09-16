using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EchoReplay;

public sealed record DeviceChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class AudioDevices
{
    public static List<DeviceChoice> List(DataFlow flow)
    {
        var result = new List<DeviceChoice> { new("", flow == DataFlow.Render ? "跟隨 Windows 預設播放裝置" : "跟隨 Windows 預設麥克風") };
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) result.Add(new(device.ID, device.FriendlyName));
        }
        return result;
    }
}

// Uses WASAPI packet timestamps (100 ns QPC units), not callback arrival time.
// Windows converts each endpoint to 48 kHz float before it reaches the shared timeline.
public sealed class CaptureSource : IDisposable
{
    private readonly TimelineBuffer buffer;
    private readonly long origin;
    private readonly bool loopback;
    private readonly string deviceId;
    private readonly string? processName;
    private readonly string? otherProcessName;
    private readonly bool excludeSelf;
    private readonly Func<int?>? processSelector;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Thread thread;
    private string status = "正在連接…";
    private volatile bool connected;
    private float peak;
    private long peakTime;
    private long packets;
    private long discontinuities;
    private int activeProcessId;
    public int ActiveProcessId => Volatile.Read(ref activeProcessId);
    public string Status => status;
    public bool Connected => connected;
    public long Packets => Interlocked.Read(ref packets);
    public long Discontinuities => Interlocked.Read(ref discontinuities);
    public float Peak => Stopwatch.GetElapsedTime(Interlocked.Read(ref peakTime)).TotalMilliseconds < 300 ? Volatile.Read(ref peak) : 0;

    public CaptureSource(TimelineBuffer buffer, long origin, bool loopback, string deviceId,
        string? processName = null, bool excludeSelf = false, string? otherProcessName = null, Func<int?>? processSelector = null)
    {
        this.buffer = buffer;
        this.origin = origin;
        this.loopback = loopback;
        this.deviceId = deviceId;
        this.processName = processName;
        this.excludeSelf = excludeSelf;
        this.otherProcessName = otherProcessName;
        this.processSelector = processSelector;
        thread = new Thread(Run) { IsBackground = true, Name = loopback ? "System audio capture" : "Microphone capture" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Run()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try { if (processName is not null || excludeSelf || processSelector is not null) CaptureProcess(); else Capture(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                status = "連線中斷，2 秒後重試 · " + ex.Message;
                connected = false;
                Volatile.Write(ref peak, 0);
            }
            if (cancellation.Token.WaitHandle.WaitOne(2000)) break;
        }
        connected = false;
        status = "已暫停";
    }

    private int? ResolveProcess(int preferred = 0)
    {
        if (processSelector is not null) return processSelector();
        if (excludeSelf) return Environment.ProcessId;
        var entries = ProcessCatalog.Snapshot();
        var selected = ProcessCatalog.Resolve(entries, processName ?? "", preferred);
        var other = ProcessCatalog.Resolve(entries, otherProcessName ?? "");
        if (selected is not null && other is not null && ProcessCatalog.Overlap(entries, selected.Id, other.Id))
            throw new InvalidOperationException("語音與遊戲的程序樹重疊，請重新選擇實際程式");
        return selected?.Id;
    }
    private void CaptureProcess()
    {
        int? id = ResolveProcess(ActiveProcessId);
        if (id is null) { connected = false; Volatile.Write(ref activeProcessId, 0); status = $"等待 {processName}.exe 啟動 · 自動重連"; return; }
        using var process = new ProcessLifetime(id.Value);
        // Opening the process handle pins its identity so a recycled PID cannot be mistaken for it.
        if (process.HasExited) return;
        if (processName is not null && !process.Name.Equals(processName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("來源程式已變更，重新尋找中");
        using var client = ProcessAudioClient.Activate((uint)id.Value, excludeSelf, cancellation.Token);
        using var ready = new AutoResetEvent(false);
        var flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
        client.Initialize(AudioClientShareMode.Shared, flags, 0, 0, WaveFormat.CreateIeeeFloatWaveFormat(TimelineBuffer.SampleRate, buffer.Channels), Guid.Empty);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        Volatile.Write(ref activeProcessId, id.Value);
        string label = excludeSelf ? "所有程式 · 已排除 EchoReplay 試聽" : $"{processName ?? "程式"} · PID {id.Value}";
        CapturePackets(client, label, () =>
        {
            if (process.HasExited || ResolveProcess(id.Value) != id.Value) throw new InvalidOperationException("程式已關閉或重新啟動");
        }, ready);
    }

    private void Capture()
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = loopback ? DataFlow.Render : DataFlow.Capture;
        using var device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia) : enumerator.GetDevice(deviceId);
        string activeId = device.ID;
        if (device.State != DeviceState.Active) throw new InvalidOperationException("選擇的裝置目前無法使用");
        using var client = device.AudioClient;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(TimelineBuffer.SampleRate, buffer.Channels);
        var flags = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
        if (loopback) flags |= AudioClientStreamFlags.Loopback;
        client.Initialize(AudioClientShareMode.Shared, flags, 1_000_000, 0, format, Guid.Empty);
        CapturePackets(client, device.FriendlyName, () =>
        {
            if (device.State != DeviceState.Active) throw new InvalidOperationException("音效裝置已中斷");
            if (string.IsNullOrEmpty(deviceId))
            {
                using var current = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                if (current.ID != activeId) throw new InvalidOperationException("預設音效裝置已切換");
            }
        });
    }

    private void CapturePackets(AudioClient client, string label, Action check, WaitHandle? ready = null)
    {
        // AudioClient owns and disposes this capture service.
        var capture = client.AudioCaptureClient;
        var data = new float[Math.Max(client.BufferSize, 4800) * buffer.Channels];
        long origin100ns = (long)(origin * (10_000_000d / Stopwatch.Frequency));
        long nextFrame = -1;
        long lastCheck = Stopwatch.GetTimestamp();
        client.Start();
        status = label;
        connected = true;
        try
        {
            var waits = ready is null ? null : new[] { cancellation.Token.WaitHandle, ready };
            while (ready is null ? !cancellation.Token.WaitHandle.WaitOne(10) : WaitHandle.WaitAny(waits!, 100) != 0)
            {
                while (!cancellation.IsCancellationRequested && capture.GetNextPacketSize() > 0)
                {
                    IntPtr pointer = capture.GetBuffer(out int frames, out var packetFlags, out _, out long timestamp);
                    try
                    {
                        int count = frames * buffer.Channels;
                        if (data.Length < count) data = new float[count];
                        if ((packetFlags & AudioClientBufferFlags.Silent) != 0) Array.Clear(data, 0, count);
                        else Marshal.Copy(pointer, data, 0, count);
                        long frame;
                        if ((packetFlags & AudioClientBufferFlags.TimestampError) == 0 && timestamp > 0)
                            frame = (long)Math.Round((timestamp - origin100ns) * (TimelineBuffer.SampleRate / 10_000_000d));
                        else
                            frame = nextFrame >= 0 ? nextFrame : CurrentFrame(origin) - frames;
                        // Suppress sub-millisecond timestamp rounding jitter; larger gaps keep their silence.
                        if (nextFrame >= 0 && Math.Abs(frame - nextFrame) <= 48) frame = nextFrame;
                        if ((packetFlags & AudioClientBufferFlags.DataDiscontinuity) != 0 && Packets > 0)
                            Interlocked.Increment(ref discontinuities);
                        buffer.Write(frame, data, frames);
                        nextFrame = frame + frames;
                        float maximum = 0;
                        for (int i = 0; i < count; i++) maximum = Math.Max(maximum, Math.Abs(data[i]));
                        Volatile.Write(ref peak, maximum);
                        Interlocked.Exchange(ref peakTime, Stopwatch.GetTimestamp());
                        Interlocked.Increment(ref packets);
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
                if (Stopwatch.GetElapsedTime(lastCheck).TotalSeconds >= 2)
                {
                    lastCheck = Stopwatch.GetTimestamp();
                    check();
                }
            }
        }
        finally { connected = false; client.Stop(); }
    }

    public static long CurrentFrame(long origin) => (long)(Stopwatch.GetElapsedTime(origin).TotalSeconds * TimelineBuffer.SampleRate);
    public void Dispose()
    {
        cancellation.Cancel();
        thread.Join();
        cancellation.Dispose();
    }
}
