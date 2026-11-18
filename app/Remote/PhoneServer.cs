using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
namespace WinSwitch.Remote;

// The phone page and the Shortcuts addresses: a small HTTP server on the home network only, with a token (SPEC.md › iPhone, no hub).
// Plain TCP rather than HttpListener, which needs admin to listen on the network
public sealed class PhoneServer
{
    static App App => App.Instance;
    TcpListener? listener;
    CancellationTokenSource? stopping;
    readonly Dictionary<string, (int failures, DateTime since)> failuresByAddress = new();
    public int Port { get; private set; }
    public string Problem { get; private set; } = "";
    // The network we're on, and whether the phone page is off here because it's not one of the allowed networks (Mat 18 Nov)
    public (string thing, string name) Network { get; private set; } = Rules.Inventory.CurrentNetwork();
    public bool OffOnThisNetwork => App.Settings.Remote.OnlyOnNetworks && !App.Settings.Remote.Networks.ContainsKey(Network.thing);
    public event Action? Changed;
    readonly System.Windows.Threading.DispatcherTimer networkTimer = new() { Interval = TimeSpan.FromSeconds(3) }; // networks connect in stages

    public PhoneServer()
    {
        networkTimer.Tick += async (sender, args) => { networkTimer.Stop(); Network = await Task.Run(Rules.Inventory.CurrentNetwork); Apply(); Changed?.Invoke(); };
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (sender, args) => App.Dispatcher.BeginInvoke(() => { networkTimer.Stop(); networkTimer.Start(); });
    }
    public bool Running => listener != null;

    public void Apply()
    {
        var remote = App.Settings.Remote;
        if (!remote.PhoneOn) { Stop(); return; }
        if (OffOnThisNetwork) { if (Running) RemoteControl.Log("phone", "network", $"off: {(Network.name == "" ? "no network" : Network.name)} isn't an allowed network"); Stop(); return; }
        if (Running && Port == remote.PhonePort) return;
        Stop();
        try
        {
            RemoteControl.EnsureToken();
            listener = new TcpListener(IPAddress.Any, remote.PhonePort); listener.Start();
            Port = remote.PhonePort; Problem = ""; stopping = new CancellationTokenSource();
            _ = AcceptLoopAsync(listener, stopping.Token);
        }
        catch (Exception error) { listener = null; Problem = $"Port {remote.PhonePort} is in use or blocked ({error.Message.TrimEnd('.')}). Pick another port."; Shell.CrashReporter.Write("Starting the phone page", error, false); }
    }

    public void Stop() { stopping?.Cancel(); try { listener?.Stop(); } catch { } listener = null; }

