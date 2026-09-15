using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace EchoReplay;

public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static Hotkey Parse(string text)
    {
        string[] parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("快捷鍵需包含 Ctrl 或 Alt，例如 Ctrl+Alt+F9。");
        uint modifiers = 0;
        foreach (string part in parts[..^1])
        {
            modifiers |= part.ToUpperInvariant() switch
            {
                "CTRL" => 2u,
                "ALT" => 1u,
                "SHIFT" => 4u,
                _ => throw new ArgumentException("快捷鍵僅支援 Ctrl、Alt、Shift 修飾鍵。")
            };
        }
        if ((modifiers & 3) == 0) throw new ArgumentException("快捷鍵需包含 Ctrl 或 Alt。");
        if (!Enum.TryParse<Key>(parts.Last(), true, out var key)) throw new ArgumentException("無法辨識快捷鍵。");
        if (key is Key.None or Key.F12 or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            throw new ArgumentException("請使用一般按鍵；F12 由 Windows 保留。");
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) throw new ArgumentException("不支援這個快捷鍵。");
        return new(modifiers, (uint)virtualKey);
    }
}

public sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr handle;
    private readonly HwndSource source;
    private int saveId = 0x510;
    private int showId = 0x511;
    private bool registered;
    private Hotkey oldSave;
    private Hotkey oldShow;
    public event Action? SaveRequested;
    public event Action? ShowRequested;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public HotkeyManager(IntPtr handle)
    {
        this.handle = handle;
        source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("無法建立快捷鍵視窗。");
        source.AddHook(Hook);
    }
    public void Apply(string saveText, string showText)
    {
        var save = Hotkey.Parse(saveText);
        var show = Hotkey.Parse(showText);
        if (save == show) throw new ArgumentException("儲存音訊與顯示介面必須使用不同快捷鍵。");
        if (registered && save == oldSave && show == oldShow) return;
        bool hadOld = registered;
        if (registered) { UnregisterHotKey(handle, saveId); UnregisterHotKey(handle, showId); registered = false; }
        try
        {
            if (!RegisterHotKey(handle, saveId, save.Modifiers | 0x4000, save.VirtualKey))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "儲存快捷鍵已被占用，請換一組。");
            if (!RegisterHotKey(handle, showId, show.Modifiers | 0x4000, show.VirtualKey))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "介面快捷鍵已被占用，請換一組。");
            oldSave = save;
            oldShow = show;
            registered = true;
        }
        catch (Exception failure)
        {
            UnregisterHotKey(handle, saveId);
            UnregisterHotKey(handle, showId);
            if (hadOld)
            {
                bool saveRestored = RegisterHotKey(handle, saveId, oldSave.Modifiers | 0x4000, oldSave.VirtualKey);
                bool showRestored = RegisterHotKey(handle, showId, oldShow.Modifiers | 0x4000, oldShow.VirtualKey);
                registered = saveRestored && showRestored;
                if (!registered)
                {
                    UnregisterHotKey(handle, saveId);
                    UnregisterHotKey(handle, showId);
                    throw new InvalidOperationException(failure.Message + " 原快捷鍵也無法恢復，請重新設定兩組快捷鍵。");
                }
            }
            throw;
        }
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312)
        {
            if (wParam.ToInt32() == saveId) { SaveRequested?.Invoke(); handled = true; }
            if (wParam.ToInt32() == showId) { ShowRequested?.Invoke(); handled = true; }
        }
        return IntPtr.Zero;
    }
    public void Suspend()
    {
        UnregisterHotKey(handle, saveId);
        UnregisterHotKey(handle, showId);
        registered = false;
    }
    public void Dispose()
    {
        UnregisterHotKey(handle, saveId);
        UnregisterHotKey(handle, showId);
        source.RemoveHook(Hook);
    }
}
