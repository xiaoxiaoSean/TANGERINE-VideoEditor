using Microsoft.Win32;
using System.IO;
using TANGERINE_VideoEditor.Tools;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace TANGERINE_VideoEditor.SystemIntegration;

internal sealed record AssociationFormat(string Label, IReadOnlyList<string> Extensions);
internal sealed record AssociationRegistrationValue(string KeyPath, string Name, string Value);
internal sealed record DefaultSettingsLaunch(string Uri, IReadOnlyList<StageException> Warnings);

// Stage head: DEFAS (DefaultAppAssociationService).
// Candidate registration and the explicit Settings handoff live here. The
// automatic action picks classic SFTA or UserChoiceLatest according to the
// current user's Windows hash version and verifies the effective association.
internal static class DefaultAppAssociationService
{
    internal const string RegisteredApplicationName = "TANGERINE VideoEditor";
    private const string CapabilitiesPath = @"Software\TangerineVideoEditor\DefaultApps\Capabilities";

    internal const string ProjectExtension = ".tvep";
    internal const string ProjectProgId = "TangerineVideoEditor.Project.tvep";
    internal const string FileTypeDescription = "TANGERINE VideoEditor 项目";

    // Only the editor's native project format is advertised. Media extensions
    // keep their existing players and are never included in an association plan.
    internal static IReadOnlyList<AssociationFormat> Formats { get; } = Array.AsReadOnly(new[]
    {
        new AssociationFormat("TVEP 项目", Array.AsReadOnly(new[] { ProjectExtension }))
    });

