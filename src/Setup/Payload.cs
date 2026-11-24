using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DeviceBatteryTray.Setup;

/// <summary>
/// 内嵌的程序文件（payload.zip，含清单 DeviceBatteryTray.files）。
/// 替换时先解压到临时子目录、把旧文件挪到备份子目录，再把新文件移进来；中途出错就回滚。
/// 只动清单里登记过的文件，所以即使安装目录里有别的文件（比如便携版解压到了桌面）也不会被删。
/// </summary>
static class Payload
{
    const string NewDir = ".setup-new", OldDir = ".setup-old";

    static Stream Open() =>
        typeof(Payload).Assembly.GetManifestResourceStream("payload.zip")
        ?? throw new InvalidOperationException("安装包不完整，请重新下载。");

    public static bool Present => typeof(Payload).Assembly.GetManifestResourceInfo("payload.zip") != null;

    /// <summary>解压后的总大小（字节）。</summary>
    public static long Size
    {
        get
        {
            using var zip = new ZipArchive(Open(), ZipArchiveMode.Read);
            return zip.Entries.Sum(e => e.Length);
        }
    }

    /// <summary>把程序文件装到 dir（覆盖旧版本）。progress 报告 0~1。</summary>
    public static void Install(string dir, Action<double> progress)
    {
        Directory.CreateDirectory(dir);
        string staging = Path.Combine(dir, NewDir), backup = Path.Combine(dir, OldDir);
        DeleteDir(staging);
        DeleteDir(backup);

        // 1. 解压到临时子目录
        List<string> files;
        using (var zip = new ZipArchive(Open(), ZipArchiveMode.Read))
        {
            var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
            files = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                var rel = entries[i].FullName.Replace('/', '\\');
                if (Path.IsPathRooted(rel) || rel.Contains("..")) throw new InvalidDataException("安装包内容异常：" + rel);
                var target = Path.Combine(staging, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entries[i].ExtractToFile(target, overwrite: true);
                files.Add(rel);
                progress(0.8 * (i + 1) / entries.Count);
            }
        }

        // 2. 旧文件（旧清单里的 + 和新文件同名的）挪到备份目录
        var moved = new List<string>();
        try
        {
            foreach (var rel in Product.ReadManifest(dir).Concat(files).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var src = Path.Combine(dir, rel);
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(src, dst);
                moved.Add(rel);
            }
            progress(0.9);

            // 3. 新文件移进来
            foreach (var rel in files)
            {
                var dst = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(Path.Combine(staging, rel), dst);
            }
        }
        catch (Exception e)
        {
            Log.W("替换文件失败，回滚: " + e);
            foreach (var rel in files)
                try { if (!File.Exists(Path.Combine(staging, rel))) File.Delete(Path.Combine(dir, rel)); } catch { } // 已经移进来的新文件
            foreach (var rel in moved)
                try { File.Move(Path.Combine(backup, rel), Path.Combine(dir, rel)); } catch { }
            DeleteDir(staging);
            DeleteDir(backup);
            throw new IOException("替换程序文件失败：" + e.Message, e);
        }

        DeleteDir(staging);
        DeleteDir(backup);
        progress(1);
    }

    /// <summary>删除清单里的程序文件和卸载程序；目录空了就一起删掉。</summary>
    public static void Remove(string dir)
    {
        foreach (var rel in Product.ReadManifest(dir).Append(Product.UninstallExe).Append(Product.Manifest))
            try { File.Delete(Path.Combine(dir, rel)); } catch (Exception e) { Log.W($"删除 {rel} 失败: {e.Message}"); }
        DeleteDir(Path.Combine(dir, NewDir));
        DeleteDir(Path.Combine(dir, OldDir));
        DeleteEmptyDirs(dir);
    }

    static void DeleteEmptyDirs(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir)) DeleteEmptyDirs(sub);
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception e) { Log.W($"删除 {dir} 失败: {e.Message}"); }
    }
}
