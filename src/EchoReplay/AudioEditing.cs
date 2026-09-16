using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoReplay;

public sealed record EditSpan(int SourceStart, int Frames, bool Muted = false);
public sealed record EditState(EditSpan[] Spans, double SystemGain = 1, double MicrophoneGain = 1,
    double FadeIn = 0, double FadeOut = 0, double GameGain = 1)
{
    public int Frames => Spans.Sum(s => s.Frames);
}

// All edit coordinates are integer 48 kHz frames on the CURRENT timeline.
// Spans always refer back to the original, so repeated editing never re-encodes audio.
public sealed class EditHistory
{
    private readonly Stack<EditState> undo = new();
    private readonly Stack<EditState> redo = new();
    public EditState Current { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public EditHistory(EditState initial) => Current = initial;
    public void Apply(EditState next) { undo.Push(Current); Current = next; redo.Clear(); }
    public void Undo() { if (undo.TryPop(out var state)) { redo.Push(Current); Current = state; } }
    public void Redo() { if (redo.TryPop(out var state)) { undo.Push(Current); Current = state; } }
    public void Edit(int start, int end, string operation)
    {
        if (start < 0 || end > Current.Frames || end <= start) throw new ArgumentException("請選取有效的時間範圍。");
        if (operation is not ("trim" or "delete" or "mute")) throw new ArgumentException("未知的剪輯操作。");
        var result = new List<EditSpan>();
        int position = 0;
        foreach (var span in Current.Spans)
        {
            int spanEnd = position + span.Frames;
            var cuts = new[] { position, Math.Clamp(start, position, spanEnd), Math.Clamp(end, position, spanEnd), spanEnd }.Distinct().Order().ToArray();
            for (int i = 0; i + 1 < cuts.Length; i++)
            {
                int a = cuts[i], b = cuts[i + 1];
                bool selected = a >= start && b <= end;
                if (operation == "trim" && !selected || operation == "delete" && selected) continue;
                result.Add(new(span.SourceStart + a - position, b - a, span.Muted || operation == "mute" && selected));
            }
            position = spanEnd;
        }
        if (result.Count == 0) throw new ArgumentException("至少保留一小段音訊；要刪除整個檔案，請使用音檔庫的刪除功能。");
        Apply(Current with { Spans = result.ToArray() });
    }
}

public sealed record AudioSource(short[] Samples, int Channels)
{
    public int Frames => Samples.Length / Channels;
    public double Sample(int frame, int channel) => frame < Frames ? Samples[frame * Channels + Math.Min(channel, Channels - 1)] / 32768d : 0;
}

public sealed class EditAudio
{
    public const int Rate = 48000;
    public const int MaxFrames = Rate * 60 * 15;
    public required string MainPath { get; init; }
    public required string[] SourcePaths { get; init; }
    public required AudioSource System { get; init; }
    public AudioSource? Microphone { get; init; }
    public AudioSource? Game { get; init; }
    public bool Applications { get; init; }
    public bool Separate { get; init; }
    public int Frames => Math.Max(Math.Max(System.Frames, Microphone?.Frames ?? 0), Game?.Frames ?? 0);
    public static EditAudio Load(string mainPath, CancellationToken cancellation = default)
    {
        string folder = Path.GetDirectoryName(mainPath)!;
        string system = Path.Combine(folder, "電腦聲音.wav"), mic = Path.Combine(folder, "麥克風.wav");
        string voice = Path.Combine(folder, "語音聊天.wav"), game = Path.Combine(folder, "遊戲.wav");
        bool applications = Path.GetFileName(mainPath) == "混音.wav" && File.Exists(voice);
        if (applications) system = voice;
        bool separate = Path.GetFileName(mainPath) == "混音.wav" && File.Exists(system);
        var paths = separate ? new[] { system, mic, applications ? game : "" }.Where(File.Exists).ToArray() : new[] { mainPath };
        var audio = new EditAudio { MainPath = Path.GetFullPath(mainPath), SourcePaths = paths,
            System = ReadSource(paths[0], cancellation), Microphone = separate && File.Exists(mic) ? ReadSource(mic, cancellation) : null,
            Game = applications && File.Exists(game) ? ReadSource(game, cancellation) : null, Applications = applications, Separate = separate };
        if (audio.Frames == 0) throw new InvalidDataException("音檔沒有可編輯的聲音。");
        return audio;
    }
    public static AudioSource ReadSource(string path, CancellationToken cancellation = default)
    {
        // WAV stays independent of Media Foundation (including Windows N editions).
        using WaveStream reader = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path) : new MediaFoundationReader(path);
        if (reader.TotalTime.TotalSeconds > 15 * 60 + 1) throw new InvalidDataException("剪輯器每次支援最多 15 分鐘的音檔。");
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels is not (1 or 2)) throw new InvalidDataException("請使用單聲道或立體聲音檔。");
        if (provider.WaveFormat.SampleRate != Rate) provider = new WdlResamplingSampleProvider(provider, Rate);
        int channels = provider.WaveFormat.Channels;
        using var pcm = new MemoryStream();
        float[] samples = new float[8192];
        byte[] bytes = new byte[samples.Length * 2];
        int read;
        while ((read = provider.Read(samples, 0, samples.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (pcm.Length + read * 2L > (long)MaxFrames * channels * 2) throw new InvalidDataException("剪輯器每次支援最多 15 分鐘的音檔。");
            for (int i = 0; i < read; i++)
            {
                short value = (short)Math.Clamp((int)Math.Round((float.IsFinite(samples[i]) ? samples[i] : 0) * 32768d), -32768, 32767);
                bytes[i * 2] = (byte)value; bytes[i * 2 + 1] = (byte)(value >> 8);
            }
            pcm.Write(bytes, 0, read * 2);
        }
        var result = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm.GetBuffer(), 0, result, 0, checked((int)pcm.Length));
        return new(result, channels);
    }
    public void Validate(EditState state)
    {
        if (state is null || state.Spans is null || state.Spans.Length is 0 or > 10000 || state.Spans.Any(s => s is null || s.Frames <= 0 || s.SourceStart < 0 || (long)s.SourceStart + s.Frames > Frames)
            || state.Spans.Sum(s => (long)s.Frames) > MaxFrames || !double.IsFinite(state.SystemGain) || state.SystemGain is < 0 or > 2
            || !double.IsFinite(state.MicrophoneGain) || state.MicrophoneGain is < 0 or > 2
            || !double.IsFinite(state.GameGain) || state.GameGain is < 0 or > 2
            || !double.IsFinite(state.FadeIn) || state.FadeIn is < 0 or > 10 || !double.IsFinite(state.FadeOut) || state.FadeOut is < 0 or > 10)
            throw new InvalidDataException("編輯進度不正確，無法載入。");
    }
}

