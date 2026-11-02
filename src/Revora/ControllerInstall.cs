using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Revora;

public sealed partial class MainController
{
    private async Task InstallSelectedAsync()
    {
        if (Busy || !scanValid) return;
        if (DateTime.UtcNow - scanFinished > TimeSpan.FromMinutes(30)) throw new UserMessageException("Die letzte Suche ist länger als 30 Minuten her. Suche bitte erneut nach Updates, bevor du Programme aktualisierst.");
        var plan = Scanner.Plan(programs); if (plan.Count == 0) return;
        preparing = true; UpdateActions();
        try
        {
            foreach (var p in plan.Where(x => x.Mode == "ChooseDownload")) if (!await ChooseDownloadAsync(p)) { Status.Text = "Vorbereitung abgebrochen"; Summary.Text = "Es wurde noch keine Installationsdatei heruntergeladen oder gestartet. Du kannst deine Auswahl ändern und es erneut versuchen."; return; }
            var review = new List<ReviewItem>();
            foreach (var p in plan)
            {
                string detail = $"Auf deinem PC: {UserText.Version(p.Installed)}\nVersion zum Installieren: {UserText.Version(p.Version, "wird vom Anbieter nicht angegeben")}";
                detail += p.Mode == "Winget" ? "\nDer Download wird über den Updatekatalog gefunden." : "\nDownloadadresse: " + p.FinalUrl;
                string technical = p.Mode == "Winget" ? $"WinGet-ID: {p.PackageId}\nZielversion: {p.Version}" : $"Downloadadresse: {p.FinalUrl}\nSHA256: {p.Sha256}";
                if (p.Manual) detail += "\nBitte selbst prüfen: Es ist nicht sicher, ob diese Datei neuer ist als dein installiertes Programm und zur richtigen Version gehört.";
                if (p.Mode == "Winget")
                {
                    Status.Text = "Angaben des Anbieters werden geladen …";
                    var metadata = await runner.RunAsync(new[] { "show", "--id", p.PackageId, "--exact", "--version", p.Version, "--source", "winget", "--disable-interactivity" }, default, TimeSpan.FromMinutes(1));
                    technical += "\n\nAngaben des Anbieters und Nutzungsbedingungen:\n" + metadata.Text;
                    if (metadata.Code != 0) throw new UserMessageException("Die Angaben zum Download konnten nicht geladen werden. Bitte prüfe deine Internetverbindung und suche erneut nach Updates.", metadata.Text);
                    var address = System.Text.RegularExpressions.Regex.Match(metadata.Text, @"^\s*(?:Installer\s+Url|Download\s+Url|Installer-URL|Installations-URL)\s*:\s*(https://\S+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (address.Success) detail += "\nVom Anbieter gemeldete Downloadadresse: " + address.Groups[1].Value;
                }
                review.Add(new ReviewItem(p.Name, detail, technical, p.Mode == "Winget" ? "Angaben des Anbieters und Nutzungsbedingungen" : "Dateiprüfung und technische Angaben"));
            }
            string ack = "Ich möchte die aufgeführten Programme installieren. Ich habe die Downloadadressen und Anbieterangaben geprüft und akzeptiere die angezeigten Nutzungsbedingungen. Der Updater darf diese Bedingungen für mich bestätigen.";
            if (Ui.Review(Window, plan.Count == 1 ? "Dieses Programm wird aktualisiert" : "Diese Programme werden aktualisiert", $"Du hast {UserText.Count(plan.Count, "Programm", "Programme")} ausgewählt. Mit „Updates starten“ wird deine Auswahl heruntergeladen und installiert. Speichere vorher deine Arbeit. Ein Installationsprogramm kann zusätzliche benötigte Komponenten mitbringen. Wenn ein Fehler auftritt, werden weitere ausgewählte Updates nicht gestartet. Mit „Abbrechen“ wird noch nichts installiert.", review, "Updates starten", ack).ShowDialog() != true) { Status.Text = "Updates nicht gestartet"; Summary.Text = "Du hast abgebrochen. Es wurde noch nichts installiert oder entfernt. Du kannst deine Auswahl ändern."; return; }
            foreach (var p in plan) p.AgreementsAccepted = true;
        }
        finally { preparing = false; UpdateActions(); }
        installing = true; UpdateActions(); var outcomes = new List<Outcome>();
        try
        {
            for (int index = 0; index < plan.Count; index++)
            {
                var p = plan[index]; Status.Text = $"Schritt {index + 1} von {plan.Count}: {p.Name}";
                try
                {
                    if (p.Mode == "Winget")
                    {
                        Summary.Text = "Dein ausgewähltes Update wird installiert. Falls sich ein Installationsfenster öffnet, folge bitte den Hinweisen dort.";
                        var result = await RunWorkerAsync(p);
                        if (!result.Success && result.Reason == "InstallerTypeChanged")
                        {
                            if (!ConfirmMigration(p)) throw new UserMessageException("Du hast das Ersetzen der alten Version abgebrochen. Sie wurde nicht entfernt. Die übrigen Updates wurden ebenfalls noch nicht gestartet.", result.Details);
                            p.Migration = true; result = await RunWorkerAsync(p);
                        }
                        if (!result.Success) throw new UserMessageException(result.Message, result.Details + "\nProtokolldatei: " + result.Log);
                        outcomes.Add(new Outcome(p.Name, "Installationsprogramm ist fertig", "Das Installationsprogramm meldet Erfolg. Der Updater prüft gleich, welche Version jetzt auf deinem PC ist.", result.Details));
                    }
                    else
                    {
                        Summary.Text = "Die Datei wird heruntergeladen und auf Echtheit geprüft. Anschließend entscheidest du noch einmal, ob das Installationsprogramm gestartet werden soll.";
                        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                        var file = await network.DownloadAsync(p, Program.DataPath("Downloads"), cancellation.Token);
                        if (!ConfirmInstaller(p, file)) { outcomes.Add(new Outcome(p.Name, "Nicht gestartet", "Du hast den Start nach dem Download abgebrochen. Die Datei wurde nicht ausgeführt.")); continue; }
                        if (await NetworkService.FileHashAsync(file.Path) != file.Hash) throw new UserMessageException("Die Datei hat sich nach der Prüfung verändert. Sie wird deshalb nicht gestartet. Bitte lade das Update erneut herunter.");
                        Summary.Text = "Das Installationsprogramm ist geöffnet. Folge seinen Hinweisen und warte, bis es fertig ist.";
                        using var process = StartInstaller(file.Path); await process.WaitForExitAsync(); int code = process.ExitCode;
                        if (code != 0 && code != 3010) throw new UserMessageException("Das Installationsprogramm hat keinen erfolgreichen Abschluss gemeldet. Prüfe seine Hinweise und ob sich das Programm noch öffnen lässt.", $"Rückgabecode des Installationsprogramms: {code}");
                        bool success = MessageBox.Show(Window, $"Ist die Installation von „{p.Name}“ vollständig fertig und erfolgreich? Warte bitte, bis auch weitere Installationsfenster abgeschlossen sind. Wähle „Nein“, wenn ein Fehler aufgetreten ist oder du unsicher bist.", "Hat die Installation funktioniert?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
                        if (success && p.Key.StartsWith("manual:", StringComparison.Ordinal))
                        {
                            string value = Ui.Prompt(Window, "Welche Version ist jetzt installiert?", "Falls du die Versionsnummer kennst, trage sie hier ein. Sie steht oft im Programm unter „Hilfe“ oder „Über“. Leer lassen behält deinen bisherigen Eintrag.", p.Version);
                            if (value.Length > 0)
                            {
                                var next = sources.Select(x => Json.Read<Source>(Json.Write(x))).ToList(); var entry = next.FirstOrDefault(x => x.Key == p.Key);
                                if (entry != null) { entry.InstalledVersion = value; store.Save(next); sources = next; }
                            }
                        }
                        outcomes.Add(new Outcome(p.Name, success ? "Du hast den Erfolg bestätigt" : "Erfolg noch nicht bestätigt", success ? "Du hast bestätigt, dass die Installation fertig ist. Der Updater prüft gleich noch einmal die gefundene Version." : "Du hast die Installation nicht als erfolgreich bestätigt. Bitte prüfe das Programm und die Hinweise im Installationsfenster."));
                    }
                }
                catch (Exception ex)
                {
                    outcomes.Add(new Outcome(p.Name, "Nicht abgeschlossen", UserText.Explain(ex), ex is UserMessageException user ? user.Details : ex.ToString()));
                    outcomes.AddRange(plan.Skip(index + 1).Select(x => new Outcome(x.Name, "Noch nicht gestartet", "Ein vorheriges Update ist fehlgeschlagen. Dieses Programm wurde deshalb noch nicht aktualisiert."))); break;
                }
            }
        }
        finally { installing = false; UpdateActions(); }
        Results(outcomes); await ScanAsync();
    }
    private async Task<WorkerResult> RunWorkerAsync(InstallPlan plan)
    {
        var jobs = Program.DataPath("Jobs"); Directory.CreateDirectory(jobs); var id = Guid.NewGuid().ToString(); var planPath = Path.Combine(jobs, id + ".native-plan.json"); var resultPath = Path.Combine(jobs, id + ".native-result.json");
        await File.WriteAllTextAsync(planPath, Json.Write(plan));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true }; start.ArgumentList.Add("--worker"); start.ArgumentList.Add(planPath); start.ArgumentList.Add(resultPath);
        using var process = Process.Start(start) ?? throw new UserMessageException("Der Installationsprozess konnte nicht gestartet werden.");
        await process.WaitForExitAsync();
        if (!File.Exists(resultPath)) throw new UserMessageException("Der Updatevorgang wurde beendet, ohne ein Ergebnis zu melden. Bitte prüfe, ob das Programm noch läuft oder ein Installationsfenster geöffnet ist. Suche danach erneut nach Updates.");
        return Json.Read<WorkerResult>(await File.ReadAllTextAsync(resultPath));
    }
    private static Process StartInstaller(string file)
    {
        ProcessStartInfo start;
        if (Path.GetExtension(file).Equals(".msi", StringComparison.OrdinalIgnoreCase)) { start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe")) { UseShellExecute = true }; start.ArgumentList.Add("/i"); start.ArgumentList.Add(file); start.ArgumentList.Add("REBOOT=ReallySuppress"); }
        else start = new ProcessStartInfo(file) { UseShellExecute = true };
        return Process.Start(start) ?? throw new UserMessageException("Das Installationsprogramm konnte nicht geöffnet werden. Bitte versuche es erneut.");
    }
}
