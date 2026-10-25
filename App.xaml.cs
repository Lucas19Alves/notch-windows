using System.Globalization;
using System.Windows;

namespace NotchApp;

public partial class App : Application
{
    Mutex? single;

    protected override void OnStartup(StartupEventArgs e)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = pt;
        Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = pt;

        if (e.Args.Contains("--selftest"))
        {
            Shutdown(SelfTest.Run());
            return;
        }
        if (e.Args.Contains("--diag"))
        {
            Shutdown(SelfTest.Diag());
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            new NotchWindow(e.Args[1], demo: e.Args.Contains("--demo")).Show();
            return;
        }
        if (e.Args.Contains("--restore-taskbar"))
        {
            // usado pelo desinstalador: devolve a barra de tarefas antes de apagar o app
            SystemService.RestoreTaskbar(autoHide: false);
            Shutdown();
            return;
        }

        single = new Mutex(true, @"Local\NotchApp.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        // o notch substitui a barra de tarefas: um erro isolado é registrado em vez de derrubar o app
        DispatcherUnhandledException += (_, x) =>
        {
            Settings.Log(x.Exception.ToString());
            x.Handled = true;
        };
        // ao fechar o Notch (ou se ele cair de vez), a barra de tarefas volta
        Exit += (_, _) => GiveTaskbarBack();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => GiveTaskbarBack();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => GiveTaskbarBack();
        new NotchWindow().Show();
    }

    static void GiveTaskbarBack()
    {
        var s = Settings.Load();
        if (s.RemoveTaskbar) SystemService.RestoreTaskbar(autoHide: true);
    }
}
