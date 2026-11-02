using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace Revora;

public interface IStartupTaskStore
{
    string? ReadXml();
    void Register(string xml);
    void Delete();
}

public static class StartupTaskDefinition
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string Create(string exe, string sid)
    {
        XElement E(string name, object value) => new(Ns + name, value);
        return new XDocument(new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", new[] { E("Author", sid), E("Description", "Revora: check program updates in the background at sign-in."), E("URI", "\\Revora-" + sid) }),
            E("Triggers", new XElement(Ns + "LogonTrigger", E("Enabled", "true"), E("UserId", sid), E("Delay", "PT10S"))),
            E("Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"), E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"), E("AllowStartOnDemand", "true"), E("StartWhenAvailable", "true"), E("Enabled", "true"), E("Hidden", "false"), E("ExecutionTimeLimit", "PT0S") }),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"), new XElement(Ns + "Exec", E("Command", exe), E("Arguments", "--background"), E("WorkingDirectory", Path.GetDirectoryName(exe)!))))).ToString();
    }
    private static bool SameUser(string value, string sid)
    {
        if (string.Equals(value, sid, StringComparison.OrdinalIgnoreCase)) return true;
        try { return string.Equals(((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value, sid, StringComparison.OrdinalIgnoreCase); }
        catch (IdentityNotMappedException) { return false; }
        catch (ArgumentException) { return false; }
    }
    public static string State(string xml, string exe, string sid)
    {
        try
        {
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1000000 });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != Ns + "Task") return "Foreign";
            string Value(XElement? node, string name) => (string?)node?.Element(Ns + name) ?? "";
            var principal = root.Element(Ns + "Principals")?.Elements().SingleOrDefault();
            var action = root.Element(Ns + "Actions")?.Elements().SingleOrDefault();
            var trigger = root.Element(Ns + "Triggers")?.Elements().SingleOrDefault();
            if (Value(root.Element(Ns + "RegistrationInfo"), "URI") != "\\Revora-" + sid || principal?.Name != Ns + "Principal" || !SameUser(Value(principal, "UserId"), sid) || Value(principal, "RunLevel") != "HighestAvailable" || Value(principal, "LogonType") != "InteractiveToken" || action?.Name != Ns + "Exec" || !string.Equals(Value(action, "Command"), exe, StringComparison.OrdinalIgnoreCase) || Value(action, "Arguments") != "--background" || !string.Equals(Value(action, "WorkingDirectory"), Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase) || trigger?.Name != Ns + "LogonTrigger" || !SameUser(Value(trigger, "UserId"), sid)) return "Foreign";
            return Value(root.Element(Ns + "Settings"), "Enabled") == "true" && Value(trigger, "Enabled") == "true" ? "Native" : "Disabled";
        }
        catch (Exception ex) when (ex is XmlException || ex is InvalidOperationException || ex is ArgumentException) { return "Foreign"; }
    }
}

public sealed class WindowsStartupTaskStore : IStartupTaskStore
{
    private readonly string sid;
    private string Name => "Revora-" + sid;
    public WindowsStartupTaskStore(string sid) => this.sid = sid;
    private dynamic Root()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new UserMessageException("Der automatische Windows-Start konnte nicht eingerichtet werden. Bitte prüfe, ob die Windows-Aufgabenplanung verfügbar ist.");
        dynamic service = Activator.CreateInstance(type)!; service.Connect(); return service.GetFolder("\\");
    }
    public string? ReadXml()
    {
        dynamic root = Root();
        return ReadExistingTask(() => { dynamic task = root.GetTask(Name); return (string)task.Xml; });
    }
    internal static string? ReadExistingTask(Func<string> read)
    {
        try { return read(); }
        // COM interop may translate ERROR_FILE_NOT_FOUND to FileNotFoundException.
        // Match the missing-task HRESULT, not a particular managed exception type.
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
    }
    public void Register(string xml)
    {
        // Use the current interactive user, never SYSTEM and never saved credentials.
        dynamic task = Root().RegisterTask(Name, xml, 6, sid, null, 3, null);
    }
    public void Delete() => Root().DeleteTask(Name, 0);
}