    async Task AcceptLoopAsync(TcpListener server, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await server.AcceptTcpClientAsync(cancel); } catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using var connection = client;
        var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        if (!RemoteControl.IsLocal(address)) return; // not from the home network: no answer at all
        client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
        using var stream = client.GetStream();
        try
        {
            var (method, path, headers, body) = await ReadRequestAsync(stream);
            if (method == "") return;
            var (status, type, content) = await RouteAsync(method, path, headers, body, address.ToString());
            var header = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header)); await stream.WriteAsync(content);
        }
        catch { }
    }

    static async Task<(string method, string path, Dictionary<string, string> headers, string body)> ReadRequestAsync(Stream stream)
    {
        var buffer = new byte[65536]; var length = 0; int headerEnd;
        while ((headerEnd = IndexOf(buffer, length, "\r\n\r\n"u8)) < 0)
        {
            if (length == buffer.Length) return ("", "", new(), "");
            var read = await stream.ReadAsync(buffer.AsMemory(length)); if (read == 0) return ("", "", new(), "");
            length += read;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return ("", "", new(), "");
        var headers = lines.Skip(1).Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2).GroupBy(parts => parts[0].Trim().ToLowerInvariant()).ToDictionary(group => group.Key, group => group.First()[1].Trim());
        var bodyLength = headers.TryGetValue("content-length", out var lengthText) && int.TryParse(lengthText, out var parsed) ? Math.Clamp(parsed, 0, 16384) : 0;
        var bodyStart = headerEnd + 4;
        while (length - bodyStart < bodyLength) { var read = await stream.ReadAsync(buffer.AsMemory(length)); if (read == 0) break; length += read; }
        return (first[0].ToUpperInvariant(), first[1], headers, Encoding.UTF8.GetString(buffer, bodyStart, Math.Max(0, Math.Min(bodyLength, length - bodyStart))));
    }
    static int IndexOf(byte[] buffer, int length, ReadOnlySpan<byte> pattern) => buffer.AsSpan(0, length).IndexOf(pattern);

    async Task<(string status, string type, byte[] content)> RouteAsync(string method, string path, Dictionary<string, string> headers, string body, string who)
    {
        path = path.Split('?')[0];
        // The phone page lives at /p/<token>/ so Add to Home Screen keeps the pairing
        if (path.StartsWith("/p/"))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !await CheckTokenAsync(parts[1], who)) return Html("403 Forbidden", "<h2>This link is out of date</h2><p>Scan the QR code in WinSwitch › Settings › Phone and Shortcuts again.</p>");
            var file = parts.Length > 2 ? parts[2] : "";
            if (file == "manifest.json") return ("200 OK", "application/manifest+json", Encoding.UTF8.GetBytes(new JsonObject { ["name"] = $"WinSwitch {Environment.MachineName}", ["short_name"] = Environment.MachineName, ["start_url"] = $"/p/{parts[1]}/", ["display"] = "standalone", ["background_color"] = "#14171c", ["theme_color"] = "#14171c", ["icons"] = new JsonArray(new JsonObject { ["src"] = "icon.png", ["sizes"] = "180x180", ["type"] = "image/png" }) }.ToJsonString()));
            if (file == "icon.png") return ("200 OK", "image/png", Resource("phone-icon.png"));
            return ("200 OK", "text/html; charset=utf-8", Resource("phone.html"));
        }
        if (!path.StartsWith("/api/")) return Html("404 Not Found", "<p>Nothing here.</p>");
        var token = headers.TryGetValue("authorization", out var authorization) && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : headers.GetValueOrDefault("x-token", "");
        if (!await CheckTokenAsync(token, who)) return Json("401 Unauthorized", new JsonObject { ["ok"] = false, ["error"] = "Wrong or missing token" });
        var action = path[5..].Trim('/').ToLowerInvariant();
        if (action == "state") return Json("200 OK", await App.Dispatcher.InvokeAsync(RemoteControl.State).Task);
        if (method != "POST") return Json("405 Method Not Allowed", new JsonObject { ["ok"] = false, ["error"] = "Use POST" });
        JsonObject? arguments = null;
        try { arguments = body.Trim() == "" ? null : JsonNode.Parse(body) as JsonObject; } catch { return Json("400 Bad Request", new JsonObject { ["ok"] = false, ["error"] = "The body isn't JSON" }); }
        if (action == "power" && arguments?["action"]?.ToString() is { } powerAction) action = powerAction.ToLowerInvariant();
        var (ok, message) = await RemoteControl.DoAsync(action, arguments, "phone " + who);
        var reply = await App.Dispatcher.InvokeAsync(RemoteControl.State).Task;
        reply["ok"] = ok; reply["message"] = message;
        return Json(ok ? "200 OK" : "400 Bad Request", reply);
    }

    // Wrong tokens are slowed down (1 s more each time) and logged; 20 in 10 minutes and that address is ignored for 10 minutes
    async Task<bool> CheckTokenAsync(string token, string who)
    {
        (int failures, DateTime since) record;
        lock (failuresByAddress) { failuresByAddress.TryGetValue(who, out record); if (DateTime.Now - record.since > TimeSpan.FromMinutes(10)) record = (0, DateTime.Now); }
        if (record.failures >= 20) return false;
        if (RemoteControl.TokenMatches(token)) return true;
        lock (failuresByAddress) failuresByAddress[who] = (record.failures + 1, record.since == default ? DateTime.Now : record.since);
        RemoteControl.Log("phone " + who, "wrong token", $"refused ({record.failures + 1} in a row)");
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(record.failures + 1, 10)));
        return false;
    }

    static (string, string, byte[]) Json(string status, JsonObject content) => (status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(content.ToJsonString()));
    static (string, string, byte[]) Html(string status, string inner) => (status, "text/html; charset=utf-8", Encoding.UTF8.GetBytes($"<!doctype html><meta name=viewport content='width=device-width'><body style='font-family:-apple-system,sans-serif;background:#14171c;color:#eee;padding:24px'>{inner}</body>"));
    static byte[] Resource(string name)
    {
        using var resource = typeof(PhoneServer).Assembly.GetManifestResourceStream("WinSwitch.Remote." + name);
        if (resource == null) return Array.Empty<byte>();
        using var memory = new MemoryStream(); resource.CopyTo(memory); return memory.ToArray();
    }
}
