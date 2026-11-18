using System.Net.NetworkInformation;
using System.Windows.Interop;
using System.Windows.Threading;
using WinSwitch.Power;
using WinSwitch.Settings;
using WinSwitch.Shell;
namespace WinSwitch.Rules;

// Watches what is plugged in, picks the profile, lays tweaks over it and writes it through Windows; notices changes made in Windows (SPEC.md › Situations, Tweaks, Playing nicely with Windows)
public sealed class Engine
{
    App App => App.Instance;
    AppSettings Settings => App.Settings;
    readonly DispatcherTimer settleTimer = new() { Interval = TimeSpan.FromSeconds(2) }, minuteTimer = new() { Interval = TimeSpan.FromSeconds(30) }, watchTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    HwndSource? messageWindow;
    Inventory? lastInventory;
    bool inventoryStale = true, evaluating, evaluateAgain;
    string lastSignature = "";
    bool? lastPluggedIn;
    readonly Dictionary<string, int> volumeBeforeTweak = new();

    public Inventory Current => lastInventory ?? new Inventory();
    public string SituationId { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public Profile? ActiveProfile { get; private set; }
    public Dictionary<string, SidePair> EffectiveValues { get; private set; } = new(); // what the profile on now sets, Starting profile and tweaks included
    public List<Tweak> ActiveTweaks { get; private set; } = new();
    public HashSet<string> NotSupported { get; } = new(); // settings Windows refused on this PC (hidden in Settings)
    public bool PluggedIn => PowerApi.PowerStatus().pluggedIn;
    public bool Automatic => Settings.Power.HeldProfileId == "";
    public event Action? Changed;
    public event Action<Inventory, Dictionary<string, string>>? ThingsAppeared; // for "Learn what I plug in"

    public void Start()
    {
        // A hidden window hears plug and unplug, screen changes and power changes
        messageWindow = new HwndSource(new HwndSourceParameters("WinSwitch events") { Width = 0, Height = 0, WindowStyle = 0 });
        messageWindow.AddHook((IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message is 0x219 /* WM_DEVICECHANGE */ or 0x7E /* WM_DISPLAYCHANGE */) { inventoryStale = true; EvaluateSoon(); }
            if (message == 0x218 /* WM_POWERBROADCAST */) EvaluateSoon();
            return IntPtr.Zero;
        });
        NetworkChange.NetworkAddressChanged += (sender, args) => App.Dispatcher.BeginInvoke(() => { inventoryStale = true; EvaluateSoon(); });
        Microsoft.Win32.SystemEvents.PowerModeChanged += (sender, args) => App.Dispatcher.BeginInvoke(() => EvaluateSoon()); // a lambda: the method group has an optional parameter, so it went through DynamicInvoke with no arguments and threw
        settleTimer.Tick += async (sender, args) => { settleTimer.Stop(); await EvaluateAsync(); };
        minuteTimer.Tick += async (sender, args) => await EvaluateAsync(); // battery level and tweak schedules
        watchTimer.Tick += (sender, args) => WatchWindowsChanges();
        minuteTimer.Start(); watchTimer.Start();
        EvaluateSoon();
    }

    // Changes are debounced: a dock connecting in stages switches once (SPEC.md › Situations)
    public void EvaluateSoon(bool rescan = false) { if (rescan) inventoryStale = true; settleTimer.Stop(); settleTimer.Start(); }

    public async Task EvaluateAsync(bool force = false)
    {
        if (evaluating) { evaluateAgain = true; return; }
        evaluating = true;
        try
        {
            do
            {
                evaluateAgain = false;
                await EvaluateOnceAsync(force);
                force = false;
            } while (evaluateAgain);
        }
        catch (Exception error) { CrashReporter.Write("Choosing the profile", error, false); }
        finally { evaluating = false; }
    }

