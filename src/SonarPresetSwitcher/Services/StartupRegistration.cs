using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace SonarPresetSwitcher.Services;

/// <summary>Per-user "start with Windows" toggle. Writes to HKCU, so no elevation is required.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SonarPresetSwitcher";
    private const string MinimizedArg = "--minimized";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                var command = BuildLaunchCommand();
                if (command is not null)
                    key.SetValue(ValueName, command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Leave the toggle reflecting reality on the next read.
        }
    }

    /// <summary>
    /// Rewrites a registered command that can no longer launch the app. Two ways that happens: the exe
    /// was moved or reinstalled elsewhere, and — the one that bites during development — the toggle was
    /// ticked while running under <c>dotnet SonarPresetSwitcher.dll</c>, which used to record the bare
    /// muxer path. Windows then ran <c>dotnet.exe --minimized</c> at boot and it died silently.
    ///
    /// Only broken commands are touched, so running a dev build does not steal the entry from an
    /// installed copy. Call once at startup, before the view model reads <see cref="IsEnabled"/>.
    /// </summary>
    public static void RepairIfBroken()
    {
        try
        {
            using var probe = Registry.CurrentUser.OpenSubKey(RunKey);
            if (probe?.GetValue(ValueName) is not string existing || IsLaunchable(existing))
                return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        SetEnabled(true);
    }

    /// <summary>
    /// The command Windows should run at logon. Prefers the apphost — the <c>.exe</c> sitting next to
    /// the managed dll — so the recorded path survives whichever way this process was started. Falls
    /// back to the muxer plus an explicit dll path when there is no apphost (framework-dependent
    /// layouts that ship dll-only).
    /// </summary>
    private static string? BuildLaunchCommand()
    {
        var host = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

        if (host is not null && !IsDotnetMuxer(host))
            return $"\"{host}\" {MinimizedArg}";

        // Built from AppContext.BaseDirectory rather than Assembly.Location: the latter is empty for a
        // single-file app (IL3000). This branch is unreachable there — the host is the apphost, not the
        // muxer — but the analyzer cannot see that, and BaseDirectory is correct either way.
        var appName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.IsNullOrEmpty(appName))
        {
            var baseDir = AppContext.BaseDirectory;

            var appHost = Path.Combine(baseDir, appName + ".exe");
            if (File.Exists(appHost))
                return $"\"{appHost}\" {MinimizedArg}";

            var assemblyPath = Path.Combine(baseDir, appName + ".dll");
            if (host is not null && File.Exists(assemblyPath))
                return $"\"{host}\" \"{assemblyPath}\" {MinimizedArg}";
        }

        return string.IsNullOrWhiteSpace(host) ? null : $"\"{host}\" {MinimizedArg}";
    }

    /// <summary>
    /// True when the recorded command still names something Windows can actually start. A bare muxer
    /// path with no dll argument is the specific shape that silently does nothing at boot.
    /// </summary>
    private static bool IsLaunchable(string command)
    {
        var target = FirstToken(command);
        if (target is null || !File.Exists(target))
            return false;

        // Only reached when the target is the muxer, whose own path never contains ".dll", so anything
        // matching here came from the arguments.
        return !IsDotnetMuxer(target) || command.Contains(".dll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts the executable from a Run command, quoted or not.</summary>
    private static string? FirstToken(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
            return null;

        if (trimmed[0] == '"')
        {
            var close = trimmed.IndexOf('"', 1);
            return close > 1 ? trimmed[1..close] : null;
        }

        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    private static bool IsDotnetMuxer(string path) =>
        string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase);
}
