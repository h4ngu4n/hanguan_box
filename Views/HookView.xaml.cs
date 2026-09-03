using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HanguanBox.Helpers;
using Microsoft.Win32;

namespace HanguanBox.Views;

public partial class HookView : UserControl
{
    private const string LauncherProcess = McLauncher.ProcessName;
    private const string HookDll = McLauncher.HookDll;

    private string? _targetDir;
    private bool _busy;

    public HookView()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = DetectAsync(firstRun: true);
    }

    // ---------- 查找目录 ----------
    private void BtnDetect_Click(object sender, RoutedEventArgs e)
        => _ = DetectAsync(firstRun: false);

    private async Task DetectAsync(bool firstRun)
    {
        if (_busy) return;
        _busy = true;
        BtnDetect.IsEnabled = false;
        BtnInject.IsEnabled = false;
        BtnStartMc.IsEnabled = false;
        SetState("检测中…", "#F5C86B", "#33F5C86B");
        if (!firstRun) Log("正在自动查找启动器目录…");

        var progress = new Progress<string>(msg => TxtHookPath.Text = msg);
        (string? dir, string how) = await Task.Run(() => McLauncher.FindDir(progress));

        if (dir is null)
        {
            _targetDir = null;
            TxtHookPath.Text = "未找到，请点击「手动浏览」选择 MCLauncher.exe";
            SetState("未找到", "#FF6B6B", "#33FF6B6B");
            Log(firstRun
                ? "未能自动找到启动器目录，请点击「手动浏览」选择 MCLauncher.exe。"
                : "自动查找失败：未找到启动器目录。");
            SetInjectState("—", "#99FFFFFF", "#26FFFFFF");
        }
        else
        {
            _targetDir = dir;
            TxtHookPath.Text = dir;
            SetState("已找到", "#7BE3A8", "#3334C77B");
            Log($"启动器目录（{how}）：{dir}");
            await CheckInjectStatusAsync();
        }

        BtnDetect.IsEnabled = true;
        BtnInject.IsEnabled = _targetDir is not null;
        BtnStartMc.IsEnabled = _targetDir is not null;
        _busy = false;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择我的世界启动器",
            Filter = "我的世界启动器 (MCLauncher.exe)|MCLauncher.exe|所有程序 (*.exe)|*.exe"
        };
        if (dlg.ShowDialog() is not true) return;

        string? dir = Path.GetDirectoryName(dlg.FileName);
        if (dir is null) return;

        _targetDir = dir;
        TxtHookPath.Text = dir;
        SetState("已选择", "#7BE3A8", "#3334C77B");
        Log($"手动指定启动器目录：{dir}");
        BtnInject.IsEnabled = true;
        BtnStartMc.IsEnabled = true;
        _ = CheckInjectStatusAsync();
    }

    // ---------- 注入 ----------
    private async void BtnInject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string? dir = _targetDir;
        if (dir is null)
        {
            Log("尚未找到启动器目录，请先「自动查找」或「手动浏览」。");
            return;
        }

        _busy = true;
        SetBusy(true);

        Log("―――― 开始注入 ――――");

        // 0. 通过 MD5 判断是否已经注入
        string dest = Path.Combine(dir, HookDll);
        string? srcMd5 = await McLauncher.GetSourceMd5Async();
        if (srcMd5 is null)
        {
            Log($"错误：软件内没有内嵌 {HookDll}，根目录也没找到该文件，无法注入。");
            Finish();
            return;
        }

        if (File.Exists(dest) && await McLauncher.Md5OfFileAsync(dest) == srcMd5)
        {
            Log($"已注入过：{dest}");
            Log($"MD5 校验一致（{srcMd5}），无需重复注入。");
            SetInjectState("已注入", "#7BE3A8", "#3334C77B");
            Finish();
            return;
        }

        await InjectAsync(dir, srcMd5);
        Finish();
    }

    // ---------- 启动 MC（未注入则先注入再启动） ----------
    private async void BtnStartMc_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string? dir = _targetDir;
        if (dir is null)
        {
            Log("尚未找到启动器目录，请先「自动查找」或「手动浏览」。");
            return;
        }

        _busy = true;
        SetBusy(true);

        Log("―――― 启动 MC ――――");

        string dest = Path.Combine(dir, HookDll);
        string? srcMd5 = await McLauncher.GetSourceMd5Async();
        if (srcMd5 is null)
        {
            Log($"错误：软件内没有内嵌 {HookDll}，根目录也没找到该文件，无法注入。");
            Finish();
            return;
        }

        // 1. 未注入则先自动完成注入
        if (File.Exists(dest) && await McLauncher.Md5OfFileAsync(dest) == srcMd5)
        {
            Log("已注入（MD5 校验一致），直接启动。");
        }
        else
        {
            Log("尚未注入或 DLL 有更新，先自动完成注入…");
            if (!await InjectAsync(dir, srcMd5))
            {
                Finish();
                return;
            }
        }

        // 2. 启动我的世界启动器
        string exe = Path.Combine(dir, McLauncher.ExeName);
        if (!File.Exists(exe))
        {
            Log($"未找到 {McLauncher.ExeName}：{exe}");
            Finish();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
            Log($"我的世界启动器已启动：{exe}");
        }
        catch (Exception ex)
        {
            Log($"启动失败：{ex.Message}");
        }

        Finish();
    }

    // 注入流程：关闭启动器 → 写入 DLL，成功返回 true
    private async Task<bool> InjectAsync(string dir, string srcMd5)
    {
        string dest = Path.Combine(dir, HookDll);

        // 先关闭我的世界启动器
        if (!await CloseLauncherAsync())
        {
            Log("无法关闭启动器，注入已取消。");
            return false;
        }

        // 将 MCL.core.dll 放入启动器目录（优先使用内嵌资源，其次软件根目录）
        try
        {
            if (File.Exists(dest))
                Log("检测到已有 DLL 且版本不同，将覆盖更新。");

            Log($"正在将 {HookDll} 放入启动器目录…");
            Stream? embedded = McLauncher.GetEmbeddedDll();

            if (embedded is not null)
            {
                using (embedded)
                using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    embedded.CopyTo(fs);
                }
                Log($"来源：软件内嵌资源（{new FileInfo(dest).Length} 字节）");
            }
            else
            {
                string source = Path.Combine(AppContext.BaseDirectory, HookDll);
                File.Copy(source, dest, overwrite: true);
                Log("来源：软件根目录");
            }

            Log($"MD5：{srcMd5}");
            Log($"完成：{dest}");
            Log("注入成功。");
            SetInjectState("已注入", "#7BE3A8", "#3334C77B");
            return true;
        }
        catch (Exception ex)
        {
            Log($"写入失败：{ex.Message}");
            return false;
        }
    }

    private void SetBusy(bool busy)
    {
        BtnDetect.IsEnabled = !busy;
        BtnBrowse.IsEnabled = !busy;
        BtnInject.IsEnabled = !busy && _targetDir is not null;
        BtnStartMc.IsEnabled = !busy && _targetDir is not null;
    }

    private void Finish()
    {
        SetBusy(false);
        _busy = false;
    }

    // ---------- MD5 校验 / 注入状态 ----------
    private async Task CheckInjectStatusAsync()
    {
        string? dir = _targetDir;
        if (dir is null) return;

        SetInjectState("校验中…", "#F5C86B", "#33F5C86B");

        string? srcMd5 = await McLauncher.GetSourceMd5Async();
        if (srcMd5 is null)
        {
            SetInjectState("缺少 DLL", "#FF6B6B", "#33FF6B6B");
            return;
        }

        string dest = Path.Combine(dir, HookDll);
        if (!File.Exists(dest))
        {
            SetInjectState("未注入", "#99FFFFFF", "#26FFFFFF");
            return;
        }

        string destMd5 = await McLauncher.Md5OfFileAsync(dest);
        if (destMd5 == srcMd5)
            SetInjectState("已注入", "#7BE3A8", "#3334C77B");
        else
            SetInjectState("DLL 有更新", "#F5C86B", "#33F5C86B");
    }

    // ---------- 关闭启动器 ----------
    private async Task<bool> CloseLauncherAsync()
    {
        var procs = Process.GetProcessesByName(LauncherProcess);
        if (procs.Length == 0)
        {
            Log("我的世界启动器未在运行，跳过关闭。");
            return true;
        }

        Log($"检测到 {procs.Length} 个启动器进程，正在请求关闭…");
        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); p.Dispose(); }
            catch { /* 已自行退出 */ }
        }

        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            if (Process.GetProcessesByName(LauncherProcess).Length == 0)
            {
                Log("启动器已关闭。");
                return true;
            }
        }

        Log("启动器未响应，正在强制结束…");
        foreach (var p in Process.GetProcessesByName(LauncherProcess))
        {
            try { p.Kill(true); p.Dispose(); }
            catch { /* 权限不足或已退出 */ }
        }

        await Task.Delay(1500);
        bool ok = Process.GetProcessesByName(LauncherProcess).Length == 0;
        Log(ok ? "启动器已强制结束。" : "仍有启动器进程无法结束，可能需要管理员权限。");
        return ok;
    }

    // ---------- 日志 / 状态 ----------
    private void Log(string message)
    {
        TxtLog.Text = TxtLog.Text == "等待操作…" ? message : $"{TxtLog.Text}\n{message}";
        LogScroll.ScrollToEnd();
    }

    private void SetState(string text, string fg, string bg)
    {
        TxtState.Text = text;
        TxtState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        StateBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
    }

    private void SetInjectState(string text, string fg, string bg)
    {
        TxtInjectState.Text = text;
        TxtInjectState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        InjectBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
    }
}
