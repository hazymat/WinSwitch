using System.Net.NetworkInformation;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Radios;
using WinSwitch.Power;
using WinSwitch.Settings;
using WinSwitch.Shell;
namespace WinSwitch.Rules;

// Notices habits and offers one change at a time, at most one a week, never the same one twice (SPEC.md › Suggestions).
// A new dock is already asked about when it first appears, so these cover battery drain asleep, brightness set by hand and Wi-Fi left on while wired.
public sealed class Suggestions
{
    App App => App.Instance;
    AppSettings Settings => App.Settings;
    SuggestionState State => Settings.Suggestions;
    readonly DispatcherTimer watchTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    (DateTime at, int percent, string profileId)? asleepOnBattery;
    public event Action? Changed;

    public void Start()
    {
        SystemEvents.PowerModeChanged += (sender, args) => App.Dispatcher.BeginInvoke(() => OnPowerMode(args.Mode));
        watchTimer.Tick += async (sender, args) => await WatchAsync();
        watchTimer.Start();
        App.Dispatcher.BeginInvoke(ShowIfDue, DispatcherPriority.ApplicationIdle);
    }

    // Battery drained while asleep: offer to hibernate after an hour asleep on battery
    void OnPowerMode(PowerModes mode)
    {
        var (pluggedIn, percent, hasBattery) = PowerApi.PowerStatus();
        if (mode == PowerModes.Suspend) { asleepOnBattery = !pluggedIn && hasBattery && App.Engine.ActiveProfile is { } profile ? (DateTime.Now, percent, profile.Id) : null; return; }
        if (mode != PowerModes.Resume || asleepOnBattery is not { } asleep) return;
        asleepOnBattery = null;
        var now = percent;
        if (pluggedIn || asleep.percent - now < 10 || DateTime.Now - asleep.at < TimeSpan.FromHours(2) || Settings.ProfileById(asleep.profileId) is not { } sleptIn) return;
        var hibernateAfter = Engine.Effective(sleptIn, Array.Empty<Tweak>(), Starting).GetValueOrDefault("hibernateAfter")?.Dc;
        if (hibernateAfter is > 0 and <= 3600) return;
        Offer(new Suggestion { Id = $"sleepDrain:{sleptIn.Id}", ProfileId = sleptIn.Id, Key = "hibernateAfter", Dc = 3600, Text = $"The battery dropped {asleep.percent - now}% while the PC was asleep. Hibernate after 1 hour asleep on battery in {sleptIn.Name}?" });
    }

    async Task WatchAsync()
    {
        if (Settings.Power.Paused || App.Engine.ActiveProfile is not { } profile) return;
        var pluggedIn = App.Engine.PluggedIn;
        var effective = Engine.Effective(profile, App.Engine.ActiveTweaks, Starting);
        // Brightness changed by hand on battery, to about the same level on three days
        if (!pluggedIn && effective.GetValueOrDefault("brightness")?.Dc is { } wanted && Direct.Brightness() is { } now && Math.Abs(now - wanted) >= 20 && App.Engine.ActiveTweaks.Count == 0)
        {
            var level = (int)Math.Round(now / 10.0) * 10;
            if (SeenOnDays($"brightness:{profile.Id}:{level}") >= 3) Offer(new Suggestion { Id = $"brightness:{profile.Id}", ProfileId = profile.Id, Key = "brightness", Ac = profile.Get("brightness")?.Ac, Dc = level, Text = $"On battery you often turn the brightness to about {level}%. Save that to {profile.Name}?" });
        }
        // Wi-Fi left on while a network cable is in, on three days
        if (profile.Get("wifi") == null && WiredUp() && await Direct.RadioState(RadioKind.WiFi) == 1 && SeenOnDays($"wifiWired:{profile.Id}") >= 3)
            Offer(new Suggestion { Id = $"wifiWired:{profile.Id}", ProfileId = profile.Id, Key = "wifi", Ac = 0, Text = $"Wi-Fi stays on while you're plugged into a network cable. Turn Wi-Fi off in {profile.Name}?" });
    }

    Profile? Starting => Settings.Profiles.FirstOrDefault(profile => profile.IsStarting);

    static bool WiredUp()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Any(network => network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet && network.OperationalStatus == OperationalStatus.Up && !network.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !network.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) && network.GetIPProperties().GatewayAddresses.Count > 0); }
        catch { return false; }
    }

    // Records today for this observation; returns how many different days it has been seen (last 14 days)
    int SeenOnDays(string observation)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (!State.Seen.TryGetValue(observation, out var days)) State.Seen[observation] = days = new();
        if (!days.Contains(today)) { days.Add(today); days.RemoveAll(day => DateTime.TryParse(day, out var date) && date < DateTime.Today.AddDays(-14)); Settings.Save(); }
        return days.Count;
    }

    void Offer(Suggestion suggestion)
    {
        if (State.Waiting != null || State.Answered.Contains(suggestion.Id)) return;
        State.Waiting = suggestion; Settings.Save();
        ShowIfDue();
    }

    // The tip: at most one a week; the callout in Settings › Profiles stays until it's answered
    void ShowIfDue()
    {
        if (State.Waiting is not { } suggestion || State.LastShown is { } shown && DateTime.Now - shown < TimeSpan.FromDays(7)) return;
        State.LastShown = DateTime.Now; Settings.Save();
        App.Notices.Info("A suggestion from WinSwitch", suggestion.Text, () => App.OpenSettings("Profiles", suggestion.ProfileId), "Show me", always: true);
        Changed?.Invoke();
    }

    public void Answer(bool accept)
    {
        if (State.Waiting is not { } suggestion) return;
        if (accept && Settings.ProfileById(suggestion.ProfileId) is { } profile) Templates.Set(profile, suggestion.Key, suggestion.Ac ?? profile.Get(suggestion.Key)?.Ac, suggestion.Dc ?? profile.Get(suggestion.Key)?.Dc);
        State.Answered.Add(suggestion.Id); State.Waiting = null;
        State.Seen = State.Seen.Where(entry => !entry.Key.StartsWith(suggestion.Id.Split(':')[0] + ":" + suggestion.ProfileId)).ToDictionary(entry => entry.Key, entry => entry.Value);
        Settings.Save(); App.Engine.EvaluateSoon(); Changed?.Invoke();
    }
}