    async Task EvaluateOnceAsync(bool force)
    {
        if (inventoryStale || lastInventory == null)
        {
            inventoryStale = false;
            var previous = lastInventory;
            var inventory = await Task.Run(Inventory.Take);
            lastInventory = inventory;
            Dictionary<string, string>? appeared = null;
            if (previous != null)
            {
                appeared = inventory.Things.Where(thing => !previous.Things.ContainsKey(thing.Key)).ToDictionary(thing => thing.Key, thing => thing.Value);
                if (appeared.Count > 0) ThingsAppeared?.Invoke(inventory, appeared);
            }
            // Asked about anything never seen before, also when it was plugged in while WinSwitch wasn't running (asleep, restarting). A fresh install with no situations yet just records what's there
            var neverSeen = inventory.Things.Where(thing => !Settings.General.KnownThings.Contains(thing.Key)).ToDictionary(thing => thing.Key, thing => thing.Value);
            if (neverSeen.Count > 0)
            {
                var freshInstall = Settings.General.KnownThings.Count == 0 && Settings.Situations.Count == 0;
                Settings.General.KnownThings.AddRange(neverSeen.Keys); Settings.Save();
                // What the dock brought: everything that appeared with it when plugged in now, or only its monitors and network adapter when it was already there at start (the laptop's own Wi-Fi, camera and Bluetooth are never part of a dock)
                if (!freshInstall) AskAboutNewDock(neverSeen, appeared is { Count: > 0 } ? appeared : DockLike(neverSeen));
            }
        }
        if (Settings.Power.Paused) { ActiveProfile = null; ActiveTweaks = new(); EffectiveValues = new(); await App.Apps.ApplyAsync(null, PluggedIn); Changed?.Invoke(); return; }
        var (pluggedIn, batteryPercent, _) = PowerApi.PowerStatus();
        var (situation, reason) = Match(Current, pluggedIn, batteryPercent);
        var situationId = situation?.Id ?? "default";
        // A profile picked by hand holds until the situation changes
        if (Settings.Power.HeldProfileId != "" && (Settings.Power.HeldSituation != situationId || Settings.ProfileById(Settings.Power.HeldProfileId) == null)) { Settings.Power.HeldProfileId = ""; Settings.Save(); }
        var profile = Settings.Power.HeldProfileId != "" ? Settings.ProfileById(Settings.Power.HeldProfileId) : Settings.ProfileById(situation?.ProfileId ?? "") ?? Settings.ProfileById(Settings.DefaultProfileId);
        if (Settings.Power.HeldProfileId != "") reason = "Picked by hand";
        var tweaks = Settings.Tweaks.Where(TweakIsOn).ToList();
        var previousProfile = ActiveProfile; var previousPluggedIn = lastPluggedIn;
        SituationId = situationId; Reason = reason; ActiveProfile = profile; ActiveTweaks = tweaks; lastPluggedIn = pluggedIn;
        if (profile == null) { Changed?.Invoke(); return; }
        var effective = Effective(profile, tweaks, Settings.Profiles.FirstOrDefault(candidate => candidate.IsStarting));
        // WinSwitch locks at the profile's Lock time, so Windows mustn't also ask for a password whenever the screen goes off (Mat 18 Nov)
        if (effective.TryGetValue("lockAfter", out var lockPair) && (lockPair.Ac != null || lockPair.Dc != null)) effective["signInOnWake"] = new SidePair { Ac = lockPair.Ac == null ? effective.GetValueOrDefault("signInOnWake")?.Ac : 0, Dc = lockPair.Dc == null ? effective.GetValueOrDefault("signInOnWake")?.Dc : 0 };
        EffectiveValues = effective;
        var signature = profile.Id + "|" + string.Join(",", tweaks.Select(tweak => tweak.Id)) + "|" + pluggedIn + "|" + profile.SoundOutput + "|" + System.Text.Json.JsonSerializer.Serialize(effective);
        var signatureChanged = force || signature != lastSignature;
        if (signatureChanged)
        {
            lastSignature = signature;
            if (profile.SoundOutput != "" && !Direct.SetOutput(profile.SoundOutput)) CrashReporter.Write($"Sound output {profile.SoundOutputName} isn't plugged in; left as it is", null, false); // before volume, which applies to the default output
            await ApplyAsync(effective, pluggedIn);
            ApplyVolumeChanges(tweaks);
        }
        // Apps to stop: acts only when the list for this profile and power source changes; the switching panel then says what happened
        var panelShowing = await App.Apps.ApplyAsync(profile, pluggedIn);
        if (signatureChanged && !panelShowing)
        {
            if (previousProfile != null && previousProfile.Id != profile.Id) App.Notices.Info($"{profile.Name} profile on", reason, () => HoldProfile(previousProfile.Id));
            else if (previousPluggedIn != null && previousPluggedIn != pluggedIn) App.Notices.Info(pluggedIn ? "Plugged in" : $"On battery, {batteryPercent}%", $"{profile.Name} profile, {(pluggedIn ? "plugged in" : "on battery")} settings");
        }
        Changed?.Invoke();
    }

