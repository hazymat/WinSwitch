using Velopack;
using WinSwitch.Power;
namespace WinSwitch;

public static class Program
{
    [STAThread]
    public static void Main(string[] commandLineArgs)
    {
        // Must run first: handles install, update and uninstall hooks. Uninstalling puts the PC back as it was before WinSwitch (SPEC.md › Original settings)
        VelopackApp.Build().OnBeforeUninstallFastCallback(version => { try { Restorer.RestoreEverything(Snapshot.Load(), deletePlan: true); } catch { } try { Apps.AppStopper.StartAllAgain(Settings.AppSettings.Load()); } catch { } try { Shell.HelperClient.DoAsync("uninstall").Wait(30000); } catch { } }).Run();
        // Anything that fails before App.OnStartup installs the crash reporter still lands in crash.log, never a silent exit
        try { var app = new App(); app.InitializeComponent(); app.Run(); }
        catch (Exception startError) { Shell.CrashReporter.Write("Couldn't start", startError); throw; }
    }
}
