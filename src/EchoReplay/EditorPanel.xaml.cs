using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;

namespace EchoReplay;

public partial class EditorPanel : UserControl
{
    private Func<string> root = () => "";
    private List<LibraryClip> clips = new();
    private LibraryClip? current;
    private EditAudio? audio;
    private EditHistory? history;
    private CancellationTokenSource? operation;
    private WasapiOut? output;
    private EditPcmStream? playback;
    private double playbackStart;
    private bool dirty, seeking;
    private int refreshVersion;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    public bool Busy => operation is not null;
    public event Action<string>? ClipDeleted;
    public EditorPanel()
    {
        InitializeComponent();
        Waveform.SelectionChanged += () =>
        {
            StartBox.Text = Waveform.SelectionStart.ToString("0.000"); EndBox.Text = Waveform.SelectionEnd.ToString("0.000");
            SelectionText.Text = $"選取 {Waveform.SelectionEnd - Waveform.SelectionStart:0.000} 秒";
        };
        Loaded += async (_, _) => { if (!Busy) await RefreshLibrary(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopPlayback(); };
        timer.Tick += (_, _) =>
        {
            if (playback is null || seeking) return;
            double time = playbackStart + playback.Position / (double)playback.WaveFormat.AverageBytesPerSecond;
            SeekSlider.Value = time; Waveform.Playhead = time; Waveform.InvalidateVisual();
            PlaybackTime.Text = TimeSpan.FromSeconds(time).ToString(@"mm\:ss\.fff");
        };
        timer.Start();
    }
    public void Configure(Func<string> outputFolder) => root = outputFolder;
    internal void ScrollEffectsIntoView() => EditorScroller.ScrollToEnd();
    public async Task RefreshLibrary(string? selectedPath = null)
    {
        int version = ++refreshVersion;
        string folder = root();
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            string? selected = selectedPath ?? (ClipsList.SelectedItem as LibraryClip)?.AudioPath;
            var found = await Task.Run(() => ClipLibrary.Scan(folder));
            if (version != refreshVersion) return;
            clips = found;
            Filter();
            ClipsList.SelectedItem = clips.FirstOrDefault(c => string.Equals(c.AudioPath, selected, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { Status("讀取音檔庫失敗：" + ex.Message); }
    }
    private void Filter()
    {
        if (ClipsList is null || SearchBox is null) return;
        ClipsList.ItemsSource = clips.Where(c => c.Name.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    }
    private void Status(string message) => StatusText.Text = message;
    private async Task Work(string message, Func<CancellationToken, Task> action)
    {
        if (Busy) return;
        StopPlayback();
        operation = new CancellationTokenSource(); InteractionRoot.IsEnabled = false;
        CancelButton.Visibility = WorkProgress.Visibility = Visibility.Visible;
        WorkProgress.IsIndeterminate = true; Status(message);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status("操作已取消，原始音檔保持完整。"); }
        catch (Exception ex) { Status("操作失敗：" + ex.Message); }
        finally { operation.Dispose(); operation = null; InteractionRoot.IsEnabled = true; CancelButton.Visibility = WorkProgress.Visibility = Visibility.Collapsed; }
    }
    public async Task OpenPath(string path)
    {
        if (Busy || !ResolveUnsaved()) return;
        await RefreshLibrary(path);
        var clip = clips.FirstOrDefault(c => string.Equals(c.AudioPath, path, StringComparison.OrdinalIgnoreCase));
        if (clip is null) { Status("找不到音檔，請重新整理音檔庫。"); return; }
        await Work("讀取音檔與波形…", async token =>
        {
            var loaded = await Task.Run(() => EditAudio.Load(clip.AudioPath, token), token);
            string? warning = null;
            var state = EditFiles.InitialState(loaded);
            try { state = await Task.Run(() => EditFiles.LoadProject(loaded) ?? state, token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { warning = "舊進度未載入：" + ex.Message; }
            var peaks = await Task.Run(() => EditFiles.Peaks(loaded, state, token), token);
            token.ThrowIfCancellationRequested();
            audio = loaded; history = new(state); current = clip; dirty = false;
            ClipTitle.Text = clip.Name; EditorBody.IsEnabled = EditorFooter.IsEnabled = true;
            EditorScroller.ScrollToTop();
            SystemEnabled.Content = loaded.Separate ? "電腦聲音" : "音檔／混音";
            MicrophoneControls.IsEnabled = loaded.Microphone is not null;
            TrackInfo.Text = loaded.Separate ? "原始分軌可各自調整；所有剪輯會保持兩軌同步。" : "此音檔只有混音，無法單獨移除其中的人聲或遊戲聲。";
            Present(peaks, true);
            Status(warning ?? "已載入。拖曳波形選取，或直接選最後幾秒。");
        });
    }
    private void Present(float[] peaks, bool resetSelection)
    {
        if (history is null || audio is null) return;
        var state = history.Current;
        Waveform.Peaks = peaks; Waveform.Duration = state.Frames / (double)EditAudio.Rate;
        Waveform.Fit(); Waveform.Playhead = 0; SeekSlider.Maximum = Waveform.Duration; SeekSlider.Value = 0;
        if (resetSelection) Waveform.SetSelection(0, Waveform.Duration);
        else Waveform.SetSelection(Math.Min(Waveform.SelectionStart, Waveform.Duration), Math.Min(Waveform.SelectionEnd, Waveform.Duration));
        SystemEnabled.IsChecked = state.SystemGain > 0; SystemVolume.Value = state.SystemGain > 0 ? state.SystemGain : 1;
        MicrophoneEnabled.IsChecked = state.MicrophoneGain > 0; MicrophoneVolume.Value = state.MicrophoneGain > 0 ? state.MicrophoneGain : 1;
        FadeInBox.Text = state.FadeIn.ToString("0.###"); FadeOutBox.Text = state.FadeOut.ToString("0.###");
        UndoButton.IsEnabled = history.CanUndo; RedoButton.IsEnabled = history.CanRedo;
        ClipInfo.Text = $"原始 {audio.Frames / (double)EditAudio.Rate:0.000} 秒 → 成品 {Waveform.Duration:0.000} 秒 · {state.Spans.Length} 個片段" + (dirty ? " · 尚未保存進度" : "");
    }
    private async Task Change(Action action, bool resetSelection = true)
    {
        if (audio is null || history is null) return;
        await Work("更新剪輯與波形…", async token =>
        {
            action(); dirty = true;
            // Once an edit is committed, finish its small waveform job even if Cancel is pressed.
            var peaks = await Task.Run(() => EditFiles.Peaks(audio, history.Current, CancellationToken.None));
            Present(peaks, resetSelection); Status("剪輯已更新。可復原，或保存進度後下次繼續。");
        });
    }
    private bool SaveProject()
    {
        if (audio is null || history is null) return true;
        try { EditFiles.SaveProject(audio, history.Current); dirty = false; ClipInfo.Text = ClipInfo.Text.Replace(" · 尚未保存進度", ""); Status("編輯進度已保存，重新開啟音檔即可繼續。"); return true; }
        catch (Exception ex) { Status("無法保存進度：" + ex.Message); return false; }
    }
    private bool ResolveUnsaved()
    {
        if (!dirty) return true;
        var answer = MessageBox.Show(Window.GetWindow(this), "目前音檔有尚未保存的編輯。是否保存進度？\n選擇「否」會放棄這次尚未保存的修改。", "保存編輯進度", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SaveProject();
    }
    public bool TryClose()
    {
        if (Busy) { Status("請先等候工作完成，或按取消。背景錄音會繼續。"); return false; }
        if (!ResolveUnsaved()) return false;
        StopPlayback(); timer.Stop(); return true;
    }
    private void StartPlayback(double start, double end)
    {
        if (audio is null || history is null || end - start < 1d / EditAudio.Rate) return;
        StopPlayback();
        try
        {
            int first = Math.Clamp((int)Math.Round(start * EditAudio.Rate), 0, history.Current.Frames - 1);
            int last = Math.Clamp((int)Math.Round(end * EditAudio.Rate), first + 1, history.Current.Frames);
            playback = new EditPcmStream(audio, history.Current, first, last - first) { Loop = LoopBox.IsChecked == true };
            playbackStart = first / (double)EditAudio.Rate;
            output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
            var ownOutput = output;
            output.PlaybackStopped += (_, e) => Dispatcher.InvokeAsync(() =>
            {
                if (output != ownOutput) return;
                StopPlayback(); if (e.Exception is not null) Status("播放失敗：" + e.Exception.Message);
            });
            output.Init(playback); output.Play(); PreviewButton.Content = "Ⅱ 暫停";
        }
        catch (Exception ex) { StopPlayback(); Status("無法播放：" + ex.Message); }
    }
    private void StopPlayback()
    {
        var previous = output; output = null;
        previous?.Stop(); previous?.Dispose(); playback?.Dispose(); playback = null;
        PreviewButton.Content = "▶ 播放選取範圍";
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (output?.PlaybackState == PlaybackState.Playing) { output.Pause(); PreviewButton.Content = "▶ 繼續播放"; }
        else if (output?.PlaybackState == PlaybackState.Paused) { output.Play(); PreviewButton.Content = "Ⅱ 暫停"; }
        else StartPlayback(Waveform.SelectionStart, Waveform.SelectionEnd > Waveform.SelectionStart ? Waveform.SelectionEnd : Waveform.Duration);
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void Loop_Changed(object sender, RoutedEventArgs e) { if (playback is not null) playback.Loop = LoopBox.IsChecked == true; }
    private void Seek_Begin(object sender, MouseButtonEventArgs e) { seeking = true; }
    private void Seek_End(object sender, MouseButtonEventArgs e) { seeking = false; Seek(); }
    private void Seek_KeyUp(object sender, KeyEventArgs e) => Seek();
    private void Seek()
    {
        Waveform.Playhead = SeekSlider.Value; Waveform.InvalidateVisual();
        StartPlayback(SeekSlider.Value, Waveform.SelectionEnd > SeekSlider.Value ? Waveform.SelectionEnd : Waveform.Duration);
    }
    private async void Open_Click(object sender, RoutedEventArgs e) { if (ClipsList.SelectedItem is LibraryClip clip) await OpenPath(clip.AudioPath); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshLibrary();
    private void Search_Changed(object sender, TextChangedEventArgs e) => Filter();
    private void Tail_Click(object sender, RoutedEventArgs e)
    {
        double seconds = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
        Waveform.SetSelection(Math.Max(0, Waveform.Duration - seconds), Waveform.Duration);
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => Waveform.SetSelection(0, Waveform.Duration);
    private void Zoom_Click(object sender, RoutedEventArgs e) => Waveform.ZoomSelection();
    private void Fit_Click(object sender, RoutedEventArgs e) => Waveform.Fit();
    private void Pan_Click(object sender, RoutedEventArgs e) => Waveform.Pan(double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));
    private static double Number(TextBox box)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || !double.IsFinite(value)) throw new ArgumentException("請輸入有效的秒數。");
        return value;
    }
    private void Range_Click(object sender, RoutedEventArgs e)
    {
        try { double start = Number(StartBox), end = Number(EndBox); if (start < 0 || end <= start || end > Waveform.Duration + 0.0005) throw new ArgumentException("終點必須晚於起點，且不能超過音檔長度。"); Waveform.SetSelection(start, end); }
        catch (Exception ex) { Status(ex.Message); }
    }
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        int start = (int)Math.Round(Waveform.SelectionStart * EditAudio.Rate), end = (int)Math.Round(Waveform.SelectionEnd * EditAudio.Rate);
        await Change(() => history!.Edit(start, end, (string)((Button)sender).Tag));
    }
    private async void Undo_Click(object sender, RoutedEventArgs e) => await Change(() => history!.Undo());
    private async void Redo_Click(object sender, RoutedEventArgs e) => await Change(() => history!.Redo());
    private async void Effects_Click(object sender, RoutedEventArgs e)
    {
        await Change(() =>
        {
            var next = history!.Current with { SystemGain = SystemEnabled.IsChecked == true ? SystemVolume.Value : 0,
                MicrophoneGain = MicrophoneEnabled.IsChecked == true ? MicrophoneVolume.Value : 0, FadeIn = Number(FadeInBox), FadeOut = Number(FadeOutBox) };
            if (next.FadeIn is < 0 or > 10 || next.FadeOut is < 0 or > 10) throw new ArgumentException("淡入／淡出請輸入 0～10 秒。");
            history.Apply(next);
        }, false);
    }
    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject();
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (audio is null || history is null || current is null) return;
        bool wav = FormatBox.SelectedIndex == 2;
        string folder = Path.Combine(root(), "Exports");
        try { Directory.CreateDirectory(folder); } catch (Exception ex) { Status(ex.Message); return; }
        string safeName = string.Concat(current.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "另存剪輯成品", InitialDirectory = folder, FileName = safeName + "_edited", DefaultExt = wav ? ".wav" : ".mp3", Filter = wav ? "WAV 音檔|*.wav" : "MP3 音檔|*.mp3", OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        int bitrate = FormatBox.SelectedIndex == 1 ? 320000 : 192000;
        await Work("正在匯出…背景錄音會繼續。", async token =>
        {
            WorkProgress.IsIndeterminate = false; WorkProgress.Value = 0;
            int lastPercent = -1;
            var progress = new Progress<double>(value => WorkProgress.Value = value * 100);
            await Task.Run(() => EditFiles.Export(audio, history.Current, dialog.FileName, bitrate, token, value =>
            {
                int percent = (int)(value * 100); if (percent != lastPercent) { lastPercent = percent; ((IProgress<double>)progress).Report(value); }
            }), token);
            bool saved = SaveProject();
            await RefreshLibrary();
            Status("成品已匯出：" + dialog.FileName + (saved ? "。不再需要原檔時，可在列表選取原始錄音後刪除。" : "。編輯進度保存失敗，請另行保存。"));
        });
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "匯入音檔副本", Filter = "音檔|*.wav;*.mp3" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string? imported = null;
        await Work("匯入音檔副本…", async token => { imported = await Task.Run(() => ClipLibrary.Import(dialog.FileName, root(), token), token); await RefreshLibrary(imported); Status("已匯入副本，外部原檔未變更。"); });
        if (imported is not null) await OpenPath(imported);
    }
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ClipsList.SelectedItem is not LibraryClip clip) return;
        var input = new TextBox { Text = clip.Name, Margin = new Thickness(0, 10, 0, 12) };
        var save = new Button { Content = "儲存名稱", IsDefault = true };
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(new TextBlock { Text = "音檔庫顯示名稱" }); panel.Children.Add(input); panel.Children.Add(save);
        var dialog = new Window { Owner = Window.GetWindow(this), Title = "重新命名", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, Style = (Style)FindResource(typeof(Window)) };
        save.Click += (_, _) => { try { ClipLibrary.Rename(clip, input.Text); dialog.DialogResult = true; } catch (Exception ex) { MessageBox.Show(dialog, ex.Message); } };
        if (dialog.ShowDialog() == true) { await RefreshLibrary(clip.AudioPath); if (current?.AudioPath == clip.AudioPath) { current = clips.FirstOrDefault(c => c.AudioPath == clip.AudioPath); ClipTitle.Text = current?.Name; } }
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (ClipsList.SelectedItem is not LibraryClip clip) return;
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(clip.AudioPath)!) { UseShellExecute = true }); } catch (Exception ex) { Status(ex.Message); }
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ClipsList.SelectedItem is not LibraryClip clip) return;
        try
        {
            string target = ClipLibrary.DeletionTarget(clip, root());
            string message = clip.RecordingFolder is null ? "此音檔及它的編輯進度" : "這段原始錄音、電腦／麥克風分軌及編輯進度";
            if (MessageBox.Show(Window.GetWindow(this), $"將「{clip.Name}」移至資源回收筒？\n\n會移除：{message}。\n獨立存放的匯出成品不會被刪除。\n\n{target}", "刪除音檔", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            StopPlayback();
            // Shell recycle dialogs require the UI's STA thread. No permanent-delete fallback.
            ClipLibrary.Recycle(clip, root());
            if (current?.AudioPath == clip.AudioPath) { audio = null; history = null; current = null; dirty = false; EditorBody.IsEnabled = EditorFooter.IsEnabled = false; ClipTitle.Text = "音檔已移至資源回收筒"; ClipInfo.Text = "需要時可從 Windows 資源回收筒還原。"; Waveform.Peaks = []; Waveform.Duration = 0; Waveform.Fit(); }
            ClipDeleted?.Invoke(clip.AudioPath);
            await RefreshLibrary(); Status("已移至資源回收筒；匯出成品仍保留。");
        }
        catch (OperationCanceledException) { Status("已取消刪除。"); }
        catch (Exception ex) { await RefreshLibrary(); Status("刪除未完成：" + ex.Message); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();

    // Exercises the real WPF editor with synthetic audio in an isolated profile.
    internal async Task<object> Diagnose(string directory)
    {
        Directory.CreateDirectory(directory);
        string recording = Path.Combine(directory, "Replay_demo"); Directory.CreateDirectory(recording);
        short[] samples = new short[EditAudio.Rate * 12 * 2];
        for (int i = 0; i < samples.Length / 2; i++)
        {
            double time = i / (double)EditAudio.Rate;
            double envelope = 0.2 + 0.6 * Math.Pow(Math.Sin(time * 3), 2);
            short value = (short)(Math.Sin(time * Math.PI * 880) * 8000 * envelope);
            samples[i * 2] = samples[i * 2 + 1] = value;
        }
        string path = Path.Combine(recording, "混音.wav");
        WaveExporter.WritePcm(path, samples, 2); WaveExporter.WritePcm(Path.Combine(recording, "電腦聲音.wav"), samples, 2);
        WaveExporter.WritePcm(Path.Combine(recording, "麥克風.wav"), samples.Where((_, i) => i % 2 == 0).Select(v => (short)(v / 3)).ToArray(), 1);
        File.WriteAllText(Path.Combine(recording, "錄音資訊.json"), "{}");
        var demo = new LibraryClip(path, recording, "朋友的精彩反應 · 示範錄音", DateTime.Now, 12);
        ClipLibrary.Rename(demo, demo.Name);
        await OpenPath(path);
        if (audio is null || history is null) throw new InvalidOperationException(StatusText.Text);
        Tail_Click(new Button { Tag = "5" }, new RoutedEventArgs());
        bool tail = Math.Abs(Waveform.SelectionStart - 7) < 0.001;
        await Change(() => history.Edit(7 * EditAudio.Rate, 12 * EditAudio.Rate, "trim"));
        await Change(() => history.Edit(EditAudio.Rate, 2 * EditAudio.Rate, "delete"));
        await Change(() => history.Edit(EditAudio.Rate, EditAudio.Rate * 3 / 2, "mute"));
        await Change(() => history.Undo()); await Change(() => history.Redo());
        await Change(() => history.Apply(history.Current with { SystemGain = 0.7, MicrophoneGain = 0.3, FadeIn = 0.05, FadeOut = 0.1 }));
        bool timeline = history.Current.Frames == 4 * EditAudio.Rate && history.Current.Spans.Any(s => s.Muted);
        bool saved = SaveProject();
        string exported = Path.Combine(directory, "Exports", "示範音效.wav");
        await Task.Run(() => EditFiles.Export(audio, history.Current, exported, 192000, default));
        await OpenPath(path);
        bool restored = history!.Current.Frames == 4 * EditAudio.Rate && Math.Abs(history.Current.SystemGain - 0.7) < 0.001;
        bool? preview = null;
        if (App.Arguments.Contains("--editor-preview"))
        {
            StartPlayback(0, 0.3); await Task.Delay(180); preview = playback is not null && playback.Position > 0; StopPlayback();
        }
        // Only the synthetic recording created by this diagnostic is recycled.
        string imported = await Task.Run(() => ClipLibrary.Import(exported, directory, default));
        var disposable = ClipLibrary.Scan(directory).Single(c => c.AudioPath == imported);
        ClipLibrary.Recycle(disposable, directory);
        bool recycled = !Directory.Exists(disposable.RecordingFolder) && File.Exists(exported) && File.Exists(path);
        await RefreshLibrary(path); Waveform.SetSelection(2.6, 3.8);
        Status("示範剪輯：選取、接合、靜音、復原、分軌與淡化、保存／重開、WAV 匯出及安全刪除已完成。");
        return new { Success = tail && timeline && saved && restored && recycled && preview != false, TailSelection = tail, TimelineEditing = timeline, ProjectRestored = restored, RecyclePreservesExport = recycled, PreviewPlayed = preview };
    }
}
