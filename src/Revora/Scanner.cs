using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Revora;

public sealed record WingetRow(string Key, string Name, string Id, string Installed, string Version);
public sealed class Scanner
{
    private readonly ICommandRunner runner;
    private readonly NetworkService network;
    public Scanner(ICommandRunner runner, NetworkService network) { this.runner = runner; this.network = network; }
    public static List<WingetRow> ParseTable(string text)
    {
        var rows = new List<WingetRow>(); var lines = Regex.Replace(text, @"\x1b\[[0-9;?]*[A-Za-z]", "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        for (int n = 1; n < lines.Length; n++)
        {
            if (!Regex.IsMatch(lines[n], @"^\s*-{5,}\s*$")) continue;
            var header = Regex.Matches(lines[n - 1], @"\S+"); if (header.Count < 3 || header.Count > 5) continue;
            var starts = header.Cast<Match>().Select(m => m.Index).ToArray();
            bool hasAvailable = header.Count == 5 || (header.Count == 4 && !Regex.IsMatch(header[3].Value, "^(Source|Quelle)$", RegexOptions.IgnoreCase));
            foreach (var line in lines.Skip(n + 1))
            {
                if (line.Length <= starts[2]) continue;
                var cells = new List<string>();
                for (int c = 0; c < starts.Length; c++) cells.Add(starts[c] >= line.Length ? "" : line.Substring(starts[c], Math.Min(c + 1 < starts.Length ? starts[c + 1] - starts[c] : line.Length - starts[c], line.Length - starts[c])).Trim());
                if (cells[0].Length == 0 || cells[2].Length == 0 || !Regex.IsMatch(cells[1], @"^[A-Za-z0-9][A-Za-z0-9._+-]{1,199}$") || cells[1].Contains("...")) continue;
                rows.Add(new WingetRow("winget:" + cells[1], cells[0], cells[1], cells[2], hasAvailable ? cells[3] : ""));
            }
            break;
        }
        return rows;
    }
    public static string VersionState(string installed, string available)
    {
        if (available.Length == 0) return "Manual"; if (installed.Length == 0) return "Unknown"; if (installed == available) return "Current";
        if (System.Version.TryParse(installed, out var iv) && System.Version.TryParse(available, out var av)) return av > iv ? "Newer" : "Older";
        return "Different";
    }
    public async Task<ScanResult> ScanAsync(List<Source> sources, CancellationToken token, bool acceptSources = false, List<Installation>? fixtureRegistry = null)
    {
        var registry = fixtureRegistry ?? await Task.Run(RegistryInventory.Read, token).ConfigureAwait(false);
        var rows = new List<WingetRow>(); string report = "", error = "";
        try
        {
            var args = new List<string> { "list", "--source", "winget", "--disable-interactivity" }; if (acceptSources) args.Add("--accept-source-agreements");
            var result = await runner.RunAsync(args, token, TimeSpan.FromMinutes(5)).ConfigureAwait(false); report = result.Text;
            if (result.Code != 0) throw new UserMessageException($"Die Suche im Updatekatalog ist fehlgeschlagen. Bitte prüfe die Internetverbindung und versuche es erneut. Fehlernummer für Rückfragen: {result.Code}.");
            rows = ParseTable(report);
            if (rows.Count == 0 && Regex.IsMatch(report, "-{5,}")) throw new InvalidDataException("Die Antwort des Updatekatalogs konnte nicht gelesen werden. Deshalb werden keine Updates daraus angeboten. Bitte versuche die Suche später erneut.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { error = ex.Message; report += "\n" + error; }
        var steamNames = registry.Where(x => x.IsSteam).Select(x => x.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsSteamName(string name)
        {
            if (steamNames.Contains(name.Trim())) return true;
            if (!name.Contains('…')) return false; var prefix = name.Split('…')[0]; if (prefix.Length < 12) return false;
            var matches = registry.Where(x => x.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList(); return matches.Count > 0 && matches.All(x => x.IsSteam);
        }
        rows = rows.Where(x => !IsSteamName(x.Name)).ToList();
        var inventory = rows.ToList(); var names = rows.Select(x => x.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in registry.Where(x => !x.IsSteam && !names.Contains(x.Name.Trim()))) inventory.Add(new WingetRow(r.RegistryKey, r.Name, "", r.Version, ""));
        foreach (var s in sources.Where(x => x.Key.StartsWith("manual:", StringComparison.Ordinal) && !IsSteamName(x.Name))) inventory.Add(new WingetRow(s.Key, s.Name, "", s.InstalledVersion, ""));
        var programs = new List<ProgramItem>();
        foreach (var group in inventory.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested(); var first = group.First(); var installed = string.Join(" / ", group.Select(x => x.Installed).Distinct().Order());
            var versions = group.Select(x => x.Version).Where(x => x.Length > 0).Distinct().ToList(); var version = versions.Count == 1 ? versions[0] : "";
            var model = new ProgramItem { Key = first.Key, Name = first.Name, Id = first.Id, Installed = installed, Version = version, SourceLabel = "Noch kein Downloadlink hinterlegt", Status = "Noch keine Updatesuche möglich. Trage über „Quelle ändern“ einen passenden Anbieterlink ein.", StatusColor = "#EDBD91" };
            var locations = new List<Installation>();
            foreach (var r in registry.Where(x => !x.IsSteam))
            {
                bool exact = first.Id.Length == 0 ? r.RegistryKey == first.Key : group.Any(x => string.Equals(r.Name.Trim(), x.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                bool partial = !exact && first.Id.Length > 0 && group.Any(x => x.Name.Contains('…') && x.Name.Split('…')[0].Length >= 12 && r.Name.StartsWith(x.Name.Split('…')[0], StringComparison.OrdinalIgnoreCase));
                if (exact || partial) locations.Add(new Installation { Name = r.Name, Version = r.Version, Publisher = r.Publisher, Path = r.Path, Origin = r.Origin, Match = partial ? "Dieser Ordner könnte zum Programm gehören. Bitte prüfe Name und Version." : "In den Windows-Programminformationen gefunden" });
            }
            model.Installations = locations.DistinctBy(x => x.Name + "|" + x.Version + "|" + x.Path).ToList();
            var installedVersions = group.Select(x => x.Installed).Where(x => x.Length > 0).Distinct().ToList();
            model.InstallationsNotice = installedVersions.Count > 1 ? "Mehrere Versionen auf deinem PC: " + string.Join(" / ", installedVersions) + ". Wähle selbst, welche Version du ansehen möchtest. Beim Update wählst du den Download; das Installationsprogramm zeigt dir die weiteren Schritte." : model.Installations.Count > 1 ? "Es wurden mehrere mögliche Programmordner gefunden. Bitte wähle den passenden Ordner selbst aus." : "";
            var source = sources.FirstOrDefault(x => string.Equals(x.Key, first.Key, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (source?.Mode == "Download")
                {
                    var resolved = await network.ResolveAsync(source, true, token).ConfigureAwait(false);
                    model.Mode = "Download"; model.Url = resolved.Url; model.FinalUrl = resolved.FinalUrl; model.Sha256 = resolved.Sha256; model.Version = resolved.Version;
                    model.SourceLabel = "Dein Downloadlink · " + new Uri(resolved.FinalUrl).Host;
                    var state = VersionState(model.Installed, model.Version);
                    switch (state)
                    {
                        case "Newer": model.Status = "Update verfügbar"; model.HasUpdate = model.CanInstall = true; break;
                        case "Current": model.Status = "Die angebotene Version ist bereits auf deinem PC."; break;
                        case "Older": model.Status = "Der Download enthält eine ältere Version als deine. Sie wird nicht installiert."; break;
                        case "Manual": model.Status = "Du kannst diese Datei installieren. Ob sie neuer ist als dein Programm, musst du selbst prüfen."; model.Manual = model.CanInstall = true; break;
                        case "Unknown": model.Status = "Deine installierte Version ist nicht bekannt. Bitte vergleiche sie selbst mit dem angebotenen Download."; model.Manual = model.CanInstall = true; break;
                        default: model.Status = "Die Versionsnummern lassen sich nicht sicher vergleichen. Bitte prüfe selbst, ob du dieses Update brauchst."; model.Manual = model.CanInstall = model.HasUpdate = true; break;
                    }
                }
                else
                {
                    var package = source?.Mode == "Winget" ? source.PackageId : first.Id;
                    if (package.Length > 0)
                    {
                        Validate.Package(package); model.Mode = "Winget"; model.PackageId = package; model.SourceLabel = "Automatisch gefundener Download";
                        if (source?.Mode == "Winget") model.Version = rows.FirstOrDefault(x => x.Id == package)?.Version ?? "";
                        model.CanInstall = model.HasUpdate = model.Version.Length > 0; model.Status = model.CanInstall ? "Update verfügbar" : "Im Updatekatalog wurde keine neuere Version gefunden.";
                        if (group.Count() > 1) { model.Mode = "ChooseDownload"; model.CanInstall = model.Manual = true; model.Status = "Mehrere Versionen gefunden. Du kannst dieses Programm anhaken und anschließend einen Download wählen. Der Updater entfernt dabei keine alte Version."; }
                    }
                }
                model.StatusColor = model.Manual ? "#EDBD91" : model.HasUpdate ? "#B7E5CB" : "#A3BAD6";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { model.CanInstall = model.HasUpdate = false; model.Status = "Der Downloadlink konnte nicht geprüft werden. " + UserText.Explain(ex); model.SourceLabel = "Dein Downloadlink ist zurzeit nicht nutzbar."; model.StatusColor = "#EDBD91"; }
            programs.Add(model);
        }
        report += $"\nSteam-Filter: {steamNames.Count} registrierte Steam-Titel werden ignoriert. Steam selbst bleibt enthalten.";
        return new ScanResult(programs.OrderByDescending(x => x.HasUpdate).ThenBy(x => x.Name).ToList(), report, error);
    }
    public static List<InstallPlan> Plan(IEnumerable<ProgramItem> programs)
    {
        var plans = new List<InstallPlan>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in programs.Where(x => x.Selected))
        {
            if (!p.CanInstall) throw new UserMessageException($"'{p.Name}' kann gerade nicht installiert werden. Bitte suche erneut nach Updates.");
            string key;
            if (p.Mode is "Winget" or "ChooseDownload") { Validate.Package(p.PackageId); if (p.Mode == "Winget" || p.Version.Length > 0) Validate.Version(p.Version); key = "winget:" + p.PackageId; }
            else if (p.Mode == "Download") { Validate.InstallerExtension(p.Url); Validate.Https(p.FinalUrl); Validate.HashValue(p.Sha256); key = p.Key; }
            else throw new UserMessageException("Für diese Auswahl fehlt ein nutzbarer Downloadlink. Bitte suche erneut nach Updates.");
            if (!seen.Add(key)) throw new UserMessageException("Zwei deiner Einträge führen zum gleichen Update. Bitte hake nur einen davon an.");
            plans.Add(new InstallPlan { Key = p.Key, Name = p.Name, Mode = p.Mode, PackageId = p.PackageId, Installed = p.Installed, Version = p.Version, Url = p.Url, FinalUrl = p.FinalUrl, Sha256 = p.Sha256, Manual = p.Manual });
        }
        return plans;
    }
    public async Task<(List<DownloadChoice> Choices, string Report)> DownloadChoicesAsync(InstallPlan plan, CancellationToken token)
    {
        Validate.Package(plan.PackageId); if (plan.Version.Length > 0) Validate.Version(plan.Version);
        var suffix = Regex.Match(plan.PackageId, @"[._-](x64|x86|arm64)$", RegexOptions.IgnoreCase);
        var arches = suffix.Success ? new[] { suffix.Groups[1].Value.ToLowerInvariant() } : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? new[] { "arm64", "x64", "x86" } : new[] { "x64", "x86" };
        var choices = new List<DownloadChoice>(); string report = "";
        foreach (var arch in arches)
        {
            var args = new List<string> { "show", "--id", plan.PackageId, "--exact", "--source", "winget", "--architecture", arch, "--disable-interactivity" }; if (plan.Version.Length > 0) args.AddRange(new[] { "--version", plan.Version });
            var result = await runner.RunAsync(args, token, TimeSpan.FromMinutes(1)).ConfigureAwait(false); report += $"\nWinGet-Auswahl für {arch}\n{result.Text}"; if (result.Code != 0) continue;
            var url = Regex.Match(result.Text, @"^\s*(?:Installer\s+Url|Download\s+Url|Installer-URL|Installations-URL)\s*:\s*(https://\S+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var hash = Regex.Match(result.Text, @"^\s*[^\r\n:]*SHA256\s*:\s*([a-f0-9]{64})\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!url.Success || !hash.Success || choices.Any(x => x.Url == url.Groups[1].Value)) continue;
            try { Validate.InstallerExtension(url.Groups[1].Value); } catch (InvalidDataException) { continue; }
            var version = plan.Version.Length > 0 ? plan.Version : Regex.Match(result.Text, @"^Version\s*:\s*(\S+)", RegexOptions.Multiline).Groups[1].Value;
            choices.Add(new DownloadChoice { Name = UserText.Architecture(arch), Url = url.Groups[1].Value, Sha256 = hash.Groups[1].Value, Version = version });
        }
        return (choices, report);
    }
}