public sealed class EditPcmStream : WaveStream
{
    private readonly EditAudio audio;
    private readonly EditState state;
    private readonly int[] ends;
    private readonly int start, count;
    private readonly CancellationToken cancellation;
    private long position;
    public bool Loop { get; set; }
    public Action<double>? Progress { get; set; }
    public EditPcmStream(EditAudio audio, EditState state, int start = 0, int? count = null, CancellationToken cancellation = default)
    {
        audio.Validate(state);
        this.audio = audio; this.state = state; this.start = start; this.count = count ?? state.Frames - start; this.cancellation = cancellation;
        if (start < 0 || this.count <= 0 || (long)start + this.count > state.Frames) throw new ArgumentOutOfRangeException(nameof(start));
        int end = 0; ends = state.Spans.Select(s => end += s.Frames).ToArray();
    }
    public override WaveFormat WaveFormat { get; } = new(EditAudio.Rate, 16, 2);
    public override long Length => (long)count * 4;
    public override long Position { get => position; set => position = Math.Clamp(value / 4 * 4, 0, Length); }
    public override int Read(byte[] buffer, int offset, int requested)
    {
        cancellation.ThrowIfCancellationRequested();
        int written = 0;
        while (written + 4 <= requested)
        {
            if (position >= Length) { if (Loop) position = 0; else break; }
            int frame = start + (int)(position / 4);
            int index = Array.BinarySearch(ends, frame + 1);
            if (index < 0) index = ~index;
            var span = state.Spans[index];
            int sourceFrame = span.SourceStart + frame - (index == 0 ? 0 : ends[index - 1]);
            double fade = 1;
            if (state.FadeIn > 0) fade = Math.Min(fade, frame / (state.FadeIn * EditAudio.Rate));
            if (state.FadeOut > 0) fade = Math.Min(fade, (state.Frames - 1 - frame) / (state.FadeOut * EditAudio.Rate));
            for (int channel = 0; channel < 2; channel++)
            {
                double value = span.Muted ? 0 : audio.System.Sample(sourceFrame, channel) * state.SystemGain + (audio.Microphone?.Sample(sourceFrame, channel) ?? 0) * state.MicrophoneGain
                    + (audio.Game?.Sample(sourceFrame, channel) ?? 0) * state.GameGain;
                double magnitude = Math.Abs(value);
                if (magnitude > 0.8 && (audio.Separate || state.SystemGain > 1)) value = Math.Sign(value) * (0.8 + 0.2 * Math.Tanh((magnitude - 0.8) / 0.2));
                short sample = (short)Math.Clamp((int)Math.Round(value * fade * 32768), -32768, 32767);
                buffer[offset + written++] = (byte)sample; buffer[offset + written++] = (byte)(sample >> 8);
            }
            position += 4;
        }
        Progress?.Invoke(position / (double)Length);
        return written;
    }
}

