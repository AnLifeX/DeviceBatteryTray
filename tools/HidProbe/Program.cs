// HidProbe: 调试用小工具，列出 HID 接口，并向指定接口发送原始报告、读取回包。
// 用法:
//   HidProbe list <vid> <pid>
//   HidProbe send <vid> <pid> <usagePage> <hexBytes(含ReportID)> [读包数=3] [超时ms=500]
//     hexBytes 例: "04 01 00 1A"，不足输出报告长度的部分自动补 0
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
    // caps 结构体每个 72 字节，ReportID 在偏移 2；reportType: 0=Input 1=Output 2=Feature
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort len, IntPtr p);
    [DllImport("hid.dll")] public static extern int HidP_GetButtonCaps(int reportType, byte[] caps, ref ushort len, IntPtr p);

    public static string ReportIds(IntPtr pp, int type)
    {
        var ids = new SortedSet<byte>();
        foreach (var get in new Func<int, byte[], ushort, (int, byte[], ushort)>[] {
            (t, b, l) => { var r = HidP_GetValueCaps(t, b, ref l, pp); return (r, b, l); },
            (t, b, l) => { var r = HidP_GetButtonCaps(t, b, ref l, pp); return (r, b, l); } })
        {
            var (_, buf, n) = get(type, new byte[72 * 64], 64);
            for (int i = 0; i < n; i++) ids.Add(buf[i * 72 + 2]);
        }
        return string.Join(",", ids.Select(x => "0x" + x.ToString("X2")));
    }
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);

    [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int req, IntPtr dev);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
}

record HidIf(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage, int InLen, int OutLen, int FeatLen)
{
    public string Ids { get; init; } = "";
}

static class Hid
{
    public static IEnumerable<string> Paths()
    {
        Native.HidD_GetHidGuid(out var g);
        var set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT|DEVICEINTERFACE
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) yield break;
                Native.SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                var buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out _, IntPtr.Zero))
                        yield return Marshal.PtrToStringUni(buf + 4)!;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    public static SafeFileHandle Open(string path, bool rw) =>
        Native.CreateFile(path, rw ? 0xC0000000 : 0, 3, IntPtr.Zero, 3, rw ? 0x40000000u : 0, IntPtr.Zero); // OVERLAPPED when rw

    public static IEnumerable<HidIf> Enumerate(ushort vid, ushort pid)
    {
        foreach (var p in Paths())
        {
            using var h = Open(p, false);
            if (h.IsInvalid) continue;
            var a = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
            if (!Native.HidD_GetAttributes(h, ref a) || a.VendorID != vid || a.ProductID != pid) continue;
            if (!Native.HidD_GetPreparsedData(h, out var pp)) continue;
            Native.HidP_GetCaps(pp, out var c);
            var ids = $"inIDs=[{Native.ReportIds(pp, 0)}] outIDs=[{Native.ReportIds(pp, 1)}] featIDs=[{Native.ReportIds(pp, 2)}]";
            Native.HidD_FreePreparsedData(pp);
            yield return new HidIf(p, a.VendorID, a.ProductID, c.UsagePage, c.Usage, c.InputReportByteLength, c.OutputReportByteLength, c.FeatureReportByteLength) { Ids = ids };
        }
    }
}

