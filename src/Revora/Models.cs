using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revora;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text.TrimStart('\uFEFF'), Options) ?? throw new InvalidOperationException("Leere JSON-Datei.");
}

public sealed class Source
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "Download";
    public string Url { get; set; } = "";
    public string Feed { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string PackageId { get; set; } = "";
}
public sealed class SourceDocument
{
    public int SchemaVersion { get; set; } = 2;
    public List<Source> Sources { get; set; } = new();
}
public sealed class Installation
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Path { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Match { get; set; } = "";
    public bool IsSteam { get; set; }
    public string RegistryKey { get; set; } = "";
    [JsonIgnore] public string Detail => $"Version auf deinem PC: {UserText.Version(Version)}\n{(Path.Length == 0 ? "Für diese Version ist kein Programmordner bekannt." : "Programmordner: " + Path)}" + (Match.Contains("könnte", StringComparison.Ordinal) || Match.Contains("Möglicher", StringComparison.Ordinal) ? "\nDieser Ordner könnte zum Programm gehören. Bitte prüfe Name und Version." : "") + (Origin.Contains("Programmsymbol", StringComparison.Ordinal) ? "\nDer Ordner wurde aus dem Programmsymbol ermittelt. Bitte prüfe, ob er passt." : "");
}
public sealed class ProgramItem : INotifyPropertyChanged
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Installed { get; set; } = "";
    public string Version { get; set; } = "";
    public string Mode { get; set; } = "None";
    public string PackageId { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string Status { get; set; } = "";
    public string StatusColor { get; set; } = "#A3BAD6";
    public bool CanInstall { get; set; }
    public bool HasUpdate { get; set; }
    public bool Manual { get; set; }
    public string Url { get; set; } = "";
    public string FinalUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public List<Installation> Installations { get; set; } = new();
    public string InstallationsNotice { get; set; } = "";
    [JsonIgnore] public string VersionLabel => $"Installiert: {UserText.Version(Installed)}" + (Version.Length > 0 ? "  →  " + Version : CanInstall ? " · Downloadversion unbekannt" : "");
    private bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    private bool notificationsMuted;
    public bool NotificationsMuted
    {
        get => notificationsMuted;
        set
        {
            if (notificationsMuted == value) return;
            notificationsMuted = value;
            foreach (var name in new[] { nameof(NotificationsMuted), nameof(NotificationAction), nameof(NotificationHint) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
    [JsonIgnore] public string NotificationAction => NotificationsMuted ? "Hinweise einschalten" : "Hinweise ausblenden";
    [JsonIgnore] public string NotificationHint => NotificationsMuted ? "Updatehinweise ausgeblendet" : "";
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed class InstallPlan
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Installed { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string FinalUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool Manual { get; set; }
    public bool Migration { get; set; }
    public bool AgreementsAccepted { get; set; }
}
public sealed record ScanResult(List<ProgramItem> Programs, string Report, string Error);
public sealed record CommandResult(int Code, string Text);
public sealed record DownloadInfo(string Url, string FinalUrl, string Sha256, string Version);
public sealed record DownloadedFile(string Path, string Hash, string Signature, string Publisher);
public sealed record Outcome(string Name, string State, string Detail, string TechnicalDetails = "");
public sealed class WorkerResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Log { get; set; } = "";
    public string RecoveryDirectory { get; set; } = "";
    public string Details { get; set; } = "";
}
public sealed class DownloadChoice
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Version { get; set; } = "";
    public string Detail => $"Version zum Download: {UserText.Version(Version)}\nDownloadadresse: {Url}";
}
