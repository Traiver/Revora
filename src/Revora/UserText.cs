using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Revora;

public sealed class UserMessageException : Exception
{
    public string Details { get; }
    public UserMessageException(string message, string details = "") : base(message) => Details = details;
}

public static class UserText
{
    public static string Explain(Exception error)
    {
        if (error is UserMessageException) return error.Message;
        if (error is JsonException) return "Die heruntergeladenen Angaben haben nicht das erwartete Format. Bitte prüfe den Link für die automatische Versionssuche oder frage beim Anbieter nach.";
        if (error is HttpRequestException) return "Die Verbindung zum Anbieter ist fehlgeschlagen. Prüfe deine Internetverbindung und versuche es erneut. Möglicherweise ist die Website gerade nicht erreichbar.";
        if (error is UnauthorizedAccessException) return "Windows hat den Zugriff auf eine benötigte Datei oder einen Ordner verweigert. Prüfe, ob der Speicherort zugänglich ist, und versuche es erneut.";
        if (error is OperationCanceledException) return "Die Suche oder der Download wurde abgebrochen oder hat zu lange gedauert. Prüfe deine Internetverbindung und versuche es erneut.";
        if (error is Win32Exception) return "Windows konnte das benötigte Programm nicht starten. Prüfe, ob die Datei noch vorhanden ist, und versuche es erneut. Weitere Angaben findest du in den technischen Details.";
        if (error is FileNotFoundException || error is DirectoryNotFoundException) return "Eine benötigte Datei oder ein Ordner wurde nicht gefunden. Vielleicht wurde etwas verschoben oder entfernt. Bitte versuche den Vorgang erneut.";
        if (error is InvalidDataException) return error.Message.Split('\n')[0];
        if (error is IOException) return "Eine Datei konnte nicht gelesen oder gespeichert werden. Prüfe, ob genügend Speicherplatz vorhanden und der Speicherort zugänglich ist. Versuche es danach erneut.";
        if (error is InvalidOperationException) return "Der Vorgang konnte gerade nicht ausgeführt werden. Bitte versuche es erneut. Falls es wieder passiert, starte den Updater neu.";
        return "Der Vorgang konnte nicht abgeschlossen werden. Bitte versuche es erneut. Falls das wieder passiert, helfen die technischen Details bei der Fehlersuche.";
    }
    public static string Version(string value, string empty = "nicht bekannt") => value.Length == 0 ? empty : value;
    public static string Count(int value, string singular, string plural) => value + " " + (value == 1 ? singular : plural);
    public static string Architecture(string arch) => arch switch { "x64" => "64-Bit-Ausgabe (x64)", "x86" => "32-Bit-Ausgabe (x86)", "arm64" => "Ausgabe für ARM-Prozessoren (ARM64)", _ => arch };
}
