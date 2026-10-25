using System.Runtime.InteropServices;

namespace NotchApp;

public record AudioOutput(string Id, string Name, string Kind, bool IsDefault, bool IsBluetooth, bool Connected);

// Saídas de áudio: lista, troca a padrão e liga/desliga fones Bluetooth sem abrir as Configurações.
static class AudioDevices
{
    static readonly Native.PROPERTYKEY DeviceDesc = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
    static readonly Native.PROPERTYKEY InterfaceName = new("026E516E-B814-414B-83CD-856D6FEF4822", 2);
    static readonly Guid BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D"); // KSPROPSETID_BtAudio
    const int Active = 1, All = 0xF;

    static Native.IMMDeviceEnumerator Enumerator() => (Native.IMMDeviceEnumerator)new Native.MMDeviceEnumerator();

    public static string? DefaultId()
    {
        try
        {
            Enumerator().GetDefaultAudioEndpoint(0, 1, out var d);
            d.GetId(out var id);
            return id;
        }
        catch { return null; }
    }

    public static List<AudioOutput> Outputs()
    {
        var result = new List<AudioOutput>();
        try
        {
            var defaultId = DefaultId();
            Enumerator().EnumAudioEndpoints(0, All, out var collection);
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                device.GetId(out var id);
                device.GetState(out var state);
                var bt = BtControl(device) != null;
                // saídas desconectadas só interessam se forem fones Bluetooth que dá para religar daqui
                if (state != Active && !bt) continue;
                device.OpenPropertyStore(0, out var store);
                var name = Read(store, InterfaceName) ?? "Saída de áudio";
                var kind = Read(store, DeviceDesc) ?? "";
                result.Add(new AudioOutput(id, name, kind, id == defaultId, bt, state == Active));
            }
        }
        catch (Exception e) { Settings.Log("Falha listando saídas: " + e.Message); }
        // um fone Bluetooth aparece duas vezes (estéreo e headset): fica uma entrada por aparelho
        return result
            .GroupBy(o => o.IsBluetooth ? o.Name : o.Id)
            .Select(g => g.OrderByDescending(o => o.IsDefault).ThenByDescending(o => o.Connected).First())
            .OrderByDescending(o => o.Connected).ThenBy(o => o.Name)
            .ToList();
    }

    public static void SetDefault(string id)
    {
        var policy = (Native.IPolicyConfig)new Native.PolicyConfigClient();
        for (var role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role);
    }

    // KSPROPERTY_ONESHOT_RECONNECT = 0, DISCONNECT = 1
    public static bool Connect(string id, bool connect)
    {
        try
        {
            var device = Find(id);
            var ks = device == null ? null : BtControl(device);
            if (ks == null) return false;
            var prop = new Native.KSIDENTIFIER { Set = BtAudio, Id = connect ? 0u : 1u, Flags = 1 };
            return ks.KsProperty(ref prop, (uint)Marshal.SizeOf<Native.KSIDENTIFIER>(), IntPtr.Zero, 0, out _) >= 0;
        }
        catch (Exception e)
        {
            Settings.Log("Falha no Bluetooth: " + e.Message);
            return false;
        }
    }

    static Native.IMMDevice? Find(string id)
    {
        Enumerator().EnumAudioEndpoints(0, All, out var collection);
        collection.GetCount(out var count);
        for (uint i = 0; i < count; i++)
        {
            collection.Item(i, out var device);
            device.GetId(out var other);
            if (other == id) return device;
        }
        return null;
    }

    // segue o conector da saída até o driver; se for Bluetooth, abre o driver e devolve o controle dele
    static Native.IKsControl? BtControl(Native.IMMDevice device)
    {
        try
        {
            var iid = typeof(Native.IDeviceTopology).GUID;
            device.Activate(ref iid, 23, IntPtr.Zero, out var t);
            ((Native.IDeviceTopology)t).GetConnector(0, out var connector);
            connector.GetDeviceIdConnectedTo(out var driver);
            if (!driver.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) && !driver.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase))
                return null;
            Enumerator().GetDevice(driver, out var filter);
            var ksIid = typeof(Native.IKsControl).GUID;
            filter.Activate(ref ksIid, 23, IntPtr.Zero, out var ks);
            return (Native.IKsControl)ks;
        }
        catch { return null; }
    }

    public static string DriverId(string id)
    {
        try
        {
            var iid = typeof(Native.IDeviceTopology).GUID;
            Find(id)!.Activate(ref iid, 23, IntPtr.Zero, out var t);
            ((Native.IDeviceTopology)t).GetConnector(0, out var connector);
            connector.GetDeviceIdConnectedTo(out var driver);
            return driver;
        }
        catch (Exception e) { return "erro: " + e.Message; }
    }

    static string? Read(Native.IPropertyStore store, Native.PROPERTYKEY key)
    {
        var k = key;
        if (store.GetValue(ref k, out var v) != 0) return null;
        try { return v.vt == 31 ? Marshal.PtrToStringUni(v.pointer) : null; } // VT_LPWSTR
        finally { Native.PropVariantClear(ref v); }
    }
}
