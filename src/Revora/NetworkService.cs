using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Revora;

public sealed class NetworkService : IDisposable
{
    private readonly HttpClient client;
    public NetworkService(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Revora/0.2.0-beta");
    }
    private async Task<HttpResponseMessage> SendAsync(string url, HttpMethod method, CancellationToken token)
    {
        for (int hop = 0; hop <= 5; hop++)
        {
            var uri = Validate.Https(url);
            using var request = new HttpRequestMessage(method, uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location == null) throw new UserMessageException("Der Downloadlink verweist auf eine andere Seite, nennt aber kein gültiges Ziel. Bitte verwende einen neuen Link vom Anbieter.");
                url = new Uri(uri, location).AbsoluteUri; continue;
            }
            if (!response.IsSuccessStatusCode) { var code = response.StatusCode; response.Dispose(); throw new UserMessageException("Die Downloadseite antwortet nicht wie erwartet. Bitte prüfe den Link oder versuche es später erneut.", $"HTTP-Status: {(int)code} ({code})"); }
            return response;
        }
        throw new UserMessageException("Dieser Link führt über zu viele weitere Adressen. Bitte kopiere einen direkten Downloadlink von der Website des Anbieters.");
    }
    private static void InstallerContent(HttpResponseMessage response)
    {
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Der Link liefert keine Installationsdatei. Kopiere den Link zur .exe- oder .msi-Datei des Anbieters, nicht den Link zur Webseite.");
    }
    public async Task<DownloadInfo> ResolveAsync(Source source, bool probe, CancellationToken token)
    {
        string url = source.Url, hash = source.Sha256, version = "";
        if (source.Feed.Length > 0)
        {
            using var response = await SendAsync(source.Feed, HttpMethod.Get, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > 1048576) throw new UserMessageException("Der Link zur automatischen Versionssuche liefert eine zu große Datei. Bitte prüfe, ob du den richtigen Link eingetragen hast.");
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var memory = new MemoryStream(); var buffer = new byte[8192];
            while (true) { int n = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (n == 0) break; if (memory.Length + n > 1048576) throw new UserMessageException("Der Link zur automatischen Versionssuche liefert eine zu große Datei. Bitte prüfe den Link."); await memory.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false); }
            using var data = JsonDocument.Parse(memory.ToArray());
            var root = data.RootElement;
            if (!root.TryGetProperty("version", out var v) || !root.TryGetProperty("url", out var u)) throw new InvalidDataException("Die Datei für die automatische Versionssuche enthält nicht alle nötigen Angaben. Sie muss eine Versionsnummer und einen Downloadlink angeben.");
            version = v.GetString() ?? ""; url = u.GetString() ?? "";
            hash = root.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
            if (version.Length == 0) throw new InvalidDataException("Die Datei für die automatische Versionssuche nennt keine Versionsnummer. Bitte prüfe den Link beim Anbieter.");
        }
        Validate.InstallerExtension(url); Validate.HashValue(hash); var final = url;
        if (probe)
        {
            using var response = await SendAsync(url, HttpMethod.Head, token).ConfigureAwait(false); InstallerContent(response);
            final = response.RequestMessage!.RequestUri!.AbsoluteUri;
        }
        return new DownloadInfo(url, final, hash, version);
    }
    public async Task<DownloadedFile> DownloadAsync(InstallPlan plan, string directory, CancellationToken token)
    {
        Validate.Https(plan.FinalUrl); var extension = Validate.InstallerExtension(plan.Url); Validate.HashValue(plan.Sha256);
        Directory.CreateDirectory(directory); var file = Path.Combine(directory, Guid.NewGuid() + extension);
        try
        {
            using (var response = await SendAsync(plan.FinalUrl, HttpMethod.Get, token).ConfigureAwait(false))
            {
                if (response.RequestMessage!.RequestUri!.AbsoluteUri != plan.FinalUrl) throw new UserMessageException("Der Link führt inzwischen zu einer anderen Adresse. Bitte suche erneut nach Updates und bestätige den neuen Download.");
                InstallerContent(response);
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
            VerifyHeader(file, extension);
            var hash = await FileHashAsync(file, token).ConfigureAwait(false);
            if (plan.Sha256.Length > 0 && !string.Equals(hash, plan.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Die geladene Datei stimmt nicht mit dem Prüfcode des Anbieters überein. Sie wird nicht gestartet. Bitte prüfe den Downloadlink beim Anbieter.");
            var signature = await Task.Run(() => SignatureVerifier.Verify(file), token).ConfigureAwait(false);
            return new DownloadedFile(file, hash, signature.Status, signature.Publisher);
        }
        catch { if (File.Exists(file)) File.Delete(file); throw; }
    }
    public static void VerifyHeader(string path, string extension)
    {
        using var stream = File.OpenRead(path); var head = new byte[8]; int count = stream.Read(head);
        if (extension == ".exe" && count >= 2 && head[0] == 77 && head[1] == 90) return;
        if (extension == ".msi" && count == 8 && Convert.ToHexString(head) == "D0CF11E0A1B11E1") return;
        throw new InvalidDataException("Die geladene Datei ist keine passende Installationsdatei. Sie wird nicht gestartet. Bitte verwende einen anderen Downloadlink des Anbieters.");
    }
    public static async Task<string> FileHashAsync(string path, CancellationToken token = default)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    public void Dispose() => client.Dispose();
}

public static class SignatureVerifier
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileInfo { public uint Size; public IntPtr Path; public IntPtr Handle; public IntPtr Subject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallback; public IntPtr SipClient; public uint UIChoice; public uint RevocationChecks; public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UIContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
    public static (string Status, string Publisher) Verify(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var info = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = Marshal.StringToCoTaskMemUni(path) };
        var ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf<FileInfo>()); Marshal.StructureToPtr(info, ptr, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UIChoice = 2, UnionChoice = 1, File = ptr, StateAction = 1, ProviderFlags = 0x1000 };
        try
        {
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result == unchecked((int)0x800B0100)) return ("NotSigned", "Keine digitale Signatur");
            if (result != 0) throw new UserMessageException("Windows kann die Echtheit der Datei nicht bestätigen. Sie wird nicht gestartet. Bitte prüfe den Downloadlink beim Anbieter.", $"Windows-Signaturprüfung: 0x{result:X8}");
            string publisher = "Der Name konnte nicht aus der Datei gelesen werden";
            try { using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); publisher = certificate.GetNameInfo(X509NameType.SimpleName, false); } catch (CryptographicException) { }
            return ("Valid", publisher);
        }
        finally { data.StateAction = 2; _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data); Marshal.FreeCoTaskMem(ptr); Marshal.FreeCoTaskMem(info.Path); }
    }
}
