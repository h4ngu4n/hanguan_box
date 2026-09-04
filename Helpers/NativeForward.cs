using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HanguanBox.Helpers;

/// <summary>服务器 native 转发服务：内嵌资源释放、injector.exe 进程管理与状态通知</summary>
internal static class NativeForward
{
    public const string ExeName = "injector.exe";
    public const string DllName = "MinecraftProxy.dll";
    public const string ListenAddress = "127.0.0.1:25565";

    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "prxy");
    private static readonly string ExePath = Path.Combine(Dir, ExeName);

    private static Process? _proc;
    private static Stopwatch? _uptime;
    private static bool _manualStop;

    public static bool IsRunning => _proc is { HasExited: false };
    public static string ExeFullPath => ExePath;

    /// <summary>运行状态变化（true = 运行中）。回调在线程池线程触发</summary>
    public static event Action<bool>? StateChanged;
    /// <summary>子进程输出行（stdout / stderr）。回调在线程池线程触发</summary>
    public static event Action<string>? OutputReceived;
    /// <summary>进程退出（退出码 / 是否手动关闭 / 已运行时长）。回调在线程池线程触发</summary>
    public static event Action<int, bool, TimeSpan>? Exited;

    // ---------- 软件启动时拉起服务 ----------
    public static void LaunchOnStartup()
    {
        try { Start(); }
        catch (Exception ex)
        {
            StateChanged?.Invoke(false);
            OutputReceived?.Invoke($"[转发服务] 启动失败：{ex.Message}");
        }
    }

    // ---------- 启动服务：清理残留 → 释放内嵌文件 → injector.exe MinecraftProxy.dll ----------
    public static void Start()
    {
        if (IsRunning) return;

        KillOrphans();
        ExtractFiles();

        _manualStop = false;
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = $"\"{DllName}\"",
            WorkingDirectory = Dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        p.Exited += (_, _) => OnExited(p);

        _proc = p;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _uptime = Stopwatch.StartNew();

        StateChanged?.Invoke(true);
    }

    // ---------- 手动关闭服务 ----------
    public static void Stop()
    {
        if (_proc is null) return;
        _manualStop = true;

        try
        {
            if (!_proc.HasExited)
                _proc.Kill(entireProcessTree: true);
        }
        catch { /* 已自行退出或权限不足 */ }
    }

    // ---------- 进程退出 ----------
    private static void OnExited(Process p)
    {
        TimeSpan uptime = _uptime?.Elapsed ?? TimeSpan.Zero;
        bool manual = _manualStop;
        int code;
        try { code = p.ExitCode; }
        catch { code = -1; }

        if (ReferenceEquals(_proc, p))
        {
            _proc = null;
            _uptime = null;
        }

        StateChanged?.Invoke(false);
        Exited?.Invoke(code, manual, uptime);
    }

    // ---------- 清理上次异常退出留下的同名进程 ----------
    private static void KillOrphans()
    {
        string name = Path.GetFileNameWithoutExtension(ExeName);
        foreach (Process p in Process.GetProcessesByName(name))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase))
                    p.Kill(entireProcessTree: true);
            }
            catch { /* 权限不足或已退出 */ }
            finally { p.Dispose(); }
        }
    }

    // ---------- 释放内嵌资源到软件目录 ----------
    private static void ExtractFiles()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        Directory.CreateDirectory(Dir);

        foreach (string file in new[] { ExeName, DllName })
        {
            using Stream? src = asm.GetManifestResourceStream("prxy/" + file);
            if (src is null)
                throw new FileNotFoundException($"程序集内缺少内嵌资源 prxy/{file}");

            string dest = Path.Combine(Dir, file);
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(fs);
        }
    }
}
