using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Revora;

public sealed record AppRelease(string Tag, string Url, string Hash, long Size);

// No credentials, scripts or third-party updater are required. Only this repository's
// versioned Windows assets are eligible, including betas while Revora is in beta.
public sealed class SelfUpdateService : IDisposable
{
    public const string CurrentTag = "v0.2.0-beta";
    private const long MaxDownload = 250 * 1024 * 1024;
    private static readonly string[] RuntimeLicenses = { "NETCore-LICENSE.txt", "NETCore-THIRD-PARTY-NOTICES.txt", "WindowsDesktop-LICENSE.txt" };
    private readonly HttpClient client;
    public SelfUpdateService(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Revora/0.2.0-beta");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
    public async Task<AppRelease?> CheckAsync(CancellationToken token)
    {
        using var response = await client.GetAsync("https://api.github.com/repos/Traiver/Revora/releases?per_page=100", HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new UserMessageException("Revora-Updates konnten nicht geprüft werden. Versuche es später erneut.", "GitHub: " + (int)response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var limited = new MemoryStream(); await CopyLimitedAsync(stream, limited, 4 * 1024 * 1024, token);
        return SelectRelease(System.Text.Encoding.UTF8.GetString(limited.ToArray()));
    }
    internal static bool TryVersion(string tag, out Version version, out bool beta)
    {
        var match = Regex.Match(tag, @"^v?(\d+)\.(\d+)\.(\d+)(-beta)?$");
        beta = match.Groups[4].Success; version = new Version(0, 0, 0);
        return match.Success && Version.TryParse(match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value, out version!);
    }
    internal static int Compare(string left, string right)
    {
        if (!TryVersion(left, out var a, out bool ab) || !TryVersion(right, out var b, out bool bb)) throw new InvalidDataException("Ungültige Revora-Version.");
        int order = a.CompareTo(b); return order != 0 ? order : ab == bb ? 0 : ab ? -1 : 1;
    }
    internal static AppRelease? SelectRelease(string json)
    {
        using var doc = JsonDocument.Parse(json); AppRelease? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!TryVersion(tag, out _, out _) || Compare(tag, CurrentTag) <= 0) continue;
            string name = "Revora-" + tag.TrimStart('v') + "-win-x64.zip";
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != name) continue;
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                string hash = asset.TryGetProperty("digest", out var digest) ? digest.GetString() ?? "" : "";
                long size = asset.GetProperty("size").GetInt64();
                if (url != "https://github.com/Traiver/Revora/releases/download/" + tag + "/" + name || !Regex.IsMatch(hash, "^sha256:[a-fA-F0-9]{64}$") || size <= 0 || size > MaxDownload) continue;
                if (best == null || Compare(tag, best.Tag) > 0) best = new AppRelease(tag, url, hash[7..], size);
            }
        }
        return best;
    }
    internal async Task DownloadAsync(AppRelease release, string path, CancellationToken token)
    {
        var url = new Uri(release.Url);
        for (int hops = 0; hops < 6; hops++)
        {
            bool allowed = url.Scheme == "https" && url.IsDefaultPort && url.UserInfo.Length == 0 && (url.Host == "github.com" || url.Host == "release-assets.githubusercontent.com" || url.Host == "objects.githubusercontent.com");
            if (!allowed) throw new InvalidDataException("Unerwartete Downloadquelle für Revora.");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                url = new Uri(url, response.Headers.Location ?? throw new InvalidDataException("Downloadadresse fehlt.")); continue;
            }
            response.EnsureSuccessStatusCode();
            using var input = await response.Content.ReadAsStreamAsync(token);
            using (var output = File.Create(path)) await CopyLimitedAsync(input, output, Math.Min(MaxDownload, release.Size), token);
            if (new FileInfo(path).Length != release.Size || !Hash(path).Equals(release.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Der Revora-Download ist unvollständig oder beschädigt. Es wurde nichts geändert.");
            return;
        }
        throw new InvalidDataException("Zu viele Downloadweiterleitungen.");
    }
    internal static async Task CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0) { total += count; if (total > limit) throw new InvalidDataException("Datei überschreitet die zulässige Größe."); await output.WriteAsync(buffer.AsMemory(0, count), token); }
    }
    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static void ExtractExecutable(string zip, string destination)
    {
        using var archive = ZipFile.OpenRead(zip);
        // Extract exactly one known file, never paths supplied by an archive.
        var candidates = archive.Entries.Where(e => e.FullName == "Revora.exe").ToArray();
        if (candidates.Length != 1 || candidates[0].Length < 1024 || candidates[0].Length > 400 * 1024 * 1024) throw new InvalidDataException("Das Archiv enthält keine eindeutige Revora.exe.");
        using var input = candidates[0].Open(); using var output = File.Create(destination);
        CopyLimitedAsync(input, output, candidates[0].Length, default).GetAwaiter().GetResult();
    }
    internal static void ValidateExecutable(string path, string tag)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (stream.Length < 1024 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Keine Windows-Anwendung.");
        stream.Position = 0x3c; int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 24) throw new InvalidDataException("Ungültige Anwendung.");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException("Das Update ist keine x64-Anwendung.");
        var info = FileVersionInfo.GetVersionInfo(path);
        TryVersion(tag, out var expected, out _);
        if (info.ProductName != "Revora" || new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != expected) throw new InvalidDataException("Das Update gehört nicht zur angekündigten Revora-Version.");
    }
    private static void ExtractLicenses(string zip, string folder)
    {
        using var archive = ZipFile.OpenRead(zip);
        Directory.CreateDirectory(Path.Combine(folder, "licenses"));
        foreach (var name in RuntimeLicenses)
        {
            var entries = archive.Entries.Where(e => e.FullName.Replace('\\', '/') == "licenses/" + name).ToArray();
            if (entries.Length != 1 || entries[0].Length < 1 || entries[0].Length > 8 * 1024 * 1024) throw new InvalidDataException("Im Revora-Download fehlen die Lizenzhinweise.");
            using var input = entries[0].Open(); using var output = File.Create(Path.Combine(folder, "licenses", name));
            CopyLimitedAsync(input, output, entries[0].Length, default).GetAwaiter().GetResult();
        }
    }
    internal static void WithUpdatedLicenses(string source, string target, Action start)
    {
        Directory.CreateDirectory(target);
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Der Lizenzordner ist eine Verknüpfung. Bitte aktualisiere Revora manuell.");
        var originals = RuntimeLicenses.ToDictionary(name => name, name => File.Exists(Path.Combine(target, name)) ? File.ReadAllBytes(Path.Combine(target, name)) : null);
        var changed = new System.Collections.Generic.List<string>();
        try
        {
            foreach (string name in RuntimeLicenses)
            {
                string path = Path.Combine(target, name);
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ein Lizenzhinweis ist eine Verknüpfung.");
                changed.Add(name); File.Copy(Path.Combine(source, name), path, true);
            }
            start();
        }
        catch
        {
            foreach (string name in changed) { string path = Path.Combine(target, name); if (originals[name] is byte[] bytes) File.WriteAllBytes(path, bytes); else File.Delete(path); }
            throw;
        }
    }
    public async Task<string> PrepareAsync(AppRelease release, CancellationToken token)
    {
        string current = Environment.ProcessPath ?? "";
        if (!Path.GetFileName(current).Equals("Revora.exe", StringComparison.OrdinalIgnoreCase)) throw new UserMessageException("Öffne die fertige Revora.exe, um Revora zu aktualisieren.");
        CleanupOldDownloads();
        string folder = Program.DataPath(Path.Combine("SelfUpdate", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(folder);
        string zip = Path.Combine(folder, "update.zip"), candidate = Path.Combine(folder, "Revora.new.exe");
        await DownloadAsync(release, zip, token); ExtractExecutable(zip, candidate); ValidateExecutable(candidate, release.Tag); ExtractLicenses(zip, folder);
        File.Delete(zip);
        string helper = Path.Combine(folder, "Revora.exe"); File.Copy(current, helper);
        var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var arg in new[] { "--apply-self-update", current, parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(), release.Tag, Hash(candidate) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Updatehelfer konnte nicht gestartet werden.");
        return folder;
    }
    private static void CleanupOldDownloads()
    {
        string root = Program.DataPath("SelfUpdate");
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string folder in Directory.GetDirectories(root))
        {
            try
            {
                if (!Regex.IsMatch(Path.GetFileName(folder), "^[a-f0-9]{32}$") || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || Directory.GetCreationTimeUtc(folder) > DateTime.UtcNow.AddDays(-7)) continue;
                var files = Directory.GetFiles(folder);
                string[] known = { "Revora.exe", "Revora.new.exe", "update.zip", "ready" };
                string licenses = Path.Combine(folder, "licenses");
                if (Directory.GetDirectories(folder).Any(d => d != licenses) || files.Any(f => !known.Contains(Path.GetFileName(f)) || (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)) continue;
                if (Directory.Exists(licenses) && ((File.GetAttributes(licenses) & FileAttributes.ReparsePoint) != 0 || Directory.GetDirectories(licenses).Length > 0 || Directory.GetFiles(licenses).Any(f => !RuntimeLicenses.Contains(Path.GetFileName(f)) || (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0))) continue;
                string helper = Path.Combine(folder, "Revora.exe");
                if (File.Exists(helper)) { using var check = new FileStream(helper, FileMode.Open, FileAccess.Read, FileShare.None); }
                foreach (var file in files) File.Delete(file);
                if (Directory.Exists(licenses)) { foreach (var file in Directory.GetFiles(licenses)) File.Delete(file); Directory.Delete(licenses); }
                Directory.Delete(folder);
            }
            catch (IOException) { /* An active or locked helper is left alone. */ }
            catch (UnauthorizedAccessException) { /* Cache cleanup is optional. */ }
        }
    }
    // Atomic replacement keeps the previous EXE beside the application. Nothing in
    // the user's settings or project sources is extracted or overwritten.
    internal static void ReplaceWithRollback(string candidate, string target, Action start)
    {
        string next = target + ".new", backup = target + ".previous";
        if (File.Exists(next)) throw new IOException("Eine vorherige Updatevorbereitung ist noch vorhanden: " + next);
        File.Copy(candidate, next);
        bool replaced = false;
        try { File.Replace(next, target, backup); replaced = true; start(); }
        catch { if (replaced) File.Replace(backup, target, null); throw; }
        finally { if (File.Exists(next)) File.Delete(next); }
    }
    internal static int Apply(string[] args)
    {
        string folder = AppContext.BaseDirectory, target = Path.GetFullPath(args[1]), candidate = Path.Combine(folder, "Revora.new.exe");
        if (!Path.GetFileName(target).Equals("Revora.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(target) == Path.GetFullPath(Environment.ProcessPath!)) throw new InvalidDataException("Ungültiges Updateziel.");
        ValidateExecutable(candidate, args[4]);
        if (!Hash(candidate).Equals(args[5], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Die vorbereitete Anwendung wurde verändert.");
        try
        {
            using var parent = Process.GetProcessById(int.Parse(args[2]));
            if (parent.StartTime.ToUniversalTime().Ticks != long.Parse(args[3]) || !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Die laufende Anwendung passt nicht zum Update.");
            if (!parent.WaitForExit(60000)) throw new IOException("Revora wurde nicht rechtzeitig beendet. Bitte versuche es erneut.");
        }
        catch (ArgumentException) { /* Parent already exited. */ }
        var previous = FileVersionInfo.GetVersionInfo(target);
        if (previous.ProductName != "Revora" || !TryVersion(args[4], out var wanted, out _) || new Version(previous.FileMajorPart, previous.FileMinorPart, previous.FileBuildPart) > wanted) throw new InvalidDataException("Das installierte Programm passt nicht zum Update.");
        string ack = Path.Combine(folder, "ready");
        try
        {
            WithUpdatedLicenses(Path.Combine(folder, "licenses"), Path.Combine(Path.GetDirectoryName(target)!, "licenses"), () => ReplaceWithRollback(candidate, target, () =>
            {
                var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
                start.ArgumentList.Add("--self-update-ready"); start.ArgumentList.Add(ack);
                using var child = Process.Start(start) ?? throw new IOException("Die neue Version konnte nicht gestartet werden.");
                var watch = Stopwatch.StartNew();
                while (!File.Exists(ack) && !child.HasExited && watch.Elapsed < TimeSpan.FromSeconds(45)) Thread.Sleep(200);
                if (!File.Exists(ack)) { if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); } throw new IOException("Die neue Version konnte nicht vollständig gestartet werden. Die bisherige Version wurde wiederhergestellt."); }
            }));
            File.Delete(candidate);
            // A running helper cannot delete itself. The small recovery folder is
            // kept until a later update clears caches older than seven days.
            return 0;
        }
        catch
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! })?.Dispose();
            throw;
        }
    }
    public void Dispose() => client.Dispose();
}
