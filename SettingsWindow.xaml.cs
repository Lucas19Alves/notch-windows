using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace NotchApp;

// No estilo do Ajustes do iOS: cada mudança vale na hora, sem botão Salvar.
public partial class SettingsWindow : Window
{
    readonly Settings s;
    readonly bool loading = true;
    public event Action? Changed;

    public SettingsWindow(Settings settings, bool notificationsAvailable)
    {
        s = settings;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var dark = 1; // barra de título escura
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };

        AnimationsSwitch.IsChecked = s.Animations;
        AutoHideSwitch.IsChecked = s.AutoHide;
        (s.HideDelaySeconds <= 2 ? Delay2 : s.HideDelaySeconds >= 8 ? Delay8 : Delay4).IsChecked = true;
        KeepPlayingSwitch.IsChecked = s.KeepVisibleWhilePlaying;
        HoverSwitch.IsChecked = s.ExpandOnHover;
        FullscreenSwitch.IsChecked = s.HideInFullscreen;
        BannersSwitch.IsChecked = s.ShowBanners;
        HudSwitch.IsChecked = s.ShowHud;
        TrackSwitch.IsChecked = s.ShowTrackChanges;
        DevicesSwitch.IsChecked = s.ShowDevices;
        AlertsSwitch.IsChecked = s.ShowSmallAlerts;
        (s.IslandScale <= 0.86 ? Size85 : s.IslandScale <= 0.93 ? Size92 : s.IslandScale >= 1.05 ? Size110 : Size100).IsChecked = true;
        RenderBlocks();
        AiSwitch.IsChecked = s.ShowAiLimits;
        StartupSwitch.IsChecked = s.StartWithWindows;
        TaskbarSwitch.IsChecked = SystemService.TaskbarAutoHide;
        RemoveTaskbarSwitch.IsChecked = s.RemoveTaskbar;
        UpdateAutoHideRow();
        NotifStatus.Text = notificationsAvailable ? "Permitido" : "Bloqueado";
        if (!AiUsageService.LoginFound)
        {
            AiSwitch.IsEnabled = false;
            AiNote.Text = "Entre no Claude Code neste computador para ver os limites de uso aqui.";
        }
        UpdateDelayRow();
        loading = false;
    }

    void UpdateAutoHideRow()
    {
        AutoHideRow.IsEnabled = RemoveTaskbarSwitch.IsChecked != true;
        AutoHideRow.Opacity = AutoHideRow.IsEnabled ? 1 : 0.4;
    }

    void UpdateDelayRow()
    {
        DelayRow.IsEnabled = AutoHideSwitch.IsChecked == true;
        DelayRow.Opacity = DelayRow.IsEnabled ? 1 : 0.4;
    }

    void Changed_Click(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        s.Animations = AnimationsSwitch.IsChecked == true;
        s.AutoHide = AutoHideSwitch.IsChecked == true;
        s.HideDelaySeconds = Delay2.IsChecked == true ? 2 : Delay8.IsChecked == true ? 8 : 4;
        s.KeepVisibleWhilePlaying = KeepPlayingSwitch.IsChecked == true;
        s.ExpandOnHover = HoverSwitch.IsChecked == true;
        s.HideInFullscreen = FullscreenSwitch.IsChecked == true;
        s.ShowBanners = BannersSwitch.IsChecked == true;
        s.ShowHud = HudSwitch.IsChecked == true;
        s.ShowTrackChanges = TrackSwitch.IsChecked == true;
        s.ShowDevices = DevicesSwitch.IsChecked == true;
        s.ShowSmallAlerts = AlertsSwitch.IsChecked == true;
        s.IslandScale = Size85.IsChecked == true ? 0.85 : Size92.IsChecked == true ? 0.92 : Size110.IsChecked == true ? 1.1 : 1.0;
        s.ShowAiLimits = AiSwitch.IsChecked == true;
        s.StartWithWindows = StartupSwitch.IsChecked == true;
        var removing = RemoveTaskbarSwitch.IsChecked == true;
        if (s.RemoveTaskbar && !removing) TaskbarSwitch.IsChecked = false; // ao devolver a barra, ela volta fixa
        s.RemoveTaskbar = removing;
        // com a barra removida, o ocultar automático fica por conta do Notch
        if (!removing && SystemService.TaskbarAutoHide != (TaskbarSwitch.IsChecked == true))
            SystemService.TaskbarAutoHide = TaskbarSwitch.IsChecked == true;
        UpdateAutoHideRow();
        UpdateDelayRow();
        s.Save();
        Changed?.Invoke();
    }

    static readonly Dictionary<string, string> BlockNames = new()
    {
        ["Status"] = "Câmera, microfone e Claude",
        ["Timer"] = "Timer e cronômetro",
        ["Notifs"] = "Notificações",
        ["Player"] = "Música",
        ["Controls"] = "Wi-Fi, Bluetooth, brilho e volume",
        ["Output"] = "Saída de áudio",
        ["Energy"] = "Modo de energia",
        ["Quick"] = "Foco, ponto de acesso e captura",
        ["Actions"] = "Botões de ação",
        ["Shelf"] = "Arquivos guardados",
    };

    // lista de blocos da Central: setas mudam a ordem, a chave mostra ou esconde
    void RenderBlocks()
    {
        BlocksList.Children.Clear();
        var order = s.Order();
        for (var i = 0; i < order.Count; i++)
        {
            var key = order[i];
            var index = i;
            var row = new Grid { Style = (Style)FindResource("RowGrid") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            arrows.Children.Add(Arrow("", "Subir", index > 0, () => Move(order, index, -1)));
            arrows.Children.Add(Arrow("", "Descer", index < order.Count - 1, () => Move(order, index, 1)));
            row.Children.Add(arrows);

            var label = new TextBlock { Style = (Style)FindResource("Label"), Text = BlockNames[key] };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var toggle = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = !s.CentralHidden.Contains(key) };
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked == true) s.CentralHidden.Remove(key);
                else if (!s.CentralHidden.Contains(key)) s.CentralHidden.Add(key);
                SaveBlocks();
            };
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);

            if (i > 0) BlocksList.Children.Add(new Border { Style = (Style)FindResource("Sep") });
            BlocksList.Children.Add(row);
        }
    }

    Button Arrow(string glyph, string tip, bool enabled, Action click)
    {
        var b = new Button { Style = (Style)FindResource("MiniRound"), Content = glyph, ToolTip = tip, IsEnabled = enabled, Margin = new Thickness(0, 0, 4, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    void Move(List<string> order, int index, int delta)
    {
        (order[index], order[index + delta]) = (order[index + delta], order[index]);
        s.CentralOrder = order;
        SaveBlocks();
        RenderBlocks();
    }

    void SaveBlocks()
    {
        s.Save();
        Changed?.Invoke();
    }

    void NotifAccess_Click(object sender, RoutedEventArgs e) => SystemService.Open("ms-settings:privacy-notifications");

    void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
