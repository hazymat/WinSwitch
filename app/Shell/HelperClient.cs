using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace WinSwitch.Shell;

// Talks to the optional helper service over its local pipe; every call fails quietly when the helper isn't installed (SPEC.md › Architecture)
public static class HelperClient
{
    public const string PipeName = "WinSwitchHelper";

    public static async Task<JsonNode?> SendAsync(string command, object? arguments = null, int timeoutMilliseconds = 15000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(400);
            var request = arguments == null ? new JsonObject() : JsonSerializer.SerializeToNode(arguments)!.AsObject();
            request["command"] = command;
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(request.ToJsonString());
            var replyTask = reader.ReadLineAsync();
            if (await Task.WhenAny(replyTask, Task.Delay(timeoutMilliseconds)) != replyTask) return null;
            var reply = JsonNode.Parse(await replyTask ?? "{}");
            if (reply?["ok"]?.GetValue<bool>() != true) { CrashReporter.Write($"Helper refused {command}: {reply?["error"]}", null, false); return null; }
            return reply;
        }
        catch { return null; }
    }

    public static async Task<bool> DoAsync(string command, object? arguments = null) => await SendAsync(command, arguments) != null;
    public static async Task<string> VersionAsync() => (await SendAsync("version", null, 1500))?["value"]?.GetValue<string>() ?? "";
}
