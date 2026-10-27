using System.IO;
using System.Windows;
namespace Harmonica.Desktop;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        bool verify = args.Length > 0 && args[0] == "--self-test";
        using var mutex = new Mutex(true, "Local\\HarmonicaStudio.Desktop.v1", out bool first);
        if (!first && !verify) { MessageBox.Show("口琴演奏器已经在运行，请切换到已有窗口。", "口琴演奏器"); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => {
            Storage.Log(e.Exception.ToString());
            if (verify) { e.Handled = true; app.Shutdown(1); }
            else { e.Handled = true; MessageBox.Show("程序遇到错误，将退出并释放按键。\n" + e.Exception.Message, "口琴演奏器"); if (app.MainWindow is MainWindow main) main.Close(); else app.Shutdown(1); }
        };
        if (verify)
        {
            app.Startup += async (_, _) => {
                try { await DesktopVerification.RunAsync(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "harmonica-verification.json"), args.Length > 2 ? args[2] : null); app.Shutdown(0); }
                catch (Exception e) { Storage.Log(e.ToString()); if (args.Length > 1) File.WriteAllText(args[1], e.ToString()); app.Shutdown(1); }
            };
        }
        else
        {
            app.Startup += (_, _) => {
                var window = new MainWindow(); app.MainWindow = window;
                window.Closed += (_, _) => app.Shutdown(); window.Show();
            };
        }
        return app.Run();
    }
}
