using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace NotchApp;

static class SystemService
{
    // ---- volume (Core Audio; flow 0 = saída padrão, 1 = microfone padrão) ----
    static T? Activate<T>(int flow) where T : class
    {
        try
        {
            var enumerator = (Native.IMMDeviceEnumerator)new Native.MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(flow, 1, out var device) != 0) return null;
            var iid = typeof(T).GUID;
            device.Activate(ref iid, 23, IntPtr.Zero, out var instance);
            return (T)instance;
        }
        catch { return null; }
    }

    static Native.IAudioEndpointVolume? Endpoint() => Activate<Native.IAudioEndpointVolume>(0);

    // ---- nível real do som que está saindo (para o equalizador da pílula) ----
    static Native.IAudioMeterInformation? meter;
    static string? meterId;
    static readonly float[] peaks = new float[2];

    public static (float Left, float Right) Peaks()
    {
        try
        {
            var id = AudioDevices.DefaultId();
            if (id != meterId) { meter = Activate<Native.IAudioMeterInformation>(0); meterId = id; }
            if (meter == null) return (0, 0);
            meter.GetMeteringChannelCount(out var count);
            if (count >= 2)
            {
                meter.GetChannelsPeakValues(2, peaks);
                return (peaks[0], peaks[1]);
            }
            meter.GetPeakValue(out var p);
            return (p, p);
        }
        catch { return (0, 0); }
    }

    // ---- Caps Lock ----
    public static bool CapsLock => (Native.GetKeyState(0x14) & 1) != 0;

    // ---- microfone ----
    static Native.IAudioEndpointVolume? mic;
    static VolumeCallback? micCallback;
    public static event Action<bool>? MicMuteChanged;

    public static void WatchMicrophone()
    {
        mic = Activate<Native.IAudioEndpointVolume>(1);
        if (mic == null) return;
        mic.GetMute(out var last);
        micCallback = new VolumeCallback((_, muted) =>
        {
            if (muted == last) return; // o mesmo aviso dispara para mudanças de volume do microfone
            last = muted;
            MicMuteChanged?.Invoke(muted);
        });
        mic.RegisterControlChangeNotify(micCallback);
    }

    // marca as mudanças feitas pelo próprio Notch, para o aviso de volume só aparecer nas teclas/outros apps
    static Guid ownChange = new("6F1C2B7A-3D4E-4F5A-9B8C-7D6E5F4A3B2C");
    static Native.IAudioEndpointVolume? watched;
    static VolumeCallback? callback;
    static ManagementEventWatcher? brightnessWatcher;

    public static event Action<float, bool>? VolumeChanged;
    public static event Action<int>? BrightnessChanged;

    public static double Volume
    {
        get
        {
            float v = 0;
            Endpoint()?.GetMasterVolumeLevelScalar(out v);
            return v;
        }
        set => Endpoint()?.SetMasterVolumeLevelScalar((float)Math.Clamp(value, 0, 1), ref ownChange);
    }

    public static bool Muted
    {
        get
        {
            var m = false;
            Endpoint()?.GetMute(out m);
            return m;
        }
        set => Endpoint()?.SetMute(value, ref ownChange);
    }

    static string? watchedId;

    // chamado de novo quando a saída padrão muda, senão as teclas de volume da saída nova não aparecem
    public static void WatchVolume()
    {
        var id = AudioDevices.DefaultId();
        if (id == watchedId && watched != null) return;
        try { if (watched != null && callback != null) watched.UnregisterControlChangeNotify(callback); } catch { }
        watchedId = id;
        watched = Endpoint();
        if (watched == null) return;
        callback ??= new VolumeCallback((level, muted) => VolumeChanged?.Invoke(level, muted));
        watched.RegisterControlChangeNotify(callback);
    }

    [ComVisible(true)]
    public sealed class VolumeCallback(Action<float, bool> handler) : Native.IAudioEndpointVolumeCallback
    {
        // AUDIO_VOLUME_NOTIFICATION_DATA: Guid contexto, BOOL mudo, float volume
        public int OnNotify(IntPtr data)
        {
            if (Marshal.PtrToStructure<Guid>(data) != ownChange)
                handler(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20)), Marshal.ReadInt32(data, 16) != 0);
            return 0;
        }
    }

    public static void WatchBrightness()
    {
        try
        {
            brightnessWatcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            brightnessWatcher.EventArrived += (_, e) => BrightnessChanged?.Invoke(Convert.ToInt32(e.NewEvent["Brightness"]));
            brightnessWatcher.Start();
        }
        catch { } // monitor externo: sem controle de brilho
    }

    // ---- brilho (WMI; só existe em telas internas de notebook) ----
    public static int? Brightness
    {
        get
        {
            try
            {
                using var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (var o in q.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch { }
            return null;
        }
        set
        {
            try
            {
                using var q = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject o in q.Get()) o.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(value ?? 0, 0, 100)]);
            }
            catch { }
        }
    }

    // ---- rádios e rede ----
    public static async Task<Radio?> GetRadio(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch { return null; }
    }

    public static string? NetworkName
    {
        get
        {
            try { return NetworkInformation.GetInternetConnectionProfile()?.ProfileName; }
            catch { return null; }
        }
    }

    // ---- energia e sessão ----
    public static void Lock() => Native.LockWorkStation();
    public static void Sleep() => Native.SetSuspendState(false, false, false);

    public static void Shutdown(bool restart) =>
        Process.Start(new ProcessStartInfo("shutdown", restart ? "/r /t 0" : "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });

    public static void OpenStart()
    {
        const byte win = 0x5B;
        Native.keybd_event(win, 0, 0, UIntPtr.Zero);
        Native.keybd_event(win, 0, 2, UIntPtr.Zero);
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target)) { UseShellExecute = true }); }
        catch (Exception e) { Settings.Log($"Não abriu {target}: {e.Message}"); }
    }

    // ---- barra de tarefas ----
    static Native.APPBARDATA TaskbarData() => new()
    {
        cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
        hWnd = Native.FindWindow("Shell_TrayWnd", null),
    };

    public static bool TaskbarAutoHide
    {
        get
        {
            var d = TaskbarData();
            return ((ulong)Native.SHAppBarMessage(4, ref d) & 1) != 0; // ABM_GETSTATE, ABS_AUTOHIDE
        }
        set
        {
            var d = TaskbarData();
            d.lParam = value ? 1 : 0;
            Native.SHAppBarMessage(10, ref d); // ABM_SETSTATE
        }
    }

    // Remove a barra de vez: liga o ocultar automático (a área de trabalho passa a ocupar a tela toda) e esconde
    // a janela da barra em todos os monitores, então ela também não aparece ao encostar o mouse embaixo.
    // O Explorer às vezes a mostra de novo (menu Iniciar, reinício do Explorer): quem chama repete a cada segundo.
    static IEnumerable<IntPtr> TaskbarWindows()
    {
        var main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        var other = IntPtr.Zero;
        while ((other = Native.FindWindowEx(IntPtr.Zero, other, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return other;
    }

    public static void RemoveTaskbar()
    {
        if (!TaskbarAutoHide) TaskbarAutoHide = true;
        foreach (var w in TaskbarWindows())
            if (Native.IsWindowVisible(w)) Native.ShowWindow(w, 0); // SW_HIDE
    }

    // devolve a barra; o ocultar automático continua como estiver nas configurações
    public static void RestoreTaskbar(bool autoHide)
    {
        foreach (var w in TaskbarWindows()) Native.ShowWindow(w, 8); // SW_SHOWNA
        if (TaskbarAutoHide != autoHide) TaskbarAutoHide = autoHide;
    }

    // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
    public static bool FullscreenAppActive => Native.SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4;

    // ---- iniciar com o Windows ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (on) key.SetValue("NotchApp", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("NotchApp", false);
    }

    // ---- ícones de arquivos e apps ----
    public static ImageSource? IconFor(string path)
    {
        var info = new Native.SHFILEINFO();
        var full = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(full) && !Directory.Exists(full)) full = FindOnPath(full) ?? full;
        if (Native.SHGetFileInfo(full, 0, ref info, (uint)Marshal.SizeOf(info), 0x100) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { Native.DestroyIcon(info.hIcon); }
    }

    static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
            .Select(dir => Path.Combine(dir.Trim(), exe))
            .FirstOrDefault(File.Exists);
}