    private static void ValidateExtension(string extension)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) ||
            !Formats.Any(format => format.Extensions.Contains(extension, StringComparer.Ordinal)))
            throw new StageException("DEFAS0001", "此功能需要 Windows 10 或 Windows 11，且仅支持 .tvep 项目文件。"); //DEFAS0001
    }

    internal static string GetProgId(string extension)
    {
        ValidateExtension(extension);
        return ProjectProgId;
    }

    internal static void SetThisAppDefault(string extension, string executablePath)
    {
        ValidateExtension(extension);
        RegisterHandler(extension, executablePath);
        string progId = GetProgId(extension);
        if (string.Equals(QueryCurrentProgId(extension), progId, StringComparison.OrdinalIgnoreCase)) return;
        if (LatestUserChoice.IsLatestFormatActive(extension))
            LatestUserChoice.SetDefault(extension, progId, () => QueryCurrentProgId(extension));
        else
            SftaUserChoice.SetDefault(extension, progId, () => QueryCurrentProgId(extension));
    }

    internal static string GetExecutablePath()
    {
        // Environment.ProcessPath also handles renamed release apphosts and
        // single-file publishing, where Assembly.Location is empty. A dotnet.exe
        // host is not a usable handler: Windows must launch the application itself.
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new StageException("DEFAS0002", "无法注册程序的可执行文件。请运行本软件的 EXE，不要通过 dotnet.exe 运行 DLL。"); //DEFAS0002
        return Path.GetFullPath(path);
    }

    // The explicit write plan makes the registration scope reviewable and testable
    // without changing actual associations. Extension default values are untouched;
    // OpenWithProgids advertises a candidate rather than choosing a default.
    internal static IReadOnlyList<AssociationRegistrationValue> BuildRegistrationPlan(string extension, string executablePath)
    {
        string progId = GetProgId(extension);
        string handler = @"Software\Classes\" + progId;
        // Use Windows' plain white document icon on our ProgID. The extension
        // itself receives no icon override, so another app's choice is unaffected.
        string fileIcon = $"\"{Path.Combine(Environment.SystemDirectory, "shell32.dll")}\",0";
        string applicationIcon = $"\"{executablePath}\",0";
        return new AssociationRegistrationValue[]
        {
            new(handler, "", FileTypeDescription),
            new(handler + @"\DefaultIcon", "", fileIcon),
            new(handler + @"\shell", "", "open"),
            new(handler + @"\shell\open\command", "", $"\"{executablePath}\" \"%1\""),
            new(@"Software\Classes\" + extension + @"\OpenWithProgids", progId, ""),
            new(CapabilitiesPath, "ApplicationName", RegisteredApplicationName),
            new(CapabilitiesPath, "ApplicationDescription", "打开和编辑 TANGERINE VideoEditor 视频项目"),
            new(CapabilitiesPath, "ApplicationIcon", applicationIcon),
            new(CapabilitiesPath + @"\FileAssociations", extension, progId),
            new(@"Software\RegisteredApplications", RegisteredApplicationName, CapabilitiesPath)
        };
    }

    private sealed record SavedValue(string KeyPath, string Name, bool Existed, object? Value, RegistryValueKind Kind);

    internal static void RegisterHandler(string extension, string executablePath)
    {
        ValidateExtension(extension);
        if (!File.Exists(executablePath) || !Path.IsPathFullyQualified(executablePath) ||
            !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executablePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new StageException("DEFAS0002", "无法注册程序的可执行文件。请运行本软件的 EXE，不要通过 dotnet.exe 运行 DLL。"); //DEFAS0002
        Mutex? gate = null;
        bool acquired = false;
        try
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                string sid = identity.User?.Value ?? throw new InvalidOperationException("其他实例可能正在注册默认打开程序，或注册锁不可用。请稍后重试。");
                gate = new Mutex(false, @"Local\TangerineVideoEditor.DefaultApps." + sid);
                try { acquired = gate.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException)
                {
                    // We own an abandoned mutex. A terminated process may have left
                    // a partial candidate registration; applying the entire plan again
                    // repairs it. No protected default choice is modified by either run.
                    acquired = true;
                }
                if (!acquired) throw new TimeoutException("其他实例可能正在注册默认打开程序，或注册锁不可用。请稍后重试。");
            }
            catch (Exception exception)
            {
                throw new StageException("DEFAS0009", "其他实例可能正在注册默认打开程序，或注册锁不可用。请稍后重试。", exception); //DEFAS0009
            }
            ApplyRegistrationPlan(Registry.CurrentUser, BuildRegistrationPlan(extension, executablePath));
            try
            {
                // Invalidate Shell's association cache after all readbacks succeed.
                // This informs Explorer of a new candidate; it does not choose that app.
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED / SHCNF_IDLIST
            }
            catch (Exception exception)
            {
                throw new StageException("DEFAS0008", "注册已写入，但无法通知资源管理器。请重试以刷新可用打开程序。", exception); //DEFAS0008
            }
        }
        finally
        {
            // Mutex ownership is thread-affine. This method is synchronous and its
            // caller runs it wholly on one worker thread, including rollback/release.
            if (acquired) gate!.ReleaseMutex();
            gate?.Dispose();
        }
    }

    // Accept a registry root so the journal can also be exercised under an isolated
    // disposable test key. Production always passes Registry.CurrentUser; no HKLM
    // write path is exposed by the window, and this helper does not notify the Shell.
    internal static void ApplyRegistrationPlan(RegistryKey root, IEnumerable<AssociationRegistrationValue> plan)
    {
        List<SavedValue> journal = [];
        try
        {
            foreach (AssociationRegistrationValue item in plan)
            {
                using RegistryKey key = root.CreateSubKey(item.KeyPath, writable: true)
                    ?? throw new IOException("为当前用户注册可选打开程序失败。请检查注册表权限或组织策略。原有默认选择未被覆盖。");
                bool existed = key.GetValueNames().Contains(item.Name, StringComparer.OrdinalIgnoreCase);
                // Record before writing: even SetValue can fail after some previous
                // writes succeeded. Preserve both the original value and its kind.
                journal.Add(new(item.KeyPath, item.Name, existed,
                    existed ? key.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null,
                    existed ? key.GetValueKind(item.Name) : RegistryValueKind.String));
                key.SetValue(item.Name, item.Value, RegistryValueKind.String);
                if (key.GetValueKind(item.Name) != RegistryValueKind.String ||
                    !Equals(key.GetValue(item.Name), item.Value))
                    throw new IOException("为当前用户注册可选打开程序失败。请检查注册表权限或组织策略。原有默认选择未被覆盖。");
            }
        }
        catch (Exception registrationError)
        {
            List<Exception> rollbackErrors = [];
            foreach (SavedValue saved in journal.AsEnumerable().Reverse())
            {
                try
                {
                    using RegistryKey key = root.CreateSubKey(saved.KeyPath, writable: true)
                        ?? throw new IOException("注册失败，部分注册值无法恢复。可选程序注册可能不完整；解决注册表权限问题后请重试。原有默认选择未被覆盖。");
                    if (saved.Existed) key.SetValue(saved.Name, saved.Value!, saved.Kind);
                    else key.DeleteValue(saved.Name, throwOnMissingValue: false);
                    // Do not delete ancestor keys: other applications or an instance
                    // of this app may have added values since the snapshot was taken.
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new StageException("DEFAS0007", "注册失败，部分注册值无法恢复。可选程序注册可能不完整；解决注册表权限问题后请重试。原有默认选择未被覆盖。",
                    new AggregateException(new[] { registrationError }.Concat(rollbackErrors))); //DEFAS0007
            throw new StageException("DEFAS0003", "为当前用户注册可选打开程序失败。请检查注册表权限或组织策略。原有默认选择未被覆盖。", registrationError); //DEFAS0003
        }
    }

    internal static string BuildSettingsUri(bool useThisApp, int build, int revision)
    {
        // App-specific Settings URIs were added by the April 2023 cumulative
        // updates, not by the initial Windows 11 release. Never assume all builds
        // >= 22000 understand registeredAppUser. Windows 10 and unpatched 21H2 /
        // 22H2 use the general page and the window supplies the current extension.
        bool appPageSupported = build >= 22631 || (build == 22621 && revision >= 1555) ||
            (build == 22000 && revision >= 1817);
        return useThisApp && appPageSupported
            ? "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(RegisteredApplicationName)
            : "ms-settings:defaultapps";
    }

    internal static DefaultSettingsLaunch OpenWindowsSettings(bool useThisApp)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new StageException("DEFAS0001", "此功能需要 Windows 10 或 Windows 11，且仅支持 .tvep 项目文件。"); //DEFAS0001
        List<StageException> warnings = [];
        int build = Environment.OSVersion.Version.Build;
        int revision = 0;
        if (useThisApp && build is 22000 or 22621)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                revision = key?.GetValue("UBR") is int value ? value : 0;
            }
            catch (Exception exception)
            {
                // Failing to read the OS revision does not prevent using the
                // general Settings page. Surface the diagnostic instead of silently
                // assuming a newer UI protocol or asking for unnecessary elevation.
                warnings.Add(new StageException("DEFAS0004", "无法读取 Windows 更新修订号，改用通用默认应用设置页。", exception)); //DEFAS0004
            }
        }
        string uri = BuildSettingsUri(useThisApp, build, revision);
        try { Launch(uri); }
        catch (Exception exception) when (uri != "ms-settings:defaultapps")
        {
            warnings.Add(new StageException("DEFAS0005", "无法打开本软件专属设置页，改用通用默认应用设置页。", exception)); //DEFAS0005
            uri = "ms-settings:defaultapps";
            Launch(uri);
        }
        // A successful launch is only a handoff. Settings can reuse an existing
        // process, so Process.Start returning null is not a failed default change.
        return new(uri, warnings);
    }

    private static void Launch(string uri)
    {
        try { using Process? process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            throw new StageException("DEFAS0005", "无法打开 Windows 默认应用设置。您可以手动打开系统设置后重试，或跳过此扩展名。", exception); //DEFAS0005
        }
    }

    internal static string? QueryCurrentProgId(string extension)
    {
        ValidateExtension(extension);
        try
        {
            // Query the effective Shell association including the user's choice,
            // rather than mistaking our capability registration for the default.
            uint length = 0;
            int result = AssocQueryString(0, 20, extension, null, null, ref length); // ASSOCSTR_PROGID
            if (result is unchecked((int)0x80070483) or unchecked((int)0x80070002)) return null;
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (length is 0 or > 32768) throw new InvalidDataException("无法查询实际生效的默认应用。此格式不会被报告为设置成功。");
                StringBuilder buffer = new((int)length);
                result = AssocQueryString(0, 20, extension, null, buffer, ref length);
                if (result == 0) return buffer.ToString();
                if (result is unchecked((int)0x80070483) or unchecked((int)0x80070002)) return null;
                // Retry only a buffer-size race caused by a simultaneous Settings
                // change; other HRESULTs are actionable errors, not "not this app".
                if (result != 1 && result != unchecked((int)0x80004003)) Marshal.ThrowExceptionForHR(result);
            }
            throw new InvalidDataException("无法查询实际生效的默认应用。此格式不会被报告为设置成功。");
        }
        catch (Exception exception)
        {
            throw new StageException("DEFAS0006", "无法查询实际生效的默认应用。此格式不会被报告为设置成功。", exception); //DEFAS0006
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryString(uint flags, uint str, string association, string? extra,
        StringBuilder? output, ref uint length);
}
