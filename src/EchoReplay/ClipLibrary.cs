using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.VisualBasic.FileIO;
using NAudio.Wave;

namespace EchoReplay;

public sealed record LibraryClip(string AudioPath, string? RecordingFolder, string Name, DateTime Created, double Seconds)
{
    public string Detail => $"{Created:MM/dd HH:mm} · {(Seconds >= 0 ? TimeSpan.FromSeconds(Seconds).ToString(@"mm\:ss") : "MP3")} · {(RecordingFolder is null ? "成品／音檔" : "原始錄音")}";
}

public static class ClipLibrary
{
    private static string LabelPath(string path) => path + ".label.json";
    public static List<LibraryClip> Scan(string root)
    {
        var result = new List<LibraryClip>();
        if (!Directory.Exists(root)) return result;
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(folder).StartsWith('.') || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
            string main = Path.Combine(folder, "混音.wav");
            if (File.Exists(main) && File.Exists(Path.Combine(folder, "錄音資訊.json"))) Add(main, folder);
        }
        foreach (string folder in new[] { root, Path.Combine(root, "Exports") })
        {
            if (!Directory.Exists(folder)) continue;
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string file in Directory.EnumerateFiles(folder))
                if (!Path.GetFileName(file).StartsWith('.') && Path.GetExtension(file).ToLowerInvariant() is ".wav" or ".mp3") Add(file, null);
        }
        return result.OrderByDescending(c => c.Created).ToList();
        void Add(string path, string? folder)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                string name = folder is null ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(folder);
                try { if (File.Exists(LabelPath(path))) name = JsonSerializer.Deserialize<string>(File.ReadAllText(LabelPath(path))) ?? name; } catch { }
                double seconds = -1;
                try
                {
                    using WaveStream wave = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                        ? new WaveFileReader(path) : new MediaFoundationReader(path);
                    seconds = wave.TotalTime.TotalSeconds;
                }
                catch { } // Keep damaged/unsupported files visible so they can still be removed.
                result.Add(new(path, folder, name, File.GetCreationTime(path), seconds));
            }
            catch (IOException) { } // Another process may be moving a clip while the list refreshes.
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
        }
    }
    public static void Rename(LibraryClip clip, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new ArgumentException("名稱請輸入 1～100 個字元。");
        File.WriteAllText(LabelPath(clip.AudioPath), JsonSerializer.Serialize(name));
    }
    public static string Import(string source, string root, CancellationToken cancellation)
    {
        var audio = EditAudio.ReadSource(source, cancellation);
        Directory.CreateDirectory(root);
        string name = "Import_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N")[..6];
        string temporary = Path.Combine(root, "." + name + ".partial"), destination = Path.Combine(root, name);
        Directory.CreateDirectory(temporary);
        try
        {
            WaveExporter.WritePcm(Path.Combine(temporary, "混音.wav"), audio.Samples, audio.Channels);
            File.WriteAllText(Path.Combine(temporary, "錄音資訊.json"), JsonSerializer.Serialize(new { ImportedFrom = Path.GetFileName(source) }));
            File.WriteAllText(LabelPath(Path.Combine(temporary, "混音.wav")), JsonSerializer.Serialize(Path.GetFileNameWithoutExtension(source)));
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(temporary, destination);
            return Path.Combine(destination, "混音.wav");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    public static string DeletionTarget(LibraryClip clip, string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string audio = Path.GetFullPath(clip.AudioPath);
        if (clip.RecordingFolder is string folder)
        {
            folder = Path.GetFullPath(folder);
            if (!string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase) || !Path.Combine(folder, "混音.wav").Equals(audio, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(folder, "錄音資訊.json"))) throw new IOException("錄音不在目前音檔庫中，請重新整理。");
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(folder).Any()) throw new IOException("資料夾含有其他目錄，請使用檔案總管檢查後再刪除。");
            string[] allowed = ["混音.wav", "電腦聲音.wav", "語音聊天.wav", "遊戲.wav", "麥克風.wav", "錄音資訊.json", "混音.wav.echoedit.json", "混音.wav.label.json"];
            if (Directory.EnumerateFiles(folder).Any(f => !allowed.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase) || (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("資料夾含有其他檔案（可能是成品），為避免誤刪，請先移出其他檔案。");
            return folder;
        }
        string parent = Path.GetDirectoryName(audio)!;
        if (!(parent.Equals(root, StringComparison.OrdinalIgnoreCase) || parent.Equals(Path.Combine(root, "Exports"), StringComparison.OrdinalIgnoreCase))
            || Path.GetExtension(audio).ToLowerInvariant() is not (".wav" or ".mp3") || (File.GetAttributes(audio) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("音檔不在目前音檔庫中，請重新整理。");
        return audio;
    }
    public static void Recycle(LibraryClip clip, string root)
    {
        string target = DeletionTarget(clip, root);
        if (clip.RecordingFolder is not null)
            FileSystem.DeleteDirectory(target, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        else
        {
            FileSystem.DeleteFile(target, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            foreach (string sidecar in new[] { EditFiles.ProjectPath(target), LabelPath(target) })
                if (File.Exists(sidecar)) FileSystem.DeleteFile(sidecar, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
    }
}