    // First matching situation wins; the last is always Anything else (SPEC.md › Situations)
    public (Situation? situation, string reason) Match(Inventory inventory, bool pluggedIn, int batteryPercent)
    {
        foreach (var situation in Settings.Situations)
        {
            var matched = situation.Kind switch
            {
                "dock" => DockMatches(situation, inventory),
                "network" => inventory.NetworkThing != "" && situation.Things.ContainsKey(inventory.NetworkThing),
                "battery" => !pluggedIn && batteryPercent < situation.BatteryBelow,
                _ => situation.Things.Keys.Any(inventory.Things.ContainsKey),
            };
            if (matched) return (situation, situation.Kind == "battery" ? $"On battery and under {situation.BatteryBelow}%" : $"{situation.Name} connected");
        }
        return (null, pluggedIn ? "Plugged in" : "On battery");
    }

    // A rule that can never win because an earlier one always matches first: the same battery level or lower, the same network, the same dock or device
    public static Situation? CoveredBy(List<Situation> situations, Situation later)
    {
        foreach (var earlier in situations.TakeWhile(situation => situation != later))
        {
            if (earlier.Kind != later.Kind) continue;
            if (later.Kind == "battery" ? earlier.BatteryBelow >= later.BatteryBelow : earlier.Things.Count > 0 && earlier.Things.Keys.ToHashSet().SetEquals(later.Things.Keys)) return earlier;
        }
        return null;
    }

    // "When the Home dock is connected", for rule lists and profile summaries
    public static string Words(Situation situation) => situation.Kind switch { "battery" => $"on battery under {situation.BatteryBelow}%", "network" => $"on {situation.Name}", _ => $"{situation.Name} connected" };

    public static bool ScheduleSaysOn(Tweak tweak)
    {
        if (!tweak.Scheduled || !TimeSpan.TryParse(tweak.From, out var from) || !TimeSpan.TryParse(tweak.To, out var to)) return false;
        var now = DateTime.Now.TimeOfDay;
        return from <= to ? now >= from && now < to : now >= from || now < to;
    }
    // A tweak ticked by hand holds until its next scheduled change
    public bool TweakIsOn(Tweak tweak)
    {
        var scheduled = ScheduleSaysOn(tweak);
        if (Settings.Power.TweakOverrides.TryGetValue(tweak.Id, out var manual))
        {
            if (!tweak.Scheduled || manual.ScheduledWhenSet == scheduled) return manual.On;
            Settings.Power.TweakOverrides.Remove(tweak.Id); Settings.Save();
        }
        return scheduled;
    }
    public void SetTweak(Tweak tweak, bool on) { Settings.Power.TweakOverrides[tweak.Id] = new TweakOverride { On = on, ScheduledWhenSet = ScheduleSaysOn(tweak) }; Settings.Save(); _ = EvaluateAsync(); }

