using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Revora;

public sealed class WorkerService
{
    private readonly ICommandRunner runner;
    public WorkerService(ICommandRunner runner) => this.runner = runner;
    public async Task<WorkerResult> ExecuteAsync(InstallPlan plan, string logPath)
    {
        var result = new WorkerResult { Log = logPath };
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "");
        try
        {
            Validate.Package(plan.PackageId); Validate.Version(plan.Version);
            if (plan.Mode != "Winget" || !plan.AgreementsAccepted) throw new InvalidDataException("Deine Bestätigung fehlt. Bitte wähle das Programm erneut aus und bestätige die angezeigten Bedingungen.");
            async Task<CommandResult> Run(List<string> args)
            {
                var command = await runner.RunAsync(args).ConfigureAwait(false);
                await File.AppendAllTextAsync(logPath, string.Join(" ", args) + "\n" + command.Text + "\n").ConfigureAwait(false);
                return command;
            }
            var identity = new[] { "--id", plan.PackageId, "--exact", "--version", plan.Version, "--source", "winget", "--accept-source-agreements", "--accept-package-agreements" };
            CommandResult install;
            if (plan.Migration)
            {
                result.RecoveryDirectory = Path.Combine(Path.GetDirectoryName(logPath)!, "Installer-" + Guid.NewGuid());
                var download = new List<string> { "download" }; download.AddRange(identity); download.AddRange(new[] { "--download-directory", result.RecoveryDirectory });
                if ((await Run(download).ConfigureAwait(false)).Code != 0) throw new UserMessageException("Die neue Installationsdatei konnte nicht heruntergeladen werden. Deine bisherige Programmversion wurde nicht entfernt. Bitte versuche es später erneut.");
                var extensions = new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip" };
                if (!Directory.Exists(result.RecoveryDirectory) || !Directory.EnumerateFiles(result.RecoveryDirectory, "*", SearchOption.AllDirectories).Any(x => extensions.Contains(Path.GetExtension(x).ToLowerInvariant()))) throw new UserMessageException("Nach dem Download wurde keine Installationsdatei gefunden. Deine bisherige Programmversion wurde deshalb nicht entfernt.");
                var uninstall = await Run(new List<string> { "uninstall", "--id", plan.PackageId, "--exact", "--source", "winget", "--interactive", "--accept-source-agreements" }).ConfigureAwait(false);
                if (uninstall.Code != 0) throw new UserMessageException("Das Entfernen der alten Version wurde nicht erfolgreich abgeschlossen. Die neue Version wurde deshalb noch nicht installiert. Bitte prüfe, ob sich das bisherige Programm noch öffnen lässt.");
                var args = new List<string> { "install" }; args.AddRange(identity); args.Add("--interactive"); install = await Run(args).ConfigureAwait(false);
            }
            else { var args = new List<string> { "upgrade" }; args.AddRange(identity); args.Add("--interactive"); install = await Run(args).ConfigureAwait(false); }
            result.Success = install.Code == 0;
            if (!result.Success && System.Text.RegularExpressions.Regex.IsMatch(install.Text, "install technology|Installationstechnologie", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { result.Reason = "InstallerTypeChanged"; result.Message = "Ein normales Update ist für diese neue Version nicht möglich. Das Programm muss stattdessen entfernt und neu installiert werden. Dafür ist deine zusätzliche Zustimmung erforderlich."; }
            else if (install.Code == -1978335189) result.Message = "Für diese Installation wurde kein passendes Update gefunden. Suche erneut nach Updates oder verwende einen Downloadlink des Anbieters.";
            else result.Message = result.Success ? "Das Installationsprogramm meldet, dass es fertig ist." : "Das Update wurde nicht erfolgreich abgeschlossen. Prüfe, ob das Installationsprogramm noch eine Meldung zeigt. Suche danach erneut nach Updates.";
            result.Details = $"WinGet-Rückgabecode: {install.Code}\n\n" + install.Text.Trim();
        }
        catch (Exception ex) { result.Message = UserText.Explain(ex); result.Details = ex.ToString(); }
        if (result.RecoveryDirectory.Length > 0)
        {
            result.Message += "\nDer Downloadordner bleibt erhalten. Seinen Speicherort findest du unter den technischen Details.";
            result.Details += "\nDownloadordner für eine erneute Installation: " + result.RecoveryDirectory;
        }
        return result;
    }
}
