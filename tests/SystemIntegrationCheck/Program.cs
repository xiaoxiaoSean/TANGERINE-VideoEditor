using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

internal static class Program
{
    private const string Prefix = "TANGERINE_VideoEditor.SystemIntegration.";
    private const string Extension = ".tvep";
    private const string ProgId = "TangerineVideoEditor.Project.tvep";
    private const string Handler = @"Software\Classes\TangerineVideoEditor.Project.tvep";
    private const string Capabilities = @"Software\TangerineVideoEditor\DefaultApps\Capabilities";
    private const string FileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.tvep";
    private const string RegisteredName = "TANGERINE VideoEditor";
    private static readonly Assembly Product = typeof(MainWindow).Assembly;
    private static readonly Type Registration = Product.GetType(Prefix + "AssociationRegistrationValue", true)!;
    private static readonly List<string> Failures = [];
    private static int Assertions;
    private static int Cases;

    [STAThread]
    private static int Main()
    {
        Run("version/assembly-file-display", CheckVersion);
        Run("registration/quoted-command-white-document-icon", CheckPlan);
        Run("registration/native-extension-only", CheckExtensionValidation);
        Run("registration/windows-settings-uri-versions", CheckSettingsUri);
        Run("registration/apply-and-readback", CheckApply);
        Run("registration/rollback-preserves-data-and-kind", CheckRegistrationRollback);
        Run("unregister/preserves-foreign-defaults-and-choices", CheckForeignData);
        Run("unregister/removes-own-legacy-extension-default", CheckOwnDefault);
        Run("unregister/refuses-foreign-progid", CheckForeignHandler);
        Run("unregister/preserves-foreign-capabilities-registration", CheckForeignCapabilities);
        Run("unregister/unknown-associations-block-app-removal", CheckUnknownAssociations);
        Run("choices/own-classic-and-latest", () => CheckChoices(ProgId, ProgId));
        Run("choices/own-classic-foreign-latest", () => CheckChoices(ProgId, "Foreign.Latest"));
        Run("choices/foreign-classic-own-latest", () => CheckChoices("Foreign.Classic", ProgId));
        Run("choices/foreign-classic-and-latest", () => CheckChoices("Foreign.Classic", "Foreign.Latest"));
        Run("choices/failed-probe-restores-original-keys", CheckChoiceRollback);
        Run("choices/own-orphan-cleanup-foreign-preservation", CheckOrphans);
        Run("choices/missing-key-validation", CheckMissingChoice);
        Run("hash/classic-case-and-minute-contract", CheckClassicHash);
        Run("hash/native-resource-and-license-architecture", CheckNativeResource);
        Run("hash/latest-native-compute-and-millisecond-contract", CheckLatestHash);
        Run("ui/project-open-path-and-dialog-layout", CheckUi);
        Console.WriteLine($"System integration: {Cases - Failures.Count}/{Cases} cases passed, {Assertions} assertions.");
        foreach (string failure in Failures) Console.Error.WriteLine(failure);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void CheckVersion()
    {
        Require(Product.GetName().Version == new Version(1, 0, 0, 0), "Assembly version is not 1.0.0.0");
        var file = FileVersionInfo.GetVersionInfo(Product.Location);
        Require(file.FileMajorPart == 1 && file.FileMinorPart == 0 && file.FileBuildPart == 0,
            "File version is not 1.0.0");
        Type version = Product.GetType("TANGERINE_VideoEditor.AppVersion", true)!;
        Require(Equals(version.GetProperty("Number")!.GetValue(null), "1.0.0"), "Version number differs");
        Require(Equals(version.GetProperty("DisplayName")!.GetValue(null), "v1.0.0"), "Version display differs");
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Require(Equals(version.GetProperty("DisplayName")!.GetValue(null), "v1.0.0"), "Display is culture-dependent");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void CheckPlan()
    {
        const string executable = @"C:\Program Files\橘子 视频剪辑\TANGERINE VideoEditor.exe";
        object[] plan = Plan(executable);
        Require(plan.Length == 10, "Unexpected registration scope");
        Require(PlanValue(plan, Handler + @"\shell\open\command", "") == $"\"{executable}\" \"%1\"",
            "Explorer command does not quote both executable and project path");
        Require(PlanValue(plan, Handler + @"\DefaultIcon", "") ==
            $"\"{Path.Combine(Environment.SystemDirectory, "shell32.dll")}\",0", "Project icon is not Windows' white document");
        Require(PlanValue(plan, Capabilities, "ApplicationIcon") == $"\"{executable}\",0", "Application icon differs");
        Require(PlanValue(plan, @"Software\Classes\.tvep\OpenWithProgids", ProgId) == "", "OpenWith candidate missing");
        Require(PlanValue(plan, @"Software\RegisteredApplications", RegisteredName) == Capabilities, "Capabilities registration differs");
        Require(!plan.Any(item => Field(item, "KeyPath") == @"Software\Classes\.tvep"), "Plan overwrites extension default/icon");
        Require(!plan.Any(item => Field(item, "KeyPath").Contains("FileExts", StringComparison.OrdinalIgnoreCase)),
            "Candidate registration changes protected UserChoice");
        Require(plan.All(item => Field(item, "KeyPath").StartsWith(@"Software\", StringComparison.Ordinal)), "Unexpected registry location");
    }

    private static void CheckExtensionValidation()
    {
        Require(Equals(Call("DefaultAppAssociationService", "GetProgId", Extension), ProgId), "Wrong native ProgID");
        foreach (string invalid in new[] { ".mp4", ".txt", ".zip", "tvep", ".TVEP", ".tvep\\shell", "" })
            ExpectStage("DEFAS0001", () => Call("DefaultAppAssociationService", "BuildRegistrationPlan", invalid, @"C:\editor.exe"));
    }

    private static void CheckSettingsUri()
    {
        string Uri(bool specific, int build, int revision) =>
            (string)Call("DefaultAppAssociationService", "BuildSettingsUri", specific, build, revision)!;
        const string general = "ms-settings:defaultapps";
        string specific = general + "?registeredAppUser=" + System.Uri.EscapeDataString(RegisteredName);
        Require(Uri(true, 19045, 0) == general, "Windows 10 URI");
        Require(Uri(true, 22000, 1816) == general && Uri(true, 22000, 1817) == specific, "Windows 11 21H2 URI gate");
        Require(Uri(true, 22621, 1554) == general && Uri(true, 22621, 1555) == specific, "Windows 11 22H2 URI gate");
        Require(Uri(true, 22631, 0) == specific && Uri(true, 26100, 0) == specific, "Modern Windows URI");
        Require(Uri(false, 26100, 0) == general, "General Settings request must remain general");
    }

    private static void CheckApply() => InSandbox(root =>
    {
        object[] plan = Plan(@"C:\Program Files\Video Editor.exe");
        Apply(root, plan);
        foreach (object item in plan)
        {
            using RegistryKey key = root.OpenSubKey(Field(item, "KeyPath"))!;
            Require(Equals(key.GetValue(Field(item, "Name")), Field(item, "Value")), "Registered value readback differs");
            Require(key.GetValueKind(Field(item, "Name")) == RegistryValueKind.String, "Registration kind differs");
        }
        Apply(root, plan);
        Require(Read(root, Handler + @"\shell\open\command", "") == "\"C:\\Program Files\\Video Editor.exe\" \"%1\"", "Repeated registration differs");
    });

    private static void CheckRegistrationRollback() => InSandbox(root =>
    {
        Write(root, "Journal", "existing", @"%TEMP%\old", RegistryValueKind.ExpandString);
        Write(root, "Journal", "", 73, RegistryValueKind.DWord);
        object[] broken =
        [
            Value("Journal", "existing", "replacement"),
            Value("Journal", "", "replacement"),
            Value("Journal", "new", "temporary"),
            Value("Journal", "existing", "second replacement"),
            Value("FailureInjection", "invalid-null-value", null!)
        ];
        ExpectStage("DEFAS0003", () => Apply(root, broken));
        using RegistryKey key = root.OpenSubKey("Journal")!;
        Require(Equals(key.GetValue("existing", null, RegistryValueOptions.DoNotExpandEnvironmentNames), @"%TEMP%\old"), "ExpandString data was lost");
        Require(key.GetValueKind("existing") == RegistryValueKind.ExpandString, "ExpandString kind was lost");
        Require(Equals(key.GetValue(""), 73) && key.GetValueKind("") == RegistryValueKind.DWord, "Default value/kind was lost");
        Require(!key.GetValueNames().Contains("new"), "New value survived rollback");
        using RegistryKey? failed = root.OpenSubKey("FailureInjection");
        Require(failed == null || failed.GetValueNames().Length == 0, "Failed write survived rollback");
    });

    private static void CheckForeignData() => InSandbox(root =>
    {
        Apply(root, Plan(@"C:\editor.exe"));
        Write(root, @"Software\Classes\.tvep", "", "Foreign.Editor");
        Write(root, @"Software\Classes\.tvep\DefaultIcon", "", "foreign.ico");
        Write(root, @"Software\Classes\.tvep\OpenWithProgids", "Foreign.Editor", "");
        Write(root, @"Software\RegisteredApplications", "Foreign App", @"Software\Foreign\Capabilities");
        Write(root, Capabilities, "ForeignValue", 17, RegistryValueKind.DWord);
        Write(root, Capabilities + @"\ForeignChild", "data", "retain");
        Write(root, FileExts + @"\UserChoice", "ProgId", "Foreign.Editor");
        Write(root, FileExts + @"\UserChoice", "Hash", "foreign-hash");
        RemoveRegistration(root);
        Require(Read(root, @"Software\Classes\.tvep", "") == "Foreign.Editor", "Foreign extension default removed");
        Require(Read(root, @"Software\Classes\.tvep\DefaultIcon", "") == "foreign.ico", "Foreign icon removed");
        Require(Read(root, @"Software\Classes\.tvep\OpenWithProgids", "Foreign.Editor") == "", "Foreign OpenWith candidate removed");
        Require(Read(root, @"Software\Classes\.tvep\OpenWithProgids", ProgId) == null, "Own OpenWith candidate remains");
        Require(Read(root, @"Software\RegisteredApplications", "Foreign App") != null, "Foreign registered app removed");
        Require(Read(root, @"Software\RegisteredApplications", RegisteredName) == null, "Own registered app remains");
        Require(Read(root, Capabilities + @"\ForeignChild", "data") == "retain", "Foreign child removed");
        using (RegistryKey retained = root.OpenSubKey(Capabilities)!)
            Require(Equals(retained.GetValue("ForeignValue"), 17), "Foreign capability value removed");
        Require(Read(root, FileExts + @"\UserChoice", "Hash") == "foreign-hash", "Foreign UserChoice altered");
        Require(!Exists(root, Handler), "Owned handler remains");
        RemoveRegistration(root);
        Require(Read(root, FileExts + @"\UserChoice", "Hash") == "foreign-hash", "Repeated unregister altered choice");
    });

    private static void CheckOwnDefault() => InSandbox(root =>
    {
        Apply(root, Plan(@"C:\editor.exe"));
        Write(root, @"Software\Classes\.tvep", "", ProgId.ToUpperInvariant());
        RemoveRegistration(root);
        Require(Read(root, @"Software\Classes\.tvep", "") == null, "Own legacy extension default remains");
        Require(!Exists(root, Capabilities), "Empty capabilities remain");
    });

    private static void CheckForeignHandler() => InSandbox(root =>
    {
        Apply(root, Plan(@"C:\editor.exe"));
        Write(root, Handler, "", "Foreign editor description");
        Expect<InvalidDataException>(() => Call("DefaultAppUnregistrationService", "RemoveFormatRegistration", root, Extension));
        Require(Read(root, Handler, "") == "Foreign editor description", "Foreign handler changed");
        Require(Read(root, @"Software\Classes\.tvep\OpenWithProgids", ProgId) == "", "Candidate removed before ownership validation");
    });

    private static void CheckForeignCapabilities() => InSandbox(root =>
    {
        Write(root, Capabilities, "ApplicationName", "Foreign app");
        Write(root, @"Software\RegisteredApplications", RegisteredName, @"Software\Foreign\Capabilities");
        Expect<InvalidDataException>(() => Call("DefaultAppUnregistrationService", "RemoveApplicationRegistration", root));
        Require(Read(root, Capabilities, "ApplicationName") == "Foreign app", "Foreign capabilities altered");
        Require(Read(root, @"Software\RegisteredApplications", RegisteredName) == @"Software\Foreign\Capabilities", "Foreign capabilities reference altered");
    });

    private static void CheckUnknownAssociations() => InSandbox(root =>
    {
        Apply(root, Plan(@"C:\editor.exe"));
        Call("DefaultAppUnregistrationService", "RemoveFormatRegistration", root, Extension);
        Write(root, Capabilities + @"\FileAssociations", ".foreign", "Foreign.Editor");
        Expect<InvalidDataException>(() => Call("DefaultAppUnregistrationService", "RemoveApplicationRegistration", root));
        Require(Read(root, Capabilities + @"\FileAssociations", ".foreign") == "Foreign.Editor", "Unknown association removed");
        Require(Read(root, @"Software\RegisteredApplications", RegisteredName) == Capabilities, "Application unregistered despite unknown association");
    });

    private static void CheckChoices(string classic, string latest) => InSandbox(root =>
    {
        WriteChoices(root, classic, latest);
        ClearChoices(root, _ => HasOurChoice(root));
        Require(Exists(root, FileExts + @"\UserChoice") == (classic != ProgId), "Classic ownership cleanup differs");
        Require(Exists(root, FileExts + @"\UserChoiceLatest") == (latest != ProgId), "Latest ownership cleanup differs");
        if (classic != ProgId) Require(Read(root, FileExts + @"\UserChoice", "Hash") == "classic-hash", "Foreign classic data changed");
        if (latest != ProgId) Require(Read(root, FileExts + @"\UserChoiceLatest", "Hash") == "latest-hash", "Foreign latest data changed");
        using (RegistryKey association = root.OpenSubKey(FileExts)!)
            Require(!association.GetSubKeyNames().Any(name => name.StartsWith("TvepUnregister")), "Successful cleanup leaves transaction backups");
        ClearChoices(root, _ => HasOurChoice(root));
        Require(!HasOurChoice(root), "Repeated choice cleanup differs");
    });

    private static void CheckChoiceRollback() => InSandbox(root =>
    {
        WriteChoices(root, ProgId, ProgId);
        Write(root, FileExts + @"\UserChoice", "extra", 29, RegistryValueKind.DWord);
        Write(root, FileExts + @"\UserChoiceLatest\ProgId", "extra", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        ExpectStage("UNRAS0003", () => ClearChoices(root, _ => true));
        Require(Read(root, FileExts + @"\UserChoice", "ProgId") == ProgId, "Classic key not restored");
        Require(Read(root, FileExts + @"\UserChoice", "Hash") == "classic-hash", "Classic hash not preserved");
        Require(Read(root, FileExts + @"\UserChoiceLatest\ProgId", "ProgId") == ProgId, "Latest key not restored");
        Require(Read(root, FileExts + @"\UserChoiceLatest", "Hash") == "latest-hash", "Latest hash not preserved");
        using (RegistryKey classic = root.OpenSubKey(FileExts + @"\UserChoice")!)
            Require(Equals(classic.GetValue("extra"), 29) && classic.GetValueKind("extra") == RegistryValueKind.DWord, "Classic value kind lost");
        using (RegistryKey latest = root.OpenSubKey(FileExts + @"\UserChoiceLatest\ProgId")!)
            Require(latest.GetValue("extra") is byte[] bytes && bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "Latest binary value lost");
        using RegistryKey association = root.OpenSubKey(FileExts)!;
        Require(association.GetSubKeyNames().Order().SequenceEqual(new[] { "UserChoice", "UserChoiceLatest" }), "Rollback leaves renamed keys");
    });

    private static void CheckOrphans() => InSandbox(root =>
    {
        string ours = "TvepUnregister" + Guid.NewGuid().ToString("N");
        string latest = "TvepUnregister" + Guid.NewGuid().ToString("N");
        string foreign = "TvepUnregister" + Guid.NewGuid().ToString("N");
        Write(root, FileExts + "\\" + ours, "ProgId", ProgId);
        Write(root, FileExts + "\\" + latest + @"\ProgId", "ProgId", ProgId);
        Write(root, FileExts + "\\" + foreign, "ProgId", "Foreign.Editor");
        Write(root, FileExts + @"\TvepUnregisterDoNotTouch", "ProgId", ProgId);
        ClearChoices(root, _ => false);
        Require(!Exists(root, FileExts + "\\" + ours), "Own classic orphan remains");
        Require(!Exists(root, FileExts + "\\" + latest), "Own latest orphan remains");
        Require(Exists(root, FileExts + "\\" + foreign), "Foreign orphan deleted");
        Require(Exists(root, FileExts + @"\TvepUnregisterDoNotTouch"), "Unrecognized key deleted");
    });

    private static void CheckMissingChoice() => InSandbox(root =>
    {
        ClearChoices(root, _ => false);
        Require(!Exists(root, FileExts), "Missing association unexpectedly created");
        ExpectStage("UNRAS0003", () => ClearChoices(root, _ => true));
        ExpectStage("DEFAS0001", () => Call("DefaultAppUnregistrationService", "ClearOurChoices", root, ".mp4", (Func<string, bool>)(_ => false), false));
    });

    private static void CheckClassicHash()
    {
        const string sid = "S-1-5-21-111111111-222222222-333333333-1001";
        const string experience = "User Choice set via Windows User Experience {D18B6DD5-6124-4341-9318-804003BAFA0B}";
        DateTime timestamp = new(2026, 10, 18, 2, 10, 12, DateTimeKind.Utc);
        string Hash(string extension, string identity, string progId, DateTime time) =>
            (string)Call("SftaUserChoice", "GenerateHash", extension, identity, progId, time, experience)!;
        string hash = Hash(Extension, sid, ProgId, timestamp);
        Require(Convert.FromBase64String(hash).Length == 8, "Classic hash size differs");
        Require(Hash(Extension.ToUpperInvariant(), sid.ToLowerInvariant(), ProgId.ToUpperInvariant(), timestamp) == hash, "Hash canonicalization differs");
        Require(Hash(Extension, sid, ProgId, timestamp.AddSeconds(40)) == hash, "Hash is not stable within a minute");
        Require(Hash(Extension, sid, ProgId, timestamp.AddMinutes(1)) != hash, "Hash ignores timestamp");
        Require(Hash(".other", sid, ProgId, timestamp) != hash, "Hash ignores extension");
        Require(Hash(Extension, sid + "0", ProgId, timestamp) != hash, "Hash ignores SID");
        Require(Hash(Extension, sid, "Foreign.Editor", timestamp) != hash, "Hash ignores ProgID");
    }

    private static void CheckNativeResource()
    {
        foreach (string name in new[] { "TANGERINE_VideoEditor.SFTA.MIT.txt", "TANGERINE_VideoEditor.UserChoiceLatestHash.MIT.txt" })
        {
            using Stream stream = Product.GetManifestResourceStream(name) ?? throw new InvalidDataException(name);
            using StreamReader reader = new(stream);
            Require(reader.ReadToEnd().Contains("MIT", StringComparison.OrdinalIgnoreCase), "Missing native dependency license");
        }
        using Stream native = Product.GetManifestResourceStream("TANGERINE_VideoEditor.TvepLatestHash.dll")
            ?? throw new InvalidDataException("Native latest hash resource absent");
        using PEReader pe = new(native);
        Require(pe.PEHeaders.CoffHeader.Machine == System.Reflection.PortableExecutable.Machine.Amd64, "Native hash resource must be x64");
        Require(pe.PEHeaders.PEHeader!.ExportTableDirectory.Size > 0, "Native hash export table missing");
        Require((pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0, "Native resource is not a DLL");
    }

    private static void CheckLatestHash()
    {
        const string sid = "S-1-5-21-111111111-222222222-333333333-1001";
        long time = new DateTime(2026, 10, 18, 2, 10, 12, 123, DateTimeKind.Utc).ToFileTimeUtc();
        string Hash(string machine, long timestamp, string progId = ProgId) =>
            (string)Call("LatestUserChoice", "ComputeHash", Extension, sid, machine, progId, timestamp)!;
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string machine in new[] { "11111111-2222-3333-4444-555555555550", "11111111-2222-3333-4444-555555555551", "11111111-2222-3333-4444-555555555552" })
        {
            string hash = Hash(machine, time);
            Require(Convert.FromBase64String(hash).Length == 8, "Latest native hash size differs");
            Require(Hash(machine, time + 9999) == hash, "Latest hash does not discard sub-millisecond ticks");
            Require(Hash(machine, time + 10000) != hash, "Latest hash ignores timestamp");
            Require(Hash(machine, time, "Foreign.Editor") != hash, "Latest hash ignores ProgID");
            hashes.Add(hash);
        }
        Require(hashes.Count == 3, "Latest hash ignores machine identity/salt branch");
    }

    private static void CheckUi()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            Pump(CheckOpenProjectAsync());
            var about = new AboutWindow();
            try
            {
                Require(Math.Abs(about.Width / SystemParameters.WorkArea.Width - .4) < .001, "About size is not proportional");
                var details = ((Grid)about.Content).Children.OfType<TextBox>().Single();
                Require(details.Text.Contains("v1.0.0", StringComparison.Ordinal) && details.IsReadOnly && details.IsReadOnlyCaretVisible,
                    "About version is absent or cannot be selected");
                RenderDialog(about, "about", new Size(480, 220));
            }
            finally { about.Close(); }

            var associations = (Window)Activator.CreateInstance(Product.GetType("TANGERINE_VideoEditor.DefaultOpenWithWindow", true)!)!;
            try
            {
                Require(Math.Abs(associations.Width / SystemParameters.WorkArea.Width - .46) < .001, "Association dialog size is not proportional");
                var layout = (Grid)associations.Content;
                Button[] buttons = Descendants<Button>(layout).ToArray();
                Require(buttons.Length == 6, "Association actions missing");
                string[] labels = buttons.Select(button => button.Content is TextBlock text ? text.Text : button.Content?.ToString() ?? "").ToArray();
                foreach (string label in new[] { "使用本应用", "仅注册打开方式", "打开 Windows 默认应用设置", "取消设置并删除注册", "刷新状态", "关闭" })
                    Require(labels.Contains(label), "Association action missing: " + label);
                RenderDialog(associations, "default-open-with", new Size(640, 440));
                CheckDialogButtonBounds(associations, new Size(480, 340));
            }
            finally { associations.Close(); }
        }
        finally { app.Shutdown(); }
    }

    private static async Task CheckOpenProjectAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TangerineVideoEditor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var window = new MainWindow();
        try
        {
            Require(window.Title.Contains("v1.0.0", StringComparison.Ordinal), "Main title version missing");
            Require(((TextBlock)window.FindName("VersionText")).Text == "v1.0.0", "Workspace version missing");
            Require(window.FindName("DefaultOpenWithMenuItem") is MenuItem, "Association menu entry missing");
            var fixture = ProjectDocument.CreateDefault();
            fixture.Name = "测试项目：中文和 spaces";
            fixture.Sequence.Width = 720;
            fixture.Sequence.Height = 1280;
            fixture.Sequence.FramesPerSecond = 24;
            string path = Path.Combine(directory, "含有 空格 与中文.TVeP");
            await ProjectFileService.SaveAsync(fixture, path);
            var open = typeof(MainWindow).GetMethod("OpenProjectPathAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)open.Invoke(window, [path, true])!;
            var loaded = (ProjectDocument)typeof(MainWindow).GetField("_project", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Require(loaded.Id == fixture.Id && loaded.Name == fixture.Name, "Project path did not load the actual serialized project");
            Require(loaded.Sequence.Width == 720 && loaded.Sequence.Height == 1280 && loaded.Sequence.FramesPerSecond == 24,
                "Project loading lost sequence values");
            Require(Equals(typeof(MainWindow).GetField("_projectPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window), path),
                "Quoted Explorer project path was not kept intact");
            Require(((TextBlock)window.FindName("ProjectNameText")).Text == Path.GetFileName(path), "Loaded project chrome differs");
            Require(((TextBlock)window.FindName("StatusText")).Text.Contains(Path.GetFileName(path), StringComparison.Ordinal), "Project load status missing filename");
            Require(Equals(typeof(MainWindow).GetField("_isProjectDirty", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window), false),
                "Opening a project unexpectedly marks it dirty");
            await ProjectFileService.ReleaseCachedMediaAsync(loaded);
        }
        finally
        {
            foreach (FieldInfo field in typeof(MainWindow).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                if (field.GetValue(window) is DispatcherTimer timer) timer.Stop();
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timeout.Tick += (_, _) => frame.Continue = false;
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        if (!task.IsCompleted) throw new TimeoutException("STA project integration test did not finish");
        task.GetAwaiter().GetResult();
    }

    private static void RenderDialog(Window window, string name, Size size)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        byte[] pixels = new byte[(int)size.Width * (int)size.Height * 4];
        bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
        Require(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0), "Dialog rendering is blank");
        string artifacts = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(artifacts, name + ".png"));
        encoder.Save(file);
        CheckDialogButtonBounds(window, size);
    }

    private static void CheckDialogButtonBounds(Window window, Size size)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        foreach (Button button in Descendants<Button>(content))
        {
            Point corner = button.TranslatePoint(new Point(), content);
            Require(button.ActualWidth > 0 && button.ActualHeight > 0, "Action has no usable layout");
            Require(corner.X >= -1 && corner.Y >= -1 && corner.X + button.ActualWidth <= size.Width + 1 &&
                corner.Y + button.ActualHeight <= size.Height + 1, "Action exceeds compact dialog bounds");
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(element, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void WriteChoices(RegistryKey root, string classic, string latest)
    {
        Write(root, FileExts + @"\UserChoice", "ProgId", classic);
        Write(root, FileExts + @"\UserChoice", "Hash", "classic-hash");
        Write(root, FileExts + @"\UserChoiceLatest\ProgId", "ProgId", latest);
        Write(root, FileExts + @"\UserChoiceLatest", "Hash", "latest-hash");
    }

    private static bool HasOurChoice(RegistryKey root) =>
        string.Equals(Read(root, FileExts + @"\UserChoice", "ProgId"), ProgId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Read(root, FileExts + @"\UserChoiceLatest\ProgId", "ProgId"), ProgId, StringComparison.OrdinalIgnoreCase);

    private static void ClearChoices(RegistryKey root, Func<string, bool> probe) =>
        Call("DefaultAppUnregistrationService", "ClearOurChoices", root, Extension, probe, false);

    private static void RemoveRegistration(RegistryKey root)
    {
        Call("DefaultAppUnregistrationService", "RemoveFormatRegistration", root, Extension);
        Call("DefaultAppUnregistrationService", "RemoveApplicationRegistration", root);
    }

    private static object[] Plan(string executable) =>
        ((System.Collections.IEnumerable)Call("DefaultAppAssociationService", "BuildRegistrationPlan", Extension, executable)!).Cast<object>().ToArray();

    private static string PlanValue(object[] plan, string path, string name) =>
        Field(plan.Single(item => Field(item, "KeyPath") == path && Field(item, "Name") == name), "Value");

    private static string Field(object item, string name) => (string)item.GetType().GetProperty(name)!.GetValue(item)!;
    private static object Value(string key, string name, string value) => Activator.CreateInstance(Registration, key, name, value)!;

    private static void Apply(RegistryKey root, object[] plan)
    {
        Array typed = Array.CreateInstance(Registration, plan.Length);
        for (int index = 0; index < plan.Length; index++) typed.SetValue(plan[index], index);
        Call("DefaultAppAssociationService", "ApplyRegistrationPlan", root, typed);
    }

    private static object? Call(string name, string method, params object?[] args)
    {
        Type type = Product.GetType(Prefix + name, true)!;
        MethodInfo member = type.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        try { return member.Invoke(null, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static void InSandbox(Action<RegistryKey> test)
    {
        // Every mutable helper receives this disposable root. Never invoke the
        // public operations that use real FileExts, Shell notifications or Settings.
        string path = @"Software\TangerineVideoEditor.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using RegistryKey root = Registry.CurrentUser.CreateSubKey(path, writable: true);
            test(root);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    private static void Write(RegistryKey root, string path, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using RegistryKey key = root.CreateSubKey(path, writable: true);
        key.SetValue(name, value, kind);
    }

    private static string? Read(RegistryKey root, string path, string name)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static bool Exists(RegistryKey root, string path)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        return key != null;
    }

    private static void ExpectStage(string stage, Action test)
    {
        StageException error = Expect<StageException>(test);
        Require(error.StageCode == stage, $"Expected {stage}, got {error.StageCode}");
    }

    private static T Expect<T>(Action test) where T : Exception
    {
        try { test(); }
        catch (T error) { Assertions++; return error; }
        throw new InvalidDataException($"Expected {typeof(T).Name}");
    }

    private static void Require(bool condition, string message)
    {
        Assertions++;
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Run(string name, Action test)
    {
        Cases++;
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failures.Add($"FAIL {name}: {error}"); }
    }
}
