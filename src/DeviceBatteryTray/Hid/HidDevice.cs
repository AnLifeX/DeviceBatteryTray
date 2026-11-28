using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeviceBatteryTray.Hid;

/// <summary>一个 HID collection（Windows 上每个 top-level collection 是一个独立的设备路径）。</summary>
public sealed record HidInterface(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage,
                                  int InputLength, int OutputLength, int FeatureLength, string Product, string Serial = "")
{
    /// <summary>
    /// 区分同型号多个接收器的稳定标识：优先用 HID 序列号；没有序列号时用设备路径里的实例段
    /// （和插在哪个 USB 口有关，换口会变），取 8 位哈希。
    /// </summary>
    public string InstanceKey
    {
        get
        {
            if (Serial.Length > 0) return "sn-" + Serial;
            var parts = Path.Split('#');
            string inst = parts.Length > 2 ? parts[2] : Path;
            var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(inst.ToLowerInvariant()));
            return "p-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }
    }
}

public static class HidEnumerator
{
    public static List<HidInterface> Find(ushort vid, ushort pid)
    {
        var list = new List<HidInterface>();
        foreach (var path in Native.EnumerateHidPaths())
        {
            using var h = Native.Open(path, readWrite: false);
            if (h.IsInvalid) continue;
            var a = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
            if (!Native.HidD_GetAttributes(h, ref a) || a.VendorID != vid || a.ProductID != pid) continue;
            if (!Native.HidD_GetPreparsedData(h, out var pp)) continue;
            try
            {
                Native.HidP_GetCaps(pp, out var c);
                var name = new byte[256];
                string product = Native.HidD_GetProductString(h, name, name.Length)
                    ? System.Text.Encoding.Unicode.GetString(name).TrimEnd('\0') : "";
                var sn = new byte[256];
                string serial = Native.HidD_GetSerialNumberString(h, sn, sn.Length)
                    ? System.Text.Encoding.Unicode.GetString(sn).TrimEnd('\0').Trim() : "";
                list.Add(new HidInterface(path, vid, pid, c.UsagePage, c.Usage,
                    c.InputReportByteLength, c.OutputReportByteLength, c.FeatureReportByteLength, product, serial));
            }
            finally { Native.HidD_FreePreparsedData(pp); }
        }
        return list;
    }
}

/// <summary>打开一个 HID collection 做「写一个输出报告 → 读到匹配的输入报告」的请求/应答。</summary>
public sealed class HidConnection : IDisposable
{
    private readonly FileStream _fs;
    public HidInterface Interface { get; }
    public static Action<string>? Debug;

    private HidConnection(HidInterface i, FileStream fs) { Interface = i; _fs = fs; }

    public static HidConnection? Open(HidInterface i)
    {
        var h = Native.Open(i.Path, readWrite: true);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new HidConnection(i, new FileStream(h, FileAccess.ReadWrite, 1, isAsync: true));
    }

    /// <summary>发送报告（自动补 0 到输出报告长度），在超时内读取直到 match 返回 true。</summary>
    public byte[]? Request(byte[] report, Func<byte[], bool> match, int timeoutMs = 800)
    {
        // 必须先挂上读请求再写：部分接收器（如 VGN）应答极快，先写后读会丢包
        using var cts = new CancellationTokenSource(timeoutMs);
        using var cancelReg = cts.Token.Register(() => Native.CancelIoEx(_fs.SafeFileHandle, IntPtr.Zero));
        var reader = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var inBuf = new byte[Interface.InputLength];
                    int n = await _fs.ReadAsync(inBuf, 0, inBuf.Length, cts.Token);
                    Debug?.Invoke($"RX[{n}] {BitConverter.ToString(inBuf, 0, Math.Min(n, 16))}");
                    if (n > 0 && match(inBuf)) return inBuf;
                }
            }
            catch (OperationCanceledException) { return null; }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
        });

        Thread.Sleep(20); // 让读请求先下到驱动
        var buf = new byte[Interface.OutputLength];
        Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
        try
        {
            // 必须使用异步写入并共享超时：同步 Write 在设备休眠或 USB 挂起时会无限期阻塞整个轮询线程
            _fs.WriteAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            cts.Cancel();
            Native.CancelIoEx(_fs.SafeFileHandle, IntPtr.Zero);
            reader.Wait(200);
            return null;
        }
        Debug?.Invoke($"TX[{buf.Length}] {BitConverter.ToString(buf, 0, 16)} via {Interface.Path}");

        try
        {
            return reader.GetAwaiter().GetResult();
        }
        finally
        {
            cts.Cancel();
        }
    }

    public byte[]? GetFeature(byte reportId)
    {
        var b = new byte[Interface.FeatureLength];
        b[0] = reportId;
        return Native.HidD_GetFeature(_fs.SafeFileHandle, b, b.Length) ? b : null;
    }

    public void Dispose()
    {
        Native.CancelIoEx(_fs.SafeFileHandle, IntPtr.Zero);
        _fs.Dispose();
    }
}

internal static class Native
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
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] b, int len);

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int req, IntPtr dev);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    public static SafeFileHandle Open(string path, bool readWrite) =>
        CreateFile(path, readWrite ? 0xC0000000 : 0, 3, IntPtr.Zero, 3, readWrite ? 0x40000000u : 0, IntPtr.Zero);

    public static IEnumerable<string> EnumerateHidPaths()
    {
        HidD_GetHidGuid(out var g);
        var set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) yield break;
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                var buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out _, IntPtr.Zero))
                        yield return Marshal.PtrToStringUni(buf + 4)!;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
