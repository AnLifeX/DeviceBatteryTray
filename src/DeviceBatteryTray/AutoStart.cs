using DeviceBatteryTray.Devices;
using Microsoft.Win32;

namespace DeviceBatteryTray;

/// <summary>
/// 开机自启。管理员身份运行时用「计划任务 + 最高权限」，开机不弹 UAC；
/// 普通权限时退回到 HKCU\Run（此时黑武士读不到）。两种方式都带 --background，开机不弹主页面。
/// </summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "DeviceBatteryTray";
    public const string BackgroundArg = "--background";

    public static bool Enabled
    {
        get
        {
            if (Schtasks("/query /tn " + Program.TaskName) == 0) return true;
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string v && v.Contains(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            k.DeleteValue(RunName, throwOnMissingValue: false);
            Schtasks($"/delete /tn {Program.TaskName} /f");
            if (!value) return;

            if (FlydigiVader.IsElevated()) CreateLogonTask();
            else k.SetValue(RunName, $"\"{Application.ExecutablePath}\" {BackgroundArg}");
        }
    }

    /// <summary>旧版本建的自启项没有 --background（开机会弹主页面），启动时补上。</summary>
    public static void Migrate()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                if (k?.GetValue(RunName) is string v && !v.Contains(BackgroundArg))
                    k.SetValue(RunName, $"\"{Application.ExecutablePath}\" {BackgroundArg}");

            if (!FlydigiVader.IsElevated()) return;
            var xml = Program.RunSchtasks($"/query /tn {Program.TaskName} /xml", out int code);
            if (code == 0 && !xml.Contains(BackgroundArg))
            {
                Log.W("计划任务缺少 --background，重建");
                CreateLogonTask();
            }
        }
        catch (Exception e) { Log.W("迁移自启项失败: " + e.Message); }
    }

    /// <summary>
    /// 用 XML 建计划任务：当前用户登录时以最高权限启动。
    /// 必须关掉默认的「运行超过 72 小时自动结束」和「仅接通电源时运行」，托盘程序要常驻。
    /// </summary>
    static void CreateLogonTask()
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        string Esc(string s) => System.Security.SecurityElement.Escape(s);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>任务栏显示无线外设电量</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>"{Esc(Application.ExecutablePath)}"</Command><Arguments>{BackgroundArg}</Arguments></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "DeviceBatteryTray.task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        Program.RunSchtasks($"/create /tn {Program.TaskName} /xml \"{file}\" /f", out int code);
        Log.W($"创建计划任务：退出码 {code}");
        File.Delete(file);
    }

    static int Schtasks(string args)
    {
        try { Program.RunSchtasks(args, out int code); return code; }
        catch { return -1; }
    }
}
