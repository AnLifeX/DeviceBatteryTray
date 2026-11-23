// GameSirProbe: 32 位小程序，直接调用盖世小鸡官方 SDK (sapjoyuniapis_x86_v1.0.11.dll) 读取手柄信息。
// 用法: GameSirProbe <sapjoyuniapis dll 路径> [等待秒数=4]
// SDK 接口来源: GameSir Connect 1.3.7 dist/electron/main.js 中的 ffi 定义。
using System.Runtime.InteropServices;

static unsafe class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct SAPJOY_ATTRIBUTES
    {
        public uint size;
        public IntPtr pcwszName;
        public fixed ushort vid[16];
        public fixed ushort pid[16];
        public IntPtr pfnEvtFirmwareUpdate;
        public IntPtr pfnEvtDeviceChange;
        public IntPtr pfnEvtGamepadStateNotfication;
        public IntPtr pvContext;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void DeviceChange(IntPtr dev, int arrived, int index, IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int CreateFn(ref SAPJOY_ATTRIBUTES a, out IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetInfoFn(IntPtr dev, byte* info);

    static readonly List<IntPtr> Devices = new();
    static DeviceChange? keep;

    static int Main(string[] args)
    {
        var dllPath = args[0];
        int wait = args.Length > 1 ? int.Parse(args[1]) : 4;
        var lib = NativeLibrary.Load(dllPath);
        IatHook.Install(lib);
        var create = Marshal.GetDelegateForFunctionPointer<CreateFn>(NativeLibrary.GetExport(lib, "SAPJoyUni_Create"));
        var getInfo = Marshal.GetDelegateForFunctionPointer<GetInfoFn>(NativeLibrary.GetExport(lib, "SAPJoyUniDevice_GetDeviceInfo"));

        keep = (dev, arrived, index, ctx) =>
        {
            Console.WriteLine($"[回调] dev=0x{dev:X} arrived={arrived} index={index}");
            lock (Devices) { if (arrived != 0) Devices.Add(dev); else Devices.Remove(dev); }
        };

        var a = new SAPJOY_ATTRIBUTES
        {
            size = (uint)sizeof(SAPJOY_ATTRIBUTES),
            pcwszName = Marshal.StringToHGlobalUni("GameSirConnect"),
            pfnEvtDeviceChange = Marshal.GetFunctionPointerForDelegate(keep),
        };
        // 与 GameSir Connect 相同: vid=13623(0x3537), pid=[4176,4158,4110]
        a.vid[0] = 0x3537;
        a.pid[0] = 4176; a.pid[1] = 4158; a.pid[2] = 4110;

        int r = create(ref a, out var handle);
        Console.WriteLine($"SAPJoyUni_Create = {r}, size={a.size}");
        if (r != 0) return 1;

        Thread.Sleep(wait * 1000);

        lock (Devices)
        {
            if (Devices.Count == 0) Console.WriteLine("没有发现设备");
            foreach (var d in Devices)
            {
                var buf = stackalloc byte[64];
                int s = getInfo(d, buf);
                var hex = string.Join(" ", Enumerable.Range(0, 16).Select(i => buf[i].ToString("X2")));
                // {id, firmware[2], mac[6], battery, type}
                Console.WriteLine($"GetDeviceInfo = {s}: {hex}");
                Console.WriteLine($"  id={buf[0]} fw={buf[1]}.{buf[2]} mac={string.Join(":", Enumerable.Range(3, 6).Select(i => buf[i].ToString("X2")))} battery={buf[9]} type={buf[10]}");
            }
        }
        return 0;
    }
}

// 改写 SDK DLL 的导入表，记录它和接收器之间的原始 HID 数据（仅调试用）
static unsafe class IatHook
{
    [DllImport("kernel32.dll")] static extern bool VirtualProtect(IntPtr a, nuint size, uint prot, out uint old);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr m, string n);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string n);

    static delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, IntPtr, int> origWrite;
    static delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, IntPtr, IntPtr, int> origReadEx;
    static readonly Dictionary<IntPtr, (IntPtr buf, IntPtr routine)> pending = new();

    static void Log(string tag, byte* b, uint n)
    {
        var hex = string.Join(" ", Enumerable.Range(0, (int)Math.Min(n, 64u)).Select(i => b[i].ToString("X2")));
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {tag}[{n}]: {hex}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static int HookWrite(IntPtr h, byte* buf, uint n, uint* written, IntPtr ov)
    {
        if (n <= 64) Log("TX", buf, n); // 跳过 SDK 写自己日志文件的文本
        return origWrite(h, buf, n, written, ov);
    }

    static delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, IntPtr, int> origRead;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static int HookRead(IntPtr h, byte* buf, uint n, uint* got, IntPtr ov)
    {
        int r = origRead(h, buf, n, got, ov);
        if (r != 0 && got != null && *got > 0 && *got <= 64) Log("RX", buf, *got);
        else if (n <= 64) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} ReadFile(len={n}, overlapped={(ov != IntPtr.Zero)}) ret={r}");
        return r;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static void HookCompletion(uint err, uint n, IntPtr ov)
    {
        (IntPtr buf, IntPtr routine) e;
        lock (pending) { e = pending[ov]; pending.Remove(ov); }
        if (err == 0) Log("RX", (byte*)e.buf, n);
        ((delegate* unmanaged[Stdcall]<uint, uint, IntPtr, void>)e.routine)(err, n, ov);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static int HookReadEx(IntPtr h, byte* buf, uint n, IntPtr ov, IntPtr routine)
    {
        lock (pending) pending[ov] = ((IntPtr)buf, routine);
        return origReadEx(h, buf, n, ov, (IntPtr)(delegate* unmanaged[Stdcall]<uint, uint, IntPtr, void>)&HookCompletion);
    }

    public static void Install(IntPtr module)
    {
        var k32 = GetModuleHandleW("kernel32.dll");
        origWrite = (delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, IntPtr, int>)GetProcAddress(k32, "WriteFile");
        origReadEx = (delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, IntPtr, IntPtr, int>)GetProcAddress(k32, "ReadFileEx");
        Patch(module, "WriteFile", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, IntPtr, int>)&HookWrite);
        origRead = (delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, IntPtr, int>)GetProcAddress(k32, "ReadFile");
        Patch(module, "ReadFile", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, IntPtr, int>)&HookRead);
    }

    static void Patch(IntPtr mod, string func, IntPtr hook)
    {
        byte* b = (byte*)mod;
        int nt = *(int*)(b + 0x3C);
        int impRva = *(int*)(b + nt + 0x18 + 96 + 8); // PE32: OptionalHeader.DataDirectory[1]
        for (byte* d = b + impRva; *(int*)(d + 12) != 0; d += 20)
        {
            var dll = Marshal.PtrToStringAnsi((IntPtr)(b + *(int*)(d + 12)))!;
            if (!dll.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase)) continue;
            int* names = (int*)(b + *(int*)d), iat = (int*)(b + *(int*)(d + 16));
            for (int i = 0; names[i] != 0; i++)
            {
                if (names[i] < 0) continue; // 按序号导入
                var n = Marshal.PtrToStringAnsi((IntPtr)(b + names[i] + 2));
                if (n != func) continue;
                VirtualProtect((IntPtr)(&iat[i]), 4, 0x04, out var old);
                iat[i] = (int)hook;
                VirtualProtect((IntPtr)(&iat[i]), 4, old, out _);
                Console.WriteLine($"已 hook {func}");
            }
        }
    }
}