    // Starting profile underneath (except what's yours to change), then the profile, then any tweaks on top
    public static Dictionary<string, SidePair> Effective(Profile profile, IEnumerable<Tweak> tweaks, Profile? starting = null)
    {
        var values = new Dictionary<string, SidePair>();
        if (starting != null && starting != profile) foreach (var (key, pair) in starting.Values.Where(entry => !Catalog.YoursToChange.Contains(entry.Key))) values[key] = pair.Clone();
        foreach (var (key, pair) in profile.Values)
        {
            if (!values.TryGetValue(key, out var target)) values[key] = target = new SidePair();
            if (pair.Ac != null) target.Ac = pair.Ac;
            if (pair.Dc != null) target.Dc = pair.Dc;
        }
        foreach (var tweak in tweaks)
            foreach (var (key, pair) in tweak.Values)
            {
                if (!values.TryGetValue(key, out var target)) values[key] = target = new SidePair();
                if (pair.Ac != null) target.Ac = pair.Ac;
                if (pair.Dc != null) target.Dc = pair.Dc;
            }
        return values;
    }

    // ---- Writing (SPEC.md › Profiles: WinSwitch's own plan, a copy of yours) ----
    public Guid EnsureOwnPlan()
    {
        if (Guid.TryParse(Settings.Power.OwnPlan, out var ownPlan) && PowerApi.Schemes().Contains(ownPlan)) return ownPlan;
        var active = PowerApi.ActiveScheme() ?? Guid.Empty;
        var copy = PowerApi.Duplicate(active, "WinSwitch");
        Settings.Power.WritesToOriginal = copy == null;
        Settings.Power.OwnPlan = (copy ?? active).ToString(); Settings.Save();
        if (copy == null) CrashReporter.Write("Windows wouldn't copy the power plan; WinSwitch writes to the current one and puts it back on restore", null, false);
        return copy ?? active;
    }

    async Task ApplyAsync(Dictionary<string, SidePair> effective, bool pluggedIn)
    {
        var plan = EnsureOwnPlan();
        foreach (var (key, pair) in effective)
        {
            var setting = Catalog.Find(key);
            if (setting == null || NotSupported.Contains(key) || pair.Ac == null && pair.Dc == null) continue;
            bool ok;
            if (setting.Kind == SettingKind.Plan)
            {
                var acNow = Reader.ReadPlan(plan, setting, true); var dcNow = Reader.ReadPlan(plan, setting, false);
                if (acNow == null && dcNow == null) { NotSupported.Add(key); continue; }
                ok = (pair.Ac == null || acNow == (uint)pair.Ac) && (pair.Dc == null || dcNow == (uint)pair.Dc) || Writer.WritePlan(plan, setting, pair);
                Settings.Power.LastWritten[key] = new SidePair { Ac = pair.Ac ?? (int?)Reader.ReadPlan(plan, setting, true), Dc = pair.Dc ?? (int?)Reader.ReadPlan(plan, setting, false) };
            }
            else if (setting.Kind == SettingKind.Refresh) ok = await ChangeRefreshAsync(pair, pluggedIn);
            else ok = await Writer.WriteDirect(setting, pair, pluggedIn);
            if (!ok) { NotSupported.Add(key); CrashReporter.Write($"Windows didn't take {setting.Label}; hidden on this PC", null, false); }
        }
        // Making the plan active (again) is what makes Windows use the new values
        PowerApi.SetActiveScheme(plan);
        Settings.Save();
    }

    // A refresh rate change blanks the screen for a moment, so a card says so first (with a way out) and offers Undo after (Mat 17 Nov)
    async Task<bool> ChangeRefreshAsync(SidePair pair, bool pluggedIn)
    {
        if ((pluggedIn ? pair.Ac : pair.Dc) is not { } wanted) return true;
        var hertz = Direct.ResolveRefresh(wanted); var before = Direct.RefreshRate();
        if (hertz == 0 || before == null || before == hertz) return true;
        var cancelled = false;
        App.Notices.Info($"Changing the screen to {hertz} Hz", "It may go blank for a second.", () => { cancelled = true; LeaveRefresh(pluggedIn); }, $"Keep {before} Hz", always: true);
        await Task.Delay(3000);
        if (cancelled) return true;
        if (!Direct.SetRefreshRate(hertz)) return false;
        App.Notices.Info($"Screen now {hertz} Hz", $"It was {before} Hz.", () => { Direct.SetRefreshRate(before.Value); LeaveRefresh(pluggedIn); }, "Undo", always: true);
        return true;
    }
    // "Keep" or Undo: this profile leaves the refresh rate alone on this power source from now on
    void LeaveRefresh(bool pluggedIn)
    {
        if (ActiveProfile?.Values.TryGetValue("refresh", out var pair) != true) return;
        if (pluggedIn) pair!.Ac = null; else pair!.Dc = null;
        Settings.Save(); App.NotifySettingsChanged();
    }

