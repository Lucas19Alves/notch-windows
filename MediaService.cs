using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NotchApp;

// Lê o que está tocando em qualquer player que use os controles de mídia do Windows (Spotify, navegadores...)
public sealed class MediaService
{
    GlobalSystemMediaTransportControlsSessionManager? manager;
    GlobalSystemMediaTransportControlsSession? session;
    string? artKey;
    TimeSpan position;
    DateTimeOffset positionAt;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public BitmapSource? Art { get; private set; }
    public Color Accent { get; private set; }
    public bool Playing { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool HasSession => session != null || demo;

    // dados fictícios para as imagens de divulgação (--snapshot --demo); trava os eventos do player real
    bool demo;

    public void Demo(string title, string artist, BitmapSource art, TimeSpan duration, TimeSpan at)
    {
        demo = true;
        (Title, Artist, Art, Accent, Playing, Duration) = (title, artist, art, Img.Accent(art), true, duration);
        position = at;
        positionAt = DateTimeOffset.Now;
        Changed?.Invoke();
    }
    public event Action? Changed;

    public TimeSpan CurrentPosition
    {
        get
        {
            var p = Playing ? position + (DateTimeOffset.Now - positionAt) : position;
            return p > Duration ? Duration : p;
        }
    }

    public async Task Start()
    {
        try { manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception e) { Settings.Log("Mídia indisponível: " + e.Message); return; }
        manager.CurrentSessionChanged += (_, _) => Ui(Attach);
        Attach();
    }

    // eventos do WinRT chegam em outra thread; tudo roda na thread da interface
    static void Ui(Action a) => Application.Current.Dispatcher.BeginInvoke(a);

    void Attach()
    {
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnProps;
            session.PlaybackInfoChanged -= OnPlayback;
            session.TimelinePropertiesChanged -= OnTimeline;
        }
        session = manager?.GetCurrentSession();
        if (session != null)
        {
            session.MediaPropertiesChanged += OnProps;
            session.PlaybackInfoChanged += OnPlayback;
            session.TimelinePropertiesChanged += OnTimeline;
        }
        _ = Refresh();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Ui(() => _ = Refresh());
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Ui(() => _ = Refresh());
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => Ui(() => _ = Refresh());

    async Task Refresh()
    {
        if (demo) return;
        var s = session;
        if (s == null)
        {
            Title = Artist = "";
            Art = null;
            artKey = null;
            Playing = false;
            Duration = TimeSpan.Zero;
            Changed?.Invoke();
            return;
        }
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (s != session || demo) return; // a sessão mudou (ou entrou a demonstração) durante o await
            Title = props.Title ?? "";
            Artist = props.Artist ?? "";
            var key = Title + "|" + Artist;
            // a capa costuma chegar num segundo evento, depois do título
            if (key != artKey || Art == null)
            {
                artKey = key;
                var art = await Img.Load(props.Thumbnail, 160);
                if (demo) return;
                Art = art;
                if (Art != null) Accent = Img.Accent(Art);
            }
            Playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var t = s.GetTimelineProperties();
            Duration = t.EndTime - t.StartTime;
            position = t.Position;
            positionAt = t.LastUpdatedTime.Year > 2000 ? t.LastUpdatedTime : DateTimeOffset.Now;
        }
        catch (Exception e) { Settings.Log("Falha lendo mídia: " + e.Message); }
        Changed?.Invoke();
    }

    public async Task PlayPause() { if (session != null) await session.TryTogglePlayPauseAsync(); }
    public async Task Next() { if (session != null) await session.TrySkipNextAsync(); }
    public async Task Previous() { if (session != null) await session.TrySkipPreviousAsync(); }
}

public static class Img
{
    public static async Task<BitmapSource?> Load(IRandomAccessStreamReference? source, int pixels)
    {
        if (source == null) return null;
        try
        {
            using var stream = (await source.OpenReadAsync()).AsStreamForRead();
            var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = pixels;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    // cor média ponderada pela saturação: as cores vivas da capa dominam, e o resultado é clareado para aparecer no preto
    public static Color Accent(BitmapSource source)
    {
        var bmp = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bmp.PixelWidth * 4;
        var px = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(px, stride, 0);

        double r = 0, g = 0, b = 0, total = 0;
        for (var i = 0; i + 3 < px.Length; i += 16)
        {
            double B = px[i], G = px[i + 1], R = px[i + 2];
            var weight = (Math.Max(R, Math.Max(G, B)) - Math.Min(R, Math.Min(G, B))) / 255.0 + 0.02;
            r += R * weight; g += G * weight; b += B * weight; total += weight;
        }
        if (total == 0) return Color.FromRgb(0x8A, 0xB4, 0xFF);
        r /= total; g /= total; b /= total;
        var max = Math.Max(r, Math.Max(g, b));
        var k = max < 1 ? 1 : 230 / max;
        return Color.FromRgb((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
    }
}
