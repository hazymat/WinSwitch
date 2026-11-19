using System.Runtime.InteropServices;
using System.Text;
namespace WinSwitch.Shell;

// Mice that move by themselves while you're away (a sensor on a shiny desk, a mouse knocked in a bag): each tiny move resets Windows' idle clock,
// so the screen never goes off. Raw input names the device; a move counts as stray when you'd been idle 30 s+, it was tiny, and nothing followed (Mat 19 Nov)
public static class StrayInput
{
    public sealed class Device { public string Name = ""; public int Moves; public DateTime LastMove; }
    public static readonly Dictionary<IntPtr, Device> Devices = new();
    static readonly Dictionary<IntPtr, string> names = new();
    static Sink? sink;
    static (IntPtr device, uint at)? candidate; // a tiny move after a quiet spell, confirmed stray if nothing follows
    static int idleBefore; // seconds idle at the last check, before any new input

    public static void Start()
    {
        try { sink = new Sink(); var device = new RawInputDevice { UsagePage = 1, Usage = 2 /* mouse */, Flags = 0x100 /* RIDEV_INPUTSINK */, Target = sink.Handle }; RegisterRawInputDevices(new[] { device }, 1, Marshal.SizeOf<RawInputDevice>()); }
        catch { }
        var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        check.Tick += (sender, args) =>
        {
            if (candidate is { } move && (uint)Environment.TickCount - move.at > 4000)
            {
                if (Math.Abs((int)(IdleWatcher.LastInput() - move.at)) < 1000) { if (!Devices.TryGetValue(move.device, out var device)) Devices[move.device] = device = new Device { Name = NameOf(move.device) }; device.Moves++; device.LastMove = DateTime.Now; }
                candidate = null;
            }
            idleBefore = IdleWatcher.IdleSeconds;
        };
        check.Start();
    }

    static void OnMouse(IntPtr device, int flags, int buttons, int moveX, int moveY)
    {
        if ((flags & 1) != 0 || buttons != 0) { candidate = null; return; } // absolute (touch, remote desktop) or a click: you
        if (candidate != null) { candidate = null; return; } // a second move: someone's using it
        if (idleBefore >= 30 && Math.Abs(moveX) + Math.Abs(moveY) <= 3) candidate = (device, (uint)Environment.TickCount);
    }

    // "Logitech USB Receiver" from the HID strings, else the vendor and product ids from its path
    static string NameOf(IntPtr device)
    {
        if (names.TryGetValue(device, out var known)) return known;
        var name = "A mouse";
        try
        {
            uint size = 0; GetRawInputDeviceInfo(device, 0x20000007 /* RIDI_DEVICENAME */, null, ref size);
            var path = new StringBuilder((int)size + 1); GetRawInputDeviceInfo(device, 0x20000007, path, ref size);
            var handle = CreateFile(path.ToString(), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle != new IntPtr(-1))
            {
                var product = new StringBuilder(128); var maker = new StringBuilder(128);
                var hasProduct = HidD_GetProductString(handle, product, 256); var hasMaker = HidD_GetManufacturerString(handle, maker, 256); CloseHandle(handle);
                if (hasProduct) name = (hasMaker && !product.ToString().StartsWith(maker.ToString(), StringComparison.OrdinalIgnoreCase) ? maker + " " : "") + product;
            }
            else if (System.Text.RegularExpressions.Regex.Match(path.ToString(), @"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } ids) name = $"A mouse (VID {ids.Groups[1].Value}, PID {ids.Groups[2].Value})";
        }
        catch { }
        return names[device] = name;
    }

    // A hidden window that receives the mouse's raw input even when WinSwitch isn't in front
    sealed class Sink : System.Windows.Forms.NativeWindow
    {
        public Sink() => CreateHandle(new System.Windows.Forms.CreateParams());
        protected override void WndProc(ref System.Windows.Forms.Message message)
        {
            if (message.Msg == 0x00FF /* WM_INPUT */)
            {
                try
                {
                    var headerSize = Marshal.SizeOf<RawInputHeader>(); uint size = 0;
                    GetRawInputData(message.LParam, 0x10000003 /* RID_INPUT */, IntPtr.Zero, ref size, headerSize);
                    var buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetRawInputData(message.LParam, 0x10000003, buffer, ref size, headerSize) == size)
                        {
                            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
                            if (header.Type == 0 /* RIM_TYPEMOUSE */) OnMouse(header.Device, Marshal.ReadInt16(buffer, headerSize), Marshal.ReadInt16(buffer, headerSize + 4), Marshal.ReadInt32(buffer, headerSize + 12), Marshal.ReadInt32(buffer, headerSize + 16));
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                catch { }
            }
            base.WndProc(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct RawInputHeader { public uint Type, Size; public IntPtr Device, WParam; }
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, int size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, int headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(IntPtr device, StringBuilder buffer, int length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetManufacturerString(IntPtr device, StringBuilder buffer, int length);
}
