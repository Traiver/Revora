using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Revora;

public static class Validate
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static void Package(string value) { if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._+-]{1,199}$")) throw new InvalidDataException("Das Programm konnte im Updatekatalog nicht eindeutig zugeordnet werden. Bitte suche erneut nach Updates."); }
    public static void Version(string value) { if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._+~-]{0,99}$")) throw new InvalidDataException("Die angebotene Version konnte nicht eindeutig erkannt werden. Suche erneut nach Updates oder trage einen Downloadlink des Anbieters ein."); }
    public static Uri Https(string value) { if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || value.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new InvalidDataException("Der Link muss vollständig sein, mit https:// beginnen und darf keinen Benutzernamen oder Passwort enthalten."); return uri; }
    public static void HashValue(string value) { if (value.Length > 0 && !Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("Der Datei-Prüfcode muss genau 64 Zeichen (0–9 und a–f) enthalten. Kopiere ihn vollständig vom Anbieter oder lasse das optionale Feld leer."); }
    public static string InstallerExtension(string url) { var ext = Path.GetExtension(Https(url).AbsolutePath).ToLowerInvariant(); if (ext != ".exe" && ext != ".msi") throw new InvalidDataException("Dieser Link führt nicht direkt zu einer Installationsdatei. Bitte verwende den Anbieterlink zur .exe- oder .msi-Datei, keine normale Webseite."); return ext; }
}

public sealed class SourceStore
{
    public string FilePath { get; }
    public SourceStore(string filePath) => FilePath = filePath;
    public List<Source> Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var text = File.ReadAllText(FilePath).TrimStart('\uFEFF');
            using var doc = JsonDocument.Parse(text);
            List<Source> sources;
            if (doc.RootElement.ValueKind == JsonValueKind.Array) sources = Json.Read<List<Source>>(text);
            else { var data = Json.Read<SourceDocument>(text); if (data.SchemaVersion != 2) throw new InvalidDataException("Unbekannte Dateiversion."); sources = data.Sources; }
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sources)
            {
                if (s.Key.Length == 0) s.Key = "manual:" + Validate.Hash(s.Name + s.Url);
                if (s.Name.Length == 0 || (s.Mode != "Download" && s.Mode != "Winget") || !keys.Add(s.Key)) throw new InvalidDataException("Ungültige oder doppelte Quellenzuordnung.");
            }
            return sources;
        }
        catch (Exception ex) { throw new UserMessageException("Deine gespeicherten Downloadlinks konnten nicht gelesen werden. Die Datei wurde nicht verändert. Falls du eine Sicherung hast, kannst du sie wiederherstellen. Den Speicherort und die genaue Fehlermeldung findest du in den technischen Details.", $"Datei: {FilePath}\n{ex}"); }
    }
    public void Save(List<Source> sources)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + "." + Guid.NewGuid() + ".tmp";
        try
        {
            File.WriteAllText(temp, Json.Write(new SourceDocument { Sources = sources }), new UTF8Encoding(false));
            _ = new SourceStore(temp).Load();
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak"); else File.Move(temp, FilePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static class RegistryInventory
{
    public static bool IsSteam(string key, string uninstall, string quiet) => Regex.IsMatch(key, @"^Steam App \d+$", RegexOptions.IgnoreCase) || new[] { uninstall, quiet }.Any(x => Regex.IsMatch(x, @"steam://uninstall/\d+|steam\.exe""?\s+[-/]uninstall\s+\d+", RegexOptions.IgnoreCase));
    public static (string Path, string Origin) Folder(string location, string icon)
    {
        var match = Regex.Match(icon, "^\\s*\"([^\"]+)\"");
        icon = match.Success ? match.Groups[1].Value : Regex.Replace(icon, @",\s*-?\d+\s*$", "");
        foreach (var pair in new[] { (location, false), (icon, true) })
        {
            var path = Environment.ExpandEnvironmentVariables(pair.Item1.Trim().Trim('"'));
            if (!Regex.IsMatch(path, @"^[A-Za-z]:[\\/]")) continue;
            try
            {
                path = Path.GetFullPath(path);
                if (pair.Item2)
                {
                    if (!File.Exists(path)) continue;
                    path = Path.GetDirectoryName(path)!;
                    if ((path + "\\").StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (Directory.Exists(path)) return (path, pair.Item2 ? "Aus dem Programmsymbol ermittelt – bitte den Ordner prüfen" : "Von Windows gemeldeter Programmordner");
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException) { }
        }
        return ("", "Windows hat für diese Version keinen Ordner angegeben");
    }
    public static List<Installation> Read()
    {
        var list = new List<Installation>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name); if (key == null) continue;
                    string Get(string p) => Convert.ToString(key.GetValue(p)) ?? "";
                    var display = Get("DisplayName"); if (display.Length == 0 || Get("SystemComponent") == "1" || Get("ParentKeyName").Length > 0) continue;
                    var publisher = Get("Publisher"); var version = Get("DisplayVersion"); var folder = Folder(Get("InstallLocation"), Get("DisplayIcon"));
                    var identity = display.Trim().ToLowerInvariant() + "|" + publisher.Trim().ToLowerInvariant();
                    if (!seen.Add(identity + "|" + version + "|" + folder.Path)) continue;
                    list.Add(new Installation { Name = display, Version = version, Publisher = publisher, Path = folder.Path, Origin = folder.Origin, Match = "Windows-Registrierung", RegistryKey = "registry:" + Validate.Hash(identity), IsSteam = IsSteam(name, Get("UninstallString"), Get("QuietUninstallString")) });
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException) { }
            }
        }
        return list;
    }
}

