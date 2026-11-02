using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Revora;

public sealed partial class MainController
{
    private readonly SelfUpdateService selfUpdater = new();
    private bool selfUpdatesEnabled, checkingSelfUpdate;
    private AppRelease? appRelease;
    private string notifiedAppVersion = "";

    private async Task ScanAllAsync()
    {
        await ScanAsync();
        await CheckSelfUpdateAsync();
    }
    private async Task CheckSelfUpdateAsync()
    {
        if (!selfUpdatesEnabled || disposed || checkingSelfUpdate || installing || preparing) return;
        checkingSelfUpdate = true;
        var button = Window.C<Button>("SelfCheckButton"); var hint = Window.C<TextBlock>("SelfUpdateHint");
        button.IsEnabled = false; hint.Text = "Revora sucht auf GitHub nach einer neuen Version …";
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var found = await selfUpdater.CheckAsync(cancel.Token);
            if (disposed) return;
            appRelease = found;
            Window.C<Border>("SelfUpdateCard").Visibility = found == null ? Visibility.Collapsed : Visibility.Visible;
            hint.Text = found == null ? "Revora ist aktuell. Neue Versionen werden automatisch mitgeprüft." : "Eine neue Revora-Version ist verfügbar.";
            if (found != null)
            {
                Window.C<TextBlock>("SelfUpdateTitle").Text = "Revora " + found.Tag.TrimStart('v').Replace("-beta", " Beta") + " ist verfügbar";
                if (TrayEnabled && !Window.IsVisible && notifiedAppVersion != found.Tag) { notify("Revora-Update verfügbar", "Öffne Revora, um die neue Version herunterzuladen und zu installieren."); notifiedAppVersion = found.Tag; }
            }
        }
        catch (Exception ex)
        {
            if (!disposed) hint.Text = "Revora-Updates konnten gerade nicht geprüft werden. Prüfe deine Verbindung und versuche es später erneut.";
            // Keep a previously found release available during a temporary outage.
            if (!disposed) Window.C<TextBox>("DiagnosticReport").Text += "\nRevora-Updates: " + ex.Message;
        }
        finally { checkingSelfUpdate = false; if (!disposed) button.IsEnabled = !Busy; }
    }
    private async Task InstallSelfUpdateAsync()
    {
        if (Busy || appRelease == null) return;
        var release = appRelease;
        preparing = true; UpdateActions();
        try
        {
            var dialog = Ui.Review(Window, "Revora aktualisieren", "Revora wird heruntergeladen, geprüft und neu gestartet. Deine Einstellungen und Programmquellen bleiben erhalten.", new[] { new ReviewItem("Neue Version: " + release.Tag.TrimStart('v').Replace("-beta", " Beta"), "Download aus dem offiziellen GitHub-Projekt Traiver/Revora. Die bisherige Anwendung wird als Revora.exe.previous gesichert. Falls der Neustart scheitert, wird sie wiederhergestellt.", release.Url + "\nSHA256: " + release.Hash) }, "Aktualisieren und neu starten");
            if (dialog.ShowDialog() != true) return;
            Window.C<TextBlock>("SelfUpdateTitle").Text = "Revora wird heruntergeladen und geprüft …";
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await selfUpdater.PrepareAsync(release, cancel.Token);
            exiting = true;
            Window.Close();
            Application.Current.Shutdown();
        }
        finally { preparing = false; if (!disposed && !exiting) { UpdateActions(); if (appRelease != null) Window.C<TextBlock>("SelfUpdateTitle").Text = "Neue Revora-Version verfügbar"; } }
    }
}
