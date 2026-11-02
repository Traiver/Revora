using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Revora;

// Dependency-free test harness: never invokes WinGet, installers, Explorer or the real startup folder.
public static class SelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, message); }
    private static async Task RejectAsync(Func<Task> action, string message) { bool rejected = false; try { await action(); } catch { rejected = true; } Check(rejected, message); }
    private sealed class FakeRunner : ICommandRunner
    {
        public List<string[]> Calls { get; } = new();
        public Func<IReadOnlyList<string>, CommandResult> Run { get; set; } = _ => new(0, "");
        public Task<CommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken token = default, TimeSpan? timeout = null) { Calls.Add(args.ToArray()); return Task.FromResult(Run(args)); }
    }
    private sealed class FakeHttp : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { var response = Respond(request); response.RequestMessage = request; return Task.FromResult(response); }
    }
    private sealed class FakeStartupTasks : IStartupTaskStore
    {
        public string? Xml { get; set; }
        public bool FailRegistration { get; set; }
        public Exception? ReadFailure { get; set; }
        public int Registrations { get; private set; }
        public string? ReadXml() { if (ReadFailure != null) throw ReadFailure; return Xml; }
        public void Register(string xml) { if (FailRegistration) throw new IOException("Fixture registration failure"); Xml = xml; Registrations++; }
        public void Delete() => Xml = null;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    private static string ReadExecutableManifest(string path)
    {
        var module = LoadLibraryEx(path, IntPtr.Zero, 2); Check(module != IntPtr.Zero, "Cannot open EXE resources");
        try
        {
            var resource = FindResource(module, (IntPtr)1, (IntPtr)24); Check(resource != IntPtr.Zero, "EXE manifest missing");
            var bytes = new byte[SizeofResource(module, resource)];
            System.Runtime.InteropServices.Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\0');
        }
        finally { FreeLibrary(module); }
    }
    private static string Table(params (string Name, string Id, string Installed, string Offered)[] rows)
    {
        var list = new List<string> { string.Format("{0,-30}{1,-30}{2,-14}{3,-14}{4}", "Name", "Id", "Version", "Available", "Source"), new string('-', 100) };
        list.AddRange(rows.Select(x => string.Format("{0,-30}{1,-30}{2,-14}{3,-14}{4}", x.Name, x.Id, x.Installed, x.Offered, "winget"))); return string.Join("\n", list);
    }
    public static int Run(string reportPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "Revora-NativeTests-" + Guid.NewGuid()); Directory.CreateDirectory(root); var passed = new List<string>();
        try
        {
            foreach (var bad in new[] { "http://example.org/a.exe", "https://user:pass@example.org/a.exe", "file:///C:/a.exe", "invalid" }) Reject(() => Validate.Https(bad), "Unsafe URL accepted");
            foreach (var bad in new[] { "--all", "Vendor App", "Vendor.App;calc", "Vendor.App\"" }) Reject(() => Validate.Package(bad), "Unsafe package accepted");
            Check(Scanner.VersionState("2.0", "1.0") == "Older" && Scanner.VersionState("2.0", "2.0") == "Current" && Scanner.VersionState("", "2.0") == "Unknown" && Scanner.VersionState("1.0", "") == "Manual", "Version comparison failed"); passed.Add("URL/package validation and version states");
            var manifest = System.Xml.Linq.XDocument.Parse(ReadExecutableManifest(Path.Combine(AppContext.BaseDirectory, "Revora.exe")));
            var execution = manifest.Descendants().Single(node => node.Name.LocalName == "requestedExecutionLevel");
            Check((string?)execution.Attribute("level") == "requireAdministrator" && (string?)execution.Attribute("uiAccess") == "false", "Packaged EXE does not request administrator privileges");
            passed.Add("Native Windows EXE manifest requires administrator approval without UIAccess");
            SelfUpdateTests.Run(root, passed);
            var store = new SourceStore(Path.Combine(root, "sources.json"));
            File.WriteAllText(store.FilePath, "[{\"Name\":\"Legacy\",\"Url\":\"https://example.org/a.exe\",\"InstalledVersion\":\"1.0\"}]");
            var before = File.ReadAllText(store.FilePath); var sources = store.Load(); Check(sources.Count == 1 && sources[0].Key.StartsWith("manual:") && before == File.ReadAllText(store.FilePath), "Legacy config changed on read");
            store.Save(sources); Check(File.Exists(store.FilePath + ".bak"), "Backup missing"); var saved = File.ReadAllText(store.FilePath); Reject(() => store.Save(new() { sources[0], sources[0] }), "Duplicate sources accepted"); Check(saved == File.ReadAllText(store.FilePath), "Rejected config changed original"); passed.Add("Legacy configuration, backup and atomic validation");
            Check(RegistryInventory.IsSteam("Steam App 123", "", "") && RegistryInventory.IsSteam("Other", "steam://uninstall/123", "") && !RegistryInventory.IsSteam("Steam", "Steam\\uninstall.exe", ""), "Program classification failed");
            var a = Path.Combine(root, "v1"); var b = Path.Combine(root, "v2"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            var fakeIcon = Path.Combine(b, "app.exe"); File.WriteAllBytes(fakeIcon, new byte[] { 77, 90 });
            Check(RegistryInventory.Folder(a, "\"" + fakeIcon + "\",0").Path == a && RegistryInventory.Folder("", "\"" + fakeIcon + "\",0").Path == b && RegistryInventory.Folder("\\\\server\\share", "https://example.org/a.exe").Path.Length == 0, "Folder detection failed"); passed.Add("Program classification and safe registry folders");
            var table = Table(("Fixture App", "Vendor.App", "1.0", "3.0"), ("Fixture App", "Vendor.App", "2.0", "3.0"), ("Steam Game", "Vendor.Game", "1.0", "2.0"), ("Steam", "Valve.Steam", "1.0", "2.0"));
            var runner = new FakeRunner { Run = _ => new(0, table) }; using var network = new NetworkService(new FakeHttp());
            var registry = new List<Installation> { new() { Name = "Fixture App", Version = "1.0", Path = a }, new() { Name = "Fixture App", Version = "2.0", Path = b }, new() { Name = "Steam Game", Version = "1.0", IsSteam = true } };
            var scan = new Scanner(runner, network).ScanAsync(new(), default, false, registry).GetAwaiter().GetResult();
            Check(scan.Programs.Count == 2 && scan.Programs.All(x => !x.Selected) && scan.Programs.Any(x => x.Id == "Valve.Steam"), "Program filtering or opt-in selection failed");
            var app = scan.Programs.Single(x => x.Id == "Vendor.App"); Check(app.Mode == "ChooseDownload" && app.CanInstall && app.Installations.Count == 2 && app.InstallationsNotice.Contains("2.0"), "Multiple-version source/folder choice failed");
            Check(Scanner.Plan(scan.Programs).Count == 0, "Empty selection produced work"); app.Selected = true; var plan = Scanner.Plan(scan.Programs); Check(plan.Count == 1 && plan[0].Mode == "ChooseDownload", "Selected-only plan failed"); app.Version = "4.0"; Check(plan[0].Version == "3.0", "Plan not a snapshot"); passed.Add("Scan, selected-only snapshots and multi-version paths");
            var http = new FakeHttp { Respond = request => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":\"2.0\",\"url\":\"https://example.org/app.exe\"}", Encoding.UTF8, "application/json") }; return response; } };
            using (var net = new NetworkService(http))
            {
                var resolved = net.ResolveAsync(new Source { Feed = "https://example.org/feed.json" }, false, default).GetAwaiter().GetResult(); Check(resolved.Version == "2.0" && resolved.Url.EndsWith("app.exe"), "Feed-only source failed");
                RejectAsync(async () => await net.ResolveAsync(new Source { Url = "https://example.org/app.exe" }, true, default), "HTML/JSON installer probe accepted").GetAwaiter().GetResult();
                http.Respond = _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://example.org/unsafe.exe") } };
                RejectAsync(async () => await net.ResolveAsync(new Source { Url = "https://example.org/app.exe" }, true, default), "HTTPS downgrade accepted").GetAwaiter().GetResult();
                http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 77, 90, 0, 0, 0, 0, 0, 0 }) };
                RejectAsync(async () => await net.DownloadAsync(new InstallPlan { Url = "https://example.org/app.exe", FinalUrl = "https://example.org/app.exe", Sha256 = new string('a', 64) }, root, default), "Hash mismatch accepted").GetAwaiter().GetResult();
            }
            Reject(() => NetworkService.VerifyHeader(fakeIcon, ".msi"), "Wrong MSI format accepted");
            Check(SignatureVerifier.Verify(Path.Combine(AppContext.BaseDirectory, "Revora.exe")).Status == "NotSigned", "Unsigned native executable not detected"); passed.Add("Feed, HTTPS redirects, content types, file/hash checks and native Authenticode");
            foreach (var scenario in new[] { "Mismatch", "DownloadFailure", "UninstallFailure", "MigrationSuccess" })
            {
                var fake = new FakeRunner(); fake.Run = args =>
                {
                    if (args[0] == "upgrade") return new(-1978335189, "The install technology of the newer version specified is different from the current version installed.");
                    if (args[0] == "download") { if (scenario == "DownloadFailure") return new(3, "failed"); var dir = args[args.ToList().IndexOf("--download-directory") + 1]; Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "fixture.exe"), new byte[] { 77, 90 }); }
                    if (args[0] == "uninstall" && scenario == "UninstallFailure") return new(4, "failed"); return new(0, "ok");
                };
                var p = new InstallPlan { Name = "Fixture", Mode = "Winget", PackageId = "Vendor.App", Version = "2.0", AgreementsAccepted = true, Migration = scenario != "Mismatch" };
                var result = new WorkerService(fake).ExecuteAsync(p, Path.Combine(root, scenario + ".log.txt")).GetAwaiter().GetResult(); var ops = string.Join(",", fake.Calls.Select(x => x[0]));
                Check(scenario switch { "Mismatch" => result.Reason == "InstallerTypeChanged" && ops == "upgrade", "DownloadFailure" => !result.Success && ops == "download", "UninstallFailure" => !result.Success && ops == "download,uninstall", _ => result.Success && ops == "download,uninstall,install" }, "Migration state machine failed: " + scenario);
                Check(fake.Calls.All(x => !x.Contains("--all") && !x.Contains("--force")), "Worker broadened package scope");
            }
            var deniedRunner = new FakeRunner(); var denied = new WorkerService(deniedRunner).ExecuteAsync(new InstallPlan { Mode = "Winget", PackageId = "Vendor.App", Version = "2.0" }, Path.Combine(root,"denied.log.txt")).GetAwaiter().GetResult(); Check(!denied.Success && deniedRunner.Calls.Count == 0, "Unconfirmed worker executed"); passed.Add("Installer migration and agreement gates without real process execution");
            var nativePlan = Path.Combine(root, "native-denied-plan.json"); var nativeResult = Path.Combine(root, "native-denied-result.json");
            File.WriteAllText(nativePlan, Json.Write(new InstallPlan { Mode = "Winget", PackageId = "Revora.Tests.NeverInstall.Package", Version = "1.0" }));
            bool managedHost = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase);
            var nativeStart = new System.Diagnostics.ProcessStartInfo(managedHost ? Environment.ProcessPath! : Path.Combine(AppContext.BaseDirectory, "Revora.exe")) { UseShellExecute = false, CreateNoWindow = true };
            if (managedHost) nativeStart.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Revora.dll"));
            foreach (var arg in new[] { "--worker", nativePlan, nativeResult }) nativeStart.ArgumentList.Add(arg);
            using (var native = System.Diagnostics.Process.Start(nativeStart) ?? throw new Exception("Test worker not started"))
            {
                if (!native.WaitForExit(15000)) { native.Kill(true); throw new Exception("Background worker did not finish"); }
                Check(native.ExitCode != 0 && File.Exists(nativeResult) && !Json.Read<WorkerResult>(File.ReadAllText(nativeResult)).Success, "Background worker did not safely reject unconfirmed work");
            }
            passed.Add("Windowless worker rejects unconfirmed requests without launching WinGet");
            var startup = new StartupService(Path.Combine(root, "startup.lnk"), Environment.ProcessPath); Check(!startup.Enabled, "Default startup enabled"); startup.Set(true); Check(startup.State() == "Native", "Native shortcut not recognized"); startup.Set(false); Check(!File.Exists(startup.LinkPath), "Opt-out left link"); passed.Add("Native EXE startup opt-in using isolated shortcut");
            var renamedStartup = new StartupService(Path.Combine(root, "rename-startup.lnk"), Path.Combine(root, "Revora.exe"));
            new StartupService(renamedStartup.LinkPath, Path.Combine(root, LegacyCompatibility.OriginalName + ".exe")).Set(true);
            Check(renamedStartup.State() == "Legacy" && renamedStartup.MigrateLegacy() && renamedStartup.State() == "Native", "Rename lost existing startup opt-in");
            renamedStartup.Set(false); Check(!renamedStartup.MigrateLegacy() && !renamedStartup.Enabled, "Rename enabled startup without consent");
            new StartupService(renamedStartup.LinkPath, Path.Combine(root, "Other.exe")).Set(true);
            Check(renamedStartup.State() == "Foreign", "Foreign startup entry not protected");
            bool protectedLink = false; try { renamedStartup.Set(false); } catch (UserMessageException) { protectedLink = true; }
            Check(protectedLink && File.Exists(renamedStartup.LinkPath), "Foreign startup entry modified"); File.Delete(renamedStartup.LinkPath);
            passed.Add("Revora rename preserves startup choice and protects foreign entries");
            var startupFolder = Path.Combine(root, "startup-folder"); Directory.CreateDirectory(startupFolder);
            var oldStartupPath = Path.Combine(startupFolder, LegacyCompatibility.OriginalName + ".lnk");
            new StartupService(oldStartupPath, Path.Combine(root, LegacyCompatibility.PreviousName + ".exe")).Set(true);
            var revoraStartup = new StartupService(exe: Path.Combine(root, "Revora.exe"), startupFolder: startupFolder);
            Check(revoraStartup.Enabled && revoraStartup.MigrateLegacy() && revoraStartup.State() == "Native" && !File.Exists(oldStartupPath) && File.Exists(revoraStartup.LinkPath), "Previous startup link did not migrate to Revora");
            revoraStartup.Set(false); Check(!revoraStartup.Enabled && !revoraStartup.MigrateLegacy(), "Revora enabled disabled startup");
            new StartupService(oldStartupPath, Path.Combine(root, "Other.exe")).Set(true);
            Check(!revoraStartup.Enabled && !revoraStartup.MigrateLegacy(), "Foreign old shortcut enabled startup");
            revoraStartup.Set(true); revoraStartup.Set(false); Check(File.Exists(oldStartupPath), "Foreign old shortcut was removed");
            passed.Add("Revora startup filename migration preserves opt-in and leaves foreign legacy shortcuts untouched");
            var taskSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            var taskExe = Path.Combine(root, "apps & tools", "Revora.exe"); var taskLink = Path.Combine(root, "task-startup.lnk");
            var tasks = new FakeStartupTasks(); var elevatedStartup = new StartupService(taskLink, taskExe, tasks: tasks, sid: taskSid);
            Check(!elevatedStartup.Enabled && tasks.Registrations == 0 && !elevatedStartup.MigrateLegacy(), "Task startup auto-enabled");
            new StartupService(taskLink, taskExe).Set(true); Check(elevatedStartup.State() == "Legacy", "Old opted-in shortcut not recognized");
            tasks.FailRegistration = true; Reject(() => elevatedStartup.MigrateLegacy(), "Task registration failure ignored");
            Check(File.Exists(taskLink) && elevatedStartup.Enabled, "Failed task migration deleted old opt-in");
            tasks.FailRegistration = false; Check(elevatedStartup.MigrateLegacy() && tasks.Registrations == 1 && !File.Exists(taskLink) && elevatedStartup.State() == "Native", "Elevated task migration failed");
            Check(StartupTaskDefinition.State(tasks.Xml!, taskExe, taskSid) == "Native" && tasks.Xml!.Contains("HighestAvailable") && tasks.Xml.Contains("InteractiveToken") && !tasks.Xml.Contains("<Password>"), "Elevated task does not preserve interactive user without stored password");
            string ownTask = tasks.Xml!;
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!; scheduler.Connect();
            dynamic nativeDefinition = scheduler.NewTask(0); nativeDefinition.XmlText = ownTask;
            Check(StartupTaskDefinition.State((string)nativeDefinition.XmlText, taskExe, taskSid) == "Native", "Windows rejected or changed the startup task definition");
            tasks.Xml = ownTask.Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>");
            Check(!elevatedStartup.Enabled && !elevatedStartup.MigrateLegacy(), "Disabled task was re-enabled automatically");
            elevatedStartup.Set(true); elevatedStartup.Set(false); Check(tasks.Xml == null && !elevatedStartup.Enabled, "Task opt-out left automatic startup enabled");
            tasks.Xml = ownTask.Replace("--background", "--other-action");
            Reject(() => elevatedStartup.Set(true), "Foreign scheduled task overwritten"); Reject(() => elevatedStartup.Set(false), "Foreign scheduled task deleted");
            Check(tasks.Xml.Contains("--other-action"), "Foreign task changed"); tasks.Xml = null;
            passed.Add("Elevated interactive startup task preserves opt-in, migrates atomically, respects opt-out and protects foreign tasks");
            Check(WindowsStartupTaskStore.ReadExistingTask(() => throw new FileNotFoundException("Task not found")) == null, "Managed missing-task exception escaped");
            Check(WindowsStartupTaskStore.ReadExistingTask(() => throw new System.Runtime.InteropServices.COMException("Task not found", unchecked((int)0x80070002))) == null, "COM missing-task exception escaped");
            Reject(() => WindowsStartupTaskStore.ReadExistingTask(() => throw new UnauthorizedAccessException("Fixture denial")), "Permission failure was treated as a missing task");
            Reject(() => WindowsStartupTaskStore.ReadExistingTask(() => throw new System.Runtime.InteropServices.COMException("Account configuration failure", unchecked((int)0x8004130F))), "Task configuration failure was treated as a missing task");
            Check(new WindowsStartupTaskStore("MissingSelfTest-" + Guid.NewGuid().ToString("N")).ReadXml() == null, "Real Windows lookup for an absent task failed");
            passed.Add("Real missing Windows task and managed/COM not-found mappings return disabled; other errors remain distinguishable");
            foreach (bool backgroundMode in new[] { false, true })
            {
                var unavailableTasks = new FakeStartupTasks { ReadFailure = new UnauthorizedAccessException("Fixture scheduler unavailable") };
                var unavailableStartup = new StartupService(Path.Combine(root, "unavailable-startup-" + backgroundMode + ".lnk"), Path.Combine(root, "Revora.exe"), tasks: unavailableTasks, sid: taskSid);
                var unavailableRunner = new FakeRunner { Run = _ => new CommandResult(0, Table(("Unavailable scheduler fixture", "Revora.UnavailableFixture", "1.0", "2.0"))) };
                int recoveryNotices = 0;
                using var recovery = new MainController(backgroundMode, new SourceStore(Path.Combine(root, "unavailable-profile-" + backgroundMode + ".json")), unavailableStartup, unavailableRunner, (_, _) => recoveryNotices++);
                Check(!recovery.Window.C<CheckBox>("StartupOption").IsEnabled && recovery.Window.C<TextBlock>("StartupHint").Text.Contains("trotzdem verwenden"), "Unavailable startup backend blocks the app or lacks explanation");
                AwaitUi(() => backgroundMode ? recovery.StartAsync() : recovery.FirstRenderAsync(false));
                Check(unavailableRunner.Calls.Count == 1 && recovery.Window.C<ListBox>("ProgramsList").Items.Count == 1 && recovery.Window.C<TextBox>("DiagnosticReport").Text.Contains("Fixture scheduler unavailable"), "Autostart failure prevented scan or lost diagnostics");
                Check(!recovery.Window.IsVisible && recoveryNotices == (backgroundMode ? 1 : 0) && unavailableTasks.Registrations == 0, "Autostart failure showed a foreground window or changed Windows settings");
                recovery.Window.Close();
            }
            passed.Add("Unavailable startup backend leaves scans working; background recovery stays hidden and can notify");
            var main = Ui.Load("MainWindow.xaml"); Check(main.Icon != null && main.Title == "Revora · 0.2.0 Beta", "Revora branding missing"); var list = main.C<ListBox>("ProgramsList"); Check(list.Items.Count == 0 && main.C<Border>("ProgramsCard").Visibility == Visibility.Collapsed && main.C<CheckBox>("StartupOption").IsChecked != true, "UI not scan-first/opt-in");
            main.C<Border>("ProgramsCard").Visibility = Visibility.Visible; list.ItemsSource = scan.Programs; int clicks = 0; string key = "";
            main.C<Border>("SelfUpdateCard").Visibility = Visibility.Visible; main.C<TextBlock>("SelfUpdateTitle").Text = "Revora 0.3.0 Beta ist verfügbar";
            Ui.RouteRowButtons(main, button => { clicks++; key = Convert.ToString(button.Tag) ?? ""; });
            foreach (var width in new[] { 504, 520, 640, 900, 1200 }) Render(main, width, 850);
            main.C<Border>("ActionBar").Visibility = Visibility.Visible;
            main.C<TextBlock>("SelectionText").Text = "12 ausgewählt · davon 10 momentan nicht in der Liste sichtbar";
            main.C<Button>("InstallButton").Content = "Auswahl prüfen (12)";
            foreach (int height in new[] { 480, 640, 850 })
            {
                Render(main, 520, height);
                var action = main.C<Button>("InstallButton"); var rootView = (FrameworkElement)main.Content;
                var point = action.TransformToAncestor(rootView).Transform(new Point(0, 0));
                Check(point.Y >= 0 && point.Y + action.ActualHeight <= rootView.ActualHeight + 1, "Update action scrolls out of view");
            }
            Button? Find(DependencyObject node, string name) { if (node is Button button && button.Name == name) return button; for (int n = 0; n < VisualTreeHelper.GetChildrenCount(node); n++) { var found = Find(VisualTreeHelper.GetChild(node,n), name); if (found != null) return found; } return null; }
            var folderButton = Find(main.Content as DependencyObject ?? main, "OpenInstallFolderButton") ?? throw new Exception("Row button not rendered"); folderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(clicks == 1 && key.Length > 0, "Templated row click lost");
            Render(main, 520, 480);
            var pageScroll = main.C<ScrollViewer>("MainScroll");
            Check(pageScroll.ScrollableHeight > 0, "Wheel regression fixture must overflow");
            int selectedBeforeWheel = scan.Programs.Count(x => x.Selected);
            void Wheel(UIElement target, int delta)
            {
                target.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
                pageScroll.UpdateLayout();
            }
            pageScroll.ScrollToTop(); pageScroll.UpdateLayout();
            Wheel(folderButton, -120);
            double downOffset = pageScroll.VerticalOffset;
            if (SystemParameters.WheelScrollLines != 0) Check(downOffset > 0, "Wheel over a row button does not scroll the page");
            Wheel(list, 120);
            Check(pageScroll.VerticalOffset <= downOffset && (SystemParameters.WheelScrollLines == 0 || pageScroll.VerticalOffset < downOffset), "Wheel over program list does not scroll upward");
            Check(clicks == 1 && scan.Programs.Count(x => x.Selected) == selectedBeforeWheel, "Wheel changed selection or activated a row action");
            passed.Add("Mouse wheel over program list and row buttons scrolls the page in both directions");
            var review = Ui.Review(main, "Test", "No action", new[] { new ReviewItem("Fixture", new string('x', 250), new string('y', 500), "Angaben des Anbieters und Nutzungsbedingungen") }, "Confirm", "Acknowledgment"); Check(!review.C<Button>("ConfirmReview").IsEnabled, "Review skips consent"); review.C<CheckBox>("AckBox").IsChecked = true; Check(review.C<Button>("ConfirmReview").IsEnabled, "Review acknowledgment failed");
            foreach (int width in new[] { 464, 480, 640 }) Render(review, width, 700);
            Check(new ReviewItem("", "").TechnicalVisibility == Visibility.Collapsed && new ReviewItem("", "", "Details").TechnicalVisibility == Visibility.Visible, "Optional details not separated");
            Check(!UserText.Explain(new System.Text.Json.JsonException("raw json error")).Contains("raw json error") && !UserText.Explain(new HttpRequestException("raw socket error")).Contains("raw socket error"), "Technical errors shown without explanation");
            foreach (var file in new[] { "SourceWindow.xaml", "DownloadWindow.xaml", "LocationsWindow.xaml", "ReviewWindow.xaml" }) { var window = Ui.Load(file); if (file == "SourceWindow.xaml") { Check(!window.C<Expander>("AdvancedSourceOptions").IsExpanded, "Advanced source fields open by default"); window.C<Expander>("AdvancedSourceOptions").IsExpanded = true; } foreach (int width in new[] { 464, 480, 640 }) Render(window, width, 700); window.Close(); }
            review.Close(); main.Close(); passed.Add("Real XAML resources, empty welcome, row clicks, consent and responsive layouts");
            var startRunner = new FakeRunner { Run = _ => new CommandResult(0, Table(("Revora startup fixture", "Revora.TestFixture", "1.0", "2.0"))) };
            using (var controller = new MainController(false, new SourceStore(Path.Combine(root,"empty-profile.json")), new StartupService(Path.Combine(root,"ui-startup.lnk"), Path.Combine(AppContext.BaseDirectory,"Revora.exe")), startRunner))
            {
                Check(controller.Window.C<Button>("CheckButton").IsEnabled && !controller.Window.C<Button>("InstallButton").IsEnabled && controller.Window.C<CheckBox>("StartupOption").IsChecked != true, "Controller startup gates failed");
                Check(!File.Exists(Path.Combine(root,"ui-startup.lnk")), "Controller auto-enabled startup");
                Check(controller.Window.C<CheckBox>("OnlyUpdates").IsChecked == true, "Only updates is not enabled by default");
                AwaitUi(() => controller.FirstRenderAsync(false));
                Check(startRunner.Calls.Count == 1 && startRunner.Calls[0][0] == "list", "Initial display did not run exactly one read-only scan");
                Check(controller.Window.C<Border>("ProgramsCard").Visibility == Visibility.Visible && controller.Window.C<ListBox>("ProgramsList").Items.Count == 1, "Initial scan does not show filtered updates");
                Check(!controller.Window.C<Button>("InstallButton").IsEnabled && !File.Exists(Path.Combine(root,"ui-startup.lnk")) && !File.Exists(Path.Combine(root,"empty-profile.json")), "Initial scan selected updates or changed preferences");
                AwaitUi(() => controller.FirstRenderAsync(false));
                Check(startRunner.Calls.Count == 1, "Rendering the window again repeated the startup scan");
                Render(controller.Window, 520, 760); controller.Window.Close();
            }
            passed.Add("First display scans once, defaults to updates only, and does not select/install updates or change startup");
            var hiddenStartup = new StartupService(Path.Combine(root, "hidden-startup.lnk"), Path.Combine(AppContext.BaseDirectory, "Revora.exe")); hiddenStartup.Set(true);
            var hiddenRunner = new FakeRunner { Run = _ => new CommandResult(0, Table(("Revora background fixture", "Revora.BackgroundFixture", "1.0", "2.0"))) };
            var notices = new List<(string Title, string Message)>();
            using (var controller = new MainController(true, new SourceStore(Path.Combine(root, "hidden-profile.json")), hiddenStartup, hiddenRunner, (title, message) => notices.Add((title, message))))
            {
                bool everVisible = false; controller.Window.IsVisibleChanged += (_, _) => everVisible |= controller.Window.IsVisible;
                AwaitUi(controller.StartAsync);
                Check(!everVisible && !controller.Window.IsVisible && !controller.Window.ShowInTaskbar, "Windows login briefly showed the foreground window");
                Check(hiddenRunner.Calls.Count == 1 && notices.Count == 1 && notices[0].Title.Contains("Updates verfügbar"), "Silent startup did not scan and notify about updates");
                Check(!controller.Window.C<Button>("InstallButton").IsEnabled && controller.Window.C<CheckBox>("OnlyUpdates").IsChecked == true, "Background scan selected updates or lost default filter");
                AwaitUi(() => controller.ScanAsync()); Check(notices.Count == 1, "Unchanged updates triggered duplicate notifications");
                hiddenRunner.Run = _ => new CommandResult(0, ""); AwaitUi(() => controller.ScanAsync()); Check(notices.Count == 1, "No-update scan triggered a notification");
                hiddenRunner.Run = _ => new CommandResult(0, Table(("Revora background fixture", "Revora.BackgroundFixture", "1.0", "3.0")));
                AwaitUi(() => controller.ScanAsync()); Check(notices.Count == 2 && !everVisible, "New updates did not notify silently");
                hiddenRunner.Run = _ => new CommandResult(1, "Source agreements require user consent");
                AwaitUi(() => controller.ScanAsync());
                Check(!everVisible && !controller.Window.IsVisible && controller.Window.C<TextBlock>("StatusText").Text.Contains("Zustimmung"), "Background agreement request opened a dialog or lost pending consent");
                Check(hiddenRunner.Calls.All(call => call[0] == "list" && !call.Contains("--accept-source-agreements")), "Silent scan installed something or accepted terms automatically");
                controller.Window.C<CheckBox>("StartupOption").IsChecked = false; controller.Window.Close();
            }
            passed.Add("Windows login scans without ever showing a window; only new updates notify, no updates stay quiet");
            passed.Add("Background consent requests remain silent and never accept terms or start installations");
            var hintSettings = new NotificationSettingsStore(Path.Combine(root, "mute-settings.json"));
            Check(hintSettings.Load().Count == 0 && !File.Exists(hintSettings.FilePath), "Default hint settings write a file");
            hintSettings.Save(new[] { "winget:Revora.MuteFixture" });
            hintSettings.Save(new[] { "winget:Revora.MuteFixture", "winget:Revora.OtherFixture" });
            Check(hintSettings.Load().Count == 2 && new NotificationSettingsStore(hintSettings.FilePath + ".bak").Load().Count == 1, "Hint preference backup lost previous settings");
            string savedHints = File.ReadAllText(hintSettings.FilePath);
            Reject(() => hintSettings.Save(new[] { "" }), "Invalid mute key was saved");
            Check(File.ReadAllText(hintSettings.FilePath) == savedHints, "Invalid mute settings replaced valid settings");
            var brokenHints = new NotificationSettingsStore(Path.Combine(root, "broken-hints.json")); File.WriteAllText(brokenHints.FilePath, "broken json");
            Reject(() => brokenHints.Load(), "Corrupt hint settings ignored"); Check(File.ReadAllText(brokenHints.FilePath) == "broken json", "Corrupt hint settings overwritten");
            hintSettings.Save(Array.Empty<string>());
            passed.Add("Per-program hint preferences persist atomically with backup and preserve invalid files");
            var muteRunner = new FakeRunner { Run = _ => new CommandResult(0, Table(("Revora muted fixture", "Revora.MuteFixture", "1.0", "2.0"), ("Revora active fixture", "Revora.ActiveFixture", "1.0", "2.0"))) };
            using (var controller = new MainController(false, new SourceStore(Path.Combine(root, "mute-profile.json")), new StartupService(Path.Combine(root, "mute-startup.lnk"), Environment.ProcessPath), muteRunner, notificationStore: hintSettings))
            {
                AwaitUi(() => controller.FirstRenderAsync(false));
                var entries = controller.Window.C<ListBox>("ProgramsList");
                var mutedItem = entries.Items.Cast<ProgramItem>().Single(item => item.Key == "winget:Revora.MuteFixture");
                mutedItem.Selected = true; Render(controller.Window, 520, 480);
                var row = entries.ItemContainerGenerator.ContainerFromItem(mutedItem);
                var muteButton = Find(row, "MuteUpdatesButton") ?? throw new Exception("Mute action missing from row");
                muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(mutedItem.NotificationsMuted && !mutedItem.Selected && !entries.Items.Contains(mutedItem) && hintSettings.Load().Contains(mutedItem.Key), "Mute action did not persist, clear selection and hide update");
                controller.Window.C<CheckBox>("OnlyUpdates").IsChecked = false;
                controller.Window.C<Button>("SelectVisible").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(entries.Items.Contains(mutedItem) && !mutedItem.Selected && entries.Items.Cast<ProgramItem>().Any(item => item.Key == "winget:Revora.ActiveFixture" && item.Selected), "Bulk selection included muted program or all-program filter lost it");
                Render(controller.Window, 520, 480); row = entries.ItemContainerGenerator.ContainerFromItem(mutedItem);
                muteButton = Find(row, "MuteUpdatesButton") ?? throw new Exception("Restore hint action missing");
                Check(Convert.ToString(muteButton.Content) == "Hinweise einschalten", "Muted row does not offer restore action");
                muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!mutedItem.NotificationsMuted && !hintSettings.Load().Contains(mutedItem.Key), "Restore action did not persist");
                controller.ToggleUpdateHints(mutedItem.Key); controller.Window.Close();
            }
            passed.Add("Row actions mute and restore hints; muted programs remain reachable and bulk selection skips them");
            var mutedStartup = new StartupService(Path.Combine(root, "muted-background.lnk"), Environment.ProcessPath); mutedStartup.Set(true);
            var mutedRunner = new FakeRunner { Run = _ => new CommandResult(0, Table(("Revora muted fixture", "Revora.MuteFixture", "1.0", "2.0"))) };
            int mutedNotices = 0;
            using (var controller = new MainController(true, new SourceStore(Path.Combine(root, "muted-background-profile.json")), mutedStartup, mutedRunner, (_, _) => mutedNotices++, new NotificationSettingsStore(hintSettings.FilePath)))
            {
                AwaitUi(controller.StartAsync);
                Check(mutedNotices == 0 && controller.Window.C<ListBox>("ProgramsList").Items.Count == 0 && controller.Window.C<TextBlock>("StatusText").Text.Contains("ausgeblendet"), "Restart lost mute preference or notified about muted update");
                mutedRunner.Run = _ => new CommandResult(0, Table(("Revora muted fixture", "Revora.MuteFixture", "1.0", "9.0")));
                AwaitUi(() => controller.ScanAsync()); Check(mutedNotices == 0, "Later versions of muted program triggered a reminder");
                controller.ToggleUpdateHints("winget:Revora.MuteFixture");
                mutedRunner.Run = _ => new CommandResult(0, Table(("Revora muted fixture", "Revora.MuteFixture", "1.0", "10.0")));
                AwaitUi(() => controller.ScanAsync()); Check(mutedNotices == 1 && !controller.Window.IsVisible, "Restored hints did not notify silently");
                controller.Window.C<CheckBox>("StartupOption").IsChecked = false; controller.Window.Close();
            }
            passed.Add("Muted updates stay silent across restart and future versions; restoring hints enables notifications again");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); File.WriteAllText(reportPath, Json.Write(new { Success = true, Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), Checks = passed })); return 0;
        }
        catch (Exception ex) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); File.WriteAllText(reportPath, Json.Write(new { Success = false, Passed = passed, Error = ex.ToString() })); return 1; }
        finally { if (Path.GetDirectoryName(root) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(root).StartsWith("Revora-NativeTests-", StringComparison.Ordinal)) Directory.Delete(root, true); }
    }
    private static void AwaitUi(Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        try
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var task = action();
            timer.Tick += (_, _) => { if (task.IsCompleted || timeout.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; };
            if (!task.IsCompleted) { timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); }
            Check(task.IsCompleted, "UI startup scan timed out"); task.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void Render(Window window, int width, int height)
    {
        window.Width = width; window.Height = height; var content = (FrameworkElement)window.Content; content.Measure(new Size(width,height)); content.Arrange(new Rect(0,0,width,height)); content.UpdateLayout();
        void CheckScroll(DependencyObject node) { if (node is ScrollViewer scroll) Check(scroll.ScrollableWidth < 1, "Horizontal overflow in " + window.Title); for (int n = 0; n < VisualTreeHelper.GetChildrenCount(node); n++) CheckScroll(VisualTreeHelper.GetChild(node,n)); }
        CheckScroll(content);
    }
}
