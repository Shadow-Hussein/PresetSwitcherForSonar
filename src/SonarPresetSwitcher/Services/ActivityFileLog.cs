using System.IO;
using System.Reflection;

namespace SonarPresetSwitcher.Services;

/// <summary>
/// Mirrors the activity log to <c>%APPDATA%\SonarPresetSwitcher\activity.log</c>.
///
/// The in-window log is the one the user reads, but it only exists while the app does, and at logon
/// the window is never shown — <c>MainWindow.Loaded</c> does not fire under <c>--minimized</c>, so
/// nothing about a boot survives to be looked at afterwards. Startup is exactly when this feature has
/// to work and exactly when nobody is watching, so it goes to disk as well.
///
/// Failures are swallowed in the same spirit as <see cref="SettingsStore"/>: logging must never be the
/// reason something breaks.
/// </summary>
public static class ActivityFileLog
{
    /// <summary>Roughly a few thousand lines — enough to cover several boots.</summary>
    private const long MaxBytes = 256 * 1024;

    private static readonly Lock Gate = new();

    public static string FilePath { get; } = Path.Combine(SettingsStore.Directory, "activity.log");

    private static string PreviousFilePath { get; } = FilePath + ".1";

    /// <summary>
    /// Rolls the file if it has grown too large and writes a banner, so the lines that follow can be
    /// told apart from the previous run's.
    /// </summary>
    public static void StartSession(string[] args)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);

            var file = new FileInfo(FilePath);
            if (file.Exists && file.Length > MaxBytes)
                File.Move(FilePath, PreviousFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do; Append will fail the same way and stay just as quiet.
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var arguments = args.Length == 0 ? "none" : string.Join(' ', args);

        Append($"--- started, version {version}, args: {arguments} ---");
    }

    public static void Append(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.Directory);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written is not worth a crash.
        }
    }
}
