using Velopack;
using WinSwitch.Power;
namespace WinSwitch;

public static class Program
{
    [STAThread]
    public static void Main(string[] commandLineArgs)
    {
        // Must run first: handles install, update and uninstall hooks. Uninstalling puts the PC back as it was before WinSwitch (SPEC.md › Original settings)
        VelopackApp.Build().OnBeforeUninstallFastCallback(version => { try { Restorer.RestoreEverything(Snapshot.Load(), deletePlan: true); } catch { } try { Apps.AppStopper.StartAllAgain(Settings.AppSettings.Load()); } catch { } }).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