    void ApplyVolumeChanges(List<Tweak> tweaks)
    {
        foreach (var tweak in Settings.Tweaks.Where(tweak => tweak.VolumeChange is not null and not 0))
        {
            var on = tweaks.Contains(tweak);
            if (on && !volumeBeforeTweak.ContainsKey(tweak.Id) && Direct.Volume() is { } before) { volumeBeforeTweak[tweak.Id] = before; Direct.SetVolume(before + tweak.VolumeChange!.Value); }
            // Put it back when the tweak ends, unless you changed it yourself in the meantime
            if (!on && volumeBeforeTweak.Remove(tweak.Id, out var original) && Direct.Volume() == Math.Clamp(original + tweak.VolumeChange!.Value, 0, 100)) Direct.SetVolume(original);
        }
    }

    // ---- Hand-picked profile, automatic, pause ----
    public void HoldProfile(string profileId) { Settings.Power.HeldProfileId = profileId; Settings.Power.HeldSituation = SituationId == "" ? "default" : SituationId; Settings.Power.Paused = false; Settings.Save(); _ = EvaluateAsync(); }
    public void GoAutomatic() { Settings.Power.HeldProfileId = ""; Settings.Power.Paused = false; Settings.Save(); _ = EvaluateAsync(); }
    public void Pause() { Settings.Power.Paused = true; lastSignature = ""; Settings.Save(); Changed?.Invoke(); }
    public void Resume() { Settings.Power.Paused = false; lastSignature = ""; Settings.Save(); _ = EvaluateAsync(true); }

    // ---- Changes made in Windows win, and WinSwitch asks whether to keep them (SPEC.md › Playing nicely with Windows) ----
    bool askedAboutPlan;
    void WatchWindowsChanges()
    {
        if (Settings.Power.Paused || ActiveProfile == null || !Guid.TryParse(Settings.Power.OwnPlan, out var plan)) return;
        try
        {
            var active = PowerApi.ActiveScheme();
            if (active != null && active != plan && !Settings.Power.WritesToOriginal)
            {
                if (!askedAboutPlan)
                {
                    askedAboutPlan = true;
                    var planName = PowerApi.SchemeName(active.Value);
                    App.Notices.Ask("plan", "Power plan changed in Windows", $"You picked {(planName == "" ? "another plan" : $"the {planName} plan")}. WinSwitch won't change anything until you say.", new() { ("Use WinSwitch again", () => { askedAboutPlan = false; Resume(); }), ("Leave it", () => { askedAboutPlan = false; Pause(); }) });
                }
                return;
            }
            foreach (var (key, written) in Settings.Power.LastWritten.ToList())
            {
                if (Catalog.Find(key) is not { Kind: SettingKind.Plan } setting) continue;
                var acNow = (int?)Reader.ReadPlan(plan, setting, true); var dcNow = (int?)Reader.ReadPlan(plan, setting, false);
                foreach (var (pluggedIn, before, now) in new[] { (true, written.Ac, acNow), (false, written.Dc, dcNow) })
                {
                    if (before == null || now == null || before == now) continue;
                    if (pluggedIn) written.Ac = now; else written.Dc = now; // kept straight away, never overwritten
                    Settings.Save();
                    AskToKeep(setting, pluggedIn, now.Value);
                }
            }
        }
        catch (Exception error) { CrashReporter.Write("Watching for changes made in Windows", error, false); }
    }

