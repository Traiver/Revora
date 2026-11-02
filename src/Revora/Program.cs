using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace Revora;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool background = args.Contains("--background", StringComparer.Ordinal);
        try
        {
            if (args.Length == 3 && args[0] == "--worker")
            {
                var plan = Json.Read<InstallPlan>(File.ReadAllText(args[1]));
                var runner = new WingetRunner();
                var result = new WorkerService(runner).ExecuteAsync(plan, Path.ChangeExtension(args[2], ".log.txt")).GetAwaiter().GetResult();
                File.WriteAllText(args[2], Json.Write(result));
                return result.Success ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--migrate-startup") { new StartupService().MigrateLegacy(); return 0; }
            if (args.Length > 0 && args[0] == "--self-test")
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var path = args.Length > 2 && args[1] == "--report" ? args[2] : Path.Combine(Path.GetTempPath(), "Revora-tests.json");
                return SelfTests.Run(path);
            }
            if (args.Length > 0 && args[0] == "--scan-test")
            {
                var path = args.Length > 2 && args[1] == "--report" ? args[2] : Path.Combine(Path.GetTempPath(), "Revora-scan-test.json");
                using var network = new NetworkService();
                var result = new Scanner(new WingetRunner(), network).ScanAsync(new SourceStore(DataPath("quellen.json")).Load(), CancellationToken.None).GetAwaiter().GetResult();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(path, Json.Write(new { Programs = result.Programs.Count, Folders = result.Programs.Count(x => x.Installations.Any(i => i.Path.Length > 0)), Multiple = result.Programs.Count(x => x.InstallationsNotice.Length > 0), Selected = result.Programs.Count(x => x.Selected), result.Error }));
                return result.Error.Length == 0 ? 0 : 1;
            }
            using var mutex = new Mutex(false, LegacyCompatibility.InstanceMutexName);
            if (!mutex.WaitOne(0)) { if (!background) MessageBox.Show("Der Updater ist bereits geöffnet. Klicke auf sein Symbol in der Taskleiste. Wenn er im Hintergrund läuft, öffnest du ihn über das kleine Symbol neben der Windows-Uhr.", "Revora"); return 0; }
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (_, e) => { if (app.MainWindow != null && !app.MainWindow.IsVisible) LogBackgroundError(e.Exception); else if (app.MainWindow?.IsLoaded == true) Ui.Message(app.MainWindow, UserText.Explain(e.Exception), e.Exception.ToString(), "Vorgang nicht abgeschlossen"); else if (background) LogBackgroundError(e.Exception); else MessageBox.Show(UserText.Explain(e.Exception), "Revora", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
                using var controller = new MainController(background);
                app.MainWindow = controller.Window;
                app.Dispatcher.BeginInvoke(new Action(async () => await controller.StartAsync()));
                return app.Run();
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            if (args.Contains("--self-test") || args.Contains("--scan-test")) { Console.Error.WriteLine(ex); var i = Array.IndexOf(args, "--report"); if (i >= 0 && i + 1 < args.Length) { try { File.WriteAllText(args[i + 1], Json.Write(new { Success = false, Error = ex.ToString() })); } catch (Exception reportError) { Console.Error.WriteLine(reportError.Message); } } }
            else if (args.Contains("--migrate-startup")) Console.Error.WriteLine(ex.Message);
            else if (background) LogBackgroundError(ex);
            else if (Application.Current != null) { try { var owner = new Window(); Ui.Message(owner, UserText.Explain(ex), ex is UserMessageException user ? user.Details : ex.ToString(), "Programm konnte nicht geöffnet werden"); } catch { MessageBox.Show(UserText.Explain(ex), "Revora", MessageBoxButton.OK, MessageBoxImage.Error); } }
            else MessageBox.Show(UserText.Explain(ex), "Revora", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
    private static void LogBackgroundError(Exception error)
    {
        try { var folder = DataPath("Logs"); Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "background.log"), DateTime.Now.ToString("s") + " " + error + Environment.NewLine); }
        catch (Exception) { /* A background startup must not open an error window. */ }
    }
    public static string DataPath(string child) => Path.Combine(LegacyCompatibility.DataDirectory, child);
}
