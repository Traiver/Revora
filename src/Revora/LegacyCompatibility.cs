using System;
using System.IO;
using System.Linq;

namespace Revora;

// Historical identifiers are deliberately isolated here. Keep them stable so
// existing profiles, startup links and running older instances remain compatible.
internal static class LegacyCompatibility
{
    internal const string OriginalName = "ProgrammUpdater";
    internal const string PreviousName = "Uply";
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OriginalName);
    internal static string InstanceMutexName => @"Local\" + OriginalName;
    internal static string[] StartupLinkNames => new[] { OriginalName + ".lnk", PreviousName + ".lnk" };

    internal static bool IsPreviousStartup(string target, string arguments, string currentExe)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var folder = Path.GetDirectoryName(currentExe)!;
        var oldExecutables = new[] { OriginalName, PreviousName }.SelectMany(name => new[] { Path.Combine(desktop, name, name + ".exe"), Path.Combine(folder, name + ".exe") });
        if (arguments == "--background" && oldExecutables.Any(path => string.Equals(target, path, StringComparison.OrdinalIgnoreCase))) return true;
        // Recognize only our own historical shortcut; never execute its script.
        var oldScript = Path.Combine(folder, OriginalName + ".ps1");
        return target.EndsWith("\\powershell.exe", StringComparison.OrdinalIgnoreCase) && arguments.Contains("\"" + oldScript + "\"", StringComparison.OrdinalIgnoreCase) && arguments.Contains("-Background", StringComparison.OrdinalIgnoreCase);
    }
}
