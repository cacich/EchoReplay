using System.Diagnostics;
using System.IO;
using System.Text.Json;
using EchoReplay;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static class ProcessCaptureTests
{
    public static int Tone(string[] args)
    {
        try
        {
            double frequency = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
            using var output = new WasapiOut(AudioClientShareMode.Shared, 60);
            output.Init(new SignalGenerator(48000, 2) { Gain = 0.035, Frequency = frequency, Type = SignalGeneratorType.Sin });
            output.Play(); File.WriteAllText(args[2], "ready");
            var watch = Stopwatch.StartNew();
            while (!File.Exists(args[3]) && watch.Elapsed.TotalSeconds < 90) Thread.Sleep(50);
            output.Stop(); return 0;
        }
        catch (Exception ex) { File.WriteAllText(args[2] + ".error", ex.ToString()); return 1; }
    }
    private static Process StartTone(double hz, string directory, string name)
    {
        string ready = Path.Combine(directory, name + ".ready"), stop = Path.Combine(directory, name + ".stop");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(ProcessCaptureTests).Assembly.Location);
        foreach (string arg in new[] { "--tone", hz.ToString(System.Globalization.CultureInfo.InvariantCulture), ready, stop }) info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        try { Wait(() => File.Exists(ready), "tone startup", () => File.Exists(ready + ".error") ? File.ReadAllText(ready + ".error") : process.HasExited ? "helper exited" : "waiting"); return process; }
        catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
    }
    private static void Wait(Func<bool> predicate, string description, Func<string>? status = null)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed.TotalSeconds > 18) throw new Exception(description + ": " + status?.Invoke()); Thread.Sleep(100); }
    }
    private static double Energy(short[] stereo, double hz)
    {
        double sin = 0, cos = 0; int frames = stereo.Length / 2;
        for (int i = 0; i < frames; i++) { double phase = i * 2 * Math.PI * hz / 48000; sin += stereo[i * 2] / 32768d * Math.Sin(phase); cos += stereo[i * 2] / 32768d * Math.Cos(phase); }
        return 2 * Math.Sqrt(sin * sin + cos * cos) / frames;
    }
    public static int Probe(string report)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(report))!; Directory.CreateDirectory(directory);
        var processes = new List<Process>();
        try
        {
            if (!CaptureModes.ProcessSupported) throw new PlatformNotSupportedException("Windows process loopback not supported");
            var voice = StartTone(880, directory, "voice"); processes.Add(voice);
            var game = StartTone(1320, directory, "game"); processes.Add(game);
            int currentVoice = voice.Id;
            long origin = Stopwatch.GetTimestamp();
            var a = new TimelineBuffer(40, 2); var b = new TimelineBuffer(40, 2); var excluded = new TimelineBuffer(40, 2); var selfExcluded = new TimelineBuffer(40, 2);
            using var voiceCapture = new CaptureSource(a, origin, true, "", processSelector: () => Volatile.Read(ref currentVoice));
            using var gameCapture = new CaptureSource(b, origin, true, "", processSelector: () => game.Id);
            using var excludeVoice = new CaptureSource(excluded, origin, true, "", excludeSelf: true, processSelector: () => Volatile.Read(ref currentVoice));
            using var excludeSelf = new CaptureSource(selfExcluded, origin, true, "", excludeSelf: true);
            Wait(() => voiceCapture.Connected && gameCapture.Connected && excludeVoice.Connected && excludeSelf.Connected, "activation", () => string.Join(" | ", voiceCapture.Status, gameCapture.Status, excludeVoice.Status, excludeSelf.Status));
            using var ownTone = new WasapiOut(AudioClientShareMode.Shared, 60);
            ownTone.Init(new SignalGenerator(48000, 2) { Gain = 0.035, Frequency = 440, Type = SignalGeneratorType.Sin }); ownTone.Play();
            Thread.Sleep(1700);
            long start = CaptureSource.CurrentFrame(origin) - 48000;
            short[] sa = a.Snapshot(start, 24000), sb = b.Snapshot(start, 24000), se = excluded.Snapshot(start, 24000), ss = selfExcluded.Snapshot(start, 24000);
            var separation = new { VoiceWanted = Energy(sa, 880), VoiceGameLeak = Energy(sa, 1320), VoiceOwnLeak = Energy(sa, 440), GameWanted = Energy(sb, 1320), GameVoiceLeak = Energy(sb, 880), ExcludedVoiceLeak = Energy(se, 880), ExcludedOther = Energy(se, 1320), ExcludedSelfLeak = Energy(ss, 440) };
            bool isolated = separation.VoiceWanted > 0.005 && separation.GameWanted > 0.005 && separation.ExcludedOther > 0.005
                && separation.VoiceGameLeak < separation.VoiceWanted * 0.05 && separation.VoiceOwnLeak < separation.VoiceWanted * 0.05
                && separation.GameVoiceLeak < separation.GameWanted * 0.05 && separation.ExcludedVoiceLeak < separation.ExcludedOther * 0.05 && separation.ExcludedSelfLeak < 0.001;
            File.WriteAllText(Path.Combine(directory, "voice.stop"), "stop"); voice.WaitForExit(5000);
            Volatile.Write(ref currentVoice, 0);
            Wait(() => !voiceCapture.Connected, "detect exit", () => voiceCapture.Status);
            var restarted = StartTone(880, directory, "voice-restarted"); processes.Add(restarted); Volatile.Write(ref currentVoice, restarted.Id);
            Wait(() => voiceCapture.Connected && voiceCapture.ActiveProcessId == restarted.Id, "reconnect", () => voiceCapture.Status);
            Thread.Sleep(1300);
            var resumed = a.Snapshot(CaptureSource.CurrentFrame(origin) - 36000, 24000);
            double restartedEnergy = Energy(resumed, 880);
            ownTone.Stop();
            var result = new { Success = isolated && restartedEnergy > 0.005, ProcessSeparation = separation, Reconnected = restartedEnergy > 0.005, RestartedEnergy = restartedEnergy,
                VoicePackets = voiceCapture.Packets, GamePackets = gameCapture.Packets, Windows = Environment.OSVersion.Version.ToString() };
            File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(File.ReadAllText(report)); return result.Success ? 0 : 1;
        }
        catch (Exception ex) { File.WriteAllText(report, JsonSerializer.Serialize(new { Success = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(File.ReadAllText(report)); return 1; }
        finally
        {
            foreach (var process in processes) { if (!process.HasExited) process.Kill(); process.WaitForExit(5000); process.Dispose(); }
        }
    }

    // The PowerShell harness starts sibling helper processes, so the real name-based
    // resolver can select them while still rejecting EchoReplay's own descendants.
    public static int EngineProbe(string report)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(report))!;
        try
        {
            var settings = new AppSettings { CaptureMode = CaptureModes.Applications, VoiceProcessName = "EchoReplayVoiceProbe", GameProcessName = "EchoReplayGameProbe", CaptureMicrophone = false, SaveSeparateTracks = false, Minutes = 1, OutputFolder = Path.Combine(directory, "clips") };
            using var engine = new ReplayEngine(); engine.Start(settings);
            Wait(() => engine.SourcesHealthy, "engine activation", () => engine.SystemSource?.Status + " | " + engine.GameSource?.Status);
            int originalVoice = engine.SystemSource!.ActiveProcessId, originalGame = engine.GameSource!.ActiveProcessId;
            Thread.Sleep(1400);
            string first = engine.SaveAsync(settings).GetAwaiter().GetResult();
            var voice = EditAudio.ReadSource(Path.Combine(first, "語音聊天.wav"));
            var game = EditAudio.ReadSource(Path.Combine(first, "遊戲.wav"));
            short[] Tail(AudioSource source) => source.Samples.Skip(Math.Max(0, source.Samples.Length - 48000)).ToArray();
            double voiceEnergy = Energy(Tail(voice), 880), voiceLeak = Energy(Tail(voice), 1320), gameEnergy = Energy(Tail(game), 1320);
            bool split = voiceEnergy > 0.005 && gameEnergy > 0.005 && voiceLeak < voiceEnergy * 0.05;
            using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "錄音資訊.json")));
            bool metadata = meta.RootElement.GetProperty("CaptureMode").GetString() == CaptureModes.Applications && EditAudio.Load(Path.Combine(first, "混音.wav")).Game is not null;
            File.WriteAllText(Path.Combine(directory, "voice.stop"), "stop");
            Wait(() => !engine.SystemSource.Connected, "voice stops", () => engine.SystemSource.Status);
            File.WriteAllText(Path.Combine(directory, "restart.request"), "restart");
            Wait(() => engine.SystemSource.Connected && engine.SystemSource.ActiveProcessId != originalVoice, "voice name-based restart", () => engine.SystemSource.Status);
            Thread.Sleep(1300);
            string second = engine.SaveAsync(settings).GetAwaiter().GetResult();
            bool reconnected = Energy(Tail(EditAudio.ReadSource(Path.Combine(second, "語音聊天.wav"))), 880) > 0.005 && engine.GameSource.ActiveProcessId == originalGame;
            using var ownTone = new WasapiOut(AudioClientShareMode.Shared, 60);
            ownTone.Init(new SignalGenerator(48000, 2) { Gain = 0.035, Frequency = 440, Type = SignalGeneratorType.Sin }); ownTone.Play();
            settings = settings with { CaptureMode = CaptureModes.System };
            engine.Start(settings); Wait(() => engine.SourcesHealthy, "exclude-own system", () => engine.SystemSource?.Status ?? "");
            Thread.Sleep(1300); string system = engine.SaveAsync(settings).GetAwaiter().GetResult();
            var systemSamples = Tail(EditAudio.ReadSource(Path.Combine(system, "混音.wav")));
            double wanted = Energy(systemSamples, 880), excludedOwn = Energy(systemSamples, 440);
            bool ownExcluded = wanted > 0.005 && excludedOwn < 0.001;
            settings = settings with { CaptureMode = CaptureModes.Device };
            engine.Start(settings); Wait(() => engine.SourcesHealthy, "legacy endpoint", () => engine.SystemSource?.Status ?? "");
            Thread.Sleep(1300); string legacy = engine.SaveAsync(settings).GetAwaiter().GetResult();
            double legacyOwn = Energy(Tail(EditAudio.ReadSource(Path.Combine(legacy, "混音.wav"))), 440);
            bool legacyWorks = legacyOwn > 0.005;
            var result = new { Success = split && metadata && reconnected && ownExcluded && legacyWorks, ApplicationTracks = split, MetadataAndEditor = metadata,
                NameBasedReconnect = reconnected, OwnPreviewExcluded = ownExcluded, LegacyEndpointWorks = legacyWorks,
                VoiceEnergy = voiceEnergy, GameEnergy = gameEnergy, CrossTalk = voiceLeak, SystemWanted = wanted, SystemOwnLeak = excludedOwn, LegacyOwnEnergy = legacyOwn };
            File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(File.ReadAllText(report)); return result.Success ? 0 : 1;
        }
        catch (Exception ex) { File.WriteAllText(report, JsonSerializer.Serialize(new { Success = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(File.ReadAllText(report)); return 1; }
    }
}
