using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace HanguanBox.Helpers;

/// <summary>我的世界启动器：目录查找、DLL 内嵌资源与 MD5 注入校验的公共逻辑</summary>
internal static class McLauncher
{
    public const string ProcessName = "WPFLauncher";
    public const string ExeName = "WPFLauncher.exe";
    public const string HookDll = "MCL.core.dll";
    public const string LauncherDirName = "MCLauncher";

    // ---------- 查找启动器目录：运行进程 → 快捷方式 → 注册表 → 磁盘深度扫描 ----------
    public static (string? dir, string how) FindDir(IProgress<string>? progress)
    {
        string? d;

        if ((d = FindFromProcess()) is not null) return (d, "运行中的进程");
        if ((d = FindFromShortcuts()) is not null) return (d, "快捷方式");
        if ((d = FindFromRegistry()) is not null) return (d, "注册表");
        if ((d = FindOnDisk(progress)) is not null) return (d, "磁盘扫描");

        return (null, string.Empty);
    }

    private static string? FindFromProcess()
    {
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                    return Path.GetDirectoryName(path);
            }
            catch { /* 权限不足或已退出 */ }
            finally { p.Dispose(); }
        }
        return null;
    }

    // 开始菜单 / 桌面快捷方式：解析 .lnk，目标位于 MCLauncher 目录内
    private static string? FindFromShortcuts()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return null;

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            string[] lnks;
            try { lnks = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (string lnkPath in lnks)
            {
                try
                {
                    string target = ResolveShortcut(shellType, lnkPath);
                    if (string.IsNullOrWhiteSpace(target)) continue;

                    string? dir = Path.GetDirectoryName(target);
                    if (dir is not null && IsLauncherDir(dir))
                        return dir;
                }
                catch { /* 解析失败的快捷方式忽略 */ }
            }
        }
        return null;
    }

    private static string ResolveShortcut(Type shellType, string lnkPath)
    {
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        return (string)lnk.TargetPath;
    }

    private static string? FindFromRegistry()
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in roots)
            {
                try
                {
                    using var key = hive.OpenSubKey(root);
                    if (key is null) continue;

                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var sk = key.OpenSubKey(sub);
                        if (sk is null) continue;

                        string name = sk.GetValue("DisplayName") as string ?? string.Empty;
                        string loc = sk.GetValue("InstallLocation") as string ?? string.Empty;
                        string icon = sk.GetValue("DisplayIcon") as string ?? string.Empty;
                        string unins = sk.GetValue("UninstallString") as string ?? string.Empty;

                        bool hit = name.Contains("我的世界")
                                || name.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)
                                || $"{icon} {unins} {loc}".Contains("mclauncher", StringComparison.OrdinalIgnoreCase);
                        if (!hit) continue;

                        foreach (var dir in CandidateDirs(loc, icon, unins))
                        {
                            if (IsLauncherDir(dir))
                                return dir;
                        }
                    }
                }
                catch { /* 注册表访问失败忽略 */ }
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs(string loc, string icon, string unins)
    {
        if (!string.IsNullOrWhiteSpace(loc))
        {
            string d = loc.Trim().Trim('"');
            if (Directory.Exists(d))
                yield return d;
        }

        foreach (string raw in new[] { icon, unins })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string p = raw.Trim().Trim('"');
            int comma = p.LastIndexOf(',');
            if (comma > 0) p = p[..comma].Trim();

            int exe = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0) p = p[..(exe + 4)];

            if (File.Exists(p))
            {
                string? d = Path.GetDirectoryName(p);
                if (d is not null)
                    yield return d;
            }
        }
    }

    // 磁盘深度扫描：BFS 查找名为 MCLauncher 的目录（忽略大小写，跳过系统目录与符号链接）
    private static string? FindOnDisk(IProgress<string>? progress)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "Windows",
            "ProgramData", "Recovery", "PerfLogs", "Config.Msi"
        };

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                continue;

            string root = drive.RootDirectory.FullName;
            var queue = new Queue<(string path, int depth)>();
            queue.Enqueue((root, 0));

            int visited = 0;
            string? fallback = null;
            var sw = Stopwatch.StartNew();

            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                visited++;

                if (visited % 2000 == 0)
                    progress?.Report($"正在扫描 {root}，已检查 {visited} 个目录，查找 {LauncherDirName} 目录…");

                if (sw.ElapsedMilliseconds > 120_000 || visited > 500_000)
                    break;

                if (depth >= 8) continue;

                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(dir, "*", options))
                    {
                        string name = Path.GetFileName(sub);
                        if (skip.Contains(name)) continue;

                        // 命中 MCLauncher 目录：内有启动器主程序直接采用，否则先记为备选继续找
                        if (name.Equals(LauncherDirName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(Path.Combine(sub, ExeName)))
                                return sub;
                            fallback ??= sub;
                        }

                        queue.Enqueue((sub, depth + 1));
                    }
                }
                catch { /* 无权限时忽略 */ }
            }

            if (fallback is not null)
                return fallback;
        }

        return null;
    }

    public static bool IsLauncherDir(string dir)
        => Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals(LauncherDirName, StringComparison.OrdinalIgnoreCase);

    // ---------- MD5 校验 ----------
    // 内置 DLL（内嵌资源优先，其次软件根目录）的 MD5
    public static async Task<string?> GetSourceMd5Async()
    {
        Stream? embedded = GetEmbeddedDll();
        if (embedded is not null)
        {
            using (embedded)
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = await md5.ComputeHashAsync(embedded);
                return Convert.ToHexString(hash);
            }
        }

        string source = Path.Combine(AppContext.BaseDirectory, HookDll);
        return File.Exists(source) ? await Md5OfFileAsync(source) : null;
    }

    public static async Task<string> Md5OfFileAsync(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, useAsync: true);
        using var md5 = System.Security.Cryptography.MD5.Create();
        byte[] hash = await md5.ComputeHashAsync(fs);
        return Convert.ToHexString(hash);
    }

    // 目标目录中的 DLL 是否与内置版本一致（已注入）
    public static async Task<bool> IsInjectedAsync(string dir)
    {
        string dest = Path.Combine(dir, HookDll);
        if (!File.Exists(dest)) return false;

        string? srcMd5 = await GetSourceMd5Async();
        if (srcMd5 is null) return false;

        return await Md5OfFileAsync(dest) == srcMd5;
    }

    // 从程序集内嵌资源读取 MCL.core.dll
    public static Stream? GetEmbeddedDll()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string? name = asm.GetManifestResourceNames().FirstOrDefault(n
            => n.Equals(HookDll, StringComparison.OrdinalIgnoreCase)
            || n.EndsWith("." + HookDll, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }
}
