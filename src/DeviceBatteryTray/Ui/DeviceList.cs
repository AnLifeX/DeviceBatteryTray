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

    /// <summary>
    /// 没有序列号的接收器按 USB 口区分（Key 以 "p-" 开头），换个口插 ID 就变了。
    /// 读完一轮后调用：某个型号里恰好有一台「从没见过的新 ID」、又恰好有一条「当前不在的旧 ID」的名称 / 显示设置时，
    /// 认为是同一台换了口，把设置迁移过来。「见过」很重要：同型号两台、拔掉改过名的那台时，剩下那台是见过的，不会被误改名。
    /// 有序列号的设备不迁移（换口 ID 不变，对不上就真是另一台）。
    /// </summary>
    public static void MigrateMovedDevices(IEnumerable<RawDevice> raw)
    {
        var s = AppSettings.Current;
        var instances = raw.Where(r => r.Reading is { State: not ReadState.NotFound }).ToList();
        var moves = new List<(string From, string To)>();
        foreach (var group in instances.GroupBy(r => r.ModelId))
        {
            var present = group.Select(r => IdOf(r.ModelId, r.Key)).ToList();
            var fresh = group.Where(r => r.Key.StartsWith("p-")).Select(r => IdOf(r.ModelId, r.Key))
                .Where(id => !s.SeenDevices.Contains(id)).ToList();
            var orphans = s.DeviceNames.Keys.Concat(s.HiddenDevices)
                .Where(id => id.StartsWith(group.Key + "#p-") && !present.Contains(id)).Distinct().ToList();
            if (fresh.Count == 1 && orphans.Count == 1) moves.Add((orphans[0], fresh[0]));
        }
        var unseen = instances.Select(r => IdOf(r.ModelId, r.Key)).Where(id => !s.SeenDevices.Contains(id)).ToList();
        if (moves.Count == 0 && unseen.Count == 0) return;
        AppSettings.Change(x =>
        {
            foreach (var (from, to) in moves)
            {
                Log.W($"设备换了 USB 口：{from} → {to}，沿用名称和显示设置");
                if (x.DeviceNames.Remove(from, out var name)) x.DeviceNames[to] = name;
                if (x.HiddenDevices.Remove(from)) x.HiddenDevices.Add(to);
            }
            x.SeenDevices.AddRange(unseen);
        });
    }

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
