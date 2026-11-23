// FlydigiProbe: 需管理员运行。通过飞智自己的 XboxComm64.dll（连接 Xbox360Filter64.sys 过滤驱动）
// 向黑武士手柄发送 GamePadInfo(0x10) 查询，记录原始回包，用于确认电量字节的真实含义。
// 用法: FlydigiProbe <Flydigi Service 目录> <日志文件>
// 若飞智服务占着通讯端口导致失败，会临时停止 FDGMonitorService，结束时一定重新启动它。
using System.Runtime.InteropServices;
using System.ServiceProcess;

static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate bool GetDataCallback(IntPtr data, uint ctx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int InitFn([MarshalAs(UnmanagedType.LPStr)] string sysPath, bool startWithSystem, GetDataCallback cb, uint ctx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate bool CanWriteFn();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate bool WriteFn(IntPtr data, uint len);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string p);

    static StreamWriter log = null!;
    static GetDataCallback? keep;
    static readonly object gate = new();

    static void L(string s) { lock (gate) { var line = $"{DateTime.Now:HH:mm:ss.fff} {s}"; log.WriteLine(line); log.Flush(); Console.WriteLine(line); } }

    static int Main(string[] args)
    {
        string dir = args[0];
        log = new StreamWriter(args[1], append: false);
        const string svcName = "FDGMonitorService";
        bool stopped = false;
        try
        {
            if (Probe(dir, "服务运行中")) return 0;

            L("第一次失败，临时停止飞智服务后重试");
            using (var sc = new ServiceController(svcName))
            {
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    stopped = true;
                    L("服务已停止");
                }
            }
            Probe(dir, "服务已停止");
            return 0;
        }
        catch (Exception e) { L("异常: " + e); return 1; }
        finally
        {
            if (stopped)
            {
                try
                {
                    using var sc = new ServiceController(svcName);
                    sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    L("服务已重新启动: " + sc.Status);
                }
                catch (Exception e) { L("!!! 重启服务失败，请手动启动 FDGMonitorService: " + e.Message); }
            }
            log.Dispose();
        }
    }

    static int hits;

    static bool Probe(string dir, string tag)
    {
        hits = 0;
        SetDllDirectoryW(dir);
        var lib = NativeLibrary.Load(Path.Combine(dir, "XboxComm64.dll"));
        var init = Marshal.GetDelegateForFunctionPointer<InitFn>(NativeLibrary.GetExport(lib, "InitXboxMonitorRes"));
        var canWrite = Marshal.GetDelegateForFunctionPointer<CanWriteFn>(NativeLibrary.GetExport(lib, "IsCurrentUsbCanWrite"));
        var write = Marshal.GetDelegateForFunctionPointer<WriteFn>(NativeLibrary.GetExport(lib, "WriteData2Usb"));

        keep = (p, ctx) =>
        {
            if (p == IntPtr.Zero) return true;
            var b = new byte[32];
            Marshal.Copy(p + 8, b, 0, 32);   // PXBOX_DATA_ITEM { ulong pipeHandle; byte usbData[32]; }
            if (b[14] == 0xA5)               // 只记厂商回包，跳过普通按键数据
            {
                Interlocked.Increment(ref hits);
                L($"RX: {BitConverter.ToString(b).Replace('-', ' ')}");
            }
            return true;
        };

        string sys = @"C:\Windows\System32\drivers\Xbox360Filter64.sys";
        int r = init(sys, false, keep, 0);
        L($"[{tag}] InitXboxMonitorRes = {r}");
        Thread.Sleep(1500);
        bool cw = canWrite();
        L($"[{tag}] IsCurrentUsbCanWrite = {cw}");
        if (!cw) return false;

        for (int i = 0; i < 3; i++)
        {
            var cmd = new byte[15];
            cmd[0] = 0xA5; cmd[1] = 0x10;          // GamePadInfo
            byte crc = 0; for (int k = 0; k < 14; k++) crc += cmd[k]; cmd[14] = crc;
            var h = Marshal.AllocHGlobal(cmd.Length);
            Marshal.Copy(cmd, 0, h, cmd.Length);
            bool ok = write(h, (uint)cmd.Length);
            Marshal.FreeHGlobal(h);
            L($"[{tag}] TX {BitConverter.ToString(cmd).Replace('-', ' ')} => {ok}");
            Thread.Sleep(1000);
        }
        L($"[{tag}] 收到厂商回包 {hits} 个");
        return hits > 0;
    }
}