    void AskToKeep(SettingDef setting, bool pluggedIn, int value)
    {
        var profile = ActiveProfile; if (profile == null) return;
        var side = setting.TwoSided ? (pluggedIn ? " (plugged in)" : " (on battery)") : "";
        void SaveTo(IEnumerable<Profile> profiles)
        {
            foreach (var target in profiles)
            {
                if (!target.Values.TryGetValue(setting.Key, out var pair)) target.Values[setting.Key] = pair = new SidePair();
                if (pluggedIn) pair.Ac = value; else pair.Dc = value;
            }
            Settings.Save(); App.NotifySettingsChanged();
        }
        App.Notices.Ask("changed:" + setting.Key + side, $"You changed {setting.Label}{side}", $"to {setting.Describe(value)} in Windows.", new()
        {
            ($"Save to {profile.Name}", () => SaveTo(new[] { profile })),
            ("All profiles", () => SaveTo(Settings.Profiles.Where(candidate => !candidate.IsStarting))),
            ("Just for now", () => { }),
        });
    }

    // A dock with a monitor is connected when one of its monitors is (Mat 18 Nov: the monitor is what counts, not the network adapter, hubs or power).
    // A dock with no monitor (USB only) still matches on most of its things
    static bool DockMatches(Situation situation, Inventory inventory)
    {
        var monitors = situation.Things.Keys.Where(thing => thing.StartsWith("monitor:")).ToList();
        if (monitors.Count > 0) return monitors.Any(inventory.Things.ContainsKey);
        return situation.Things.Count > 0 && situation.Things.Keys.Count(inventory.Things.ContainsKey) >= Math.Max(1, (int)Math.Ceiling(situation.Things.Count * 0.6));
    }

    // ---- A dock or monitor never seen before (SPEC.md › Notifications) ----
    public static Dictionary<string, string> DockLike(Dictionary<string, string> things) => things.Where(thing => thing.Key.StartsWith("monitor:") || thing.Key.StartsWith("net:") && (thing.Value.Contains("USB", StringComparison.OrdinalIgnoreCase) || thing.Value.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) || thing.Value.Contains("Dock", StringComparison.OrdinalIgnoreCase))).ToDictionary(thing => thing.Key, thing => thing.Value);

    void AskAboutNewDock(Dictionary<string, string> neverSeen, Dictionary<string, string> dockThings)
    {
        var appeared = neverSeen;
        // Only what no situation knows yet: a new dock at the same desk still asks, even though the monitor on it is already part of Desk
        var dockLike = DockLike(appeared).Where(thing => !Settings.General.IgnoredThings.Contains(thing.Key) && !Settings.Situations.Any(situation => situation.Things.ContainsKey(thing.Key))).ToDictionary(thing => thing.Key, thing => thing.Value);
        if (dockLike.Count == 0) return;
        var names = string.Join(", ", dockLike.Values.Distinct().Take(3));
        var isDock = dockLike.Count > 1 || dockLike.Keys.Any(key => key.StartsWith("net:"));
        App.Notices.Ask("newdock:" + string.Join(",", dockLike.Keys), isDock ? "New dock connected" : "New monitor connected", $"{names}. What should it use?", new()
        {
            ("Set up new profile", () => App.SetUpNewPlace(isDock ? "New dock" : dockLike.Values.First(), dockThings)),
            ($"Use {ActiveProfile?.Name ?? "this profile"}", () => { if (ActiveProfile == null) return; Settings.Situations.Insert(0, new Situation { Kind = "dock", Name = isDock ? "Dock" : dockLike.Values.First(), Things = dockThings, ProfileId = ActiveProfile.Id }); Settings.Save(); App.NotifySettingsChanged(); EvaluateSoon(); }),
            ("Ignore", () => { Settings.General.IgnoredThings.AddRange(dockLike.Keys); Settings.Save(); }),
        });
    }
}
