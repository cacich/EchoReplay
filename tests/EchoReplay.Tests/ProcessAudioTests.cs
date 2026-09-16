using System.IO;
using System.Text.Json;
using EchoReplay;
using NAudio.Wave;

internal static class ProcessAudioTests
{
    public static void RunAll(string directory, Action<string, Action> run, Action<bool, string> check)
    {
        run("Process support gate preserves legacy Windows compatibility", () =>
        {
            check(!CaptureModes.IsProcessSupported(new(10, 0, 19045)) && CaptureModes.IsProcessSupported(new(10, 0, 20348)) && CaptureModes.IsProcessSupported(new(10, 0, 26100)), "build boundary");
            check(new AppSettings().CaptureMode == CaptureModes.Device, "existing profiles keep endpoint mode");
        });
        run("Process selector normalizes names and rejects duplicate sources", () =>
        {
            check(ProcessCatalog.Normalize(" Discord.EXE ") == "Discord" && ProcessCatalog.Normalize("") == "", "normalize");
            bool rejected = false; try { ProcessCatalog.Normalize("C:\\Discord.exe"); } catch (ArgumentException) { rejected = true; } check(rejected, "name not path");
            if (!CaptureModes.ProcessSupported) return;
            foreach (var settings in new[] { new AppSettings { CaptureMode = CaptureModes.Applications, VoiceProcessName = "Discord", GameProcessName = "discord.exe" }, new AppSettings { CaptureMode = CaptureModes.Applications, VoiceProcessName = "" }, new AppSettings { CaptureMode = CaptureModes.Applications, VoiceProcessName = "EchoReplay" } })
            { rejected = false; try { CaptureModes.Validate(settings); } catch (ArgumentException) { rejected = true; } check(rejected, "invalid source rejected"); }
        });
        run("Electron process tree selects root, follows restart and detects overlap", () =>
        {
            ProcessEntry[] entries = [new(100001, 0, "Discord"), new(100002, 100001, "Discord"), new(100003, 100002, "AudioWorker"), new(100004, 0, "Game"), new(100005, 0, "Discord")];
            check(ProcessCatalog.Resolve(entries, "discord")?.Id == 100001, "root");
            check(ProcessCatalog.Resolve(entries, "Discord", 100005)?.Id == 100005, "keep current instance");
            check(ProcessCatalog.Resolve(entries.Where(e => e.Id is not (100001 or 100002 or 100003)).ToArray(), "Discord", 100001)?.Id == 100005, "restart target");
            check(ProcessCatalog.Overlap(entries, 100001, 100003) && !ProcessCatalog.Overlap(entries, 100001, 100004), "overlap");
            check(ProcessCatalog.Resolve(entries, "Missing") is null, "waiting for launch");
        });
        run("Process ancestry excludes self trees and terminates on corrupt cycles", () =>
        {
            int self = Environment.ProcessId;
            ProcessEntry[] entries = [new(100001, 0, "Parent"), new(self, 100001, "EchoReplay"), new(100002, self, "Child"), new(100003, 100004, "Loop"), new(100004, 100003, "Loop")];
            check(ProcessCatalog.Resolve(entries, "Parent") is null && ProcessCatalog.Resolve(entries, "Child") is null && ProcessCatalog.Resolve(entries, "EchoReplay") is null, "self exclusion");
            check(!ProcessCatalog.DescendsFrom(entries, 100003, 999999), "cycle bounded");
        });
        run("Three-track recording and editor mix use the same aligned samples", () =>
        {
            string root = Path.Combine(directory, "three-track-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string folder = Path.Combine(root, "Replay_test"); Directory.CreateDirectory(folder);
            short[] voice = Enumerable.Repeat((short)1000, 96000).ToArray(), mic = Enumerable.Repeat((short)2000, 48000).ToArray(), game = Enumerable.Repeat((short)3000, 96000).ToArray();
            string main = Path.Combine(folder, "混音.wav");
            WaveExporter.WritePcm(Path.Combine(folder, "語音聊天.wav"), voice, 2); WaveExporter.WritePcm(Path.Combine(folder, "麥克風.wav"), mic, 1); WaveExporter.WritePcm(Path.Combine(folder, "遊戲.wav"), game, 2);
            WaveExporter.WriteMix(main, voice, mic, 0.5, 0.5, game, 0.5);
            File.WriteAllText(Path.Combine(folder, "錄音資訊.json"), "{\"SystemGain\":0.5,\"MicrophoneGain\":0.5,\"GameGain\":0.5}");
            var audio = EditAudio.Load(main); var state = EditFiles.InitialState(audio);
            check(audio.Applications && audio.Game is not null && audio.Microphone is not null && audio.SourcePaths.Length == 3, "load three tracks");
            using var reader = new WaveFileReader(main); using var stream = new EditPcmStream(audio, state);
            byte[] recorded = new byte[reader.Length], edited = new byte[stream.Length]; reader.ReadExactly(recorded); stream.ReadExactly(edited);
            check(recorded.SequenceEqual(edited) && BitConverter.ToInt16(recorded) == 3000, "mix consistency");
            var history = new EditHistory(state); history.Edit(1000, 2000, "trim"); history.Apply(history.Current with { GameGain = 0 });
            using var voiceOnly = new EditPcmStream(audio, history.Current); byte[] sample = new byte[4]; voiceOnly.ReadExactly(sample); check(BitConverter.ToInt16(sample) == 1500, "game muted independently");
            EditFiles.SaveProject(audio, history.Current); check(EditFiles.LoadProject(audio)?.GameGain == 0, "game gain persists");
            var clip = ClipLibrary.Scan(root).Single(); check(ClipLibrary.DeletionTarget(clip, root) == folder, "three track cleanup allowed");
        });
        run("Old edit projects default the optional game gain without changing audio", () =>
        {
            var state = JsonSerializer.Deserialize<EditState>("{\"Spans\":[{\"SourceStart\":0,\"Frames\":10}],\"SystemGain\":0.7,\"MicrophoneGain\":0.4,\"FadeIn\":0,\"FadeOut\":0}")!;
            check(state.GameGain == 1 && state.SystemGain == 0.7 && state.MicrophoneGain == 0.4, "1.1 project compatibility");
            var settings = JsonSerializer.Deserialize<AppSettings>("{\"Minutes\":3,\"OutputDeviceId\":\"saved-device\"}")!;
            check(settings.CaptureMode == CaptureModes.Device && settings.OutputDeviceId == "saved-device", "old settings preserved");
        });
    }
}
