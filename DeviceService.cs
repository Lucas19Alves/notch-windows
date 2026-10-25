using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace NotchApp;

public record BtDevice(string Id, string Name, bool Connected, string Glyph, int? Battery = null);
public record TrayApp(string Path, string Name, ImageSource? Icon);
public record PrivacyUse(string App, bool Camera);

// Bluetooth pareado, apps da bandeja e uso de câmera/microfone.
static class DeviceService
{
    // ---------- Bluetooth ----------

    public static event Action<string, bool>? BluetoothChanged; // nome, conectou
    static readonly List<DeviceWatcher> watchers = [];

    public static async Task<List<BtDevice>> Paired()
    {
        var list = new List<BtDevice>();
        try
        {
            foreach (var info in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)))
            {
                using var d = await BluetoothDevice.FromIdAsync(info.Id);
                if (d == null) continue;
                list.Add(new BtDevice(d.DeviceId, d.Name, d.ConnectionStatus == BluetoothConnectionStatus.Connected, Glyph(d.ClassOfDevice.MajorClass, d.ClassOfDevice.MinorClass)));
            }
            foreach (var info in await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)))
            {
                using var d = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (d == null || list.Any(x => x.Name == d.Name)) continue;
                list.Add(new BtDevice(d.DeviceId, d.Name, d.ConnectionStatus == BluetoothConnectionStatus.Connected, LeGlyph(d.Appearance)));
            }
        }
        catch (Exception e) { Settings.Log("Falha listando Bluetooth: " + e.Message); }
        for (var i = 0; i < list.Count; i++)
            if (list[i].Connected) list[i] = list[i] with { Battery = await Battery(list[i].Id) };
        return list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    // O Windows guarda a bateria que o fone informa (pelo perfil de chamadas) num dos "nós" do aparelho;
    // todos os nós de um mesmo aparelho compartilham o ContainerId.
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public static async Task<int?> Battery(string deviceId)
    {
        try
        {
            var aep = await DeviceInformation.CreateFromIdAsync(deviceId, ["System.Devices.Aep.ContainerId"], DeviceInformationKind.AssociationEndpoint);
            if (aep.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var c) is false || c is not Guid container) return null;
            var nodes = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"", [BatteryKey], DeviceInformationKind.Device);
            foreach (var node in nodes)
                if (node.Properties.TryGetValue(BatteryKey, out var v) && v is byte level) return level;
        }
        catch { }
        return null;
    }

    static string Glyph(BluetoothMajorClass major, BluetoothMinorClass minor) => major switch
    {
        BluetoothMajorClass.AudioVideo => "",
        BluetoothMajorClass.Phone => "",
        BluetoothMajorClass.Computer => "",
        BluetoothMajorClass.Peripheral when minor.ToString().Contains("Keyboard") => "",
        BluetoothMajorClass.Peripheral when minor.ToString().Contains("Gamepad") || minor.ToString().Contains("Joystick") => "",
        BluetoothMajorClass.Peripheral => "",
        _ => "",
    };

    // aparência BLE: categoria 0x0F = dispositivo de entrada (1 teclado, 2 mouse, 3/4 controle); 0x21/0x25/0x26 = áudio
    static string LeGlyph(BluetoothLEAppearance a) => a.Category == 0x0F
        ? a.SubCategory switch { 1 => "", 3 or 4 => "", _ => "" }
        : a.Category is 0x21 or 0x25 or 0x26 ? "" : "";

    // avisa quando um aparelho conecta ou desconecta (os já conectados na abertura não contam)
    public static void WatchBluetooth()
    {
        foreach (var selector in new[]
        {
            BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
        })
        {
            var names = new Dictionary<string, string>();
            var ready = false;
            var w = DeviceInformation.CreateWatcher(selector);
            w.Added += (_, info) =>
            {
                names[info.Id] = info.Name;
                if (ready) BluetoothChanged?.Invoke(info.Name, true);
            };
            w.Removed += (_, update) =>
            {
                if (ready && names.Remove(update.Id, out var name)) BluetoothChanged?.Invoke(name, false);
            };
            w.EnumerationCompleted += (_, _) => ready = true;
            w.Start();
            watchers.Add(w);
        }
    }

    // ---------- bandeja do sistema ----------
    // O Windows 11 não expõe os ícones da bandeja; o registro guarda cada app que já pôs um ícone lá,
    // com uma foto do ícone. Cruzando com os processos abertos, sobram os apps que estão na bandeja agora.

    static readonly Dictionary<string, string> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    };

    public static List<TrayApp> TrayApps()
    {
        var result = new List<TrayApp>();
        try
        {
            var running = Process.GetProcesses().Select(p => p.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return result;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("ExecutablePath") is not string raw) continue;
                var path = ExpandKnownFolder(raw);
                var exe = Path.GetFileNameWithoutExtension(path);
                if (exe.Equals("explorer", StringComparison.OrdinalIgnoreCase) || exe.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!running.Contains(exe) || result.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                var tip = (key.GetValue("InitialTooltip") as string ?? "").Split('—', '-', '\n')[0].Trim();
                result.Add(new TrayApp(path, tip.Length > 0 ? tip : Describe(path), Snapshot(key.GetValue("IconSnapshot") as byte[]) ?? SystemService.IconFor(path)));
            }
        }
        catch (Exception e) { Settings.Log("Falha lendo a bandeja: " + e.Message); }
        return result.OrderBy(a => a.Name).ToList();
    }

    static string ExpandKnownFolder(string raw)
    {
        foreach (var (guid, folder) in KnownFolders)
            if (raw.StartsWith(guid, StringComparison.OrdinalIgnoreCase)) return folder + raw[guid.Length..];
        return Environment.ExpandEnvironmentVariables(raw);
    }

    static string Describe(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d ? d : Path.GetFileNameWithoutExtension(path); }
        catch { return Path.GetFileNameWithoutExtension(path); }
    }

    static ImageSource? Snapshot(byte[]? png)
    {
        if (png == null || png.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    // traz a janela do app para a frente; apps que ficam só na bandeja são abertos de novo (e mostram a janela)
    public static void Activate(TrayApp app)
    {
        var name = Path.GetFileNameWithoutExtension(app.Path);
        var window = Process.GetProcessesByName(name).Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        if (window != IntPtr.Zero)
        {
            BringToFront(window);
            return;
        }
        SystemService.Open(app.Path);
    }

    public static void BringToFront(IntPtr window)
    {
        Native.ShowWindow(window, Native.IsIconic(window) ? 9 : 5); // SW_RESTORE : SW_SHOW
        // o Windows só deixa trazer janelas para a frente depois de uma tecla: um Alt solto destrava
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
        Native.SetForegroundWindow(window);
    }

    // ---------- câmera e microfone ----------
    // O Windows registra início e fim de cada uso; fim = 0 quer dizer "em uso agora".

    public static List<PrivacyUse> InUse() => [.. Using("webcam", true), .. Using("microphone", false)];

    static IEnumerable<PrivacyUse> Using(string capability, bool camera)
    {
        var list = new List<PrivacyUse>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}");
            if (root == null) return list;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub);
                if (key == null) continue;
                if (sub == "NonPackaged")
                {
                    foreach (var app in key.GetSubKeyNames())
                    {
                        using var appKey = key.OpenSubKey(app);
                        if (Active(appKey)) list.Add(new PrivacyUse(Pretty(Path.GetFileNameWithoutExtension(app.Replace('#', '\\'))), camera));
                    }
                }
                else if (Active(key)) list.Add(new PrivacyUse(Pretty(sub.Split('_')[0].Split('.').Last()), camera));
            }
        }
        catch { }
        return list.DistinctBy(u => u.App);
    }

    static bool Active(RegistryKey? key) =>
        key?.GetValue("LastUsedTimeStop") is long stop && stop == 0 && key.GetValue("LastUsedTimeStart") is long start && start > 0;

    static string Pretty(string name) => name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : name;
}
