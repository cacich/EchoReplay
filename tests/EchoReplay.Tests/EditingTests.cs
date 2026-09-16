using EchoReplay;
using NAudio.Wave;
using System.IO;
using System.Text.Json;

internal static class EditingTests
{
    public static void RunAll(string root, Action<string, Action> run, Action<bool, string> check)
    {
        root = Path.Combine(root, "editing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        EditAudio Synthetic(int frames, bool separate = false) => new() { MainPath = Path.Combine(root, "test.wav"), SourcePaths = [], Separate = separate,
            System = new(Enumerable.Range(0, frames * 2).Select(i => (short)(i / 2 * 100 + i % 2 * 10)).ToArray(), 2),
            Microphone = separate ? new(Enumerable.Repeat((short)1000, frames).ToArray(), 1) : null };
        short[] Render(EditAudio audio, EditState state)
        {
            using var stream = new EditPcmStream(audio, state);
            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes); var samples = new short[bytes.Length / 2]; Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length); return samples;
        }
        run("Editor trims exact frames with no stereo channel shift", () =>
        {
            var source = Synthetic(100);
            var edits = new EditHistory(new([new(0, 100)])); edits.Edit(23, 81, "trim");
            check(Render(source, edits.Current).SequenceEqual(source.System.Samples.Skip(46).Take(116)), "exact trim");
        });
        run("Repeated middle deletions map back to original samples", () =>
        {
            var source = Synthetic(100); var edits = new EditHistory(new([new(0, 100)]));
            edits.Edit(20, 40, "delete"); edits.Edit(10, 30, "delete");
            var expected = source.System.Samples.Take(20).Concat(source.System.Samples.Skip(100));
            check(Render(source, edits.Current).SequenceEqual(expected), "ripple cuts");
        });
        run("Mute preserves time and both tracks; undo/redo restores edits", () =>
        {
            var source = Synthetic(100, true); var edits = new EditHistory(new([new(0, 100)]));
            var original = Render(source, edits.Current); edits.Edit(20, 40, "mute"); var muted = Render(source, edits.Current);
            check(muted.Length == original.Length && muted.Skip(40).Take(40).All(x => x == 0) && muted.Take(40).SequenceEqual(original.Take(40)), "mute bounds");
            edits.Undo(); check(Render(source, edits.Current).SequenceEqual(original), "undo"); edits.Redo(); check(Render(source, edits.Current).SequenceEqual(muted), "redo");
            edits.Undo(); edits.Edit(0, 50, "trim"); check(!edits.CanRedo, "new edit clears redo");
        });
        run("Track gains and mono mapping remain aligned after cutting", () =>
        {
            var source = Synthetic(100, true); var state = new EditState([new(70, 10)], 0, 0.5);
            check(Render(source, state).All(x => x == 500), "only microphone");
            check(Render(source, state with { SystemGain = 1, MicrophoneGain = 0 }).SequenceEqual(source.System.Samples.Skip(140).Take(20)), "only system");
        });
        run("Unity edit preserves high-amplitude imported PCM", () =>
        {
            var source = Synthetic(1); source.System.Samples[0] = 32000; source.System.Samples[1] = -32000;
            check(Render(source, new([new(0, 1)])).SequenceEqual(source.System.Samples), "no double limiter");
        });
        run("Fade envelopes apply to output timeline endpoints", () =>
        {
            var source = Synthetic(100, true); var values = Render(source, new([new(0, 100)], 0, 1, 0.001, 0.001));
            check(values[0] == 0 && values[^1] == 0 && values[100] == 1000, "fade endpoints and middle");
        });
        run("Editor rejects empty edits and malformed saved spans", () =>
        {
            var source = Synthetic(100); var edits = new EditHistory(new([new(0, 100)]));
            bool rejected = false; try { edits.Edit(0, 100, "delete"); } catch (ArgumentException) { rejected = true; }
            check(rejected && edits.Current.Frames == 100, "delete all prevented");
            foreach (var state in new[] { new EditState([new(99, 2)]), new EditState([new(-1, 2)]), new EditState([new(0, 2)], double.NaN), new EditState([new(0, 2)], FadeIn: double.PositiveInfinity) })
            {
                rejected = false; try { source.Validate(state); } catch (InvalidDataException) { rejected = true; } check(rejected, "validate state");
            }
        });
        run("Preview range, seek and loop read the same edited samples", () =>
        {
            var source = Synthetic(100); using var stream = new EditPcmStream(source, new([new(0, 20), new(40, 20)]), 18, 5) { Loop = true };
            byte[] bytes = new byte[40]; check(stream.Read(bytes, 0, bytes.Length) == 40, "loop fills buffer");
            check(bytes.Take(20).SequenceEqual(bytes.Skip(20)), "loop repeats"); stream.Position = 8; stream.ReadExactly(bytes.AsSpan(0, 4));
            check(BitConverter.ToInt16(bytes) == 4000, "seek source mapping");
        });
        string fixture = Path.Combine(root, "Replay_fixture"); Directory.CreateDirectory(fixture);
        string main = Path.Combine(fixture, "混音.wav");
        short[] signal = Enumerable.Range(0, 48000 * 2 * 2).Select(i => (short)(10000 * Math.Sin(i / 2 * Math.PI * 2 * 440 / 48000))).ToArray();
        WaveExporter.WritePcm(main, signal, 2); WaveExporter.WritePcm(Path.Combine(fixture, "電腦聲音.wav"), signal, 2);
        WaveExporter.WritePcm(Path.Combine(fixture, "麥克風.wav"), Enumerable.Repeat((short)500, 48000 * 2).ToArray(), 1);
        File.WriteAllText(Path.Combine(fixture, "錄音資訊.json"), "{\"SystemGain\":0.7,\"MicrophoneGain\":0.4}");
        var loaded = EditAudio.Load(main); var initial = new EditState([new(0, loaded.Frames)]);
        run("Recording opens raw tracks and honors recorded mix gains", () =>
        {
            check(loaded.Separate && loaded.Microphone?.Frames == 96000, "split source loading");
            var state = EditFiles.InitialState(loaded); check(state.SystemGain == 0.7 && state.MicrophoneGain == 0.4, "initial gains");
        });
        run("Project roundtrip restores cuts, mute and source gains", () =>
        {
            var state = new EditState([new(100, 200), new(1000, 400, true)], 0.7, 0.3, 0.01, 0.01);
            EditFiles.SaveProject(loaded, state); var actual = EditFiles.LoadProject(loaded)!;
            check(actual.Spans.SequenceEqual(state.Spans) && actual.SystemGain == state.SystemGain && actual.FadeOut == state.FadeOut, "round trip");
            string projectPath = EditFiles.ProjectPath(main); string valid = File.ReadAllText(projectPath);
            File.WriteAllText(projectPath, valid.Replace("\"Version\": 1", "\"Version\": 900"));
            bool rejected = false; try { EditFiles.LoadProject(loaded); } catch (InvalidDataException) { rejected = true; } check(rejected, "unknown version");
            File.WriteAllText(projectPath, valid);
            File.SetLastWriteTimeUtc(loaded.SourcePaths[0], DateTime.UtcNow.AddMinutes(-10));
            rejected = false; try { EditFiles.LoadProject(loaded); } catch (InvalidDataException) { rejected = true; } check(rejected, "changed source");
            EditFiles.SaveProject(loaded, initial);
        });
        string exportFolder = Path.Combine(root, "Exports"); Directory.CreateDirectory(exportFolder);
        run("WAV export matches preview PCM exactly after mixed edits", () =>
        {
            var state = new EditState([new(2400, 12000), new(48000, 10000, true), new(70000, 10000)], 0.5, 0.6, 0.02, 0.03);
            string destination = Path.Combine(exportFolder, "edited.wav"); EditFiles.Export(loaded, state, destination, 192000, default);
            using var reader = new WaveFileReader(destination); byte[] bytes = new byte[reader.Length]; reader.ReadExactly(bytes);
            var expected = Render(loaded, state); short[] actual = new short[bytes.Length / 2]; Buffer.BlockCopy(bytes, 0, actual, 0, bytes.Length);
            check(actual.SequenceEqual(expected) && reader.Length == state.Frames * 4L, "preview export match");
        });
        run("MP3 192/320 kbps encodes and decodes audible stereo", () =>
        {
            foreach (int bitrate in new[] { 192000, 320000 })
            {
                string destination = Path.Combine(exportFolder, bitrate + ".mp3"); EditFiles.Export(loaded, initial, destination, bitrate, default);
                var decoded = EditAudio.ReadSource(destination);
                check(decoded.Channels == 2 && Math.Abs(decoded.Frames / 48000d - 2) < 0.15 && decoded.Samples.Any(x => Math.Abs((int)x) > 1000), "mp3 roundtrip");
                double measured = new FileInfo(destination).Length * 8 / 2d; check(Math.Abs(measured / bitrate - 1) < 0.2, "bitrate");
            }
        });
        run("Canceled export keeps existing destination and cleans temporary files", () =>
        {
            string path = Path.Combine(exportFolder, "keep.wav"); File.WriteAllText(path, "existing");
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool rejected = false;
            try { EditFiles.Export(loaded, initial, path, 192000, canceled.Token); } catch (OperationCanceledException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == "existing" && !Directory.EnumerateFiles(exportFolder, ".echo-export-*").Any(), "atomic cancel");
            File.Delete(path);
            string mp3 = Path.Combine(exportFolder, "keep.mp3"); File.WriteAllText(mp3, "existing mp3");
            using var midEncode = new CancellationTokenSource(); rejected = false;
            try { EditFiles.Export(loaded, initial, mp3, 192000, midEncode.Token, value => { if (value > 0.1) midEncode.Cancel(); }); }
            catch (OperationCanceledException) { rejected = true; }
            check(rejected && File.ReadAllText(mp3) == "existing mp3" && !Directory.EnumerateFiles(exportFolder, ".echo-export-*").Any(), "mid encode cancel");
            File.Delete(mp3);
        });
        run("Export refuses to overwrite sources or put finished clips beside originals", () =>
        {
            foreach (string target in new[] { main, loaded.SourcePaths[0], Path.Combine(fixture, "finished.mp3") })
            { bool rejected = false; try { EditFiles.Export(loaded, initial, target, 192000, default); } catch (IOException) { rejected = true; } check(rejected, "protected original"); }
        });
        run("Library import copies MP3, records label and discovers exports", () =>
        {
            string source = Path.Combine(exportFolder, "192000.mp3"); byte[] before = File.ReadAllBytes(source);
            string imported = ClipLibrary.Import(source, root, default);
            check(File.Exists(imported) && File.ReadAllBytes(source).SequenceEqual(before), "import preserves external source");
            var list = ClipLibrary.Scan(root); check(list.Any(c => c.AudioPath == main) && list.Any(c => c.AudioPath == source) && list.Any(c => c.AudioPath == imported), "scan groups and exports");
            var clip = list.Single(c => c.AudioPath == imported); ClipLibrary.Rename(clip, "我的音效"); check(ClipLibrary.Scan(root).Any(c => c.Name == "我的音效"), "rename");
        });
        run("Deletion scope rejects unrelated files and protects exports", () =>
        {
            var clip = ClipLibrary.Scan(root).Single(c => c.AudioPath == main); check(ClipLibrary.DeletionTarget(clip, root) == fixture, "one recording group");
            string unrelated = Path.Combine(fixture, "do-not-delete.txt"); File.WriteAllText(unrelated, "keep");
            bool rejected = false; try { ClipLibrary.DeletionTarget(clip, root); } catch (IOException) { rejected = true; } check(rejected, "unknown file guard"); File.Delete(unrelated);
            rejected = false; try { ClipLibrary.DeletionTarget(clip, exportFolder); } catch (IOException) { rejected = true; } check(rejected, "outside library guard");
            check(Directory.EnumerateFiles(exportFolder, "*.mp3").Count() == 2, "exports preserved");
        });
        run("Waveform reflects muted sections and bounded peak buckets", () =>
        {
            var peaks = EditFiles.Peaks(loaded, new([new(0, 48000), new(48000, 48000, true)]), default);
            check(peaks.Length == 400 && peaks.Take(200).Any(x => x > 0.1) && peaks.Skip(200).All(x => x == 0), "waveform follows edits");
        });
        run("Editing render continues while capture ring receives writes", () =>
        {
            var buffer = new TimelineBuffer(2, 2); float[] packet = Enumerable.Repeat(0.25f, 960).ToArray();
            Parallel.Invoke(() => { for (int i = 0; i < 500; i++) buffer.Write(i * 480L, packet, 480); }, () => EditFiles.Peaks(loaded, initial, default));
            check(buffer.Snapshot(499 * 480L, 480).All(s => s == 8192), "capture remains independent");
        });
    }
}
