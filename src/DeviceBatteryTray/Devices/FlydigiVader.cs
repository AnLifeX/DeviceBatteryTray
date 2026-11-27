using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeviceBatteryTray.Devices;

/// <summary>
/// 飞智 黑武士 3 Pro（XInput 模式，接收器伪装成 045E:028E）。
/// 接收器没有厂商 HID 接口，只能经飞智内核过滤驱动 Xbox360Filter64.sys 收发，
/// 这里直接加载飞智安装目录下的 XboxComm64.dll（需管理员权限），与飞智后台服务可以共存。
/// 协议见 docs/protocols/flydigi-vader3pro.md。
/// </summary>
public sealed class FlydigiVader : BatteryProvider
{
    const ushort Vid = 0x045E, Pid = 0x028E;

    public override string Id => "flydigi-vader3";
    public override string DisplayName => "飞智 黑武士 3 Pro";
    public override DeviceKind Kind => DeviceKind.Gamepad;

    public static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public const string NeedAdminDetail = "需要管理员权限才能读取电量";
    public const string NotReadyDetail = "飞智驱动未连上手柄";
    public const string ServiceBusyDetail = "飞智空间站服务运行中，无法同时读取电量";

    /// <summary>飞智后台服务（FDGMonitorService → GameControllerService.exe）是否在运行。</summary>
    static bool ServiceRunning()
    {
        var ps = System.Diagnostics.Process.GetProcessesByName("GameControllerService");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    static bool ServiceInstalled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\FDGMonitorService");
            return k != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 是否值得请求管理员权限：设置里开着「读取黑武士」+ 装了飞智空间站 + 插着接收器。
    /// 不满足就以普通权限运行，不弹 UAC。
    /// </summary>
    public static bool NeedsElevation =>
        AppSettings.Current.ReadFlydigi && ServiceInstalled() && Present(Vid, Pid);

    public override BatteryReading Read()
    {
        if (!AppSettings.Current.ReadFlydigi || !Present(Vid, Pid)) return BatteryReading.NotFound;
        if (!IsElevated()) return new BatteryReading(ReadState.Error, Detail: NeedAdminDetail);
        // 飞智服务运行时会独占过滤驱动的回包（我们发得出去但永远收不到），还可能干扰空间站；
        // 这时不碰驱动，已经连上的也断开，等服务退出后再读
        if (ServiceRunning()) { Comm.Stop(); _last = null; return BatteryReading.Fail(ServiceBusyDetail); }
        if (!Comm.EnsureStarted(out var why)) return BatteryReading.Fail(why);

        // 驱动没连上手柄（不是手柄休眠）：先尝试重新连接驱动，仍不行就如实报错
        if (!Comm.CanWrite() && !Comm.Reconnect()) return BatteryReading.Fail(NotReadyDetail);

        var info = Comm.QueryGamepadInfo(TimeSpan.FromMilliseconds(1500));
        if (info == null)
        {
            // 手柄醒着时也偶尔漏一次应答（实测约每 4~5 次漏 1 次），连续多次没应答才算休眠 / 关机
            if (_last != null && ++_misses < MissesToOffline) return _last;
            _last = null;
            return BatteryReading.Offline;
        }
        _misses = 0;
        return _last = Parse(info);
    }

    const int MissesToOffline = 3;
    static BatteryReading? _last;
    static int _misses;

    static BatteryReading Parse(byte[] info)
    {
        // [16] 设备 ID（黑武士 3 Pro = 80），[23] 电量：CH571 新固件为 0~4 档（待更多数据点确认）
        int raw = info[23];
        if (raw <= 4)
            return new BatteryReading(ReadState.Ok, raw * 25, Detail: "估算");
        // 老固件：电压原始值，按飞智服务的算法 98~114 线性映射
        int pct = (int)((Math.Clamp(raw, 98, 114) - 98) / 16f * 100);
        return new BatteryReading(ReadState.Ok, pct);
    }

    /// <summary>对 XboxComm64.dll 的封装：初始化一次，回调里收集 A5 10 应答。</summary>
    private static class Comm
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool DataCallback(IntPtr data, uint ctx);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int InitFn([MarshalAs(UnmanagedType.LPStr)] string sysPath, bool startWithSystem, DataCallback cb, uint ctx);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int UninitFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool CanWriteFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool WriteFn(IntPtr data, uint len);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string? p);

        static readonly object Gate = new();
        static bool _started;
        static DataCallback? _cb;   // 必须保持引用，否则被 GC 回收后驱动回调会崩
        static InitFn? _init;
        static UninitFn? _uninit;
        static string _sys = "";
        static DateTime _lastInit;
        static readonly TimeSpan ReconnectInterval = TimeSpan.FromMinutes(1);
        static CanWriteFn? _canWrite;
        static WriteFn? _write;
        static TaskCompletionSource<byte[]>? _pending;

