using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using Windows.UI.Shell;

namespace NotchApp;

public record WifiNetwork(string Ssid, int Bars, bool Secured, bool Connected, WiFiAvailableNetwork? Network);

// Redes Wi-Fi, Foco do Windows e ponto de acesso móvel.
static class ConnectivityService
{
    // ---------- Wi-Fi ----------

    static WiFiAdapter? adapter;

    static async Task<WiFiAdapter?> Adapter()
    {
        if (adapter != null) return adapter;
        try
        {
            if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed) return null;
            return adapter = (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
        }
        catch { return null; }
    }

    // scan: false devolve na hora o resultado da última busca do Windows; a busca nova leva vários segundos
    public static async Task<List<WifiNetwork>> Networks(bool scan = true)
    {
        var a = await Adapter();
        if (a == null) return [];
        try
        {
            if (scan) await a.ScanAsync();
            var connected = (await a.NetworkAdapter.GetConnectedProfileAsync())?.ProfileName;
            return a.NetworkReport.AvailableNetworks
                .Where(n => n.Ssid.Length > 0)
                .GroupBy(n => n.Ssid) // a mesma rede em 2,4 e 5 GHz vira uma linha só
                .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .Select(n => new WifiNetwork(n.Ssid, n.SignalBars,
                    n.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None),
                    n.Ssid == connected, n))
                .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Bars)
                .Take(8)
                .ToList();
        }
        catch (Exception e)
        {
            Settings.Log("Falha procurando redes: " + e.Message);
            return [];
        }
    }

    // redes salvas e abertas conectam direto; as que pedem senha nova vão para a lista do Windows
    public static async Task<bool> Connect(WifiNetwork n)
    {
        var a = await Adapter();
        if (a == null || n.Network == null) return false;
        try { return (await a.ConnectAsync(n.Network, WiFiReconnectionKind.Automatic)).ConnectionStatus == WiFiConnectionStatus.Success; }
        catch { return false; }
    }

    public static async Task Disconnect()
    {
        (await Adapter())?.Disconnect();
    }

    // ---------- Foco (liga o Não perturbe junto) ----------

    static FocusSessionManager? focus;

    static FocusSessionManager? Focus()
    {
        try { return focus ??= FocusSessionManager.IsSupported ? FocusSessionManager.GetDefault() : null; }
        catch { return null; }
    }

    public static bool FocusSupported => Focus() != null;
    public static bool FocusActive => Focus()?.IsFocusActive == true;

    public static event Action? FocusChanged;

    public static void WatchFocus()
    {
        var f = Focus();
        if (f != null) f.IsFocusActiveChanged += (_, _) => FocusChanged?.Invoke();
    }

    public static void SetFocus(bool on)
    {
        var f = Focus();
        if (f == null) return;
        if (on) f.TryStartFocusSession(); else f.DeactivateFocus();
    }

    // ---------- ponto de acesso móvel ----------

    static NetworkOperatorTetheringManager? Tethering()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile == null ? null : NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        }
        catch { return null; }
    }

    public static bool? HotspotOn
    {
        get
        {
            var t = Tethering();
            return t == null ? null : t.TetheringOperationalState == TetheringOperationalState.On;
        }
    }

    public static async Task<bool> SetHotspot(bool on)
    {
        var t = Tethering();
        if (t == null) return false;
        try
        {
            var result = on ? await t.StartTetheringAsync() : await t.StopTetheringAsync();
            return result.Status == TetheringOperationStatus.Success;
        }
        catch { return false; }
    }
}
