using System.IO;
namespace WinSwitch.Settings;

public static class AppPaths
{
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinSwitch.exe");
    public static string ExeFolder => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;
#if PORTABLE
    public const bool IsPortable = true;
#else
    // portable.txt next to the exe also forces portable data, for a hand-copied folder
    public static readonly bool IsPortable = File.Exists(Path.Combine(ExeFolder, "portable.txt"));
#endif
    // Portable: data next to the exe. Installed: outside Velopack's own folder, so an update or reinstall never touches it
    public static readonly string DataFolder = IsPortable ? Path.Combine(ExeFolder, "WinSwitch-data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSwitch-data");
    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string SnapshotFile => Path.Combine(DataFolder, "original-settings.json"); // how the PC was before WinSwitch changed anything
    public static string LogsFolder => Directory.CreateDirectory(Path.Combine(DataFolder, "logs")).FullName;
}
