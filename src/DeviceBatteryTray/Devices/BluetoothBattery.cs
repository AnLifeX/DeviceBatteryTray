using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DeviceBatteryTray.Devices;

/// <summary>
/// 通用蓝牙设备电量：直接读 Windows 蓝牙栈缓存在设备节点上的电量属性（即「设置 → 蓝牙」里显示的电量），
/// 经典蓝牙（HFP 上报）和 BLE（系统读 GATT Battery Service 0x180F）都写在同一个属性里。
/// 只用 SetupAPI 读属性，不需要管理员、不需要驱动，也不会主动和设备通讯。说明见 docs/protocols/bluetooth.md
/// </summary>
public sealed class BluetoothBattery : BatteryProvider
{
    public override string Id => "bluetooth";
    public override string DisplayName => "蓝牙设备";
    public override DeviceKind Kind => DeviceKind.Other;
    public override ConnectionMode Connection => ConnectionMode.Bluetooth;

    // {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2：DEVPKEY_Bluetooth_Battery（BYTE，0-100）
    static readonly DevPropKey BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    // {83DA6326-97A6-4088-9453-A1923F573B29} 15：设备当前是否已连接（BOOLEAN）
    static readonly DevPropKey ConnectedKey = new(new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), 15);
    static readonly DevPropKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    static readonly DevPropKey DeviceDescKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
    static readonly DevPropKey ClassOfDeviceKey = new(new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), 10);
    static readonly DevPropKey CategoryKey = new(new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 90);
    static readonly DevPropKey ClassKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 9);

    /// <summary>蓝牙设备节点所在的枚举器：BTHENUM / BTHLE 是设备本身，BTHLEDEVICE / BTHHFENUM 是它的服务子节点。</summary>
    static readonly string[] Enumerators = { "BTHENUM", "BTHLE", "BTHLEDEVICE", "BTHHFENUM" };

    sealed class Node
    {
        public string InstanceId = "", Address = "";
        public bool IsDevice;          // BTHENUM\DEV_xxx / BTHLE\DEV_xxx
        public string? Name, Class;
        public string[]? Category;
        public uint? ClassOfDevice;
        public int? Battery;
        public bool? Connected;
    }

    public override IReadOnlyList<DeviceReading> ReadAll()
    {
        var nodes = Enumerate();
        var list = new List<DeviceReading>();
        foreach (var g in nodes.GroupBy(n => n.Address))
        {
            var dev = g.FirstOrDefault(n => n.IsDevice);
            if (dev == null) continue;
            int? battery = dev.Battery ?? g.Select(n => n.Battery).FirstOrDefault(b => b != null);
            if (battery == null) continue; // 系统从没拿到过这台设备的电量：不支持，不列出

            // 已配对但没连接：属性里还留着上次的电量，按「不在」处理（不显示），连上后系统会立刻更新
            bool connected = dev.Connected ?? false;
            var reading = connected ? new BatteryReading(ReadState.Ok, Math.Clamp(battery.Value, 0, 100)) : BatteryReading.NotFound;
            string name = dev.Name ?? ("蓝牙设备 " + FormatAddress(g.Key));
            list.Add(new DeviceReading(g.Key, reading) { Name = name, Kind = InferKind(dev, g) });
        }
        if (list.Count == 0 || list.All(r => r.Reading.State == ReadState.NotFound))
            return new[] { new DeviceReading("", BatteryReading.NotFound) };
        // 只返回连上的设备；断开的不占位（Key 为蓝牙地址，重连后名称 / 显示设置照旧）
        return list.Where(r => r.Reading.State != ReadState.NotFound).ToList();
    }

    static string FormatAddress(string a) => string.Join(":", Enumerable.Range(0, 6).Select(i => a.Substring(i * 2, 2))).ToUpperInvariant();

    // ---------- 设备类型 ----------

    static DeviceKind InferKind(Node dev, IEnumerable<Node> all)
    {
        // 1. 设备容器分类（Windows 根据 CoD / 外观值 / HID 报告推断，最准）
        foreach (var c in dev.Category ?? Array.Empty<string>())
        {
            var k = FromCategory(c);
            if (k != null) return k.Value;
        }
        // 2. 经典蓝牙 Class of Device
        if (dev.ClassOfDevice is uint cod && FromClassOfDevice(cod) is { } k2) return k2;
        // 3. 子节点：有键盘 / 鼠标 / 音频端点
        var classes = all.Select(n => n.Class).Where(c => c != null).ToList();
        if (classes.Contains("Keyboard")) return DeviceKind.Keyboard;
        if (classes.Contains("Mouse")) return DeviceKind.Mouse;
        if (all.Any(n => n.Name?.Contains("XINPUT", StringComparison.OrdinalIgnoreCase) == true)) return DeviceKind.Gamepad;
        if (classes.Contains("MEDIA") || classes.Contains("AudioEndpoint")) return DeviceKind.Headset;
        return DeviceKind.Other;
    }

    static DeviceKind? FromCategory(string c)
    {
        if (c.StartsWith("Input.Keyboard", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Keyboard;
        if (c.StartsWith("Input.Mouse", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Input.Touchpad", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Mouse;
        if (c.StartsWith("Input.Gaming", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Gamepad;
        if (c.StartsWith("Audio.Speaker", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Speaker;
        if (c.StartsWith("Communication.Headset", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Headset;
        if (c.StartsWith("Audio.", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Headset; // Headset / Headphone / Handsfree
        return null;
    }

    /// <summary>Class of Device：major = bit 8-12，minor = bit 2-7。</summary>
    static DeviceKind? FromClassOfDevice(uint cod)
    {
        uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F;
        switch (major)
        {
            case 0x04: // Audio/Video
                return minor is 0x05 or 0x07 ? DeviceKind.Speaker : DeviceKind.Headset; // 0x05 扬声器，0x07 便携音箱
            case 0x05: // Peripheral
                if ((minor & 0x30) == 0x10) return DeviceKind.Keyboard;
                if ((minor & 0x30) == 0x20) return DeviceKind.Mouse;
                if ((minor & 0x0F) is 0x01 or 0x02) return DeviceKind.Gamepad; // 操纵杆 / 手柄
                if ((minor & 0x30) == 0x30) return DeviceKind.Keyboard;       // 键鼠一体
                return null;
            default: return null;
        }
    }

    // ---------- SetupAPI ----------

    static readonly Regex AddressRe = new(@"(?<![0-9A-F])[0-9A-F]{12}(?![0-9A-F])", RegexOptions.IgnoreCase);

    /// <summary>实例 ID 里的 12 位蓝牙地址（去掉服务 GUID 后找，全 0 的本地服务不算）。</summary>
    static string? AddressOf(string instanceId)
    {
        var s = Regex.Replace(instanceId, @"\{[^}]*\}", "");
        foreach (Match m in AddressRe.Matches(s))
            if (m.Value.Trim('0').Length > 0) return m.Value.ToLowerInvariant();
        return null;
    }

    static List<Node> Enumerate()
    {
        var nodes = new List<Node>();
        foreach (var e in Enumerators)
        {
            // 不带 DIGCF_PRESENT：没连接的 BLE 设备节点可能是「不在」状态，但属性仍可读（用来判断支不支持）
            var set = SetupDiGetClassDevs(IntPtr.Zero, e, IntPtr.Zero, DIGCF_ALLCLASSES);
            if (set == InvalidHandle) continue;
            try
            {
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    var id = InstanceId(set, ref info);
                    if (id == null || AddressOf(id) is not { } addr) continue;
                    bool isDevice = (e is "BTHENUM" or "BTHLE") && id.StartsWith(e + @"\DEV_", StringComparison.OrdinalIgnoreCase);
                    var n = new Node
                    {
                        InstanceId = id, Address = addr, IsDevice = isDevice,
                        Battery = GetByte(set, ref info, BatteryKey),
                        Name = GetString(set, ref info, FriendlyNameKey) ?? GetString(set, ref info, DeviceDescKey),
                        Class = GetString(set, ref info, ClassKey),
                    };
                    if (isDevice)
                    {
                        n.Connected = GetBool(set, ref info, ConnectedKey);
                        n.Category = GetStringList(set, ref info, CategoryKey);
                        n.ClassOfDevice = GetUInt32(set, ref info, ClassOfDeviceKey);
                    }
                    nodes.Add(n);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        return nodes;
    }

    static string? InstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var sb = new char[512];
        return SetupDiGetDeviceInstanceId(set, ref info, sb, sb.Length, out int len) ? new string(sb, 0, Math.Max(0, len - 1)) : null;
    }

    static byte[]? GetRaw(IntPtr set, ref SP_DEVINFO_DATA info, DevPropKey key, out uint type)
    {
        var buf = new byte[256];
        if (SetupDiGetDeviceProperty(set, ref info, ref key, out type, buf, buf.Length, out int size, 0)) return buf[..size];
        if (Marshal.GetLastWin32Error() != 122 /* ERROR_INSUFFICIENT_BUFFER */ || size <= 0) return null;
        buf = new byte[size];
        return SetupDiGetDeviceProperty(set, ref info, ref key, out type, buf, buf.Length, out size, 0) ? buf[..size] : null;
    }

    static int? GetByte(IntPtr set, ref SP_DEVINFO_DATA info, DevPropKey key) =>
        GetRaw(set, ref info, key, out var t) is { Length: >= 1 } b && t == DEVPROP_TYPE_BYTE ? b[0] : null;

    static bool? GetBool(IntPtr set, ref SP_DEVINFO_DATA info, DevPropKey key) =>
        GetRaw(set, ref info, key, out var t) is { Length: >= 1 } b && t == DEVPROP_TYPE_BOOLEAN ? b[0] != 0 : null;

    static uint? GetUInt32(IntPtr set, ref SP_DEVINFO_DATA info, DevPropKey key) =>
        GetRaw(set, ref info, key, out var t) is { Length: >= 4 } b && t == DEVPROP_TYPE_UINT32 ? BitConverter.ToUInt32(b) : null;

    static string? GetString(IntPtr set, ref SP_DEVINFO_DATA info, DevPropKey key) =>
        GetRaw(set, ref info, key, out var t) is { } b && t == DEVPROP_TYPE_STRING ? System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0') : null;

    static string[]? GetStringList(IntPtr set, ref SP_DEVINFO_DATA info, DevPropKey key) =>
        GetRaw(set, ref info, key, out var t) is { } b && t == DEVPROP_TYPE_STRING_LIST
            ? System.Text.Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;

    const int DIGCF_ALLCLASSES = 0x04;
    const uint DEVPROP_TYPE_BYTE = 0x03, DEVPROP_TYPE_UINT32 = 0x07, DEVPROP_TYPE_BOOLEAN = 0x11,
        DEVPROP_TYPE_STRING = 0x12, DEVPROP_TYPE_STRING_LIST = 0x2012;
    static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct DevPropKey(Guid FmtId, uint Pid);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, char[] id, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DevPropKey key, out uint type,
        byte[] buffer, int size, out int required, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