        public static bool EnsureStarted(out string why)
        {
            lock (Gate)
            {
                why = "";
                if (_started) return true;
                var dir = FindFlydigiServiceDir();
                if (dir == null) { why = "未找到飞智驱动组件"; return false; }
                try
                {
                    SetDllDirectoryW(dir);
                    var lib = NativeLibrary.Load(Path.Combine(dir, "XboxComm64.dll"));
                    SetDllDirectoryW(null);
                    _init = Marshal.GetDelegateForFunctionPointer<InitFn>(NativeLibrary.GetExport(lib, "InitXboxMonitorRes"));
                    if (NativeLibrary.TryGetExport(lib, "UninitXboxMonitorRes", out var un))
                        _uninit = Marshal.GetDelegateForFunctionPointer<UninitFn>(un);
                    _canWrite = Marshal.GetDelegateForFunctionPointer<CanWriteFn>(NativeLibrary.GetExport(lib, "IsCurrentUsbCanWrite"));
                    _write = Marshal.GetDelegateForFunctionPointer<WriteFn>(NativeLibrary.GetExport(lib, "WriteData2Usb"));
                    _cb = OnData;
                    _sys = File.Exists(@"C:\Windows\System32\drivers\Xbox360Filter64.sys")
                        ? @"C:\Windows\System32\drivers\Xbox360Filter64.sys" : Path.Combine(dir, "Xbox360Filter64.sys");
                    int r = _init(_sys, false, _cb, 0);
                    _lastInit = DateTime.Now;
                    Thread.Sleep(1500); // 驱动连接建立需要一点时间
                    Log.W($"飞智驱动初始化 = {r}，可写 = {_canWrite()}");
                    _started = true;
                    return true;
                }
                catch (Exception e) { why = "加载飞智驱动组件失败：" + e.Message; return false; }
            }
        }

        public static bool CanWrite() => _started && _canWrite?.Invoke() == true;

        /// <summary>断开与过滤驱动的连接（DLL 保持加载，下次 EnsureStarted 重新连接）。</summary>
        public static void Stop()
        {
            lock (Gate)
            {
                if (!_started) return;
                _started = false;
                try { _uninit?.Invoke(); Log.W("飞智服务运行中，已断开飞智驱动"); }
                catch (Exception e) { Log.W("断开飞智驱动失败：" + e.Message); }
            }
        }

        /// <summary>
        /// 驱动不可写时（比如接收器重插、手柄晚于初始化才连上）断开重连一次；限频，避免每次轮询都卡 1.5 秒。
        /// </summary>
        public static bool Reconnect()
        {
            lock (Gate)
            {
                if (_init == null || _cb == null || DateTime.Now - _lastInit < ReconnectInterval) return false;
                _lastInit = DateTime.Now;
                try
                {
                    _uninit?.Invoke();
                    int r = _init(_sys, false, _cb, 0);
                    Thread.Sleep(1500);
                    bool ok = CanWrite();
                    Log.W($"飞智驱动重新连接 = {r}，可写 = {ok}");
                    return ok;
                }
                catch (Exception e) { Log.W("飞智驱动重新连接失败：" + e.Message); return false; }
            }
        }

        static bool OnData(IntPtr p, uint ctx)
        {
            if (p == IntPtr.Zero) return true;
            var b = new byte[32];
            Marshal.Copy(p + 8, b, 0, 32); // PXBOX_DATA_ITEM { ulong pipeHandle; byte usbData[32]; }
            if (b[14] == 0xA5 && b[15] == 0x10) Volatile.Read(ref _pending)?.TrySetResult(b);
            return true;
        }

        public static byte[]? QueryGamepadInfo(TimeSpan timeout)
        {
            if (_write == null || !CanWrite()) return null;
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pending, tcs);

            var cmd = new byte[15];
            cmd[0] = 0xA5; cmd[1] = 0x10; // GamePadInfo
            byte crc = 0; for (int i = 0; i < 14; i++) crc += cmd[i]; cmd[14] = crc;
            var h = Marshal.AllocHGlobal(cmd.Length);
            try
            {
                // 回包会在飞智服务和我们之间分发，发两次提高命中率
                for (int i = 0; i < 2 && !tcs.Task.IsCompleted; i++)
                {
                    Marshal.Copy(cmd, 0, h, cmd.Length);
                    _write(h, (uint)cmd.Length);
                    tcs.Task.Wait(timeout / 2);
                }
            }
            finally { Marshal.FreeHGlobal(h); Volatile.Write(ref _pending, null); }
            return tcs.Task.IsCompletedSuccessfully ? tcs.Task.Result : null;
        }

        static string? FindFlydigiServiceDir()
        {
            // 从飞智后台服务的注册信息找安装目录
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\FDGMonitorService");
            var img = (k?.GetValue("ImagePath") as string)?.Trim('"');
            var dir = img == null ? null : Path.GetDirectoryName(img.Split(".exe")[0] + ".exe");
            return dir != null && File.Exists(Path.Combine(dir, "XboxComm64.dll")) ? dir : null;
        }
    }
}
