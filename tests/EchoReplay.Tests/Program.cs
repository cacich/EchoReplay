using EchoReplay;
using System.IO;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Windows.Interop;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--tone") return ProcessCaptureTests.Tone(args);
        if (args.Length > 1 && args[0] == "--process-capture") return ProcessCaptureTests.Probe(args[1]);
        if (args.Length > 1 && args[0] == "--engine-capture") return ProcessCaptureTests.EngineProbe(args[1]);
        string directory = Path.GetFullPath(Path.Combine("artifacts", "test-unit"));
        Directory.CreateDirectory(directory);
        try
        {
            Run("Ring preserves only requested tail across wrap", () =>
            {
                var ring = new TimelineBuffer(1, 1);
                ring.Write(0, Enumerable.Repeat(0.25f, 48000).ToArray(), 48000);
                ring.Write(48000, Enumerable.Repeat(0.5f, 24000).ToArray(), 24000);
                short[] tail = ring.Snapshot(24000, 48000);
                Check(tail.Take(24000).All(x => x == 8192), "old half");
                Check(tail.Skip(24000).All(x => x == 16384), "new half");
            });
            Run("No stale audio appears during silent gaps", () =>
            {
                var ring = new TimelineBuffer(1, 1);
                ring.Write(0, Enumerable.Repeat(0.75f, 48000).ToArray(), 48000);
                ring.Write(96000 + 123, Enumerable.Repeat(0.5f, 20).ToArray(), 20);
                short[] data = ring.Snapshot(96000, 48000);
                Check(data.Take(123).All(x => x == 0), "silence preceding partial page");
                Check(data.Skip(123).Take(20).All(x => x == 16384), "packet");
                Check(data.Skip(143).All(x => x == 0), "silence following packet");
            });
            Run("Negative start timestamps are safely clipped", () =>
            {
                var ring = new TimelineBuffer(1, 1);
                ring.Write(-2, new[] { 0.1f, 0.2f, 0.3f, 0.4f }, 4);
                Check(ring.Snapshot(0, 2).SequenceEqual(new short[] { 9830, 13107 }), "trim start");
            });
            Run("Delayed packets do not overwrite newer ring generations", () =>
            {
                var ring = new TimelineBuffer(1, 1);
                ring.Write(48000, new[] { 0.5f }, 1);
                ring.Write(0, new[] { 0.1f }, 1);
                Check(ring.Snapshot(48000, 1)[0] == 16384, "late packet");
            });
            Run("Independent source timestamps align in a common snapshot", () =>
            {
                var a = new TimelineBuffer(1, 2);
                var b = new TimelineBuffer(1, 1);
                a.Write(1234, new[] { 0.5f, -0.5f }, 1);
                b.Write(1234, new[] { 0.25f }, 1);
                Check(a.Snapshot(1200, 100)[68] == 16384 && b.Snapshot(1200, 100)[34] == 8192, "alignment");
            });
            Run("Snapshot is independent of subsequent capture writes", () =>
            {
                var ring = new TimelineBuffer(1, 1);
                ring.Write(0, new[] { 0.5f }, 1);
                var snapshot = ring.Snapshot(0, 1);
                ring.Write(0, new[] { -0.5f }, 1);
                Check(snapshot[0] == 16384, "snapshot mutation");
            });
            Run("Invalid float data cannot poison the audio buffer", () =>
            {
                var ring = new TimelineBuffer(1, 1);
                ring.Write(0, new[] { float.NaN, float.PositiveInfinity, 2f, -2f }, 4);
                Check(ring.Snapshot(0, 4).SequenceEqual(new short[] { 0, 0, 32767, -32768 }), "sanitize");
            });
            Run("WAV duration, stereo channels and PCM samples round trip", () =>
            {
                string path = Path.Combine(directory, "stereo.wav");
                var samples = new short[96000];
                samples[0] = 12345;
                samples[1] = -12345;
                WaveExporter.WritePcm(path, samples, 2);
                using var reader = new WaveFileReader(path);
                Check(reader.WaveFormat.Channels == 2 && reader.WaveFormat.BitsPerSample == 16 && reader.WaveFormat.SampleRate == 48000, "format");
                Check(Math.Abs(reader.TotalTime.TotalSeconds - 1) < 0.0001, "duration");
                byte[] bytes = new byte[4];
                reader.ReadExactly(bytes);
                Check(BitConverter.ToInt16(bytes, 0) == 12345 && BitConverter.ToInt16(bytes, 2) == -12345, "samples");
            });
            Run("Mix duplicates mono microphone and limits overlapping peaks", () =>
            {
                string path = Path.Combine(directory, "mix.wav");
                WaveExporter.WriteMix(path, new short[] { 0, 0, 30000, -30000 }, new short[] { 8192, 30000 }, 1, 1);
                using var reader = new WaveFileReader(path);
                byte[] bytes = new byte[8];
                reader.ReadExactly(bytes);
                Check(BitConverter.ToInt16(bytes, 0) == 8192 && BitConverter.ToInt16(bytes, 2) == 8192, "mono mapping");
                Check(BitConverter.ToInt16(bytes, 4) > 30000 && BitConverter.ToInt16(bytes, 6) == 0, "limiter and cancellation");
            });
            Run("Hotkey parser rejects reserved or ambiguous combinations", () =>
            {
                Check(Hotkey.Parse("Ctrl+Alt+F9") == Hotkey.Parse("Alt+Ctrl+F9"), "normalize");
                foreach (string invalid in new[] { "F9", "Shift+F9", "Ctrl+F12", "Ctrl+None", "Win+F9" })
                {
                    bool rejected = false;
                    try { Hotkey.Parse(invalid); } catch (ArgumentException) { rejected = true; }
                    Check(rejected, "reject " + invalid);
                }
            });
            Run("Windows hotkey conflicts restore prior registrations", () =>
            {
                using var firstWindow = new HwndSource(new HwndSourceParameters("EchoReplay test A") { Width = 0, Height = 0 });
                using var secondWindow = new HwndSource(new HwndSourceParameters("EchoReplay test B") { Width = 0, Height = 0 });
                using var first = new HotkeyManager(firstWindow.Handle);
                using var second = new HotkeyManager(secondWindow.Handle);
                first.Apply("Ctrl+Alt+Shift+F6", "Ctrl+Alt+Shift+F7");
                second.Apply("Ctrl+Alt+Shift+F8", "Ctrl+Alt+Shift+F9");
                bool rejected = false;
                try { second.Apply("Ctrl+Alt+Shift+F6", "Ctrl+Alt+Shift+F10"); } catch { rejected = true; }
                Check(rejected, "conflict rejected");
                bool stillOwned = !RegisterHotKey(firstWindow.Handle, 999, 7, 0x77);
                if (!stillOwned) UnregisterHotKey(firstWindow.Handle, 999);
                Check(stillOwned, "previous F8 registration restored");
                second.Suspend();
                bool released = RegisterHotKey(firstWindow.Handle, 999, 7, 0x77);
                if (released) UnregisterHotKey(firstWindow.Handle, 999);
                Check(released, "hotkeys suspended while editing");
                second.Apply("Ctrl+Alt+Shift+F8", "Ctrl+Alt+Shift+F9");
            });
            Run("Settings survive atomic save/load and corrupt files recover", () =>
            {
                SettingsStore.DataDirectory = Path.Combine(directory, "settings");
                var expected = new AppSettings { Minutes = 3, OutputFolder = directory, SaveHotkey = "Ctrl+Shift+F8", CaptureMicrophone = false };
                SettingsStore.Save(expected);
                Check(SettingsStore.Load() == expected, "round trip");
                File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "settings.json"), "broken");
                Check(SettingsStore.Load().Minutes == 5 && SettingsStore.LoadWarning is not null, "corrupt settings fallback");
            });
            Run("Concurrent ring writes and snapshots stay bounded", () =>
            {
                var ring = new TimelineBuffer(1, 2);
                var packet = Enumerable.Repeat(0.2f, 960).ToArray();
                Parallel.Invoke(
                    () => { for (int i = 0; i < 300; i++) ring.Write(i * 480L, packet, 480); },
                    () => { for (int i = 0; i < 300; i++) Check(ring.Snapshot(i * 480L, 480).Length == 960, "snapshot length"); });
            });
            EditingTests.RunAll(directory, Run, Check);
            ProcessAudioTests.RunAll(directory, Run, Check);
            Console.WriteLine($"PASS: {passed} tests");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAILED: " + message); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
