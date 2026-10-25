using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.System.Power;

namespace NotchApp;

public record ShelfItem(string Path, string Name, ImageSource? Icon);

public partial class NotchWindow : Window
{
    enum Mode { Hidden, Pill, Hud, Banner, Expanded }
    enum Detail { None, Wifi, Bluetooth, Audio, Tray, Timer, Power }

    const double HudHeight = 44, PillHeight = 36;
    static readonly int[] TimerMinutes = [1, 3, 5, 10, 15, 25, 45, 60];
    static readonly Brush Green = Rgb(0x30, 0xD1, 0x58), Red = Rgb(0xFF, 0x45, 0x3A), Orange = Rgb(0xFF, 0x9F, 0x0A),
        Blue = Rgb(0x0A, 0x84, 0xFF), Fill3 = Rgb(0x3A, 0x3A, 0x3C), Secondary = Rgb(0x8E, 0x8E, 0x93), Indigo = Rgb(0x5E, 0x5C, 0xE6);
    static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93), Card = Color.FromRgb(0x1C, 0x1C, 0x1E);

    readonly Settings settings = Settings.Load();
    readonly MediaService media = new();
    readonly NotificationService notifs = new();
    readonly LiveTimer timer = new();
    // a ilha nasce como um ponto e "abre" na primeira animação
    readonly Spring w = new(36), h = new(36), r = new(18), y = new(0);
    // bolinha da ilha dividida: posição a partir do centro e escala
    readonly Spring bx = new(0, 300, 26), bs = new(0.4, 300, 26);
    readonly Stopwatch frameClock = Stopwatch.StartNew();
    readonly DispatcherTimer clock = Timer(1000), hoverTimer = Timer(200), leaveTimer = Timer(420),
        revertTimer = Timer(5000), hideTimer = Timer(4000), brightnessTimer = Timer(120), aiTimer = Timer(5 * 60 * 1000),
        meterTimer = Timer(33), keysTimer = Timer(200);
    readonly double[] barLevels = new double[4];
    readonly Dictionary<string, Border> slots = [];
    bool split, lastCaps;
    int lastBattery = -1;

    Mode mode = Mode.Pill;
    Detail detail;
    FrameworkElement? current;
    bool rendering, hovering, dragging, notifsOpen, started, barsOn;
    double lastFrame;
    Radio? wifi, bt;
    string? lastTrack, privacyKey;
    List<PrivacyUse> privacy = [];
    Notif? bannerNotif;
    DateTime ownBrightnessAt;
    Point dragStart;
    SettingsWindow? settingsWindow;

    static DispatcherTimer Timer(int ms) => new() { Interval = TimeSpan.FromMilliseconds(ms) };
    static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    static bool Motion => Settings.Motion;
    static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // --snapshot: abre a ilha fora da tela, passa pelos estados sozinha e salva imagens (teste visual sem mexer no mouse)
    // --demo: troca os dados pessoais (notificações, música, redes, aparelhos) por fictícios, para imagens públicas
    readonly string? snapshotDir;
    readonly bool demo;

    public NotchWindow(string? snapshotDir = null, bool demo = false)
    {
        this.snapshotDir = snapshotDir;
        this.demo = demo;
        InitializeComponent();
        Place();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        SourceInitialized += (_, _) =>
        {
            // tool window: fica fora do Alt+Tab; noactivate: clicar na ilha não tira o foco do app em uso
            var handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(handle, -20, Native.GetWindowLong(handle, -20) | 0x80 | 0x08000000);
        };
        Loaded += async (_, _) => await Init();
    }

    void Place()
    {
        Left = snapshotDir != null ? -4000 : (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
        // a janela (transparente) ocupa a altura da tela; a ilha aberta nunca passa dela
        Height = SystemParameters.PrimaryScreenHeight;
        ExpandedHost.MaxHeight = (Height - 28) / Math.Max(0.8, StageScale.ScaleY);
    }

    async Task Snap(string name, int wait = 1500)
    {
        await Task.Delay(wait);
        var root = (FrameworkElement)Content;
        // recorta só a faixa de cima onde a ilha está, sem esticar
        double width = root.ActualWidth, height = Math.Min(root.ActualHeight, (Shell.ActualHeight + (demo ? 44 : 30)) * StageScale.ScaleY);
        var area = new Rect(0, 0, width, height);
        Brush background = demo
            ? new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x4E), Color.FromRgb(0x5B, 0x2C, 0x6F), new Point(0, 0), new Point(1, 1))
            : new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4A));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, area);
            dc.DrawRectangle(new VisualBrush(root) { Viewbox = area, ViewboxUnits = BrushMappingMode.Absolute }, null, area);
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width * 2), (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var file = File.Create(System.IO.Path.Combine(snapshotDir!, name + ".png"));
        png.Save(file);
    }

    async Task RunDemoSnapshots()
    {
        await Task.Delay(1500); // deixa terminar o que o player real já tinha pedido
        ApplyDemo();
        Go(Mode.Pill); await Snap("01-pilula", 1800);
        ShowBanner(DemoIcon(Color.FromRgb(0x58, 0x65, 0xF2), "D"), null, null, "Discord", "Ana", "bora jogar hoje à noite?"); await Snap("02-notificacao", 1000);
        Go(Mode.Pill); StartTimer(25); await Snap("03-ilha-dividida", 1800);
        Go(Mode.Expanded); await Snap("04-central", 2000);
        SetDetail(Detail.Wifi); await Snap("05-wifi", 1500);
        SetDetail(Detail.Bluetooth); await Snap("06-bluetooth", 1500);
        SetDetail(Detail.Audio); await Snap("07-saida-de-audio", 1200);
        SetDetail(Detail.Timer); await Snap("08-timer", 1200);
        Go(Mode.Pill); await Task.Delay(1500);
        ShowHud("", 0.6, "60"); await Snap("09-volume", 700);
        await Task.Delay(1800);
        ShowAlert("⇪", "Caps Lock ativado", Brushes.White, "Segoe UI Symbol"); await Snap("10-caps-lock", 700);
        Application.Current.Shutdown();
    }

    void ApplyDemo()
    {
        media.Demo("Noites de Neon", "Aurora Lima", DemoArt(), TimeSpan.FromSeconds(214), TimeSpan.FromSeconds(78));
        var now = DateTimeOffset.Now;
        notifs.Demo([
            new Notif(1, "Discord", "Ana", "bora jogar hoje à noite?", now.AddMinutes(-2), DemoIcon(Color.FromRgb(0x58, 0x65, 0xF2), "D"), null),
            new Notif(2, "WhatsApp", "Família", "Mãe: já chegou em casa?", now.AddMinutes(-9), DemoIcon(Color.FromRgb(0x25, 0xD3, 0x66), "W"), null),
            new Notif(3, "Calendário", "Reunião às 15:00", "Daqui a 30 minutos", now.AddMinutes(-20), DemoIcon(Color.FromRgb(0xFF, 0x45, 0x3A), "C"), null),
        ]);
        SetRing(SessionArc, SessionText, 23);
        SetRing(WeekArc, WeekText, 41);
        AiPanel.Visibility = Visibility.Visible;
        AiPanel.Opacity = 1;
        UpdateStatusStrip();
    }

    static BitmapSource Frozen(int size, Action<DrawingContext> draw)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) draw(dc);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    static BitmapSource DemoIcon(Color color, string letter) => Frozen(64, dc =>
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 64, 64), 16, 16);
        var text = new FormattedText(letter, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 34, Brushes.White, 1);
        dc.DrawText(text, new Point((64 - text.Width) / 2, (64 - text.Height) / 2));
    });

    static BitmapSource DemoArt() => Frozen(160, dc =>
    {
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x5F, 0x6D), Color.FromRgb(0x6A, 0x3D, 0xE8), 45), null, new Rect(0, 0, 160, 160));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD2, 0x7F)), null, new Point(108, 56), 38, 38);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0x10, 0x10, 0x30)), null, new Rect(0, 112, 160, 48));
    });

    async Task RunSnapshots()
    {
        Directory.CreateDirectory(snapshotDir!);
        if (demo)
        {
            await RunDemoSnapshots();
            return;
        }
        Go(Mode.Pill); await Snap("1-pilula");
        Go(Mode.Expanded); await Snap("2-aberta");
        StartTimer(5); SetDetail(Detail.Wifi); await Snap("3-timer-wifi", 1500);
        SetDetail(Detail.Audio); await Snap("4-saida-audio");
        SetDetail(Detail.Bluetooth); await Snap("5-bluetooth", 3000);
        SetDetail(Detail.Tray); await Snap("6-bandeja");
        Go(Mode.Pill); await Snap("7-pilula-com-timer");
        ShowAlert("⇪", "Caps Lock ativado", Brushes.White, "Segoe UI Symbol"); await Snap("8-aviso-caps", 700);
        await Task.Delay(1500);
        ShowHud("", 0.6, "60"); await Snap("9-volume", 700);

        // configurações: abre fora da tela e sem ativar, para não roubar o foco de quem está usando o PC
        var page = new SettingsWindow(settings, notifs.Available) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0, Height = 2200 };
        page.Show();
        await Task.Delay(1500);
        var content = (FrameworkElement)((ScrollViewer)page.Content).Content; // a página inteira, não só o trecho visível
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            var area = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            dc.DrawRectangle(Brushes.Black, null, area);
            dc.DrawRectangle(new VisualBrush(content) { Viewbox = area, ViewboxUnits = BrushMappingMode.Absolute }, null, area);
        }
        var shot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        shot.Render(sheet);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
        using (var file = File.Create(System.IO.Path.Combine(snapshotDir!, "10-configuracoes.png"))) encoder.Save(file);
        Application.Current.Shutdown();
    }

    async Task Init()
    {
        clock.Tick += (_, _) => Tick();
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (Island.IsMouseOver || HotZone.IsMouseOver) Go(Mode.Expanded);
        };
        leaveTimer.Tick += (_, _) =>
        {
            leaveTimer.Stop();
            if (hovering) return;
            dragging = false;
            RenderShelf();
            Go(Mode.Pill);
        };
        revertTimer.Tick += (_, _) =>
        {
            revertTimer.Stop();
            if (mode is Mode.Hud or Mode.Banner) Go(Mode.Pill);
        };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer.Stop();
            if (mode == Mode.Pill && ShouldHide) Go(Mode.Hidden);
        };
        brightnessTimer.Tick += (_, _) =>
        {
            brightnessTimer.Stop();
            ownBrightnessAt = DateTime.Now;
            var v = (int)Math.Round(BrightnessLevel.Value * 100);
            Task.Run(() => SystemService.Brightness = v); // WMI é lento; não trava a interface
        };
        aiTimer.Tick += async (_, _) => await RefreshAi();

        media.Changed += OnMedia;
        notifs.Changed += OnNotifs;
        notifs.Arrived += n =>
        {
            if (!settings.ShowBanners) return;
            var hasTitle = n.Title.Length > 0;
            ShowBanner(n.Logo, null, null, n.App, hasTitle ? n.Title : n.Body, hasTitle ? n.Body : "");
            bannerNotif = n;
        };
        PowerManager.PowerSupplyStatusChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            UpdateBattery();
            var pct = PowerManager.RemainingChargePercent;
            if (!Charging) return;
            PulseGlow(Color.FromRgb(0x30, 0xD1, 0x58));
            ShowBanner(null, BatteryGlyph(pct, true), Green, "Bateria", "Carregando", $"{pct}% de carga");
        });
        PowerManager.RemainingChargePercentChanged += (_, _) => Dispatcher.BeginInvoke(OnBatteryLevel);
        SystemService.MicMuteChanged += muted => Dispatcher.BeginInvoke(() =>
            ShowAlert(muted ? "" : "", muted ? "Microfone silenciado" : "Microfone ativado", muted ? Orange : null));
        ConnectivityService.FocusChanged += () => Dispatcher.BeginInvoke(() =>
        {
            UpdateFocus();
            ShowAlert("", ConnectivityService.FocusActive ? "Foco ativado" : "Foco desativado", ConnectivityService.FocusActive ? Indigo : null);
        });
        SystemService.WatchMicrophone();
        ConnectivityService.WatchFocus();
        meterTimer.Tick += (_, _) => UpdateMeter();
        lastCaps = SystemService.CapsLock;
        keysTimer.Tick += (_, _) =>
        {
            var caps = SystemService.CapsLock;
            if (caps == lastCaps) return;
            lastCaps = caps;
            ShowAlert("⇪", caps ? "Caps Lock ativado" : "Caps Lock desativado", caps ? Brushes.White : Secondary, "Segoe UI Symbol");
        };
        keysTimer.Start();
        CreateSlots();
        SystemService.VolumeChanged += (level, muted) => Dispatcher.BeginInvoke(() => OnVolumeKey(level, muted));
        SystemService.BrightnessChanged += v => Dispatcher.BeginInvoke(() => OnBrightnessKey(v));
        DeviceService.BluetoothChanged += (name, on) => Dispatcher.BeginInvoke(() => OnBluetooth(name, on));
        SystemService.WatchVolume();
        SystemService.WatchBrightness();
        DeviceService.WatchBluetooth();
        VolumeLevel.UserChanged += v =>
        {
            SystemService.Volume = v;
            if (SystemService.Muted) SystemService.Muted = false;
            VolumeLevel.Glyph = VolumeGlyph(v, false);
        };
        BrightnessLevel.UserChanged += _ =>
        {
            brightnessTimer.Stop();
            brightnessTimer.Start();
        };
        foreach (var m in TimerMinutes)
        {
            var chip = new Button { Style = (Style)FindResource("Chip"), Content = m < 60 ? $"{m} min" : "1 h" };
            chip.Click += (_, _) => StartTimer(m);
            TimerPresets.Children.Add(chip);
        }

        ApplySettings();
        Tick();
        clock.Start();
        OnMedia();
        OnNotifs();
        RenderTimer();
        Go(Mode.Pill);

        await media.Start();
        await notifs.Start();
        OnNotifs();
        started = true;
        if (snapshotDir != null) await RunSnapshots();
    }

    public void ApplySettings()
    {
        Settings.Motion = settings.Animations;
        StageScale.ScaleX = StageScale.ScaleY = Math.Clamp(settings.IslandScale, 0.8, 1.1);
        Place(); // a altura máxima da ilha aberta depende do tamanho escolhido
        ApplyLayout();
        UpdateFocus();
        if (snapshotDir == null) SystemService.SetStartup(settings.StartWithWindows); // o teste visual não mexe na inicialização
        hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, settings.HideDelaySeconds));
        RenderShelf();
        _ = RefreshAi();
        if (settings.ShowAiLimits) aiTimer.Start(); else aiTimer.Stop();
        if (mode == Mode.Hidden && !ShouldHide) Go(Mode.Pill);
        else ScheduleHide();
    }

    // ---------- estados e animação da ilha ----------

    // atividades ao vivo seguram a ilha na tela, como no iOS
    bool ShouldHide => settings.AutoHide && !hovering && !dragging && !timer.Active && privacy.Count == 0
        && !(settings.KeepVisibleWhilePlaying && media.Playing);

    void ScheduleHide()
    {
        hideTimer.Stop();
        if (mode == Mode.Pill && ShouldHide) hideTimer.Start();
    }

    void Go(Mode m)
    {
        if (m != Mode.Expanded)
        {
            notifsOpen = false;
            if (detail != Detail.None) SetDetail(Detail.None, resize: false);
        }
        if (m != Mode.Banner) bannerNotif = null;
        mode = m;
        if (m == Mode.Expanded)
        {
            RenderNotifs();
            RenderTrayBadge();
            RefreshQuick();
            _ = RefreshControls();
            UpdateProgress();
        }
        if (m == Mode.Expanded && current != ExpandedHost) ExpandedHost.ScrollToTop();
        Swap(m switch { Mode.Hud => HudView, Mode.Banner => BannerView, Mode.Expanded => ExpandedHost, _ => PillView });
        Resize();
        ScheduleHide();
    }

    // mede o conteúdo do estado atual e manda a mola até esse tamanho
    void Resize()
    {
        UpdateSplit();
        Size size = mode switch
        {
            Mode.Expanded => Measure(ExpandedHost),
            Mode.Banner => Measure(BannerView),
            Mode.Hud => Measure(HudView),
            _ => Measure(PillView),
        };
        if (mode is Mode.Pill or Mode.Hidden) size = new Size(Math.Max(150, size.Width), PillHeight);
        if (mode == Mode.Hud) size = new Size(Math.Max(200, size.Width), HudHeight);
        w.Target = size.Width;
        h.Target = size.Height;
        r.Target = mode switch { Mode.Expanded => 32, Mode.Banner => 26, _ => size.Height / 2 };
        y.Target = mode == Mode.Hidden ? -(size.Height + 20) : 0;
        // a bolinha sai de trás da pílula e para 6 px ao lado dela
        var showBubble = split && mode == Mode.Pill;
        bx.Target = showBubble ? size.Width / 2 + 6 + 18 : size.Width / 2 - 18;
        bs.Target = showBubble ? 1 : 0.4;
        Bubble.IsHitTestVisible = showBubble;
        Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(showBubble ? 1 : 0, Motion ? Ms(showBubble ? 220 : 120) : TimeSpan.Zero));
        Animate();
    }

    // duas atividades ao mesmo tempo: a música fica na pílula e o timer vai para a bolinha
    void UpdateSplit()
    {
        var was = split;
        split = timer.Active && media.HasSession;
        if (split == was) return;
        PillTimer.Visibility = timer.Active && !split ? Visibility.Visible : Visibility.Collapsed;
    }

    void Bubble_Click(object s, MouseButtonEventArgs e) => Go(Mode.Expanded);

    void PulseGlow(Color color)
    {
        if (!Motion) return;
        var glow = new DropShadowEffect { Color = color, BlurRadius = 34, ShadowDepth = 0, Opacity = 0 };
        Shell.Effect = glow;
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = Ms(1600) };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(Ms(350))));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ms(1600)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        pulse.Completed += (_, _) => { if (Shell.Effect == glow) Shell.Effect = null; };
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
    }

    static Size Measure(FrameworkElement el)
    {
        el.UpdateLayout(); // gera os itens de listas que acabaram de mudar antes de medir
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return el.DesiredSize;
    }

    void Animate()
    {
        if (!Motion)
        {
            w.Snap(); h.Snap(); r.Snap(); y.Snap(); bx.Snap(); bs.Snap();
            Apply();
            return;
        }
        if (rendering) return;
        rendering = true;
        lastFrame = frameClock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = frameClock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - lastFrame, 0.05);
        lastFrame = now;
        w.Step(dt); h.Step(dt); r.Step(dt); y.Step(dt); bx.Step(dt); bs.Step(dt);
        Apply();
        if (w.Settled && h.Settled && r.Settled && y.Settled && bx.Settled && bs.Settled)
        {
            CompositionTarget.Rendering -= OnFrame; // parado não gasta CPU
            rendering = false;
        }
    }

    void Apply()
    {
        double width = Math.Max(1, w.Value), height = Math.Max(1, h.Value), radius = Math.Clamp(r.Value, 0, height / 2);
        Shell.Width = width;
        Shell.Height = height;
        Island.CornerRadius = new CornerRadius(radius);
        Island.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        ShellShift.Y = y.Value;
        BubbleShift.X = bx.Value;
        BubbleShift.Y = y.Value;
        BubbleScale.ScaleX = BubbleScale.ScaleY = Math.Max(0, bs.Value);
    }

    // troca o conteúdo: o antigo some rápido, o novo entra desfocado e cresce (como o morph do iOS)
    void Swap(FrameworkElement next)
    {
        if (current == next) return;
        var old = current;
        current = next;
        if (old != null)
        {
            old.IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, Motion ? Ms(110) : TimeSpan.Zero);
            fade.Completed += (_, _) => { if (current != old) old.Visibility = Visibility.Collapsed; };
            old.BeginAnimation(OpacityProperty, fade);
        }
        next.Visibility = Visibility.Visible;
        next.IsHitTestVisible = true;
        if (!Motion)
        {
            next.BeginAnimation(OpacityProperty, null);
            next.Opacity = 1;
            return;
        }
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(260)) { BeginTime = Ms(70) });
        var grow = new DoubleAnimation(0.9, 1, Ms(420)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
        var scale = (ScaleTransform)next.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var blur = new BlurEffect { Radius = 14 };
        next.Effect = blur;
        var sharpen = new DoubleAnimation(14, 0, Ms(300)) { BeginTime = Ms(40) };
        sharpen.Completed += (_, _) => { if (next.Effect == blur) next.Effect = null; };
        blur.BeginAnimation(BlurEffect.RadiusProperty, sharpen);
    }

    // um bloco que aparece dentro da ilha aberta: sobe e clareia enquanto a ilha cresce
    static void Reveal(FrameworkElement el)
    {
        el.Visibility = Visibility.Visible;
        if (!Motion) return;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(240)) { BeginTime = Ms(60) });
        var shift = new TranslateTransform();
        el.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, Ms(360)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void ShowBanner(ImageSource? icon, string? glyph, Brush? glyphBrush, string app, string title, string body, double seconds = 5)
    {
        if (mode == Mode.Expanded || Visibility != Visibility.Visible) return;
        BannerIcon.Background = Brushed(icon);
        BannerIcon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
        BannerGlyph.Text = glyph ?? "";
        BannerGlyph.Foreground = glyphBrush ?? Brushes.White;
        BannerGlyphBox.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
        BannerApp.Text = app;
        BannerTitle.Text = title;
        BannerBody.Text = body;
        BannerBody.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Go(Mode.Banner);
        Resize(); // o texto pode ter mudado com o aviso já aberto
        revertTimer.Stop();
        revertTimer.Interval = TimeSpan.FromSeconds(seconds);
        revertTimer.Start();
    }

    void ShowHud(string glyph, double level, string text)
    {
        if (!settings.ShowHud || mode == Mode.Expanded || mode == Mode.Banner || Visibility != Visibility.Visible) return;
        SetHud(glyph, (FontFamily)FindResource("Icons"), Brushes.White, null);
        HudText.Text = text;
        HudScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(Math.Clamp(level, 0, 1), Motion ? Ms(160) : TimeSpan.Zero) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        OpenHud(1.6);
    }

    // aviso pequeno: só ícone e uma frase curta (Caps Lock, microfone, Foco)
    void ShowAlert(string glyph, string label, Brush? glyphBrush = null, string? font = null)
    {
        if (!settings.ShowSmallAlerts || mode == Mode.Expanded || mode == Mode.Banner || Visibility != Visibility.Visible) return;
        SetHud(glyph, font != null ? new FontFamily(font) : (FontFamily)FindResource("Icons"), glyphBrush ?? Brushes.White, label);
        OpenHud(1.4);
    }

    void SetHud(string glyph, FontFamily font, Brush brush, string? label)
    {
        HudGlyph.Text = glyph;
        HudGlyph.FontFamily = font;
        HudGlyph.FontSize = font.Source.StartsWith("Segoe UI Symbol") ? 19 : 16; // símbolos de texto são mais finos que os ícones
        HudGlyph.Foreground = brush;
        HudLabel.Text = label ?? "";
        HudLabel.Visibility = label != null ? Visibility.Visible : Visibility.Collapsed;
        HudBar.Visibility = HudText.Visibility = label == null ? Visibility.Visible : Visibility.Collapsed;
    }

    void OpenHud(double seconds)
    {
        if (mode != Mode.Hud) Go(Mode.Hud);
        else Resize(); // trocou de volume para um aviso: a largura muda
        revertTimer.Stop();
        revertTimer.Interval = TimeSpan.FromSeconds(seconds);
        revertTimer.Start();
    }

    // bateria fraca: avisa uma vez ao cruzar 20% e 10% descarregando
    void OnBatteryLevel()
    {
        var pct = PowerManager.RemainingChargePercent;
        if (!Charging && lastBattery > 0 && settings.ShowSmallAlerts)
            foreach (var limit in new[] { 20, 10 })
                if (lastBattery > limit && pct <= limit)
                {
                    PulseGlow(Color.FromRgb(0xFF, 0x45, 0x3A));
                    ShowBanner(null, BatteryGlyph(pct, false), Red, "Bateria", "Bateria fraca", $"{pct}% restantes. Conecte o carregador.");
                }
        lastBattery = pct;
        UpdateBattery();
    }

    void Tick()
    {
        var now = DateTime.Now;
        PillClock.Text = now.ToString("HH:mm");
        ApplyTaskbar();
        UpdateBattery();
        UpdateTimer(now);
        if (mode == Mode.Expanded) UpdateProgress();

        if (now.Second % 2 == 0 || !started) // na abertura checa já, para não piscar por cima de um jogo
        {
            UpdatePrivacy();
            var hide = snapshotDir == null && settings.HideInFullscreen && mode != Mode.Expanded && SystemService.FullscreenAppActive;
            var v = hide ? Visibility.Hidden : Visibility.Visible;
            if (Visibility != v) Visibility = v;
        }
        // rede de segurança: se o mouse saiu sem disparar MouseLeave, recolhe
        if (snapshotDir == null && mode == Mode.Expanded && !Island.IsMouseOver && !dragging && !leaveTimer.IsEnabled && Mouse.Captured == null)
            leaveTimer.Start();
    }

    // ---------- bateria ----------

    static bool Charging => PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;

    static string BatteryGlyph(int pct, bool charging) =>
        ((char)((charging ? 0xEBAB : 0xEBA0) + Math.Clamp(pct / 10, 0, 10))).ToString();

    void UpdateBattery()
    {
        if (PowerManager.BatteryStatus == BatteryStatus.NotPresent)
        {
            PillBattery.Text = PillPct.Text = "";
            return;
        }
        var pct = PowerManager.RemainingChargePercent;
        PillBattery.Text = BatteryGlyph(pct, Charging);
        PillBattery.Foreground = Charging ? Green : pct <= 20 ? Red : Brushes.White;
        var text = $"{pct}%";
        if (PillPct.Text == text) return;
        PillPct.Text = text;
        if (mode is Mode.Pill or Mode.Hidden) Resize();
    }

    // ---------- câmera e microfone ----------

    void UpdatePrivacy()
    {
        if (demo) return; // não mostra quais apps reais estão usando câmera/microfone nas imagens públicas
        privacy = DeviceService.InUse();
        var key = string.Join("|", privacy.Select(p => p.App + p.Camera));
        if (key == privacyKey) return;
        var wasEmpty = string.IsNullOrEmpty(privacyKey);
        privacyKey = key;
        CamDot.Visibility = privacy.Any(p => p.Camera) ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Visibility = privacy.Any(p => !p.Camera) ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPanel.Children.Clear();
        foreach (var use in privacy)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            chip.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = use.Camera ? Green : Orange, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            chip.Children.Add(new TextBlock { Text = $"{use.App} usando {(use.Camera ? "a câmera" : "o microfone")}", FontSize = 11.5, Foreground = Secondary });
            PrivacyPanel.Children.Add(chip);
        }
        UpdateStatusStrip();
        if (privacy.Count > 0 && wasEmpty && mode == Mode.Hidden) Go(Mode.Pill);
        else if (mode is Mode.Pill or Mode.Expanded)
        {
            Resize();
            ScheduleHide();
        }
    }

    void UpdateStatusStrip() =>
        StatusStrip.Visibility = privacy.Count > 0 || AiPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

    // ---------- limites do Claude ----------

    async Task RefreshAi()
    {
        if (demo) return; // nas imagens públicas os anéis mostram números fictícios
        if (!settings.ShowAiLimits || !AiUsageService.LoginFound)
        {
            AiPanel.Visibility = Visibility.Collapsed;
            UpdateStatusStrip();
            return;
        }
        try
        {
            var usage = await AiUsageService.Fetch();
            SetRing(SessionArc, SessionText, usage.Session);
            SetRing(WeekArc, WeekText, usage.Week);
            AiPanel.ToolTip = $"Sessão de 5 horas: {usage.Session:0}%{Resets(usage.SessionReset)}\nSemana: {usage.Week:0}%{Resets(usage.WeekReset)}";
            AiPanel.Opacity = 1;
        }
        catch (Exception e)
        {
            Settings.Log(e.Message);
            AiPanel.ToolTip = "Não deu para ler os limites agora. Abra o Claude Code para renovar o login.";
            AiPanel.Opacity = 0.4;
        }
        AiPanel.Visibility = Visibility.Visible;
        UpdateStatusStrip();
        if (mode == Mode.Expanded) Resize();
    }

    static string Resets(DateTimeOffset? at) => at is { } t
        ? $", renova {(t.LocalDateTime.Date == DateTime.Today ? "às " + t.LocalDateTime.ToString("HH:mm") : t.LocalDateTime.ToString("ddd HH:mm"))}"
        : "";

    static void SetRing(System.Windows.Shapes.Path arc, TextBlock text, double percent)
    {
        arc.Data = Arc(percent / 100, 16, 2.4);
        arc.Stroke = percent >= 80 ? Red : percent >= 50 ? Orange : Green;
        text.Text = $"{percent:0}%";
    }

    // arco de anel começando no topo, no sentido horário
    static Geometry Arc(double fraction, double size, double stroke)
    {
        fraction = Math.Clamp(fraction, 0, 0.9999);
        if (fraction <= 0) return Geometry.Empty;
        double radius = (size - stroke) / 2, c = size / 2, angle = fraction * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new Point(c, c - radius) };
        figure.Segments.Add(new ArcSegment(new Point(c + radius * Math.Sin(angle), c - radius * Math.Cos(angle)),
            new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    // ---------- timer e cronômetro ----------

    void StartTimer(int minutes)
    {
        timer.StartCountdown(TimeSpan.FromMinutes(minutes), DateTime.Now);
        AfterTimerChange();
    }

    void Stopwatch_Click(object s, RoutedEventArgs e)
    {
        timer.StartStopwatch(DateTime.Now);
        AfterTimerChange();
    }

    void TimerPause_Click(object s, RoutedEventArgs e)
    {
        if (timer.Running) timer.Pause(DateTime.Now); else timer.Resume(DateTime.Now);
        RenderTimer();
    }

    void TimerCancel_Click(object s, RoutedEventArgs e)
    {
        timer.Cancel();
        AfterTimerChange();
    }

    void AfterTimerChange()
    {
        var showCard = timer.Active && TimerCard.Visibility != Visibility.Visible;
        if (detail == Detail.Timer) SetDetail(Detail.None, resize: false);
        RenderTimer();
        if (showCard) Reveal(TimerCard);
        Resize();
        ScheduleHide();
    }

    void UpdateTimer(DateTime now)
    {
        if (!timer.Active) return;
        if (timer.Finished(now))
        {
            var minutes = timer.Duration.TotalMinutes;
            timer.Cancel();
            AfterTimerChange();
            PlayAlarm();
            ShowBanner(null, "", Orange, "Timer", "Timer concluído", minutes >= 60 ? $"{minutes / 60:0.#} h" : $"{minutes:0} min", seconds: 8);
            return;
        }
        RenderTimer();
        if (mode is Mode.Pill or Mode.Hidden) Resize();
    }

    void RenderTimer()
    {
        var now = DateTime.Now;
        var active = timer.Active;
        TimerCard.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PillTimer.Visibility = active && !split ? Visibility.Visible : Visibility.Collapsed;
        TimerButton.Tag = active ? "On" : null;
        if (!active) return;
        var countdown = timer.Mode == LiveTimer.Kind.Countdown;
        var text = timer.Display(now);
        TimerDigits.Text = PillTimerText.Text = text;
        TimerGlyph.Text = PillTimerGlyph.Text = "";
        TimerLabel.Text = countdown ? (timer.Running ? "Timer" : "Timer pausado") : (timer.Running ? "Cronômetro" : "Cronômetro pausado");
        // anel: o que falta do timer, ou o ponteiro de segundos do cronômetro
        var ring = countdown ? 1 - timer.Progress(now) : timer.Elapsed(now).Seconds / 60.0;
        TimerArc.Data = Arc(ring, 40, 3);
        BubbleArc.Data = Arc(ring, 26, 2.4);
        Bubble.ToolTip = $"{TimerLabel.Text}: {text}";
        TimerPause.Content = timer.Running ? "" : "";
        TimerPause.ToolTip = timer.Running ? "Pausar" : "Continuar";
    }

    static void PlayAlarm()
    {
        try { new SoundPlayer(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Alarm01.wav")).Play(); }
        catch { SystemSounds.Exclamation.Play(); }
    }

    // ---------- volume e brilho pelas teclas ----------

    static string VolumeGlyph(double v, bool muted) =>
        muted || v < 0.01 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";

    void OnVolumeKey(float level, bool muted)
    {
        if (!VolumeLevel.Dragging) VolumeLevel.Value = muted ? 0 : level;
        VolumeLevel.Glyph = VolumeGlyph(level, muted);
        ShowHud(VolumeGlyph(level, muted), muted ? 0 : level, muted ? "" : $"{Math.Round(level * 100)}");
    }

    void OnBrightnessKey(int value)
    {
        if ((DateTime.Now - ownBrightnessAt).TotalSeconds < 1.5) return; // eco do próprio slider
        if (!BrightnessLevel.Dragging) BrightnessLevel.Value = value / 100.0;
        ShowHud("", value / 100.0, $"{value}");
    }

    void Mute_Click(object s, MouseButtonEventArgs e)
    {
        var muted = !SystemService.Muted;
        SystemService.Muted = muted;
        VolumeLevel.Value = muted ? 0 : SystemService.Volume;
        VolumeLevel.Glyph = VolumeGlyph(SystemService.Volume, muted);
        e.Handled = true;
    }

    // ---------- aparelhos ----------

    void OnBluetooth(string name, bool connected)
    {
        if (detail == Detail.Bluetooth) _ = RenderBluetooth();
        if (detail == Detail.Audio) RenderAudio();
        _ = RefreshControls();
        if (!settings.ShowDevices || string.IsNullOrWhiteSpace(name)) return;
        var audio = AudioDevices.Outputs().Any(o => o.IsBluetooth && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        ShowBanner(null, audio ? "" : "", connected ? Blue : Secondary, "Bluetooth", name, connected ? "Conectado" : "Desconectado");
        if (connected) _ = AddBatteryToBanner(name);
    }

    // o fone só informa a bateria uns segundos depois de conectar; quando chega, entra no aviso
    async Task AddBatteryToBanner(string name)
    {
        await Task.Delay(2500);
        var device = (await DeviceService.Paired()).FirstOrDefault(d => d.Name == name);
        if (device?.Battery is not { } level || mode != Mode.Banner || BannerTitle.Text != name) return;
        BannerBody.Text = $"Conectado, {level}% de bateria";
    }

    // ---------- barra de tarefas ----------

    bool taskbarRemoved;

    void ApplyTaskbar()
    {
        if (snapshotDir != null) return;
        if (settings.RemoveTaskbar)
        {
            SystemService.RemoveTaskbar(); // repetido a cada segundo: o Explorer às vezes a mostra de novo
            taskbarRemoved = true;
        }
        else if (taskbarRemoved)
        {
            SystemService.RestoreTaskbar(SystemService.TaskbarAutoHide);
            taskbarRemoved = false;
        }
    }

    // ---------- mídia ----------

    void OnMedia()
    {
        var has = media.HasSession;
        var art = media.Art;
        PillArt.Background = Brushed(art);
        PillArt.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
        var showPlayer = has && PlayerSection.Visibility != Visibility.Visible;
        PlayerSection.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (showPlayer && mode == Mode.Expanded) Reveal(PlayerSection);
        PlayerArt.Background = Brushed(art) ?? (Brush)FindResource("Group2");
        PlayerTitle.Text = media.Title.Length > 0 ? media.Title : "Reproduzindo";
        PlayerArtist.Text = media.Artist;
        PlayGlyph.Text = media.Playing ? "" : "";

        // a cor da capa tinge o equalizador e o fundo do player
        var accent = art != null ? media.Accent : Gray;
        Application.Current.Resources["Accent"] = new SolidColorBrush(accent);
        PlayerSection.Background = new LinearGradientBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B), Card, new Point(0, 0), new Point(1, 1));
        SetBars(media.Playing);

        var track = has ? media.Title + "|" + media.Artist : null;
        if (started && settings.ShowTrackChanges && media.Playing && track != lastTrack && media.Title.Length > 0)
            ShowBanner(art, "", null, "Tocando agora", media.Title, media.Artist);
        lastTrack = track;

        if (mode == Mode.Hidden && !ShouldHide) Go(Mode.Pill);
        else if (mode is Mode.Pill or Mode.Expanded or Mode.Hidden)
        {
            Resize();
            ScheduleHide();
        }
        UpdateProgress();
    }

    void SetBars(bool on)
    {
        if (on == barsOn) return;
        barsOn = on;
        PillBars.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && Motion) meterTimer.Start();
        else
        {
            meterTimer.Stop();
            foreach (FrameworkElement bar in PillBars.Children) ((ScaleTransform)bar.RenderTransform).ScaleY = 0.3;
        }
    }

    // barras seguem o som que está saindo de verdade: canais esquerdo e direito, cada barra com um peso
    // e uma oscilação própria para não andarem juntas; sobe rápido e desce devagar, como um VU
    void UpdateMeter()
    {
        if (mode is not (Mode.Pill or Mode.Hidden) || Visibility != Visibility.Visible) return; // ninguém está vendo
        var (left, right) = SystemService.Peaks();
        var t = frameClock.Elapsed.TotalSeconds;
        double[] source = [left, (left + right) / 2, right, Math.Max(left, right)];
        double[] weight = [0.85, 1.15, 0.95, 0.7];
        for (var i = 0; i < 4; i++)
        {
            var wobble = 0.75 + 0.25 * Math.Sin(t * (7 + i * 2.3) + i);
            var target = Math.Clamp(Math.Sqrt(source[i]) * weight[i] * wobble * 1.4, 0.18, 1);
            barLevels[i] += (target - barLevels[i]) * (target > barLevels[i] ? 0.6 : 0.18);
            ((ScaleTransform)((FrameworkElement)PillBars.Children[i]).RenderTransform).ScaleY = barLevels[i];
        }
    }

    void UpdateProgress()
    {
        var show = media.HasSession && media.Duration > TimeSpan.Zero;
        ProgressRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var pos = media.CurrentPosition;
        ProgressScale.ScaleX = Math.Clamp(pos / media.Duration, 0, 1);
        PosText.Text = Fmt(pos);
        DurText.Text = "-" + Fmt(media.Duration - pos);
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    static Brush? Brushed(ImageSource? s) => s == null ? null : new ImageBrush(s) { Stretch = Stretch.UniformToFill };

    async void Prev_Click(object s, RoutedEventArgs e) => await media.Previous();
    async void PlayPause_Click(object s, RoutedEventArgs e) => await media.PlayPause();
    async void Next_Click(object s, RoutedEventArgs e) => await media.Next();

    // ---------- mouse e arrastar ----------

    void HotZone_MouseEnter(object s, MouseEventArgs e)
    {
        if (mode != Mode.Hidden) return;
        Go(Mode.Pill);
        if (settings.ExpandOnHover) hoverTimer.Start();
    }

    void Island_MouseEnter(object s, MouseEventArgs e)
    {
        hovering = true;
        leaveTimer.Stop();
        hideTimer.Stop();
        if (mode == Mode.Hidden) Go(Mode.Pill);
        if (settings.ExpandOnHover && mode is not (Mode.Expanded or Mode.Banner)) hoverTimer.Start();
    }

    void Island_MouseLeave(object s, MouseEventArgs e)
    {
        hovering = false;
        hoverTimer.Stop();
        if (mode == Mode.Expanded) leaveTimer.Start();
        else ScheduleHide();
    }

    void Island_Click(object s, MouseButtonEventArgs e)
    {
        if (mode == Mode.Expanded) return;
        hoverTimer.Stop();
        // tocar no aviso de uma notificação abre o app, como no iPhone
        if (mode == Mode.Banner && bannerNotif != null)
        {
            notifs.Open(bannerNotif);
            Go(Mode.Pill);
            return;
        }
        Go(Mode.Expanded);
    }

    void Island_DragEnter(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        leaveTimer.Stop();
        hideTimer.Stop();
        dragging = true;
        RenderShelf();
        if (mode != Mode.Expanded) Go(Mode.Expanded);
    }

    void Island_DragLeave(object s, DragEventArgs e) => leaveTimer.Start();

    void Island_Drop(object s, DragEventArgs e)
    {
        dragging = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
                if (!settings.Shelf.Contains(f)) settings.Shelf.Add(f);
            settings.Save();
        }
        RenderShelf();
    }

    void OpenSettings(object s, RoutedEventArgs e)
    {
        Go(Mode.Pill);
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsWindow(settings, notifs.Available);
            settingsWindow.Changed += ApplySettings;
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        settingsWindow.Activate();
    }

    // ---------- notificações ----------

    void OnNotifs()
    {
        RenderNotifs();
        if (mode == Mode.Expanded) Resize();
    }

    void RenderNotifs()
    {
        var items = notifs.Items;
        var any = items.Count > 0;
        NotifSection.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        PillDot.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count <= 1) notifsOpen = false;
        // fechado, as notificações viram uma pilha com a mais nova na frente
        NotifList.ItemsSource = notifsOpen ? items.ToList() : items.Take(1).ToList();
        StackEdges.Visibility = !notifsOpen && items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        MoreNotifs.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Hidden;
        MoreNotifs.Content = notifsOpen ? "Mostrar menos" : $"Mais {items.Count - 1}";
    }

    void ToggleNotifs()
    {
        notifsOpen = !notifsOpen;
        RenderNotifs();
        Resize();
    }

    void MoreNotifs_Click(object s, RoutedEventArgs e) => ToggleNotifs();
    void StackEdges_Click(object s, MouseButtonEventArgs e) => ToggleNotifs();
    void DismissNotif_Click(object s, RoutedEventArgs e) => notifs.Dismiss((uint)((Button)s).Tag);
    void ClearNotifs_Click(object s, RoutedEventArgs e) => notifs.Clear();

    void NotifCard_Click(object s, MouseButtonEventArgs e)
    {
        var id = (uint)((FrameworkElement)s).Tag;
        if (notifs.Items.FirstOrDefault(n => n.Id == id) is not { } n) return;
        notifs.Open(n);
        Go(Mode.Pill);
    }

    // ---------- controles ----------

    async Task RefreshControls()
    {
        wifi ??= await SystemService.GetRadio(RadioKind.WiFi);
        bt ??= await SystemService.GetRadio(RadioKind.Bluetooth);
        WifiToggle.IsEnabled = wifi != null;
        BtToggle.IsEnabled = bt != null;
        WifiToggle.IsChecked = wifi?.State == RadioState.On;
        BtToggle.IsChecked = bt?.State == RadioState.On;
        WifiSub.Text = wifi == null ? "Indisponível" : wifi.State != RadioState.On ? "Desligado" : SystemService.NetworkName ?? "Sem conexão";
        BtSub.Text = bt == null ? "Indisponível" : bt.State != RadioState.On ? "Desligado" : "Ligado";

        SystemService.WatchVolume();
        var muted = SystemService.Muted;
        var volume = SystemService.Volume;
        if (!VolumeLevel.Dragging) VolumeLevel.Value = muted ? 0 : volume;
        VolumeLevel.Glyph = VolumeGlyph(volume, muted);

        var output = AudioDevices.Outputs().FirstOrDefault(o => o.IsDefault);
        OutputName.Text = output?.Name ?? "Sem saída de áudio";
        OutputKind.Text = output?.Kind ?? "";
        OutputGlyph.Text = OutputIcon(output);

        if (bt?.State == RadioState.On)
        {
            var connected = (await DeviceService.Paired()).Where(d => d.Connected)
                .Select(d => d.Battery is { } b ? $"{d.Name} ({b}%)" : d.Name).ToList();
            BtSub.Text = connected.Count == 0 ? "Ligado" : string.Join(", ", connected);
        }

        var b = await Task.Run(() => SystemService.Brightness);
        BrightnessLevel.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
        if (b != null && !BrightnessLevel.Dragging) BrightnessLevel.Value = b.Value / 100.0;

        if (demo)
        {
            (WifiSub.Text, BtSub.Text, OutputName.Text, OutputKind.Text, OutputGlyph.Text) = ("Casa", "Galaxy Buds (82%)", "Galaxy Buds", "Fones de ouvido", "");
            VolumeLevel.Value = 0.45;
            BrightnessLevel.Value = 0.7;
        }
    }

    static string OutputIcon(AudioOutput? o) =>
        o == null ? "" : o.IsBluetooth || o.Kind.Contains("fone", StringComparison.OrdinalIgnoreCase) || o.Kind.Contains("head", StringComparison.OrdinalIgnoreCase) ? "" : "";

    async void Wifi_Click(object s, RoutedEventArgs e) => await Toggle(wifi, WifiToggle);
    async void Bt_Click(object s, RoutedEventArgs e) => await Toggle(bt, BtToggle);

    async Task Toggle(Radio? radio, ToggleButton toggle)
    {
        if (radio == null) return;
        await radio.SetStateAsync(toggle.IsChecked == true ? RadioState.On : RadioState.Off);
        await RefreshControls();
        if (detail == Detail.Bluetooth) await RenderBluetooth();
    }

    void WifiNetworks_Click(object s, RoutedEventArgs e) => SetDetail(Detail.Wifi);

    // ---------- atalhos rápidos ----------

    void UpdateFocus()
    {
        var on = ConnectivityService.FocusActive;
        FocusMoon.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FocusTile.IsChecked = on;
        if (mode is Mode.Pill or Mode.Hidden) Resize();
    }

    // ---------- modo de energia ----------

    static readonly Dictionary<PowerModes.Mode, string> EnergyGlyphs = new()
    {
        [PowerModes.Mode.Efficiency] = "",
        [PowerModes.Mode.Balanced] = "",
        [PowerModes.Mode.Performance] = "",
    };

    void RefreshEnergy()
    {
        var current = PowerModes.Current;
        EnergyBlock.IsEnabled = current != null;
        EnergyBlock.ToolTip = current == null ? "Os modos de energia só existem com o plano Equilibrado do Windows." : null;
        EnergyEfficiency.IsChecked = current == PowerModes.Mode.Efficiency;
        EnergyBalanced.IsChecked = current == PowerModes.Mode.Balanced;
        EnergyPerformance.IsChecked = current == PowerModes.Mode.Performance;
        EnergyGlyph.Text = EnergyGlyphs[current ?? PowerModes.Mode.Balanced];
    }

    void Energy_Click(object s, RoutedEventArgs e)
    {
        var mode = s == EnergyEfficiency ? PowerModes.Mode.Efficiency : s == EnergyPerformance ? PowerModes.Mode.Performance : PowerModes.Mode.Balanced;
        if (!PowerModes.Set(mode)) ShowEmptyHint("O Windows não deixou mudar o modo de energia agora.");
        RefreshEnergy();
    }

    void RefreshQuick()
    {
        RefreshEnergy();
        FocusTile.IsEnabled = ConnectivityService.FocusSupported;
        FocusTile.IsChecked = ConnectivityService.FocusActive;
        var hotspot = ConnectivityService.HotspotOn;
        HotspotTile.IsEnabled = hotspot != null;
        HotspotTile.IsChecked = hotspot == true;
    }

    void Focus_Click(object s, RoutedEventArgs e) => ConnectivityService.SetFocus(FocusTile.IsChecked == true);

    async void Hotspot_Click(object s, RoutedEventArgs e)
    {
        var want = HotspotTile.IsChecked == true;
        HotspotTile.IsEnabled = false;
        var ok = await ConnectivityService.SetHotspot(want);
        RefreshQuick();
        if (!ok) ShowEmptyHint(want ? "O Windows não deixou ligar o ponto de acesso agora." : "O Windows não deixou desligar o ponto de acesso agora.");
    }

    // a ilha sai do caminho antes do recorte começar, senão ela aparece na captura
    void Snip_Click(object s, RoutedEventArgs e)
    {
        ((ToggleButton)s).IsChecked = false;
        Go(Mode.Pill);
        var later = Timer(350);
        later.Tick += (_, _) =>
        {
            later.Stop();
            SystemService.Open("ms-screenclip:");
        };
        later.Start();
    }

    void ShowEmptyHint(string text)
    {
        if (detail == Detail.None) ShowAlert("", text, Orange);
        else ShowEmpty(text);
    }

    // ---------- blocos da ilha aberta: ordem e visibilidade vêm das configurações ----------

    // cada bloco vai para dentro de uma "vaga": a vaga obedece às configurações, o bloco continua aparecendo
    // e sumindo sozinho conforme o conteúdo
    void CreateSlots()
    {
        var blocks = new Dictionary<string, FrameworkElement>
        {
            ["Status"] = StatusStrip, ["Timer"] = TimerCard, ["Notifs"] = NotifSection, ["Player"] = PlayerSection,
            ["Controls"] = ControlsBlock, ["Output"] = OutputBlock, ["Energy"] = EnergyBlock, ["Quick"] = QuickBlock, ["Actions"] = ActionsBlock, ["Shelf"] = ShelfSection,
        };
        foreach (var (key, el) in blocks)
        {
            ExpandedView.Children.Remove(el);
            slots[key] = new Border { Child = el };
        }
    }

    void ApplyLayout()
    {
        ExpandedView.Children.Clear();
        var order = settings.Order();
        foreach (var key in order)
        {
            var slot = slots[key];
            slot.Visibility = settings.CentralHidden.Contains(key) ? Visibility.Collapsed : Visibility.Visible;
            ExpandedView.Children.Add(slot);
            // o painel de detalhe abre logo abaixo dos botões que o chamam
            if (key == (settings.CentralHidden.Contains("Actions") ? "Controls" : "Actions")) ExpandedView.Children.Add(DetailPanel);
        }
        if (DetailPanel.Parent == null) ExpandedView.Children.Add(DetailPanel);
        if (mode == Mode.Expanded) Resize();
    }

    async Task RenderWifi(bool scan = false)
    {
        var networks = demo
            ? [new("Casa", 4, true, true, null), new("Casa-5G", 4, true, false, null), new("Escritório", 3, true, false, null), new("Café da esquina", 2, false, false, null)]
            : await ConnectivityService.Networks(scan);
        if (demo) scan = true;
        if (detail != Detail.Wifi) return;
        WifiList.Children.Clear();
        DetailEmpty.Visibility = Visibility.Collapsed;
        foreach (var n in networks)
        {
            var glyph = n.Bars switch { <= 1 => "", 2 => "", 3 => "", _ => "" };
            var sub = n.Connected ? "Conectada" : n.Secured ? "Protegida" : "Aberta";
            WifiList.Children.Add(ListRow(glyph, n.Connected, n.Ssid, sub, n.Secured && !n.Connected ? "" : null, async () =>
            {
                if (n.Connected) return;
                ShowEmpty($"Conectando a {n.Ssid}…");
                Resize();
                if (!await ConnectivityService.Connect(n))
                {
                    // rede nova com senha: o Windows pede a senha na lista dele (a ilha não recebe teclado)
                    SystemService.Open("ms-availablenetworks:");
                    DetailEmpty.Visibility = Visibility.Collapsed;
                    return;
                }
                DetailEmpty.Visibility = Visibility.Collapsed;
                await RenderWifi();
                await RefreshControls();
            }));
        }
        if (networks.Count == 0) ShowEmpty(wifi?.State != RadioState.On ? "Ligue o Wi-Fi para ver as redes."
            : scan ? "Nenhuma rede encontrada. Se a Localização do Windows estiver desligada, o Windows esconde as redes."
            : "Procurando redes…");
        Resize();
        ScrollToDetail();
        if (!scan) await RenderWifi(scan: true); // mostra a última lista já e atualiza quando a busca terminar
    }

    // ---------- painel de detalhe ----------

    void BtDevices_Click(object s, RoutedEventArgs e) => SetDetail(Detail.Bluetooth);
    void Audio_Click(object s, RoutedEventArgs e) => SetDetail(Detail.Audio);
    void Tray_Click(object s, RoutedEventArgs e) => SetDetail(Detail.Tray);
    void TimerMenu_Click(object s, RoutedEventArgs e) => SetDetail(Detail.Timer);
    void PowerMenu_Click(object s, RoutedEventArgs e) => SetDetail(Detail.Power);

    void SetDetail(Detail d, bool resize = true)
    {
        if (detail == d && d != Detail.None) d = Detail.None; // tocar de novo fecha
        detail = d;
        WifiList.Visibility = d == Detail.Wifi ? Visibility.Visible : Visibility.Collapsed;
        BtList.Visibility = d == Detail.Bluetooth ? Visibility.Visible : Visibility.Collapsed;
        AudioList.Visibility = d == Detail.Audio ? Visibility.Visible : Visibility.Collapsed;
        TrayList.Visibility = d == Detail.Tray ? Visibility.Visible : Visibility.Collapsed;
        TimerPanel.Visibility = d == Detail.Timer ? Visibility.Visible : Visibility.Collapsed;
        PowerPanel.Visibility = d == Detail.Power ? Visibility.Visible : Visibility.Collapsed;
        DetailEmpty.Visibility = Visibility.Collapsed;
        TrayButton.Tag = d == Detail.Tray ? "On" : null;
        PowerButton.Tag = d == Detail.Power ? "On" : null;
        if (!timer.Active) TimerButton.Tag = d == Detail.Timer ? "On" : null;
        Rotate(WifiChevron, d == Detail.Wifi);
        Rotate(BtChevron, d == Detail.Bluetooth);
        Rotate(AudioChevron, d == Detail.Audio);
        (DetailTitle.Text, DetailLink.Content) = d switch
        {
            Detail.Wifi => ("Wi-Fi", "Configurações"),
            Detail.Bluetooth => ("Bluetooth", "Configurações"),
            Detail.Audio => ("Saída de áudio", "Som"),
            Detail.Tray => ("Em segundo plano", ""),
            Detail.Timer => ("Timer", ""),
            Detail.Power => ("Energia", ""),
            _ => ("", ""),
        };
        DetailLink.Visibility = (DetailLink.Content as string)?.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (d == Detail.None) DetailPanel.Visibility = Visibility.Collapsed;
        else
        {
            Reveal(DetailPanel);
            ScrollToDetail();
        }

        if (d == Detail.Wifi) _ = RenderWifi();
        if (d == Detail.Bluetooth) _ = RenderBluetooth();
        if (d == Detail.Audio) RenderAudio();
        if (d == Detail.Tray) RenderTray();
        if (resize) Resize();
    }

    // se a ilha estiver no limite da tela, rola até o painel (de novo quando a lista termina de carregar)
    void ScrollToDetail() => Dispatcher.BeginInvoke(() =>
    {
        if (detail != Detail.None) DetailPanel.BringIntoView();
    }, DispatcherPriority.Loaded);

    static void Rotate(TextBlock chevron, bool open) =>
        ((RotateTransform)chevron.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(open ? 90 : 0, Motion ? Ms(220) : TimeSpan.Zero) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    void DetailLink_Click(object s, RoutedEventArgs e) => SystemService.Open(detail switch
    {
        Detail.Wifi => "ms-settings:network-wifi",
        Detail.Bluetooth => "ms-settings:bluetooth",
        _ => "ms-settings:sound",
    });

    void ShowEmpty(string text)
    {
        DetailEmpty.Text = text;
        DetailEmpty.Visibility = Visibility.Visible;
    }

    // linha de lista: ícone redondo, nome, detalhe e um sinal à direita
    Button ListRow(string glyph, bool lit, string name, string sub, string? trailing, Action click)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = lit ? Blue : Fill3,
            Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var text = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (sub.Length > 0) text.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (trailing != null)
        {
            var mark = new TextBlock { Text = trailing, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Foreground = Blue, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }
        var row = new Button { Style = (Style)FindResource("ListRow"), Content = grid };
        row.Click += (_, _) => click();
        return row;
    }

    async Task RenderBluetooth()
    {
        if (bt?.State != RadioState.On)
        {
            BtList.Children.Clear();
            ShowEmpty("Ligue o Bluetooth para ver seus aparelhos.");
            Resize();
            return;
        }
        var devices = demo
            ? [new("1", "Galaxy Buds", true, "", 82), new("2", "Mouse MX Master", true, ""), new("3", "Teclado MX Keys", false, "")]
            : await DeviceService.Paired();
        var outputs = demo ? [] : AudioDevices.Outputs().Where(o => o.IsBluetooth).ToList();
        BtList.Children.Clear();
        foreach (var d in devices)
        {
            var audio = outputs.FirstOrDefault(o => o.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
            var sub = d.Connected ? (d.Battery is { } b ? $"Conectado, {b}% de bateria" : "Conectado") : audio != null ? "Toque para conectar" : "Não conectado";
            BtList.Children.Add(ListRow(d.Glyph, d.Connected, d.Name, sub, null, () =>
            {
                if (audio == null)
                {
                    SystemService.Open("ms-settings:bluetooth"); // o Windows só conecta fones por aqui; o resto, nas Configurações
                    return;
                }
                ConnectAudio(audio.Id, !d.Connected);
            }));
        }
        if (devices.Count == 0) ShowEmpty("Nenhum aparelho pareado.");
        Resize();
        ScrollToDetail();
    }

    void RenderAudio()
    {
        AudioList.Children.Clear();
        List<AudioOutput> outputs = demo
            ? [new("1", "Galaxy Buds", "Fones de ouvido", true, true, true), new("2", "Realtek(R) Audio", "Alto-falantes", false, false, true), new("3", "Monitor LG", "HDMI", false, false, true)]
            : AudioDevices.Outputs();
        foreach (var o in outputs)
        {
            var sub = o.Connected ? o.Kind : "Desconectado, toque para conectar";
            AudioList.Children.Add(ListRow(OutputIcon(o), o.IsDefault, o.Name, sub, o.IsDefault ? "" : null, () =>
            {
                if (!o.Connected)
                {
                    ConnectAudio(o.Id, true);
                    return;
                }
                AudioDevices.SetDefault(o.Id);
                RenderAudio();
                _ = RefreshControls();
                Resize();
            }));
        }
        if (outputs.Count == 0) ShowEmpty("Nenhuma saída de áudio encontrada.");
    }

    // o fone leva alguns segundos para conectar; a lista se atualiza sozinha depois
    void ConnectAudio(string id, bool connect)
    {
        var ok = AudioDevices.Connect(id, connect);
        ShowEmpty(ok ? (connect ? "Conectando…" : "Desconectando…") : "O Windows não deixou mudar a conexão deste aparelho.");
        Resize();
        var later = Timer(3000);
        later.Tick += async (_, _) =>
        {
            later.Stop();
            DetailEmpty.Visibility = Visibility.Collapsed;
            if (detail == Detail.Bluetooth) await RenderBluetooth();
            if (detail == Detail.Audio) RenderAudio();
            await RefreshControls();
            Resize();
        };
        later.Start();
    }

    List<TrayApp> trayApps = [];

    void RenderTrayBadge()
    {
        trayApps = DeviceService.TrayApps();
        TrayCount.Text = trayApps.Count.ToString();
        TrayBadge.Visibility = trayApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void RenderTray()
    {
        RenderTrayBadge();
        TrayList.Children.Clear();
        foreach (var app in trayApps)
        {
            var content = new StackPanel();
            content.Children.Add(app.Icon != null
                ? new Image { Source = app.Icon, Width = 26, Height = 26 }
                : new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = app.Name, FontSize = 10.5, Margin = new Thickness(0, 5, 0, 0), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 60 });
            var tile = new Button { Style = (Style)FindResource("FileTile"), Content = content, ToolTip = app.Name };
            var target = app;
            tile.Click += (_, _) =>
            {
                DeviceService.Activate(target);
                Go(Mode.Pill);
            };
            TrayList.Children.Add(tile);
        }
        if (trayApps.Count == 0) ShowEmpty("Nenhum app rodando em segundo plano.");
    }

    // reiniciar e desligar ficam dentro do menu de energia: são sempre dois cliques deliberados
    void Lock_Click(object s, RoutedEventArgs e) => SystemService.Lock();
    void Sleep_Click(object s, RoutedEventArgs e) => SystemService.Sleep();
    void Restart_Click(object s, RoutedEventArgs e) => SystemService.Shutdown(restart: true);
    void Shutdown_Click(object s, RoutedEventArgs e) => SystemService.Shutdown(restart: false);

    // ---------- arquivos ----------

    void RenderShelf()
    {
        var items = settings.Shelf
            .Select(p => new ShelfItem(p, System.IO.Path.GetFileName(p.TrimEnd('\\')) is { Length: > 0 } n ? n : p, SystemService.IconFor(p)))
            .ToList();
        ShelfList.ItemsSource = items;
        ShelfSection.Visibility = items.Count > 0 || dragging ? Visibility.Visible : Visibility.Collapsed;
        ShelfHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfClear.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DropOutline.Visibility = dragging ? Visibility.Visible : Visibility.Collapsed;
        if (mode == Mode.Expanded) Resize();
    }

    void ShelfOpen_Click(object s, RoutedEventArgs e) => SystemService.Open((string)((Button)s).Tag);

    void ShelfItem_MouseDown(object s, MouseButtonEventArgs e) => dragStart = e.GetPosition(this);

    void ShelfItem_MouseMove(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var path = (string)((FrameworkElement)s).Tag;
        DragDrop.DoDragDrop((DependencyObject)s, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move);
    }

    void ShelfRemove_Click(object s, MouseButtonEventArgs e)
    {
        settings.Shelf.Remove((string)((FrameworkElement)s).Tag);
        settings.Save();
        RenderShelf();
    }

    void ShelfClear_Click(object s, RoutedEventArgs e)
    {
        settings.Shelf.Clear();
        settings.Save();
        RenderShelf();
    }
}