public sealed record SourceStamp(string Name, long Bytes, long Modified);
public sealed record EditProject(int Version, SourceStamp[] Sources, EditState State);
public static class EditFiles
{
    public static EditState InitialState(EditAudio audio)
    {
        var state = new EditState([new(0, audio.Frames)]);
        string metadata = Path.Combine(Path.GetDirectoryName(audio.MainPath)!, "錄音資訊.json");
        if (!audio.Separate || !File.Exists(metadata)) return state;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(metadata));
            if (json.RootElement.TryGetProperty("SystemGain", out var system) && json.RootElement.TryGetProperty("MicrophoneGain", out var mic))
            {
                var next = state with { SystemGain = system.GetDouble(), MicrophoneGain = mic.GetDouble(),
                    GameGain = json.RootElement.TryGetProperty("GameGain", out var gameGain) ? gameGain.GetDouble() : 1 };
                audio.Validate(next); return next;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or InvalidOperationException or FormatException) { }
        return state;
    }
    public static string ProjectPath(string mainPath) => mainPath + ".echoedit.json";
    private static SourceStamp[] Stamps(EditAudio audio) => audio.SourcePaths.Select(p => { var f = new FileInfo(p); return new SourceStamp(f.Name, f.Length, f.LastWriteTimeUtc.Ticks); }).ToArray();
    public static EditState? LoadProject(EditAudio audio)
    {
        string path = ProjectPath(audio.MainPath);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("編輯進度檔案過大。");
        var project = JsonSerializer.Deserialize<EditProject>(File.ReadAllText(path));
        if (project is null || project.Version != 1 || project.Sources is null || !project.Sources.SequenceEqual(Stamps(audio))) throw new InvalidDataException("原始音檔已變更，無法套用舊的編輯進度。");
        audio.Validate(project.State);
        return project.State;
    }
    public static void SaveProject(EditAudio audio, EditState state)
    {
        audio.Validate(state);
        string path = ProjectPath(audio.MainPath), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(new EditProject(1, Stamps(audio), state), new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Export(EditAudio audio, EditState state, string destination, int bitrate, CancellationToken cancellation, Action<double>? progress = null)
    {
        destination = Path.GetFullPath(destination);
        string sourceFolder = Path.GetDirectoryName(audio.MainPath)!;
        bool recording = Path.GetFileName(audio.MainPath) == "混音.wav";
        if (audio.SourcePaths.Append(audio.MainPath).Any(p => Path.GetFullPath(p).Equals(destination, StringComparison.OrdinalIgnoreCase))
            || recording && destination.StartsWith(sourceFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("請將成品另存於原始錄音資料夾之外，避免清理原檔時一起刪除。建議使用 Exports 資料夾。");
        string extension = Path.GetExtension(destination).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3")) throw new ArgumentException("請使用 .wav 或 .mp3 副檔名。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".echo-export-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            using var stream = new EditPcmStream(audio, state, cancellation: cancellation) { Progress = progress };
            if (extension == ".mp3") MediaFoundationEncoder.EncodeToMp3(stream, temporary, bitrate);
            else WaveFileWriter.CreateWaveFile(temporary, stream);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static float[] Peaks(EditAudio audio, EditState state, CancellationToken cancellation)
    {
        const int bucket = 240; // 5 ms: enough detail for short sound effects, bounded at 180k points.
        float[] peaks = new float[(state.Frames + bucket - 1) / bucket];
        using var stream = new EditPcmStream(audio, state, cancellation: cancellation);
        byte[] block = new byte[bucket * 4];
        for (int i = 0; i < peaks.Length; i++)
        {
            int read = stream.Read(block, 0, block.Length);
            for (int b = 0; b < read; b += 2) peaks[i] = Math.Max(peaks[i], Math.Abs(BitConverter.ToInt16(block, b) / 32768f));
        }
        return peaks;
    }
}