static class Program
{
    static ushort Hex16(string s) => Convert.ToUInt16(s.Replace("0x", ""), 16);
    static string Hex(byte[] b, int n) => string.Join(" ", b.Take(n).Select(x => x.ToString("X2")));

    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("HidProbe list <vid> <pid> | send <vid> <pid> <usagePage> <hex> [count] [timeoutMs]"); return 1; }
        ushort vid = Hex16(args[1]), pid = Hex16(args[2]);
        var ifs = Hid.Enumerate(vid, pid).ToList();

        if (args[0] == "list")
        {
            foreach (var i in ifs)
                Console.WriteLine($"UP=0x{i.UsagePage:X4} U=0x{i.Usage:X2} in={i.InLen} out={i.OutLen} feat={i.FeatLen} {i.Ids}\n   {i.Path}");
            return 0;
        }

        if (args[0] == "listen")
        {
            // HidProbe listen <vid> <pid> <秒数>：被动监听所有有输入报告的接口，不发送任何东西
            int secs = args.Length > 3 ? int.Parse(args[3]) : 10;
            var lcts = new CancellationTokenSource(secs * 1000);
            var ls = new List<Task>();
            foreach (var i in ifs.Where(i => i.InLen > 0))
            {
                var hh = Hid.Open(i.Path, true);
                if (hh.IsInvalid) { Console.WriteLine($"打开 UP=0x{i.UsagePage:X4} U=0x{i.Usage:X2} 失败(可能被系统独占)"); continue; }
                var f = new FileStream(hh, FileAccess.Read, 1, true);
                ls.Add(Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            var b = new byte[i.InLen];
                            int n = await f.ReadAsync(b, 0, b.Length, lcts.Token);
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} RX[UP=0x{i.UsagePage:X4} U=0x{i.Usage:X2}]: " + Hex(b, n));
                        }
                    }
                    catch (OperationCanceledException) { }
                    finally { f.Dispose(); }
                }));
            }
            Task.WaitAll(ls.ToArray());
            return 0;
        }

        if (args[0] == "fget")
        {
            // HidProbe fget <vid> <pid> <usagePage> <reportId>：读 Feature 报告
            ushort fup = Hex16(args[3]); byte rid = Convert.ToByte(args[4], 16);
            foreach (var i in ifs.Where(i => i.UsagePage == fup && i.FeatLen > 0))
            {
                using var fh = Hid.Open(i.Path, false);
                var fb = new byte[i.FeatLen]; fb[0] = rid;
                bool ok = Native.HidD_GetFeature(fh, fb, fb.Length);
                Console.WriteLine($"FGET[UP=0x{i.UsagePage:X4}] ok={ok} err={(ok ? "" : new Win32Exception().Message)}: {Hex(fb, fb.Length)}");
            }
            return 0;
        }

        ushort up = Hex16(args[3]);
        var payload = args[4].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray();
        int count = args.Length > 5 ? int.Parse(args[5]) : 3;
        int timeout = args.Length > 6 ? int.Parse(args[6]) : 500;

        var target = ifs.FirstOrDefault(i => i.UsagePage == up && i.OutLen > 0);
        if (target == null) { Console.WriteLine("找不到可写的目标接口"); return 2; }
        Console.WriteLine($"目标: UP=0x{target.UsagePage:X4} U=0x{target.Usage:X2} in={target.InLen} out={target.OutLen}");

        // 在所有厂商自定义接口(UsagePage>=0xFF00)上同时监听，回包可能走别的 collection
        var listeners = new List<(HidIf i, FileStream fs)>();
        FileStream? txFs = null;
        foreach (var i in ifs.Where(i => (i.UsagePage >= 0xFF00 || i.Path == target.Path) && i.InLen > 0))
        {
            var hh = Hid.Open(i.Path, true);
            if (hh.IsInvalid) { Console.WriteLine($"打开 UP=0x{i.UsagePage:X4} 失败: {new Win32Exception().Message}"); continue; }
            var f = new FileStream(hh, FileAccess.ReadWrite, 1, true);
            listeners.Add((i, f));
            if (i.Path == target.Path) txFs = f;
        }
        if (txFs == null) { Console.WriteLine("目标接口打开失败"); return 3; }

        var cts = new CancellationTokenSource(timeout * count);
        var tasks = listeners.Select(l => Task.Run(async () =>
        {
            try
            {
                for (int k = 0; k < count; k++)
                {
                    var inBuf = new byte[l.i.InLen];
                    int n = await l.fs.ReadAsync(inBuf, 0, inBuf.Length, cts.Token);
                    Console.WriteLine($"RX[UP=0x{l.i.UsagePage:X4}]: " + Hex(inBuf, n));
                }
            }
            catch (OperationCanceledException) { }
        })).ToArray();

        Thread.Sleep(50);
        var outBuf = new byte[target.OutLen];
        Array.Copy(payload, outBuf, Math.Min(payload.Length, outBuf.Length));
        txFs.Write(outBuf, 0, outBuf.Length);
        Console.WriteLine("TX: " + Hex(outBuf, Math.Min(outBuf.Length, 16)) + " ...");

        Task.WaitAll(tasks);
        foreach (var l in listeners) l.fs.Dispose();
        return 0;
    }
}
