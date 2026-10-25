using System.Windows.Media;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace NotchApp;

public record Notif(uint Id, string App, string Title, string Body, DateTimeOffset Time, ImageSource? Logo, string? AppId)
{
    public string When => Time.ToLocalTime().ToString("HH:mm");
}

// Espelha as notificações da Central de Ações (exige a permissão de acesso a notificações do Windows).
public sealed class NotificationService
{
    UserNotificationListener? listener;
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    bool polling, primed;

    public List<Notif> Items { get; } = [];
    public bool Available { get; private set; }
    public event Action? Changed;
    public event Action<Notif>? Arrived;

    public async Task Start()
    {
        try
        {
            listener = UserNotificationListener.Current;
            Available = await listener.RequestAccessAsync() == UserNotificationListenerAccessStatus.Allowed;
        }
        catch { Available = false; }
        if (!Available) return;
        poll.Tick += async (_, _) => await Poll();
        await Poll();
        poll.Start();
    }

    async Task Poll()
    {
        if (polling || listener == null) return;
        polling = true;
        try
        {
            var current = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = current.Select(n => n.Id).ToHashSet();
            var changed = Items.RemoveAll(i => !ids.Contains(i.Id)) > 0;
            var known = Items.Select(i => i.Id).ToHashSet();
            foreach (var n in current.Where(n => !known.Contains(n.Id)).OrderBy(n => n.CreationTime))
            {
                var item = await Map(n);
                Items.Insert(0, item);
                changed = true;
                if (primed) Arrived?.Invoke(item); // as que já existiam ao abrir não pulam na tela
            }
            primed = true;
            if (changed) Changed?.Invoke();
        }
        catch (Exception e) { Settings.Log("Falha lendo notificações: " + e.Message); }
        finally { polling = false; }
    }

    static async Task<Notif> Map(UserNotification n)
    {
        var texts = n.Notification.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?
            .GetTextElements().Select(t => t.Text).ToList() ?? [];
        var app = "Aplicativo";
        ImageSource? logo = null;
        string? appId = null;
        try
        {
            appId = n.AppInfo.AppUserModelId;
            app = n.AppInfo.DisplayInfo.DisplayName;
            logo = await Img.Load(n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48)), 64);
        }
        catch { } // alguns apps não expõem nome ou ícone
        return new Notif(n.Id, app, texts.FirstOrDefault() ?? "", string.Join("\n", texts.Skip(1)), n.CreationTime, logo, appId);
    }

    // abre o app que mandou a notificação (pelo mesmo ID que o menu Iniciar usa) e tira o cartão
    public void Open(Notif n)
    {
        if (!string.IsNullOrEmpty(n.AppId)) SystemService.Open($"shell:AppsFolder\\{n.AppId}");
        Dismiss(n.Id);
    }

    // dados fictícios para as imagens de divulgação (--snapshot --demo)
    public void Demo(IEnumerable<Notif> items)
    {
        poll.Stop();
        Items.Clear();
        Items.AddRange(items);
        Available = true;
        Changed?.Invoke();
    }

    public void Dismiss(uint id)
    {
        try { listener?.RemoveNotification(id); } catch { }
        Items.RemoveAll(i => i.Id == id);
        Changed?.Invoke();
    }

    public void Clear()
    {
        try { listener?.ClearNotifications(); } catch { }
        Items.Clear();
        Changed?.Invoke();
    }
}
