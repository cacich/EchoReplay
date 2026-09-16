using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Forms = System.Windows.Forms;

namespace EchoReplay;

public partial class MainWindow : Window
{
    private AppSettings settings;
    private readonly ReplayEngine engine = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem trayRecord;
    private HotkeyManager? hotkeys;
    private bool exiting;
    private bool initialized;
    private bool wasUnhealthy;
    private string? lastClip;
    private bool shownHideTip;
    private string? startupHotkeyWarning;

    public MainWindow(AppSettings settings)
    {
        this.settings = settings;
        InitializeComponent();
        Editor.Configure(() => this.settings.OutputFolder);
        Editor.ClipDeleted += path => { if (lastClip is not null && Path.Combine(lastClip, "混音.wav") == path) { lastClip = null; LastSavedText.Text = "最新原始錄音已刪除。"; PlayButton.IsEnabled = ShowClipButton.IsEnabled = false; } };
        foreach (var box in new[] { SaveKeyBox, ShowKeyBox })
        {
            box.GotKeyboardFocus += (_, _) => hotkeys?.Suspend();
            box.LostKeyboardFocus += (_, _) =>
            {
                try { hotkeys?.Apply(this.settings.SaveHotkey, this.settings.ShowHotkey); }
                catch (Exception ex) { SetNotice(ex.Message, true); }
            };
        }
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico"));
        MinutesBox.ItemsSource = new[] { 1, 3, 5, 10 };
        LoadForm();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("顯示介面", null, (_, _) => Dispatcher.Invoke(ShowAndActivate));
        menu.Items.Add("儲存最近音訊", null, (_, _) => Dispatcher.InvokeAsync(async () => await SaveClip()));
        trayRecord = new Forms.ToolStripMenuItem("開始錄音", null, (_, _) => Dispatcher.Invoke(ToggleRecording));
        menu.Items.Add(trayRecord);
        menu.Items.Add("開啟輸出資料夾", null, (_, _) => Dispatcher.Invoke(() => OpenFolder(settings.OutputFolder)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("結束 EchoReplay", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))!.Stream;
        using (iconStream)
        using (var resourceIcon = new System.Drawing.Icon(iconStream))
            tray = new Forms.NotifyIcon { Icon = (System.Drawing.Icon)resourceIcon.Clone(), Text = "EchoReplay · 聲音回放", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAndActivate);
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowAndActivate);
        SourceInitialized += (_, _) =>
        {
            hotkeys = new HotkeyManager(new WindowInteropHelper(this).Handle);
            hotkeys.SaveRequested += async () => await SaveClip();
            hotkeys.ShowRequested += ToggleVisibility;
            try { hotkeys.Apply(settings.SaveHotkey, settings.ShowHotkey); }
            catch (Exception ex) { startupHotkeyWarning = ex.Message; SetNotice(ex.Message, true); }
        };
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
        timer.Tick += (_, _) => UpdateStatus();
        timer.Start();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        UpdateLabels();
        if (settings.RecordOnLaunch || App.Arguments.Contains("--diagnose")) StartRecording();
        if (startupHotkeyWarning is not null) { SetNotice(startupHotkeyWarning, true); Notify("快捷鍵無法註冊", startupHotkeyWarning, true); }
        if (SettingsStore.LoadWarning is not null) SetNotice(SettingsStore.LoadWarning, true);
        if (App.ArgumentValue("--editor-diagnose") is string editorReport)
        {
            object result;
            try
            {
                string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(editorReport))!, "editor-clips");
                settings = settings with { OutputFolder = folder };
                LibraryTab.IsSelected = true; UpdateLayout();
                double recordingBefore = engine.AvailableSeconds;
                long packetsBefore = engine.SystemSource?.Packets ?? 0;
                result = await Editor.Diagnose(folder);
                if (engine.Running)
                {
                    bool continued = engine.AvailableSeconds > recordingBefore && (engine.SystemSource?.Packets ?? 0) > packetsBefore;
                    result = new { Success = JsonSerializer.SerializeToElement(result).GetProperty("Success").GetBoolean() && continued,
                        Editing = result, CaptureContinued = continued, SystemPackets = engine.SystemSource?.Packets ?? 0 };
                }
                UpdateLayout(); await Task.Delay(200);
                SaveVisual(Path.ChangeExtension(editorReport, ".png"));
                Editor.ScrollEffectsIntoView(); await Task.Delay(100);
                SaveVisual(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(editorReport))!, "editor-effects.png"));
            }
            catch (Exception ex) { result = new { Success = false, Error = ex.ToString() }; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(editorReport))!);
            File.WriteAllText(editorReport, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            ExitApplication();
        }
        else if (App.ArgumentValue("--ui-snapshot") is string snapshot)
        {
            await Task.Delay(1200);
            SaveVisual(snapshot);
            MainTabs.SelectedIndex = 2;
            UpdateLayout();
            await Task.Delay(300);
            SaveVisual(Path.Combine(Path.GetDirectoryName(snapshot)!, Path.GetFileNameWithoutExtension(snapshot) + "-settings.png"));
            SettingsScroller.ScrollToEnd();
            await Task.Delay(300);
            SaveVisual(Path.Combine(Path.GetDirectoryName(snapshot)!, Path.GetFileNameWithoutExtension(snapshot) + "-settings-bottom.png"));
            ExitApplication();
        }
        else if (App.ArgumentValue("--diagnose") is string report) await Diagnose(report);
    }

    private void LoadForm()
    {
        MinutesBox.SelectedItem = settings.Minutes;
        if (MinutesBox.SelectedIndex < 0) MinutesBox.SelectedItem = 5;
        OutputBox.Text = settings.OutputFolder;
        MicrophoneBox.IsChecked = settings.CaptureMicrophone;
        SeparateBox.IsChecked = settings.SaveSeparateTracks;
        RecordOnLaunchBox.IsChecked = settings.RecordOnLaunch;
        RunAtLoginBox.IsChecked = settings.RunAtLogin;
        StartHiddenBox.IsChecked = settings.StartHidden;
        SaveKeyBox.Text = settings.SaveHotkey;
        ShowKeyBox.Text = settings.ShowHotkey;
        SystemGainSlider.Value = settings.SystemGain;
        MicGainSlider.Value = settings.MicrophoneGain;
        RefreshDevices(settings.OutputDeviceId, settings.MicrophoneDeviceId);
    }
    private void RefreshDevices(string outputId, string microphoneId)
    {
        try
        {
            var outputs = AudioDevices.List(DataFlow.Render);
            var microphones = AudioDevices.List(DataFlow.Capture);
            if (!outputs.Any(x => x.Id == outputId)) outputs.Add(new(outputId, "之前選擇的播放裝置（目前未連線）"));
            if (!microphones.Any(x => x.Id == microphoneId)) microphones.Add(new(microphoneId, "之前選擇的麥克風（目前未連線）"));
            OutputDeviceBox.ItemsSource = outputs;
            MicrophoneDeviceBox.ItemsSource = microphones;
            OutputDeviceBox.SelectedValue = outputId;
            MicrophoneDeviceBox.SelectedValue = microphoneId;
        }
        catch (Exception ex) { SetNotice("無法列出音效裝置：" + ex.Message, true); }
    }
    private void UpdateLabels()
    {
        RetentionLabel.Text = $" / {settings.Minutes:00}:00";
        SaveHotkeyLabel.Text = settings.SaveHotkey.Replace("+", " + ");
        FooterText.Text = settings.ShowHotkey.Replace("+", " + ") + "  顯示／隱藏介面";
    }
    private void UpdateStatus()
    {
        double seconds = engine.AvailableSeconds;
        BufferTime.Text = TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        BufferProgress.Value = seconds / (engine.BufferMinutes * 60) * 100;
        RetentionLabel.Text = $" / {engine.BufferMinutes:00}:00";
        bool healthy = engine.Running && engine.SystemSource?.Connected == true && (!settings.CaptureMicrophone || engine.MicrophoneSource?.Connected == true);
        RecordingStatus.Text = engine.Running ? healthy ? "● 正在背景錄音" : "● 音訊來源連接中／部分中斷" : "○ 已暫停 · 緩衝仍可儲存";
        RecordingStatus.Foreground = new SolidColorBrush(healthy ? Color.FromRgb(136, 227, 196) : Color.FromRgb(239, 198, 123));
        tray.Text = engine.Running ? healthy ? "EchoReplay · 正在錄音" : "EchoReplay · 音訊來源連接中／中斷" : "EchoReplay · 已暫停";
        trayRecord.Text = engine.Running ? "暫停錄音" : "開始新的錄音";
        RecordButton.Content = trayRecord.Text;
        RecordButton.IsEnabled = !engine.Saving;
        ApplyButton.IsEnabled = !engine.Saving;
        SaveButton.IsEnabled = !engine.Saving && seconds >= 0.5;
        SaveButton.Content = engine.Saving ? "正在儲存…" : "儲存最近音訊";
        if (engine.Running)
        {
            SystemDeviceText.Text = engine.SystemSource?.Status ?? "連接中";
            MicDeviceText.Text = settings.CaptureMicrophone ? engine.MicrophoneSource?.Status ?? "連接中" : "未啟用";
        }
        else { SystemDeviceText.Text = "已暫停"; MicDeviceText.Text = settings.CaptureMicrophone ? "已暫停" : "未啟用"; }
        SystemDeviceText.ToolTip = SystemDeviceText.Text;
        MicDeviceText.ToolTip = MicDeviceText.Text;
        SetMeter(SystemMeter, SystemLevelText, engine.SystemSource);
        SetMeter(MicMeter, MicLevelText, engine.MicrophoneSource);
        if (engine.Running && !healthy && seconds > 5 && !wasUnhealthy)
        {
            wasUnhealthy = true;
            Notify("音訊來源中斷", "請檢查裝置；EchoReplay 會繼續嘗試連線。", true);
        }
        if (healthy && wasUnhealthy) { wasUnhealthy = false; SetNotice("音訊來源已重新連線，繼續錄製。中斷期間的聲音無法補回。"); }
    }
    private static void SetMeter(ProgressBar meter, TextBlock label, CaptureSource? source)
    {
        float peak = source?.Peak ?? 0;
        double db = peak > 0.00001 ? 20 * Math.Log10(peak) : -100;
        meter.Value = Math.Clamp((db + 60) / 60 * 100, 0, 100);
        label.Text = source?.Connected != true ? "尚未擷取音訊" : db < -60 ? "目前安靜 · 裝置已連接" : $"{db:0.0} dBFS";
    }
    private void StartRecording()
    {
        try
        {
            engine.Start(settings);
            wasUnhealthy = false;
            SetNotice($"正在累積最近 {settings.Minutes} 分鐘；關閉視窗後仍會繼續錄音。");
        }
        catch (Exception ex) { SetNotice("無法開始錄音：" + ex.Message, true); }
    }
    private void ToggleRecording()
    {
        if (engine.Saving) { SetNotice("請等候音檔儲存完成。"); return; }
        if (engine.Running)
        {
            engine.Stop();
            SetNotice("已暫停。仍可儲存目前緩衝；開始新的錄音會清除舊緩衝。");
        }
        else StartRecording();
        UpdateStatus();
    }
    private async Task SaveClip()
    {
        if (engine.Saving) return;
        try
        {
            bool incomplete = engine.Running && (engine.SystemSource?.Connected != true || (settings.CaptureMicrophone && engine.MicrophoneSource?.Connected != true));
            lastClip = await engine.SaveAsync(settings with { });
            LastSavedText.Text = lastClip;
            await Editor.RefreshLibrary();
            PlayButton.IsEnabled = ShowClipButton.IsEnabled = true;
            string message = "音檔已儲存：" + Path.GetFileName(lastClip);
            if (incomplete) message += "。部分來源未連線，請試聽確認。";
            SetNotice(message, incomplete);
            Notify("音檔已儲存", incomplete ? "部分來源未連線，請試聽確認。" : "混音與所選分軌已存入輸出資料夾。", incomplete);
        }
        catch (Exception ex) { SetNotice("儲存失敗：" + ex.Message, true); Notify("儲存失敗", ex.Message, true); }
    }
    private void SetNotice(string message, bool error = false)
    {
        NoticeText.Text = message;
        NoticeText.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 204, 148) : Color.FromRgb(204, 232, 220));
    }
    private void Notify(string title, string message, bool error = false) => tray.ShowBalloonTip(3500, title, message, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    public void ShowAndActivate() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ToggleVisibility() { if (IsVisible && WindowState != WindowState.Minimized) HideToTray(); else ShowAndActivate(); }
    private void HideToTray()
    {
        Hide();
        if (!shownHideTip) { shownHideTip = true; Notify("EchoReplay 仍在背景執行", settings.ShowHotkey + " 可叫回介面；系統匣右鍵選單可結束程式。"); }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; HideToTray(); } }
    internal void ExitApplication()
    {
        if (engine.Saving) { SetNotice("正在儲存音檔，完成後即可結束程式。"); ShowAndActivate(); return; }
        if (!Editor.TryClose()) { ShowAndActivate(); LibraryTab.IsSelected = true; return; }
        exiting = true;
        timer.Stop();
        hotkeys?.Dispose();
        engine.Dispose();
        tray.Visible = false;
        tray.Icon?.Dispose();
        tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }
    private void OpenFolder(string folder)
    {
        try { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) { SetNotice("無法開啟資料夾：" + ex.Message, true); }
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (engine.Saving) return;
        var previous = settings;
        bool hotkeysApplied = false;
        bool startupChanged = false;
        try
        {
            string output = Environment.ExpandEnvironmentVariables(OutputBox.Text.Trim());
            if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("輸出資料夾請使用完整路徑，例如 D:\\AudioClips。");
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            string probe = Path.Combine(output, ".echoreplay-write-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { stream.WriteByte(0); }
            var updated = new AppSettings
            {
                Minutes = (int)(MinutesBox.SelectedItem ?? 5), OutputFolder = output,
                OutputDeviceId = (string?)OutputDeviceBox.SelectedValue ?? "", MicrophoneDeviceId = (string?)MicrophoneDeviceBox.SelectedValue ?? "",
                CaptureMicrophone = MicrophoneBox.IsChecked == true, SaveSeparateTracks = SeparateBox.IsChecked == true,
                RecordOnLaunch = RecordOnLaunchBox.IsChecked == true, RunAtLogin = RunAtLoginBox.IsChecked == true, StartHidden = StartHiddenBox.IsChecked == true,
                SaveHotkey = SaveKeyBox.Text, ShowHotkey = ShowKeyBox.Text, SystemGain = SystemGainSlider.Value, MicrophoneGain = MicGainSlider.Value
            };
            hotkeys!.Apply(updated.SaveHotkey, updated.ShowHotkey);
            hotkeysApplied = true;
            if (updated.RunAtLogin || updated.RunAtLogin != previous.RunAtLogin)
            {
                LoginStartup.SetEnabled(updated.RunAtLogin);
                startupChanged = true;
            }
            SettingsStore.Save(updated);
            bool restart = updated.Minutes != previous.Minutes || updated.OutputDeviceId != previous.OutputDeviceId || updated.MicrophoneDeviceId != previous.MicrophoneDeviceId || updated.CaptureMicrophone != previous.CaptureMicrophone;
            settings = updated;
            _ = Editor.RefreshLibrary();
            UpdateLabels();
            if (restart && engine.Running) { StartRecording(); SetNotice("設定已儲存，已使用新的錄音設定重新開始累積。"); }
            else SetNotice("設定已儲存。" + (restart ? "新的錄音設定會在下次開始錄音時生效。" : ""));
        }
        catch (Exception ex)
        {
            string rollback = "";
            if (startupChanged) try { LoginStartup.SetEnabled(previous.RunAtLogin); } catch (Exception recovery) { rollback += " 開機設定恢復失敗：" + recovery.Message; }
            if (hotkeysApplied) try { hotkeys!.Apply(previous.SaveHotkey, previous.ShowHotkey); } catch (Exception recovery) { rollback += " 快捷鍵恢復失敗：" + recovery.Message; }
            SetNotice("設定未套用：" + ex.Message + rollback, true);
        }
    }
    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        string text = ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "") + ((modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "") + ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") + key;
        try
        {
            if ((modifiers & ModifierKeys.Windows) != 0) throw new ArgumentException("請使用 Ctrl／Alt／Shift 組合。");
            Hotkey.Parse(text);
            ((TextBox)sender).Text = text;
        }
        catch (Exception ex) { SetNotice(ex.Message, true); }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "選擇音檔輸出資料夾" };
        if (Directory.Exists(OutputBox.Text)) dialog.InitialDirectory = OutputBox.Text;
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName;
    }
    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices((string?)OutputDeviceBox.SelectedValue ?? "", (string?)MicrophoneDeviceBox.SelectedValue ?? "");
    private void ResetSettings_Click(object sender, RoutedEventArgs e) { LoadForm(); SetNotice("已還原尚未套用的變更。"); }
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveClip();
    private void Record_Click(object sender, RoutedEventArgs e) => ToggleRecording();
    private void Hide_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void OpenOutput_Click(object sender, RoutedEventArgs e) => OpenFolder(settings.OutputFolder);
    private void ShowClip_Click(object sender, RoutedEventArgs e) { if (lastClip is not null) OpenFolder(lastClip); }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (lastClip is null) return;
        LibraryTab.IsSelected = true;
        await Editor.OpenPath(Path.Combine(lastClip, "混音.wav"));
    }
    private void SaveVisual(string path)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private async Task Diagnose(string reportPath)
    {
        object report;
        try
        {
            await Task.Delay(1200);
            bool hiddenWorked = false;
            Hide();
            double before = engine.AvailableSeconds;
            using (var output = new WasapiOut(AudioClientShareMode.Shared, 100))
            {
                var signal = new SignalGenerator(48000, 2) { Gain = 0.08, Frequency = 660, Type = SignalGeneratorType.Sin };
                output.Init(signal.Take(TimeSpan.FromSeconds(1.5)));
                output.Play();
                await Task.Delay(1800);
            }
            await Task.Delay(700);
            hiddenWorked = !IsVisible && engine.AvailableSeconds > before + 2;
            ShowAndActivate();
            long systemPackets = engine.SystemSource?.Packets ?? 0;
            long microphonePackets = engine.MicrophoneSource?.Packets ?? 0;
            var snapshotSettings = settings with { OutputFolder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "clips") };
            string clip = await engine.SaveAsync(snapshotSettings);
            string? systemStatus = engine.SystemSource?.Status;
            string? micStatus = engine.MicrophoneSource?.Status;
            double beforeFailure = engine.AvailableSeconds;
            string blockedPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "output-is-a-file");
            File.WriteAllText(blockedPath, "test");
            bool failureHandled = false;
            try { await engine.SaveAsync(snapshotSettings with { OutputFolder = blockedPath }); }
            catch (IOException) { failureHandled = !engine.Saving && engine.Running && engine.AvailableSeconds >= beforeFailure; }
            engine.Stop();
            double stoppedSeconds = engine.AvailableSeconds;
            await Task.Delay(250);
            bool pauseWorked = engine.AvailableSeconds == stoppedSeconds;
            string pausedClip = await engine.SaveAsync(snapshotSettings);
            bool pausedSave = File.Exists(Path.Combine(pausedClip, "混音.wav"));
            engine.Start(settings);
            bool restartCleared = engine.AvailableSeconds < 1;
            await Task.Delay(400);
            double beforeSettings = engine.AvailableSeconds;
            OutputBox.Text = snapshotSettings.OutputFolder;
            SystemGainSlider.Value = 0.7;
            Apply_Click(this, new RoutedEventArgs());
            var savedSettings = SettingsStore.Load();
            bool settingsWorked = savedSettings.OutputFolder == snapshotSettings.OutputFolder && Math.Abs(savedSettings.SystemGain - 0.7) < 0.001 && engine.AvailableSeconds >= beforeSettings;
            report = new { Success = systemPackets > 0 && hiddenWorked && failureHandled && pauseWorked && pausedSave && restartCleared && settingsWorked, HiddenRecording = hiddenWorked, OutputFailurePreservesBuffer = failureHandled, PauseFreezesBuffer = pauseWorked, SaveWhilePaused = pausedSave, RestartClearsBuffer = restartCleared, SettingsApplyPreservesBuffer = settingsWorked, SystemPackets = systemPackets, MicrophonePackets = microphonePackets, SystemStatus = systemStatus, MicrophoneStatus = micStatus, Clip = clip };
        }
        catch (Exception ex) { report = new { Success = false, Error = ex.ToString() }; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        ExitApplication();
    }
}
