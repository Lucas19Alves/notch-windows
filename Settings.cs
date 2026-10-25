using System.IO;
using System.Text.Json;

namespace NotchApp;

public sealed class Settings
{
    // independente do "efeitos de animação" do Windows, que costuma vir desligado por otimizadores
    public bool Animations { get; set; } = true;
    public static bool Motion { get; set; } = true;
    public bool AutoHide { get; set; } = true;
    public int HideDelaySeconds { get; set; } = 4;
    public bool KeepVisibleWhilePlaying { get; set; } = true;
    public bool ExpandOnHover { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowBanners { get; set; } = true;
    public bool ShowHud { get; set; } = true;
    public bool ShowTrackChanges { get; set; } = true;
    public bool ShowDevices { get; set; } = true;
    public bool ShowSmallAlerts { get; set; } = true;
    public bool ShowAiLimits { get; set; }
    public double IslandScale { get; set; } = 1.0;
    public bool RemoveTaskbar { get; set; }

    // blocos da ilha aberta, na ordem em que aparecem; os desligados ficam em CentralHidden
    public static readonly string[] Blocks = ["Status", "Timer", "Notifs", "Player", "Controls", "Output", "Energy", "Quick", "Actions", "Shelf"];
    public List<string> CentralOrder { get; set; } = [.. Blocks];
    public List<string> CentralHidden { get; set; } = [];

    // ordem salva; um bloco novo que ainda não estava nela entra logo depois do vizinho que ele tem na ordem padrão
    public List<string> Order()
    {
        var order = CentralOrder.Where(Blocks.Contains).Distinct().ToList();
        for (var i = 0; i < Blocks.Length; i++)
        {
            if (order.Contains(Blocks[i])) continue;
            var after = i == 0 ? -1 : order.IndexOf(Blocks[i - 1]);
            order.Insert(after + 1, Blocks[i]);
        }
        return order;
    }
    public List<string> Shelf { get; set; } = [];

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchApp");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
        }
        catch { }
    }
}
