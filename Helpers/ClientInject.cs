using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace HanguanBox.Helpers;

/// <summary>客户端注入方式描述：内嵌资源名前缀、命令行模板与版本要求</summary>
internal sealed class InjectMethod
{
    /// <summary>资源目录名 / 释放子目录名（vape / zen）</summary>
    public string Key { get; init; } = string.Empty;
    /// <summary>界面显示名</summary>
    public string DisplayName { get; init; } = string.Empty;
    public string ExeName { get; init; } = string.Empty;
    /// <summary>随附固定 DLL（无则为 null）</summary>
    public string? DllName { get; init; }
    /// <summary>命令行模板，{0} 为目标 PID</summary>
    public string ArgsFormat { get; init; } = "{0}";
    /// <summary>注入成功的客户端版本要求</summary>
    public string VersionNote { get; init; } = string.Empty;

    /// <summary>完整命令行（仅用于日志展示）</summary>
    public string CommandLine(int pid) => $"{ExeName} {string.Format(ArgsFormat, pid)}";
}

/// <summary>客户端注入：内嵌资源释放 + 注入器调用（OpenVape V4.21 / OpenZenLoader）</summary>
internal static class ClientInject
{
    // OpenVape V4.21：Vape421Injector <PID> Vape421Native.dll，要求客户端版本大于 1.21
    public static readonly InjectMethod Vape421 = new()
    {
        Key = "vape",
        DisplayName = "OpenVape V4.21 注入",
        ExeName = "Vape421Injector.exe",
        DllName = "Vape421Native.dll",
        ArgsFormat = "{0} Vape421Native.dll",
        VersionNote = "需要客户端版本大于 1.21 才可以成功注入，低于 1.21 的客户端将注入失败。"
    };

    // OpenZenLoader：OpenZenLoader <PID> --nogui，要求客户端版本 1.20.1 + Forge 40.4.20
    public static readonly InjectMethod ZenLoader = new()
    {
        Key = "zen",
        DisplayName = "OpenZenLoader 注入",
        ExeName = "OpenZenLoader.exe",
        DllName = null,
        ArgsFormat = "{0} --nogui",
        VersionNote = "需要客户端版本 1.20.1 + Forge 40.4.20 才可以成功注入，其他版本将注入失败。"
    };

    public static readonly InjectMethod[] Methods = { Vape421, ZenLoader };

    /// <summary>释放内嵌文件并对目标 PID 执行注入，返回（是否成功，退出码，输出文本）</summary>
    public static async Task<(bool Ok, int ExitCode, string Output)> InjectAsync(InjectMethod method, int pid)
    {
        string dir = ExtractFiles(method);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(dir, method.ExeName),
            Arguments = string.Format(method.ArgsFormat, pid),
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process? p = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 {method.ExeName}");

        string stdout = await p.StandardOutput.ReadToEndAsync();
        string stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        string output = string.Join(Environment.NewLine,
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return (p.ExitCode == 0, p.ExitCode, output);
    }

    // ---------- 释放内嵌资源到软件目录对应子目录 ----------
    // 返回释放目录
    private static string ExtractFiles(InjectMethod method)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string dir = Path.Combine(AppContext.BaseDirectory, method.Key);
        Directory.CreateDirectory(dir);

        foreach (string file in new[] { method.ExeName, method.DllName }
                     .Where(f => f is not null).Select(f => f!))
        {
            string dest = Path.Combine(dir, file);

            // 已释放过（大小一致）则跳过：避免每次注入重写大文件，也规避目标进程仍占用 DLL 时覆盖失败
            if (File.Exists(dest))
            {
                try
                {
                    using Stream? probe = asm.GetManifestResourceStream(method.Key + "/" + file)
                        ?? throw new FileNotFoundException($"程序集内缺少内嵌资源 {method.Key}/{file}");
                    if (new FileInfo(dest).Length == probe.Length)
                        continue;
                }
                catch (FileNotFoundException) { throw; }
                catch { /* 大小探测失败时尝试重新释放 */ }
            }

            using Stream? src = asm.GetManifestResourceStream(method.Key + "/" + file)
                ?? throw new FileNotFoundException($"程序集内缺少内嵌资源 {method.Key}/{file}");

            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(fs);
        }

        return dir;
    }
}
