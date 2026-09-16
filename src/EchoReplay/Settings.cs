using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace EchoReplay;

public sealed record AppSettings
{
    public int Minutes { get; set; } = 5;
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "EchoReplay");
    public string OutputDeviceId { get; set; } = "";
    public string MicrophoneDeviceId { get; set; } = "";
    public string CaptureMode { get; set; } = CaptureModes.Device;
    public string VoiceProcessName { get; set; } = "Discord";
    public string GameProcessName { get; set; } = "";
    public double GameGain { get; set; } = 1;
    public bool CaptureMicrophone { get; set; } = true;
    public bool SaveSeparateTracks { get; set; } = true;
    public bool RecordOnLaunch { get; set; } = true;
    public bool StartHidden { get; set; } = false;
    public bool RunAtLogin { get; set; } = false;
    public string SaveHotkey { get; set; } = "Ctrl+Alt+F9";
    public string ShowHotkey { get; set; } = "Ctrl+Alt+F10";
    public double SystemGain { get; set; } = 1;
    public double MicrophoneGain { get; set; } = 1;
}

public static class SettingsStore
{
    public static string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoReplay");
    public static string? LoadWarning { get; private set; }
    public static AppSettings Load()
    {
        var path = Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
            if (settings.Minutes is < 1 or > 10) settings.Minutes = 5;
            if (string.IsNullOrWhiteSpace(settings.OutputFolder)) settings.OutputFolder = new AppSettings().OutputFolder;
            if (!double.IsFinite(settings.SystemGain)) settings.SystemGain = 1;
            if (!double.IsFinite(settings.MicrophoneGain)) settings.MicrophoneGain = 1;
            settings.SystemGain = Math.Clamp(settings.SystemGain, 0, 2);
            settings.MicrophoneGain = Math.Clamp(settings.MicrophoneGain, 0, 2);
            settings.GameGain = double.IsFinite(settings.GameGain) ? Math.Clamp(settings.GameGain, 0, 2) : 1;
            // Keep unsupported process settings visible. Never silently broaden an app-only recording.
            settings.VoiceProcessName = ProcessCatalog.Normalize(settings.VoiceProcessName);
            settings.GameProcessName = ProcessCatalog.Normalize(settings.GameProcessName);
            try
            {
                if (Hotkey.Parse(settings.SaveHotkey) == Hotkey.Parse(settings.ShowHotkey)) throw new ArgumentException();
            }
            catch (ArgumentException)
            {
                settings.SaveHotkey = "Ctrl+Alt+F9";
                settings.ShowHotkey = "Ctrl+Alt+F10";
                LoadWarning = "儲存的快捷鍵無效，已恢復預設快捷鍵。";
            }
            return settings;
        }
        catch (Exception ex)
        {
            LoadWarning = "設定無法讀取，已使用預設值：" + ex.Message;
            return new();
        }
    }
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        string path = Path.Combine(DataDirectory, "settings.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

public static class LoginStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("EchoReplay") is string;
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("找不到程式位置。");
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("請從 EchoReplay.exe 啟用開機執行。");
            key.SetValue("EchoReplay", "\"" + executable + "\" --background");
        }
        else key.DeleteValue("EchoReplay", false);
    }
}
