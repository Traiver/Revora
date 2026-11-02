using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Revora;

public sealed partial class MainController
{
    private async Task EditSourceAsync(string key)
    {
        if (Busy || !scanValid) return;
        bool changed;
        preparing = true; UpdateActions();
        try { changed = EditSourceCore(key); }
        finally { preparing = false; UpdateActions(); }
        if (changed) await ScanAsync();
    }
    private bool EditSourceCore(string key)
    {
        var p = programs.FirstOrDefault(x => x.Key == key); var old = sources.FirstOrDefault(x => x.Key == key);
        bool manual = key.Length == 0 || key.StartsWith("manual:", StringComparison.Ordinal);
        if (key.Length == 0) key = "manual:" + Guid.NewGuid();
        var dialog = Ui.Load("SourceWindow.xaml"); Ui.Owner(dialog, Window);
        var name = dialog.C<TextBox>("NameField"); var url = dialog.C<TextBox>("UrlField"); var feed = dialog.C<TextBox>("FeedField"); var hash = dialog.C<TextBox>("HashField"); var version = dialog.C<TextBox>("VersionField");
        var standard = dialog.C<RadioButton>("DefaultMode"); var download = dialog.C<RadioButton>("DownloadMode"); var save = dialog.C<Button>("SaveSource"); var test = dialog.C<Button>("TestSource"); var note = dialog.C<TextBlock>("ValidationText");
        name.Text = p?.Name ?? ""; version.Text = p?.Installed ?? old?.InstalledVersion ?? ""; name.IsReadOnly = version.IsReadOnly = !manual;
        dialog.C<StackPanel>("SourceNameGroup").Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        dialog.C<StackPanel>("SourceVersionGroup").Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        dialog.C<TextBlock>("AssociationText").Text = p != null ? p.Name + " · Installiert: " + UserText.Version(p.Installed) : "Füge ein nicht gefundenes Programm hinzu. Installiert wird erst nach deiner Bestätigung.";
        dialog.C<TextBlock>("DefaultHint").Text = p?.Id.Length > 0 ? "Der Updater sucht den Download wie bisher im Updatekatalog für Programme." : "Für dieses Programm wurde kein Download im Updatekatalog zugeordnet. Mit dieser Auswahl wird dein eigener Link entfernt; danach ist hier keine automatische Updatesuche möglich.";
        if (old != null) { url.Text = old.Url; feed.Text = old.Feed; hash.Text = old.Sha256; }
        if (manual) { standard.Visibility = Visibility.Collapsed; dialog.C<TextBlock>("DefaultHint").Visibility = Visibility.Collapsed; dialog.C<TextBlock>("VersionHint").Text = "Falls du die Versionsnummer kennst, trage sie ein. Sie steht oft im Programm unter „Hilfe“ oder „Über“. Du kannst das Feld auch leer lassen."; }
        if (manual || old?.Mode == "Download") download.IsChecked = true; else standard.IsChecked = true;
        using var cancellation = new CancellationTokenSource();
        Source? draft = null; bool valid = false, testing = false, remove = false;
        void Invalidate()
        {
            valid = false; draft = null; dialog.C<StackPanel>("DownloadFields").IsEnabled = download.IsChecked == true && !testing;
            dialog.C<StackPanel>("DownloadFields").Visibility = download.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            save.IsEnabled = standard.IsChecked == true && !testing;
            note.Text = standard.IsChecked == true ? "Mit „Speichern“ wird dein eigener Downloadlink entfernt und erneut nach Updates gesucht. Das installierte Programm bleibt unverändert." : "Prüfe den Link vor dem Speichern. Es wird nichts installiert.";
        }
        foreach (var field in new[] { name, url, feed, hash, version }) field.TextChanged += (_, _) => Invalidate();
        standard.Checked += (_, _) => Invalidate(); download.Checked += (_, _) => Invalidate(); Invalidate();
        test.Click += async (_, _) =>
        {
            try
            {
                if (name.Text.Trim().Length == 0 || (url.Text.Trim().Length == 0 && feed.Text.Trim().Length == 0)) throw new InvalidDataException("Trage einen Programmnamen und den Downloadlink des Anbieters ein. Wenn du eine spezielle Versionsdatei verwendest, kann sie den Downloadlink liefern.");
                var candidate = new Source { Key = key, Name = name.Text.Trim(), Url = url.Text.Trim(), Feed = feed.Text.Trim(), Sha256 = hash.Text.Trim(), InstalledVersion = version.Text.Trim() };
                testing = true; save.IsEnabled = false; dialog.C<StackPanel>("DownloadFields").IsEnabled = false; name.IsEnabled = standard.IsEnabled = false; note.Text = "Der Link wird geprüft. Es wird noch keine Installationsdatei geladen oder gestartet …";
                var data = await network.ResolveAsync(candidate, true, cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                draft = candidate; valid = true; save.IsEnabled = true; note.Text = $"Der Download ist erreichbar. Mit „Speichern“ wird der Link übernommen und erneut nach Updates gesucht. Es wird noch nichts installiert.\nDownloadadresse: {data.FinalUrl}\nVersion: {UserText.Version(data.Version, "wird nicht angegeben – bitte selbst prüfen, ob sie neuer ist")}\nPrüfe, ob der Link vom richtigen Anbieter stammt und zu diesem Programm gehört.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { valid = false; if (!cancellation.IsCancellationRequested) note.Text = UserText.Explain(ex); }
            finally { testing = false; if (!cancellation.IsCancellationRequested) { dialog.C<StackPanel>("DownloadFields").IsEnabled = true; name.IsEnabled = true; standard.IsEnabled = !manual; } }
        };
        save.Click += (_, _) => { if (standard.IsChecked == true) { remove = true; dialog.DialogResult = true; } else if (valid && draft != null) dialog.DialogResult = true; };
        dialog.C<Button>("CancelSource").Click += (_, _) => dialog.DialogResult = false;
        if (manual && old != null)
        {
            var delete = dialog.C<Button>("DeleteSource"); delete.Visibility = Visibility.Visible;
            delete.Click += (_, _) => { if (MessageBox.Show(dialog, "Möchtest du diesen selbst hinzugefügten Eintrag aus dem Updater entfernen? Das Programm selbst und seine Dateien bleiben auf deinem PC.", "Nur aus der Liste entfernen?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) { remove = true; dialog.DialogResult = true; } };
        }
        bool accepted = dialog.ShowDialog() == true; cancellation.Cancel();
        if (!accepted) return false;
        var next = sources.Where(x => x.Key != key).ToList(); if (!remove && draft != null) next.Add(draft);
        store.Save(next); sources = next; return true;
    }
    private Task<bool> ChooseDownloadAsync(InstallPlan plan)
    {
        var dialog = Ui.Load("DownloadWindow.xaml"); Ui.Owner(dialog, Window); dialog.C<TextBlock>("PackageText").Text = plan.Name + "\nAuf deinem PC: " + UserText.Version(plan.Installed);
        var choices = dialog.C<ListBox>("DownloadChoices"); var custom = dialog.C<TextBox>("CustomDownload"); var use = dialog.C<Button>("UseDownloadChoice"); var note = dialog.C<TextBlock>("ChoiceStatus");
        using var cancellation = new CancellationTokenSource(); bool working = true; DownloadInfo? chosen = null; string chosenVersion = "";
        void Refresh() { choices.IsEnabled = custom.IsEnabled = !working; use.IsEnabled = !working && (choices.SelectedItem != null || custom.Text.Trim().Length > 0); }
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem != null) custom.Text = ""; Refresh(); };
        custom.TextChanged += (_, _) => { if (custom.Text.Trim().Length > 0) choices.SelectedIndex = -1; Refresh(); };
        dialog.C<Button>("CancelDownloadChoice").Click += (_, _) => dialog.DialogResult = false;
        use.Click += async (_, _) =>
        {
            try
            {
                var pick = choices.SelectedItem as DownloadChoice;
                if (custom.Text.Trim().Length > 0) pick = new DownloadChoice { Url = custom.Text.Trim() };
                if (pick == null) return;
                working = true; Refresh(); note.Text = "Dein ausgewählter Link wird geprüft. Noch keine Installation …";
                var info = await network.ResolveAsync(new Source { Url = pick.Url, Sha256 = pick.Sha256 }, true, cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                chosen = info; chosenVersion = pick.Version; dialog.DialogResult = true;
            }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested) note.Text = UserText.Explain(ex); }
            finally { working = false; if (!cancellation.IsCancellationRequested) Refresh(); }
        };
        dialog.ContentRendered += async (_, _) =>
        {
            try
            {
                var data = await scanner.DownloadChoicesAsync(plan, cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                choices.ItemsSource = data.Choices; dialog.C<TextBox>("LookupReport").Text = data.Report;
                note.Text = data.Choices.Count > 0 ? $"{UserText.Count(data.Choices.Count, "passender Download", "passende Downloads")} gefunden. Wähle einen aus und klicke auf „Download übernehmen“. Danach prüfst du die gesamte Auswahl." : "Es wurde kein direkt nutzbarer Download gefunden. Du kannst unten einen Downloadlink von der Website des Anbieters eintragen oder abbrechen.";
            }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested) note.Text = UserText.Explain(ex); }
            finally { working = false; if (!cancellation.IsCancellationRequested) Refresh(); }
        };
        Refresh(); bool accepted = dialog.ShowDialog() == true; cancellation.Cancel();
        if (!accepted || chosen == null) return Task.FromResult(false);
        plan.Mode = "Download"; plan.Url = chosen.Url; plan.FinalUrl = chosen.FinalUrl; plan.Sha256 = chosen.Sha256; plan.Version = chosenVersion; plan.Manual = true;
        return Task.FromResult(true);
    }
    private bool ConfirmMigration(InstallPlan plan)
    {
        string detail = $"Bisher auf deinem PC: {plan.Installed}\nNeue Version: {plan.Version}\n\nWenn du fortfährst:\n1. Die neue Installationsdatei wird zuerst heruntergeladen.\n2. Deine bisherige Programmversion wird entfernt.\n3. Die neue Version wird installiert.\n\nWichtig: Einstellungen und Erweiterungen können verloren gehen. Sichere vorher wichtige Dateien und Einstellungen. Falls die neue Installation scheitert, kann das Programm vorübergehend fehlen. Die heruntergeladene Installationsdatei bleibt für einen erneuten Versuch erhalten.";
        var dialog = Ui.Review(Window, "Alte Version ersetzen?", "Der Anbieter hat die Installation geändert. Ein normales Update ist deshalb nicht möglich. Du kannst die alte Version entfernen und anschließend die neue installieren lassen. Mit „Abbrechen“ bleibt deine bisherige Version unverändert.", new[] { new ReviewItem(plan.Name, detail, "WinGet-Paket: " + plan.PackageId) }, "Alte Version ersetzen", "Ich habe wichtige Dateien und Einstellungen gesichert. Ich stimme zu, dass die bisherige Version entfernt und die neue Version installiert wird.");
        return dialog.ShowDialog() == true;
    }
    private bool ConfirmInstaller(InstallPlan plan, DownloadedFile download)
    {
        string detail = $"Downloadadresse: {plan.FinalUrl}\nAnbieter laut Datei: {download.Publisher}";
        detail += download.Signature == "NotSigned" ? "\nDer Anbieter der Datei konnte nicht bestätigt werden. Starte sie nur, wenn du diesem Download vertraust." : "\nWindows hat die digitale Unterschrift der Datei geprüft. Prüfe trotzdem, ob Anbieter und Programm zu deiner Auswahl passen.";
        string ack = download.Signature == "NotSigned" ? "Ich weiß, dass der Anbieter dieser Datei nicht bestätigt werden konnte. Ich vertraue dem Download und möchte das Installationsprogramm trotzdem starten." : "";
        return Ui.Review(Window, "Installation jetzt starten?", "Die Datei wurde heruntergeladen und geprüft. Mit „Installation starten“ öffnet sich das Installationsprogramm. Folge dann seinen Hinweisen. Mit „Abbrechen“ wird die Datei nicht ausgeführt.", new[] { new ReviewItem(plan.Name, detail, $"Signaturprüfung: {download.Signature}\nSHA256-Prüfcode: {download.Hash}") }, "Installation starten", ack).ShowDialog() == true;
    }
    private void Results(List<Outcome> outcomes)
    {
        var dialog = Ui.Review(Window, "Ergebnis", "Hier siehst du das Ergebnis für jedes ausgewählte Programm. Wenn du dieses Fenster schließt, sucht der Updater noch einmal nach Updates und prüft die gefundenen Versionsnummern.", outcomes.Select(x => new ReviewItem(x.Name + " · " + x.State, x.Detail, x.TechnicalDetails)), "Schließen und erneut prüfen");
        dialog.C<Button>("CancelReview").Visibility = Visibility.Collapsed; dialog.ShowDialog();
    }
}
