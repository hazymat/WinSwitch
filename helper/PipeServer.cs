using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Claims;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
namespace WinSwitchHelper;

// One JSON line in, one JSON line out per connection ({command, ...} -> {ok, value, error}). Only the signed-in user(s), admins and SYSTEM can open it.
sealed class PipeServer
{
    const string PipeName = "WinSwitchHelper";
    volatile bool stopping;
    Thread listener;
    NamedPipeServerStream waiting;

    public void Start() { listener = new Thread(Listen) { IsBackground = true, Name = "pipe" }; listener.Start(); }
    public void Stop() { stopping = true; try { waiting?.Dispose(); } catch { } }

    static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    void Listen()
    {
        while (!stopping)
        {
            try
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security());
                waiting = pipe;
                pipe.WaitForConnection();
                ThreadPool.QueueUserWorkItem(_ => Serve(pipe));
            }
            catch (Exception error) { if (!stopping) { Log.Write($"Pipe: {error.Message}"); Thread.Sleep(1000); } }
        }
    }

    // An account in Administrators, elevated or not (UAC leaves the group on the token as deny-only): such a user could do these things with one prompt anyway
    static bool CallerIsAdmin(NamedPipeServerStream pipe)
    {
        var admin = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                const string administrators = "S-1-5-32-544";
                admin = identity.Groups.Any(group => group.Value == administrators) || identity.Claims.Any(claim => (claim.Type == ClaimTypes.DenyOnlySid || claim.Type == ClaimTypes.GroupSid) && claim.Value == administrators);
            });
        }
        catch (Exception error) { Log.Write($"Couldn't check the caller: {error.Message}"); }
        return admin;
    }

    static void Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            var json = new JavaScriptSerializer();
            var reply = new Dictionary<string, object>();
            string command = "?";
            try
            {
                var reader = new StreamReader(pipe);
                var line = reader.ReadLine() ?? "{}";
                if (line.Length > 65536) throw new Exception("Request too long");
                var request = json.DeserializeObject(line) as Dictionary<string, object> ?? new Dictionary<string, object>();
                command = request.TryGetValue("command", out var name) ? name as string ?? "" : "";
                reply["value"] = Commands.Do(command, request, CallerIsAdmin(pipe));
                reply["ok"] = true;
            }
            catch (Exception error) { reply["ok"] = false; reply["error"] = error.Message; Log.Write($"{command} refused: {error.Message}"); }
            try { var writer = new StreamWriter(pipe) { AutoFlush = true }; writer.WriteLine(json.Serialize(reply)); pipe.WaitForPipeDrain(); } catch { }
        }
    }
}
