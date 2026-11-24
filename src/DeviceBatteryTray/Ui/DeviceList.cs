using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Ui;

/// <summary>一个型号读到的一台设备（还没套用名称 / 显示设置）。</summary>
public sealed record RawDevice(string ModelId, string Key, DeviceKind Kind, string Model, BatteryReading? Reading);

/// <summary>
/// 把读到的设备整理成界面用的列表：
/// - 设备 ID = 型号 ID，同型号多个接收器时加上 "#实例标识"（序列号或 USB 口），用于记住名称和显示开关；
/// - 同型号有多台时，第 2 台起自动编号（「VGN 键盘 2」），用户改过名字就用用户的名字；
/// - 用户在主页面关掉「显示」的设备标记为不可见（不在任务栏 / 弹出面板显示，也不提醒）。
/// </summary>
public static class DeviceList
{
    public static string IdOf(string modelId, string key) => key.Length == 0 ? modelId : modelId + "#" + key;

    public static List<DeviceStatus> Build(IEnumerable<RawDevice> raw)
    {
        var s = AppSettings.Current;
        var list = new List<DeviceStatus>();
        foreach (var group in raw.GroupBy(r => r.ModelId))
        {
            int n = 0;
            foreach (var r in group)
            {
                string id = IdOf(r.ModelId, r.Key);
                bool instance = r.Reading is { State: not ReadState.NotFound };
                string auto = instance && ++n > 1 ? $"{r.Model} {n}" : r.Model;
                string name = s.DeviceNames.TryGetValue(id, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : auto;
                list.Add(new DeviceStatus(id, r.Kind, name, r.Reading, r.Model, !s.HiddenDevices.Contains(id)));
            }
        }
        return list;
    }
}
