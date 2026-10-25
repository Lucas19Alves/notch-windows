using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchApp;

// NotchApp.exe --selftest  → código de saída 0 quando tudo passa
static class SelfTest
{
    static int failures;

    public static int Run()
    {
        var pixels = Enumerable.Repeat(new byte[] { 0, 0, 200, 255 }, 16).SelectMany(p => p).ToArray();
        var red = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var c = Img.Accent(red);
        Check(c.R > 200 && c.G < 30 && c.B < 30, $"cor de destaque de capa vermelha ({c})");

        var spring = new Spring(0) { Target = 100 };
        var peak = 0.0;
        for (var i = 0; i < 120; i++)
        {
            spring.Step(1 / 60.0);
            peak = Math.Max(peak, spring.Value);
        }
        Check(spring.Settled && spring.Value == 100, $"mola assenta no alvo ({spring.Value:0.##})");
        Check(peak is > 100 and < 115, $"mola passa um pouco do alvo, como no iOS (pico {peak:0.#})");

        spring.Target = 0;
        spring.Step(1 / 60.0);
        spring.Target = 50; // troca de alvo no meio do caminho
        for (var i = 0; i < 120; i++) spring.Step(1 / 60.0);
        Check(spring.Value == 50, "mola muda de alvo no meio da animação");

        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var timer = new LiveTimer();
        timer.StartCountdown(TimeSpan.FromMinutes(5), t0);
        Check(timer.Display(t0.AddSeconds(0.8)) == "5:00", $"timer arredonda para cima como no iOS ({timer.Display(t0.AddSeconds(0.8))})");
        timer.Pause(t0.AddMinutes(1));
        Check(timer.Display(t0.AddMinutes(3)) == "4:00", "timer pausado não anda");
        timer.Resume(t0.AddMinutes(3));
        Check(!timer.Finished(t0.AddMinutes(6).AddSeconds(59)) && timer.Finished(t0.AddMinutes(7)), "timer termina contando só o tempo rodando");
        timer.StartStopwatch(t0);
        Check(timer.Display(t0.AddSeconds(75)) == "1:15", "cronômetro conta para cima");
        timer.Cancel();
        Check(!timer.Active, "cancelar desliga o timer");

        var custom = new Settings { CentralOrder = ["Player", "Player", "Controls", "Antigo"] };
        var order = custom.Order();
        Check(order.IndexOf("Player") < order.IndexOf("Controls") && order.Count == Settings.Blocks.Length && !order.Contains("Antigo"),
            "ordem da central: mantém a escolha, tira repetidos e acrescenta blocos novos");
        var saved = new Settings { CentralOrder = ["Shelf", "Status", "Timer", "Notifs", "Player", "Controls", "Output", "Quick", "Actions"] };
        var withNew = saved.Order();
        Check(withNew.IndexOf("Energy") == withNew.IndexOf("Output") + 1 && withNew[0] == "Shelf",
            $"bloco novo entra ao lado do vizinho padrão ({string.Join(",", withNew)})");

        Console.WriteLine(failures == 0 ? "selftest ok" : $"selftest: {failures} falha(s)");
        return failures;
    }

    // NotchApp.exe --diag  → lista o que a ilha enxerga do sistema, sem abrir janela
    public static int Diag()
    {
        foreach (var o in AudioDevices.Outputs())
            Console.WriteLine($"saída: {o.Name} | {o.Kind} | padrão={o.IsDefault} bt={o.IsBluetooth} conectada={o.Connected} | driver={AudioDevices.DriverId(o.Id)}");
        // fora da thread da interface: esperar aqui dentro travaria o próprio diagnóstico
        foreach (var d in Task.Run(DeviceService.Paired).Result)
            Console.WriteLine($"bluetooth: {d.Name} | conectado={d.Connected} | bateria={d.Battery?.ToString() ?? "?"}");
        Console.WriteLine($"modo de energia: {PowerModes.Current?.ToString() ?? "indisponível"}");
        foreach (var a in DeviceService.TrayApps())
            Console.WriteLine($"bandeja: {a.Name} | ícone={a.Icon != null}");
        foreach (var p in DeviceService.InUse())
            Console.WriteLine($"em uso: {p.App} | {(p.Camera ? "câmera" : "microfone")}");
        Console.WriteLine($"login do Claude Code presente={AiUsageService.LoginFound}");
        foreach (var n in Task.Run(() => ConnectivityService.Networks()).Result)
            Console.WriteLine($"wi-fi: {n.Ssid} | barras={n.Bars} protegida={n.Secured} conectada={n.Connected}");
        Console.WriteLine($"foco: suportado={ConnectivityService.FocusSupported} ativo={ConnectivityService.FocusActive}");
        Console.WriteLine($"ponto de acesso: {ConnectivityService.HotspotOn?.ToString() ?? "indisponível"}");
        Console.WriteLine($"caps lock={SystemService.CapsLock} | pico do áudio={SystemService.Peaks()}");
        return 0;
    }

    static void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FALHA")} {name}");
        if (!ok) failures++;
    }
}