public interface ICommandRunner { Task<CommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken token = default, TimeSpan? timeout = null); }
public sealed class WingetRunner : ICommandRunner
{
    public bool MirrorConsole { get; set; }
    public static string Locate()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) { var file = Path.Combine(dir.Trim('"'), "winget.exe"); if (File.Exists(file)) return file; }
        throw new UserMessageException("Die Windows-Komponente für Programmupdates fehlt. Öffne den Microsoft Store und installiere „App-Installer“ von Microsoft. Suche danach erneut nach Updates.");
    }
    public async Task<CommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken token = default, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token); if (timeout.HasValue) cts.CancelAfter(timeout.Value);
        var start = new ProcessStartInfo(Locate()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder(); var gate = new object();
        void Append(object sender, DataReceivedEventArgs e) { if (e.Data == null) return; lock (gate) output.AppendLine(e.Data); if (MirrorConsole) Console.WriteLine(e.Data); }
        process.OutputDataReceived += Append; process.ErrorDataReceived += Append;
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); process.WaitForExit(); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        return new CommandResult(process.ExitCode, output.ToString());
    }
}

public sealed class StartupService
{
    public string LinkPath { get; }
    public string ExePath { get; }
    private readonly string[] legacyLinks;
    public StartupService(string? link = null, string? exe = null, string? startupFolder = null)
    {
        var folder = startupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        LinkPath = link ?? Path.Combine(folder, "Revora.lnk"); ExePath = exe ?? Environment.ProcessPath!;
        legacyLinks = link == null ? LegacyCompatibility.StartupLinkNames.Select(name => Path.Combine(folder, name)).ToArray() : Array.Empty<string>();
    }
    private dynamic Link(string path) { var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows kann den automatischen Start gerade nicht einrichten. Bitte versuche es nach einem Neustart erneut."); dynamic shell = Activator.CreateInstance(type)!; return shell.CreateShortcut(path); }
    private string StateOf(string path)
    {
        if (!File.Exists(path)) return "Disabled";
        dynamic link = Link(path); string target = link.TargetPath, args = link.Arguments;
        if (string.Equals(target, ExePath, StringComparison.OrdinalIgnoreCase) && args.Contains("--background", StringComparison.Ordinal)) return "Native";
        if (LegacyCompatibility.IsPreviousStartup(target, args, ExePath)) return "Legacy";
        return "Foreign";
    }
    private string[] OwnedLegacyLinks() => legacyLinks.Where(path => StateOf(path) is "Native" or "Legacy").ToArray();
    public string State() => File.Exists(LinkPath) ? StateOf(LinkPath) : OwnedLegacyLinks().Length > 0 ? "Legacy" : "Disabled";
    public void Set(bool enabled)
    {
        if (State() == "Foreign") throw new UserMessageException("Es gibt bereits einen anderen Eintrag mit diesem Namen für den Windows-Start. Er bleibt unverändert. Die Einstellung konnte deshalb nicht gespeichert werden.");
        var oldLinks = OwnedLegacyLinks();
        if (!enabled) { if (File.Exists(LinkPath)) File.Delete(LinkPath); foreach (var oldLink in oldLinks) File.Delete(oldLink); return; }
        dynamic link = Link(LinkPath); link.TargetPath = ExePath; link.Arguments = "--background"; link.WorkingDirectory = Path.GetDirectoryName(ExePath); link.Save();
        if (StateOf(LinkPath) != "Native") throw new UserMessageException("Der Start mit Windows konnte nicht gespeichert werden. Bitte versuche es erneut.");
        foreach (var oldLink in oldLinks) File.Delete(oldLink);
    }
    public bool Enabled => State() is "Native" or "Legacy";
    public bool MigrateLegacy() { if (State() != "Legacy") return false; Set(true); return true; }
}
