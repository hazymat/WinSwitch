using System.Runtime.InteropServices;
using Windows.Media.Control;
namespace WinSwitch.Power;

// Whatever Windows is playing (Spotify, a browser tab, WinSqueeze…): what it is, and play, pause, next, previous (Mat 17 Nov).
// Through Windows' media controls, the same ones as the volume pop-up; the media keys when no app has registered there.
public static class Media
{
    public record Playing(string Title, string Artist, string App, bool IsPlaying);
    static GlobalSystemMediaTransportControlsSessionManager? manager;
    static readonly System.Threading.Timer refreshTimer = new(async _ => await RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    public static Playing? Now { get; private set; }
    public static readonly string[] Commands = { "playpause", "play", "pause", "next", "previous" };

    public static void Start() => refreshTimer.Change(0, 2000);

    static async Task<GlobalSystemMediaTransportControlsSession?> SessionAsync()
    {
        try { manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); return manager.GetCurrentSession(); }
        catch { return null; }
    }

    static async Task RefreshAsync()
    {
        try
        {
            if (await SessionAsync() is not { } session) { Now = null; return; }
            var properties = await session.TryGetMediaPropertiesAsync();
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Now = new Playing(properties?.Title ?? "", properties?.Artist ?? "", AppName(session.SourceAppUserModelId), playing);
        }
        catch { Now = null; }
    }

    // "Spotify.exe" or "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> a short name
    static string AppName(string id)
    {
        var name = id.Contains('!') ? id[(id.LastIndexOf('!') + 1)..] : id;
        name = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
    }

    public static async Task<bool> DoAsync(string command)
    {
        var done = false;
        if (await SessionAsync() is { } session)
            try
            {
                done = command switch
                {
                    "playpause" => await session.TryTogglePlayPauseAsync(), "play" => await session.TryPlayAsync(), "pause" => await session.TryPauseAsync(),
                    "next" => await session.TrySkipNextAsync(), "previous" => await session.TrySkipPreviousAsync(), _ => false,
                };
            }
            catch { }
        if (!done && command is "playpause" or "next" or "previous") { Press(command switch { "next" => 0xB0, "previous" => 0xB1, _ => 0xB3 }); done = true; }
        await Task.Delay(300); await RefreshAsync();
        return done;
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    static void Press(byte key) { keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 2, UIntPtr.Zero); }
}
