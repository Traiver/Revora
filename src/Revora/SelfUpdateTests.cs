using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Revora;

internal static class SelfUpdateTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, "Unsafe self-update accepted"); }
    private sealed class Http : HttpMessageHandler
    {
        public string Body = "[]";
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri!.Host == "api.github.com" && request.Headers.Authorization == null, "Unexpected GitHub authentication or endpoint");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class DownloadHttp : HttpMessageHandler
    {
        public bool Downgrade;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(Downgrade
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://github.com/unsafe.zip") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10000]) });
    }
    private static string Release(string tag, string hash, string? url = null, bool draft = false)
    {
        string name = "Revora-" + tag.TrimStart('v') + "-win-x64.zip";
        return System.Text.Json.JsonSerializer.Serialize(new { draft, tag_name = tag, assets = new[] { new { name, browser_download_url = url ?? "https://github.com/Traiver/Revora/releases/download/" + tag + "/" + name, digest = hash, size = 10000 } } });
    }
    internal static void Run(string root, List<string> passed)
    {
        string digest = "sha256:" + new string('a', 64);
        Check(SelfUpdateService.Compare("v0.10.0-beta", "v0.2.0-beta") > 0 && SelfUpdateService.Compare("v0.2.0", "v0.2.0-beta") > 0, "Semantic self-update comparison failed");
        var http = new Http { Body = "[" + Release("v0.1.0-beta", digest) + "," + Release("v0.2.0-beta", digest) + "," + Release("v0.3.0-beta", digest) + "," + Release("v0.9.0-beta", digest, draft: true) + "]" };
        using (var updater = new SelfUpdateService(http))
        {
            Check(updater.CheckAsync(default).GetAwaiter().GetResult()?.Tag == "v0.3.0-beta", "Beta release detection failed");
            foreach (var release in new[] { Release("v0.3.0", ""), Release("v0.3.0", digest, "https://evil.example/Revora.zip"), Release("v0.3.0", digest, "http://github.com/Traiver/Revora/update.zip"), Release("v0.3.0-rc1", digest), Release("v0.2.0-beta", digest) })
            { http.Body = "[" + release + "]"; Check(updater.CheckAsync(default).GetAwaiter().GetResult() == null, "Invalid, identical or foreign release accepted"); }
            http.Status = HttpStatusCode.Forbidden;
            Reject(() => updater.CheckAsync(default).GetAwaiter().GetResult());
        }
        passed.Add("GitHub self-updates: semantic versions, beta releases, own assets, SHA256 requirement and access errors");
        var dir = Path.Combine(root, "self-update"); Directory.CreateDirectory(dir);
        string zip = Path.Combine(dir, "unsafe.zip"), extracted = Path.Combine(dir, "candidate.exe");
        var download = new DownloadHttp();
        using (var updater = new SelfUpdateService(download))
        {
            var release = new AppRelease("v0.3.0-beta", "https://github.com/Traiver/Revora/releases/download/v0.3.0-beta/Revora-0.3.0-beta-win-x64.zip", new string('a', 64), 10000);
            string path = Path.Combine(dir, "download.zip");
            Reject(() => updater.DownloadAsync(release, path, default).GetAwaiter().GetResult());
            string hash = SelfUpdateService.Hash(path);
            updater.DownloadAsync(release with { Hash = hash }, path, default).GetAwaiter().GetResult();
            download.Downgrade = true; Reject(() => updater.DownloadAsync(release, path, default).GetAwaiter().GetResult());
        }
        passed.Add("Self-update downloads verify actual byte counts and SHA256 and reject HTTPS downgrades");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        { using var stream = archive.CreateEntry("../outside.exe").Open(); stream.Write(new byte[2048]); }
        Reject(() => SelfUpdateService.ExtractExecutable(zip, extracted));
        Check(!File.Exists(extracted) && !File.Exists(Path.Combine(root, "outside.exe")), "Archive wrote outside staging");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) { using var stream = archive.CreateEntry("Revora.exe").Open(); stream.Write(new byte[2048]); }
        SelfUpdateService.ExtractExecutable(zip, extracted);
        Reject(() => SelfUpdateService.ValidateExecutable(extracted, "v0.3.0-beta"));
        SelfUpdateService.ValidateExecutable(Path.Combine(AppContext.BaseDirectory, "Revora.exe"), SelfUpdateService.CurrentTag);
        Reject(() => SelfUpdateService.ValidateExecutable(Path.Combine(AppContext.BaseDirectory, "Revora.exe"), "v9.0.0"));
        using (var input = new MemoryStream(new byte[10])) using (var output = new MemoryStream()) Reject(() => SelfUpdateService.CopyLimitedAsync(input, output, 5, default).GetAwaiter().GetResult());
        passed.Add("Self-update extraction ignores archive paths; rejects invalid EXEs, version mismatch and oversized downloads");
        string target = Path.Combine(dir, "Revora.exe"), candidate = Path.Combine(dir, "new.exe"), settings = Path.Combine(dir, "quellen.json");
        File.WriteAllText(target, "old"); File.WriteAllText(candidate, "new"); File.WriteAllText(settings, "keep");
        Reject(() => SelfUpdateService.ReplaceWithRollback(candidate, target, () => throw new IOException("Fixture startup failure")));
        Check(File.ReadAllText(target) == "old", "Failed update did not roll back");
        SelfUpdateService.ReplaceWithRollback(candidate, target, () => { });
        Check(File.ReadAllText(target) == "new" && File.ReadAllText(target + ".previous") == "old", "Atomic self-update backup missing");
        File.WriteAllText(candidate, "newest"); SelfUpdateService.ReplaceWithRollback(candidate, target, () => { });
        Check(File.ReadAllText(target) == "newest" && File.ReadAllText(target + ".previous") == "new" && File.ReadAllText(settings) == "keep", "Next update or settings preservation failed");
        passed.Add("Atomic EXE replacement, failed-start rollback, repeated upgrades and unchanged user settings");
        string sourceLicenses = Path.Combine(dir, "source-licenses"), targetLicenses = Path.Combine(dir, "licenses");
        Directory.CreateDirectory(sourceLicenses); Directory.CreateDirectory(targetLicenses);
        string[] names = { "NETCore-LICENSE.txt", "NETCore-THIRD-PARTY-NOTICES.txt", "WindowsDesktop-LICENSE.txt" };
        foreach (string name in names) { File.WriteAllText(Path.Combine(sourceLicenses, name), "new notice"); File.WriteAllText(Path.Combine(targetLicenses, name), "old notice"); }
        Reject(() => SelfUpdateService.WithUpdatedLicenses(sourceLicenses, targetLicenses, () => throw new IOException("Fixture restart failure")));
        foreach (string name in names) Check(File.ReadAllText(Path.Combine(targetLicenses, name)) == "old notice", "Failed update lost old license notices");
        SelfUpdateService.WithUpdatedLicenses(sourceLicenses, targetLicenses, () => { });
        foreach (string name in names) Check(File.ReadAllText(Path.Combine(targetLicenses, name)) == "new notice", "Runtime license update missing");
        passed.Add("Runtime license notices update together with the EXE and are restored after a failed restart");
    }
}
