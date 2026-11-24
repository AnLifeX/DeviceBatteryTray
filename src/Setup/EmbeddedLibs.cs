using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace DeviceBatteryTray.Setup;

/// <summary>
/// 从 exe 内嵌的 libs.zip 加载 WPF-UI 等依赖（见 Setup.csproj 的 EmbedReferencedLibs），安装包因此只有一个文件。
/// 必须在用到这些程序集的任何类型之前注册（Program.Main 第一行）。
/// </summary>
static class EmbeddedLibs
{
    static Dictionary<string, byte[]>? _libs;
    static readonly object Lock = new();

    public static Assembly? Resolve(object? sender, ResolveEventArgs e)
    {
        var name = new AssemblyName(e.Name).Name;
        if (name == null || name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
        lock (Lock)
        {
            _libs ??= Load();
            return _libs.TryGetValue(name, out var bytes) ? Assembly.Load(bytes) : null;
        }
    }

    static Dictionary<string, byte[]> Load()
    {
        var libs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(EmbeddedLibs).Assembly.GetManifestResourceStream("libs.zip");
        if (s == null) return libs; // 开发时直接运行输出目录，dll 就在旁边
        using var zip = new ZipArchive(s, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            using var es = entry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            libs[Path.GetFileNameWithoutExtension(entry.Name)] = ms.ToArray();
        }
        return libs;
    }
}
