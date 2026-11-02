using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Revora;

public sealed class NotificationPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> MutedPrograms { get; set; } = new();
}

public sealed class NotificationSettingsStore
{
    public string FilePath { get; }
    public NotificationSettingsStore(string filePath) => FilePath = filePath;
    public HashSet<string> Load()
    {
        if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var data = Json.Read<NotificationPreferences>(File.ReadAllText(FilePath));
            if (data.SchemaVersion != 1 || data.MutedPrograms == null || data.MutedPrograms.Count > 10000) throw new InvalidDataException("Ungültige Version oder Programmliste.");
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in data.MutedPrograms)
                if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || !keys.Add(key)) throw new InvalidDataException("Ungültiger oder doppelter Programmeintrag.");
            return keys;
        }
        catch (Exception ex) { throw new UserMessageException("Die gespeicherten Einstellungen für Updatehinweise konnten nicht gelesen werden. Die Datei wurde nicht verändert. Stelle gegebenenfalls die Sicherung wieder her.", $"Datei: {FilePath}\n{ex}"); }
    }
    public void Save(IEnumerable<string> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temp = FilePath + "." + Guid.NewGuid() + ".tmp";
        try
        {
            File.WriteAllText(temp, Json.Write(new NotificationPreferences { MutedPrograms = keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList() }), new UTF8Encoding(false));
            _ = new NotificationSettingsStore(temp).Load();
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak"); else File.Move(temp, FilePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
