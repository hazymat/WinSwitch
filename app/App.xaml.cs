using System.Windows;
using WinSwitch.Power;
using WinSwitch.Rules;
using WinSwitch.Settings;
using WinSwitch.Shell;
namespace WinSwitch;

// A tray app with no main window: the engine, the tray menu, notification cards and Settings (SPEC.md › What it is)
public partial class App : Application
{
    public static App Instance => (App)Current;
    public AppSettings Settings { get; private set; } = null!;
    public AppUpdater Updater { get; } = new();
    public Notices Notices { get; private set; } = null!;
    public Engine Engine { get; } = new();
    public Apps.AppStopper Apps { get; } = new();
    public Remote.PhoneServer Phone { get; } = new();
    public Remote.MqttLink Mqtt { get; private set; } = null!;
    public Snapshot? Snapshot { get; private set; }
    public bool IsQuitting { get; private set; }
    public event Action? SettingsChanged;
    SettingsWindow? settingsWindow;
    WizardWindow? wizardWindow;
    Mutex? singleInstanceMutex;
    TrayIcon? trayIcon;

    protected override async void OnStartup(StartupEventArgs startupArgs)
    {
        // One copy per data folder; a second launch opens Settings in the running one
        var instanceName = "WinSwitch" + (AppPaths.IsPortable ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataFolder.ToLowerInvariant())))[..12] : "");
        singleInstanceMutex = new Mutex(true, instanceName + ".SingleInstance", out var isFirstInstance);
        var openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".OpenSettings");
        if (!isFirstInstance) { openSignal.Set(); Shutdown(); return; }
        ThreadPool.RegisterWaitForSingleObject(openSignal, (state, timedOut) => Dispatcher.BeginInvoke(() => OpenSettings()), null, -1, false);
        if (AppPaths.IsPortable) AppUpdater.CleanUpAfterPortableUpdate();
        CrashReporter.Install(this);
        Settings = AppSettings.Load();
        try { StartupRegistration.Apply(Settings.General.StartWithWindows && !AppPaths.IsPortable); } catch { }
        Notices = new Notices();
        Updater.Start();
        if (Settings.General.LastRunVersion != Updater.CurrentVersion) { Settings.General.LastRunVersion = Updater.CurrentVersion; Settings.Save(); }
        // First run: read everything before changing anything (SPEC.md › First run)
        Snapshot = Snapshot.Load();
        CaptureWindow? captureWindow = null;
        if (Snapshot == null)
        {
            // The capture screen shows everything being read; the wizard follows when it's closed (Mat 17 Nov)
            captureWindow = new CaptureWindow(); captureWindow.Show();
            Snapshot = await captureWindow.CaptureAsync();
            Snapshot.Save();
            var starting = Templates.Starting(Snapshot);
            Settings.Profiles.Insert(0, starting);
            if (Settings.DefaultProfileId == "") Settings.DefaultProfileId = starting.Id;
            Settings.Save();
        }
        trayIcon = new TrayIcon();
        Engine.Start();
        Mqtt = new Remote.MqttLink();
        Phone.Apply(); _ = Mqtt.ApplyAsync();
        if (CrashReporter.TakeUnseenReport()) Notices.Info("WinSwitch had a problem last time", "Details are in Settings › About › Open logs (crash.log).", always: true);
        if (!Settings.General.WizardDone) { if (captureWindow?.IsVisible == true) captureWindow.Closed += (sender, args) => OpenWizard(); else OpenWizard(); }
    }

    public void NotifySettingsChanged() { SettingsChanged?.Invoke(); Engine.EvaluateSoon(); }

    public void OpenSettings(string tabName = "", string profileId = "")
    {
        if (settingsWindow == null) { settingsWindow = new SettingsWindow(); settingsWindow.Closed += (sender, args) => settingsWindow = null; }
        settingsWindow.SelectTab(tabName, profileId);
        settingsWindow.Show(); if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate();
    }

    public void OpenWizard()
    {
        if (wizardWindow == null) { wizardWindow = new WizardWindow(); wizardWindow.Closed += (sender, args) => wizardWindow = null; }
        wizardWindow.Show(); wizardWindow.Activate();
    }

    // A dock or monitor never seen before: a new profile (a copy of the one on now) and a situation that picks it, then Settings to adjust it
    public void SetUpNewPlace(string name, Dictionary<string, string> things)
    {
        var source = Engine.ActiveProfile;
        var profile = new Profile { Name = name, Colour = Templates.Colours[Settings.Profiles.Count % Templates.Colours.Length], Values = source?.Values.ToDictionary(entry => entry.Key, entry => entry.Value.Clone()) ?? new(), SpeedAc = source?.SpeedAc, SpeedDc = source?.SpeedDc };
        Settings.Profiles.Add(profile);
        Settings.Situations.Insert(0, new Situation { Kind = "dock", Name = name, Things = things, ProfileId = profile.Id });
        Settings.Save(); NotifySettingsChanged();
        OpenSettings("Profiles", profile.Id);
    }

    // Settings › Original settings › Restore everything (SPEC.md › Original settings)
    public async Task RestoreEverythingAsync()
    {
        Engine.Pause();
        var snapshot = Snapshot;
        await Task.Run(() => Restorer.RestoreEverything(snapshot, deletePlan: false)); // off the UI thread: the radio calls wait on Windows
        await Apps.StartEverythingAgainAsync();
        Notices.Info("Back as it was before WinSwitch", "WinSwitch is paused until you resume it.", Engine.Resume, "Resume", always: true);
        SettingsChanged?.Invoke();
    }

    public Task<string[]> ResumeArgumentsAsync() => Task.FromResult(Array.Empty<string>());
    public void PrepareForRestart()
    {
        Settings.Save(); IsQuitting = true;
        singleInstanceMutex?.ReleaseMutex(); singleInstanceMutex?.Dispose(); singleInstanceMutex = null;
    }

    public void Quit()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        try { Phone.Stop(); Mqtt?.StopAsync().Wait(1500); } catch { }
        try { Settings.Save(); settingsWindow?.Close(); wizardWindow?.Close(); PowerActions.StopKeepingAwake(); trayIcon?.Dispose(); trayIcon = null; }
        catch (Exception quitError) { CrashReporter.Write("While quitting", quitError, false); }
        finally { Shutdown(); }
    }
}
