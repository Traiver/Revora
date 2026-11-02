using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Revora;

public sealed partial class MainController : IDisposable
{
    public Window Window { get; }
    private readonly NetworkService network = new();
    private readonly ICommandRunner runner;
    private readonly Scanner scanner;
    private readonly SourceStore store;
    private readonly NotificationSettingsStore notificationSettings;
    private HashSet<string> mutedPrograms;
    private readonly StartupService startup;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer timer;
    private readonly bool backgroundRequested;
    private readonly Action<string, string> notify;
    private List<Source> sources;
    private List<ProgramItem> programs = new(), visible = new();
    private CancellationTokenSource? scanCancellation;
    private bool startupEnabled, updatingStartup, hasScanned, scanValid, installing, preparing, exiting, disposed, initialRender;
    private DateTime scanFinished, scanAttempt;
    private string lastNotification = "";
    private const int IntervalHours = 4;
    private Button CheckButton => Window.C<Button>("CheckButton");
    private Button InstallButton => Window.C<Button>("InstallButton");
    private TextBlock Status => Window.C<TextBlock>("StatusText");
    private TextBlock Summary => Window.C<TextBlock>("SummaryText");
    private ListBox List => Window.C<ListBox>("ProgramsList");
    private bool Busy => scanCancellation != null || installing || preparing;
    public MainController(bool background, SourceStore? sourceStore = null, StartupService? startupService = null, ICommandRunner? commandRunner = null, Action<string, string>? notificationSink = null, NotificationSettingsStore? notificationStore = null)
    {
        backgroundRequested = background;
        store = sourceStore ?? new SourceStore(Program.DataPath("quellen.json")); startup = startupService ?? new StartupService(); runner = commandRunner ?? new WingetRunner();
        notificationSettings = notificationStore ?? new NotificationSettingsStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.FilePath))!, "hinweise.json"));
        mutedPrograms = notificationSettings.Load();
        sources = store.Load(); scanner = new Scanner(runner, network);
        startup.MigrateLegacy(); startupEnabled = startup.Enabled;
        Window = Ui.Load("MainWindow.xaml");
        tray = new Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Information.Clone(), Text = "Revora 0.1.0 Beta", Visible = startupEnabled };
        notify = notificationSink ?? ((title, message) => tray.ShowBalloonTip(10000, title, message, Forms.ToolTipIcon.Info));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Revora öffnen", null, (_, _) => Window.Dispatcher.Invoke(OpenWindow));
        menu.Items.Add("Nach Updates suchen", null, async (_, _) => await SafeAsync(() => ScanAsync()));
        menu.Items.Add("Beenden", null, (_, _) => { if (installing) { Error("Bitte warte, bis die laufende Installation fertig ist."); return; } exiting = true; Window.Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => OpenWindow();
        tray.BalloonTipClicked += (_, _) => OpenWindow();
        var option = Window.C<CheckBox>("StartupOption"); option.IsChecked = startupEnabled;
        void ToggleStartup(object sender, RoutedEventArgs e)
        {
            if (updatingStartup) return;
            try { startup.Set(option.IsChecked == true); startupEnabled = option.IsChecked == true; }
            catch (Exception ex) { updatingStartup = true; option.IsChecked = startupEnabled; updatingStartup = false; Error(ex); }
            UpdateStartup();
        }
        option.Checked += ToggleStartup; option.Unchecked += ToggleStartup;
        CheckButton.Click += async (_, _) => await SafeAsync(() => ScanAsync());
        InstallButton.Click += async (_, _) => await SafeAsync(InstallSelectedAsync);
        Window.C<Button>("AddSource").Click += async (_, _) => await SafeAsync(() => EditSourceAsync(""));
        Window.C<TextBox>("SearchBox").TextChanged += (_, _) => Filter();
        Window.C<CheckBox>("OnlyUpdates").Checked += (_, _) => Filter(); Window.C<CheckBox>("OnlyUpdates").Unchecked += (_, _) => Filter();
        Window.C<Button>("ClearSelection").Click += (_, _) => { foreach (var p in programs) p.Selected = false; UpdateActions(); };
        Window.C<Button>("SelectVisible").Click += (_, _) => { foreach (var p in visible.Where(x => x.CanInstall && x.HasUpdate && !x.Manual && !x.NotificationsMuted)) p.Selected = true; UpdateActions(); };
        Ui.RouteRowButtons(Window, async button => await SafeAsync(async () => { var key = Convert.ToString(button.Tag) ?? ""; if (button.Name == "OpenInstallFolderButton") OpenFolder(key); else if (button.Name == "MuteUpdatesButton") ToggleUpdateHints(key); else await EditSourceAsync(key); }));
        Window.Closing += (_, e) =>
        {
            if (exiting) return;
            if (startupEnabled) { e.Cancel = true; Window.Hide(); }
            else if (installing) { e.Cancel = true; Error("Es wird gerade ein Programm installiert. Bitte warte, bis es fertig ist. Das Fenster bleibt so lange geöffnet."); }
        };
        Window.ContentRendered += async (_, _) => await FirstRenderAsync(background);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) => { if (hasScanned && !Busy && DateTime.UtcNow - scanAttempt >= TimeSpan.FromHours(IntervalHours)) await SafeAsync(() => ScanAsync()); };
        timer.Start(); UpdateStartup(); UpdateActions();
    }
    internal Task StartAsync()
    {
        if (backgroundRequested && startupEnabled)
        {
            // Never show the window on Windows login, even briefly.
            Window.ShowInTaskbar = false;
            return FirstRenderAsync(true);
        }
        Window.Show();
        return Task.CompletedTask;
    }
    private void OpenWindow()
    {
        Window.ShowInTaskbar = true;
        Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate();
    }
    internal async Task FirstRenderAsync(bool background)
    {
        if (initialRender || disposed) return;
        initialRender = true;
        if (background && startupEnabled) Window.Hide();
        await SafeAsync(() => ScanAsync());
    }
    private async Task SafeAsync(Func<Task> action) { try { await action(); } catch (OperationCanceledException) { if (!disposed) { Status.Text = "Vorgang abgebrochen"; Summary.Text = "Die Suche oder Vorbereitung wurde beendet. Es wird kein weiteres Programm installiert."; } } catch (Exception ex) { if (!disposed) { Status.Text = "Vorgang nicht abgeschlossen"; Summary.Text = UserText.Explain(ex); Error(ex); } } }
    private void Error(string message)
    {
        if (Window.IsVisible) Ui.Message(Window, message);
        else { Status.Text = "Vorgang nicht abgeschlossen"; Summary.Text = message; }
    }
    private void Error(Exception error)
    {
        if (Window.IsVisible) Ui.Message(Window, UserText.Explain(error), error is UserMessageException user ? user.Details : error.ToString(), "Vorgang nicht abgeschlossen");
        else { Status.Text = "Vorgang nicht abgeschlossen"; Summary.Text = UserText.Explain(error); Window.C<TextBox>("DiagnosticReport").Text += "\n" + error; }
    }
    private void UpdateStartup()
    {
        tray.Visible = startupEnabled;
        Window.C<TextBlock>("StartupHint").Text = startupEnabled ? "Startet beim Anmelden im Hintergrund und meldet neue Updates." : "Ausgeschaltet. Revora startet nur, wenn du es öffnest.";
        Window.C<TextBlock>("FooterText").Text = startupEnabled ? "Schließen lässt Revora im Hintergrund weiterlaufen. Die Suche erfolgt alle 4 Stunden. Beenden: Rechtsklick auf das Symbol neben der Uhr → Beenden." : "Schließen beendet Revora. Solange das Fenster offen ist, wird nach der ersten Suche alle 4 Stunden erneut geprüft.";
    }
    private void UpdateActions()
    {
        CheckButton.IsEnabled = !Busy; List.IsEnabled = !Busy && scanValid;
        foreach (var name in new[] { "AddSource", "SelectVisible", "ClearSelection", "OnlyUpdates", "SearchBox" }) Window.C<Control>(name).IsEnabled = !Busy && hasScanned && scanValid;
        int count = programs.Count(x => x.Selected), hidden = count - visible.Count(x => x.Selected);
        Window.C<TextBlock>("SelectionText").Text = !hasScanned ? "Nichts ausgewählt" : $"{count} ausgewählt" + (hidden > 0 ? $" · davon {hidden} momentan nicht in der Liste sichtbar" : "");
        InstallButton.Content = count == 0 ? "Auswahl prüfen" : $"Auswahl prüfen ({count})";
        InstallButton.IsEnabled = count > 0 && !Busy && scanValid;
        Window.C<System.Windows.Controls.Border>("ActionBar").Visibility = hasScanned ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Filter()
    {
        var query = Window.C<TextBox>("SearchBox").Text.Trim(); bool only = Window.C<CheckBox>("OnlyUpdates").IsChecked == true;
        visible = programs.Where(x => (!only || (x.HasUpdate && !x.NotificationsMuted)) && (query.Length == 0 || (x.Name + " " + x.Id + " " + x.SourceLabel).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        List.ItemsSource = visible;
        Window.C<TextBlock>("EmptyText").Text = only && query.Length == 0 ? "Keine Updatehinweise. Schalte „Nur Updates“ aus, um alle Programme und ausgeblendete Hinweise zu sehen." : "Keine passenden Programme.";
        Window.C<TextBlock>("EmptyText").Visibility = visible.Count > 0 ? Visibility.Collapsed : Visibility.Visible; UpdateActions();
    }
    private string UpdateSignature() => string.Join(";", programs.Where(x => x.HasUpdate && !x.NotificationsMuted).OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Version));
    private void UpdateScanCount()
    {
        int count = programs.Count(x => x.HasUpdate && !x.NotificationsMuted), muted = programs.Count(x => x.HasUpdate && x.NotificationsMuted);
        Status.Text = $"{UserText.Count(programs.Count, "Programm", "Programme")} gefunden · {UserText.Count(count, "Updatehinweis", "Updatehinweise")}" + (muted > 0 ? $" · {muted} ausgeblendet" : "");
    }
    internal void ToggleUpdateHints(string key)
    {
        if (Busy || !scanValid) return;
        var program = programs.FirstOrDefault(x => x.Key == key) ?? throw new UserMessageException("Dieses Programm ist nicht mehr in der Liste. Suche bitte erneut nach Updates.");
        var next = new HashSet<string>(mutedPrograms, StringComparer.OrdinalIgnoreCase);
        bool muted = !program.NotificationsMuted;
        if (muted) next.Add(key); else next.Remove(key);
        notificationSettings.Save(next);
        mutedPrograms = next; program.NotificationsMuted = muted;
        if (muted) program.Selected = false;
        Filter(); UpdateScanCount(); lastNotification = UpdateSignature();
        Summary.Text = muted ? $"Hinweise für {program.Name} sind ausgeschaltet. Schalte „Nur Updates“ aus, um sie wieder einzuschalten." : $"Hinweise für {program.Name} sind wieder eingeschaltet.";
    }
    internal async Task ScanAsync(bool acceptSources = false)
    {
        if (Busy) return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10)); scanCancellation = cancel; scanValid = false; scanAttempt = DateTime.UtcNow;
        foreach (var p in programs) p.Selected = false;
        Status.Text = "Deine Programme werden geprüft …"; Summary.Text = "Installierte Programme und verfügbare Updates werden gesucht. Es wird nichts installiert."; Window.C<ProgressBar>("ScanProgress").Visibility = Visibility.Visible; UpdateActions();
        ScanResult? data = null;
        try
        {
            data = await scanner.ScanAsync(sources, cancel.Token, acceptSources);
            if (disposed) return;
            programs = data.Programs; hasScanned = scanValid = true; scanFinished = DateTime.UtcNow;
            foreach (var p in programs) { p.NotificationsMuted = mutedPrograms.Contains(p.Key); p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProgramItem.Selected)) UpdateActions(); }; }
            Window.C<TextBox>("DiagnosticReport").Text = data.Report;
            int count = programs.Count(x => x.HasUpdate && !x.NotificationsMuted); UpdateScanCount();
            Summary.Text = data.Error.Length == 0 ?  "Zuletzt geprüft um " + DateTime.Now.ToString("HH:mm") + ". Hake die Programme an, die du aktualisieren möchtest." : "Die Updatesuche war nicht vollständig. Du siehst die gefundenen Programme, aber möglicherweise fehlen Updates. Mehr dazu unter „Suchbericht“.";
            Window.C<Border>("ProgramsCard").Visibility = Visibility.Visible; Window.C<TextBlock>("HeroTitle").Text = "Deine Programme"; CheckButton.Content = "Erneut suchen"; CheckButton.Style = (Style)Window.FindResource(typeof(Button)); Filter();
            var signature = UpdateSignature();
            if (startupEnabled && !Window.IsVisible && signature.Length > 0 && signature != lastNotification) notify("Revora · Updates verfügbar", $"{UserText.Count(count, "Updatehinweis", "Updatehinweise")}. Hier klicken, um die Programme auszuwählen.");
            lastNotification = signature;
        }
        finally { scanCancellation = null; if (!disposed) { Window.C<ProgressBar>("ScanProgress").Visibility = Visibility.Collapsed; UpdateActions(); } }
        if (data != null && !acceptSources && data.Error.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(data.Report, "agreements|Vereinbarungen|Verträge", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (!Window.IsVisible)
            {
                Status.Text = "Zustimmung für die Suche erforderlich";
                Summary.Text = "Klicke auf „Erneut suchen“, um die Bedingungen des Updatekatalogs zu prüfen. Es wurde nichts installiert.";
                return;
            }
            var dialog = Ui.Review(Window, "Zustimmung für die Updatesuche", "Für die Suche verwenden wir den Windows-Updatekatalog für Programme (WinGet). Bitte lies die Bedingungen und entscheide, ob du ihn verwenden möchtest.", new[] { new ReviewItem("Bedingungen für den Updatekatalog", "Öffne „Bedingungen des Anbieters lesen“, bevor du zustimmst. Wenn du abbrichst, wird diese Updatesuche noch nicht freigeschaltet.", data.Report, "Bedingungen des Anbieters lesen") }, "Akzeptieren und erneut suchen", "Ich habe die angezeigten Bedingungen gelesen und akzeptiere sie.");
            if (dialog.ShowDialog() == true) await ScanAsync(true);
        }
    }
    private void OpenFolder(string key)
    {
        var p = programs.FirstOrDefault(x => x.Key == key) ?? throw new InvalidOperationException("Dieses Programm ist nicht mehr in der Liste. Bitte suche erneut nach Updates.");
        var dialog = Ui.Load("LocationsWindow.xaml"); Ui.Owner(dialog, Window); dialog.C<TextBlock>("LocationProgram").Text = p.Name;
        var note = dialog.C<TextBlock>("LocationNote"); note.Text = p.InstallationsNotice.Length > 0 ? p.InstallationsNotice : "Wähle eine Version, um ihren Ordner zu öffnen.";
        if (p.Installations.Count == 0) note.Text = "Der Updater hat keinen passenden Programmordner gefunden. Wenn du weißt, wo er liegt, klicke auf „Anderen Ordner wählen“.";
        var choices = dialog.C<ListBox>("LocationChoices"); choices.ItemsSource = p.Installations;
        var open = dialog.C<Button>("OpenLocation"); choices.SelectionChanged += (_, _) => open.IsEnabled = choices.SelectedItem is Installation i && i.Path.Length > 0;
        void Open(string path)
        {
            try { if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") || !Directory.Exists(path)) throw new UserMessageException("Dieser Ordner wurde nicht gefunden. Vielleicht wurde das Programm verschoben oder entfernt. Suche erneut nach Updates oder wähle den Ordner selbst aus."); var full = Path.GetFullPath(path); var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false }; start.ArgumentList.Add(full); Process.Start(start)?.Dispose(); note.Text = "Im Explorer geöffnet: " + full; }
            catch (Exception ex) { note.Text = UserText.Explain(ex); }
        }
        open.Click += (_, _) => { if (choices.SelectedItem is Installation i) Open(i.Path); };
        dialog.C<Button>("BrowseLocation").Click += (_, _) => { using var picker = new Forms.FolderBrowserDialog { Description = "Programmordner selbst auswählen", UseDescriptionForTitle = true, ShowNewFolderButton = false }; if (picker.ShowDialog() == Forms.DialogResult.OK) Open(picker.SelectedPath); };
        dialog.C<Button>("CancelLocation").Click += (_, _) => dialog.Close(); dialog.ShowDialog();
    }
    public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); scanCancellation?.Cancel(); tray.Visible = false; var trayIcon = tray.Icon; tray.Dispose(); trayIcon?.Dispose(); network.Dispose(); }
}
