using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;

namespace EchoReplay;

public static class CaptureModes
{
    public const string Device = "Device", System = "System", Applications = "Applications";
    public static bool ProcessSupported => IsProcessSupported(Environment.OSVersion.Version);
    public static bool IsProcessSupported(Version version) => version.Major >= 10 && version.Build >= 20348;
    public static void Validate(AppSettings settings)
    {
        if (settings.CaptureMode is not (Device or System or Applications)) throw new ArgumentException("錄音模式不正確。");
        if (settings.CaptureMode != Device && !ProcessSupported)
            throw new PlatformNotSupportedException("此 Windows 版本不支援依程式擷取。請使用 Windows 11，或選擇「指定播放裝置」。");
        if (settings.CaptureMode == Applications)
        {
            string voice = ProcessCatalog.Normalize(settings.VoiceProcessName), game = ProcessCatalog.Normalize(settings.GameProcessName);
            if (voice.Length == 0) throw new ArgumentException("請選擇語音聊天程式，例如 Discord。");
            if (voice.Equals(game, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("語音與遊戲必須選擇不同程式，避免重複收錄。");
            if (ProcessCatalog.IsOwnName(voice) || ProcessCatalog.IsOwnName(game)) throw new ArgumentException("不能將 EchoReplay 本身選為錄音來源。");
            var entries = ProcessCatalog.Snapshot();
            var a = ProcessCatalog.Resolve(entries, voice); var b = ProcessCatalog.Resolve(entries, game);
            if (a is not null && b is not null && ProcessCatalog.Overlap(entries, a.Id, b.Id))
                throw new ArgumentException("這兩個程式的程序樹重疊。請選擇實際語音與遊戲程式，不要選啟動器。");
        }
    }
}

public sealed record ProcessEntry(int Id, int ParentId, string Name);
internal sealed class ProcessLifetime : IDisposable
{
    private readonly SafeProcessHandle handle;
    public ProcessLifetime(int id)
    {
        // Query-limited + synchronize, never PROCESS_ALL_ACCESS (games may be elevated).
        handle = OpenProcess(0x00100000 | 0x1000, false, id);
        if (handle.IsInvalid) { handle.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
    }
    public bool HasExited
    {
        get { uint result = WaitForSingleObject(handle, 0); if (result == uint.MaxValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return result == 0; }
    }
    public string Name
    {
        get
        {
            var path = new StringBuilder(32768); int size = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return Path.GetFileNameWithoutExtension(path.ToString());
        }
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref int size);
}
public static class ProcessCatalog
{
    public static string Normalize(string? value)
    {
        string name = (value ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length > 120 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new ArgumentException("請輸入程式名稱（例如 Discord.exe），不需要完整路徑。");
        return name;
    }
    public static bool IsOwnName(string name)
    {
        using var self = Process.GetCurrentProcess();
        return name.Equals("EchoReplay", StringComparison.OrdinalIgnoreCase) || name.Equals(self.ProcessName, StringComparison.OrdinalIgnoreCase);
    }
    public static List<string> Choices()
    {
        var entries = Snapshot();
        return entries.Where(e => e.Id > 4).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !IsOwnName(name) && Resolve(entries, name) is not null)
            .Select(name => name + ".exe").Order(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static List<ProcessEntry> Snapshot()
    {
        var result = new List<ProcessEntry>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ProcessIdToSessionId((uint)Environment.ProcessId, out uint session);
        var entry = new ProcessEntryNative { Size = (uint)Marshal.SizeOf<ProcessEntryNative>() };
        if (!Process32First(snapshot, ref entry)) return result;
        do
        {
            if (ProcessIdToSessionId(entry.Id, out uint candidateSession) && candidateSession == session)
                result.Add(new((int)entry.Id, (int)entry.ParentId, Path.GetFileNameWithoutExtension(entry.Executable)));
        } while (Process32Next(snapshot, ref entry));
        return result;
    }
    public static bool DescendsFrom(IReadOnlyList<ProcessEntry> entries, int child, int parent)
    {
        var visited = new HashSet<int>();
        while (child > 0 && visited.Add(child))
        {
            if (child == parent) return true;
            child = entries.FirstOrDefault(e => e.Id == child)?.ParentId ?? 0;
        }
        return false;
    }
    public static bool Overlap(IReadOnlyList<ProcessEntry> entries, int first, int second) => DescendsFrom(entries, first, second) || DescendsFrom(entries, second, first);
    public static ProcessEntry? Resolve(IReadOnlyList<ProcessEntry> entries, string name, int preferred = 0)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var matching = entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Electron uses several same-name child processes; select a root, not its audio worker.
        var roots = matching.Where(e => !matching.Any(parent => parent.Id != e.Id && DescendsFrom(entries, e.ParentId, parent.Id)))
            .Where(e => !DescendsFrom(entries, Environment.ProcessId, e.Id) && !DescendsFrom(entries, e.Id, Environment.ProcessId)).ToArray();
        return roots.FirstOrDefault(e => e.Id == preferred) ?? roots.OrderBy(e => e.Id).FirstOrDefault();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntryNative
    {
        public uint Size, Usage, Id;
        public UIntPtr DefaultHeap;
        public uint Module, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntryNative entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntryNative entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
}

// Windows process-loopback activation. IAgileObject allows the completion callback on MTA.
// Keep the unmanaged activation blob alive until completion, including after cancellation.
public static class ProcessAudioClient
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParameters { public int Type; public uint ProcessId; public int Mode; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public Blob Blob; }
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out int hr, out object client);
                if (hr < 0) { if (client is not null && Marshal.IsComObject(client)) Marshal.ReleaseComObject(client); Marshal.ThrowExceptionForHR(hr); }
                Result.TrySetResult(client as IAudioClient ?? throw new InvalidOperationException("Windows 未提供音訊擷取介面。"));
            }
            catch (Exception ex) { Result.TrySetException(ex); }
        }
    }
    public static AudioClient Activate(uint processId, bool exclude, CancellationToken cancellation)
    {
        if (!CaptureModes.ProcessSupported) throw new PlatformNotSupportedException("Windows 不支援程式音訊擷取。");
        var parameters = new ActivationParameters { Type = 1, ProcessId = processId, Mode = exclude ? 1 : 0 };
        IntPtr blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParameters>());
        IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var completion = new Completion();
        bool deferred = false;
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            Marshal.StructureToPtr(new PropVariant { Type = 65, Blob = new Blob { Size = (uint)Marshal.SizeOf<ActivationParameters>(), Data = blob } }, variant, false);
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, completion, out operation));
            try { return new AudioClient(completion.Result.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation).GetAwaiter().GetResult()); }
            catch
            {
                // The native API has no cancellation. A late callback still owns its arguments.
                deferred = true;
                _ = completion.Result.Task.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) Marshal.ReleaseComObject(task.Result);
                    else _ = task.Exception;
                    Cleanup(); GC.KeepAlive(completion);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                throw;
            }
        }
        finally { if (!deferred) Cleanup(); GC.KeepAlive(completion); }
        void Cleanup()
        {
            if (operation is not null) Marshal.ReleaseComObject(operation);
            Marshal.FreeHGlobal(variant); Marshal.FreeHGlobal(blob);
        }
    }
    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid iid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completion, out IActivateAudioInterfaceAsyncOperation operation);
}
