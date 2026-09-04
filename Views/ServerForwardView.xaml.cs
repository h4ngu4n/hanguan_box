using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HanguanBox.Helpers;

namespace HanguanBox.Views;

public partial class ServerForwardView : UserControl
{
    // 转发成功弹窗内容（按 1.20.1 客户端说明）
    private const string SuccessMessage =
        "服务器转发成功！\n\n" +
        "现在可以通过 127.0.0.1:25565 访问服务器（地址已复制到剪贴板）。\n\n" +
        "注意：该方式仅对 1.20.1 版本客户端有效，其他版本客户端请使用 ViaFabric 等 mod 修改协议版本。";

    public ServerForwardView()
    {
        InitializeComponent();

        NativeForward.StateChanged += OnServiceStateChanged;
        NativeForward.OutputReceived += OnServiceOutput;
        NativeForward.Exited += OnServiceExited;

        RefreshButtons(NativeForward.IsRunning);
    }

    // ---------- 状态 ----------
    // 线程池回调，统一回到 UI 线程
    private void OnServiceStateChanged(bool running)
        => Dispatcher.Invoke(() => RefreshButtons(running));

    private void OnServiceOutput(string line)
        => Dispatcher.Invoke(() => Log(line));

    private void OnServiceExited(int code, bool manual, TimeSpan uptime)
        => Dispatcher.Invoke(() => HandleExited(code, manual, uptime));

    private void RefreshButtons(bool running)
    {
        if (running)
        {
            SetState("运行中", "#7BE3A8", "#3334C77B");
            Log("转发服务运行中。");
        }
        else
        {
            SetState("已停止", "#99FFFFFF", "#26FFFFFF");
        }

        BtnStart.IsEnabled = !running;
        BtnStop.IsEnabled = running;
    }

    // ---------- 进程结束 ----------
    private void HandleExited(int code, bool manual, TimeSpan uptime)
    {
        if (manual)
        {
            Log($"转发服务已关闭（运行 {FormatUptime(uptime)}）。");
            return;
        }

        // 刚启动就退出视为异常（缺文件/端口占用等），不弹成功提示
        if (uptime < TimeSpan.FromSeconds(3))
        {
            Log($"转发服务异常退出：退出码 {code}（运行 {FormatUptime(uptime)}）。");
            return;
        }

        Log($"转发完成（运行 {FormatUptime(uptime)}，退出码 {code}）。");

        try { Clipboard.SetText(NativeForward.ListenAddress); }
        catch { /* 剪贴板被占用时忽略 */ }

        MessageBox.Show(Application.Current.MainWindow, SuccessMessage, "寒竿工具箱",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- 按钮 ----------
    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        BtnStart.IsEnabled = false;
        SetState("启动中…", "#F5C86B", "#33F5C86B");
        Log("正在启动转发服务…");

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                NativeForward.Start();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    Log($"启动失败：{ex.Message}");
                    RefreshButtons(false);
                });
            }
        });
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        Log("正在关闭转发服务…");
        NativeForward.Stop();
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(NativeForward.ListenAddress);
            Log($"已复制地址：{NativeForward.ListenAddress}");
        }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    // ---------- 日志 / 状态 ----------
    private void Log(string message)
    {
        string text = TxtLog.Text == "等待操作…" ? message : $"{TxtLog.Text}\n{message}";

        // 防止长期运行日志无限增长
        if (text.Length > 8000)
            text = "…（较早日志已省略）\n" + text[^6000..];

        TxtLog.Text = text;
        LogScroll.ScrollToEnd();
    }

    private void SetState(string text, string fg, string bg)
    {
        TxtState.Text = text;
        TxtState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        StateBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
    }

    private static string FormatUptime(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分"
         : t.TotalMinutes >= 1 ? $"{t.Minutes} 分 {t.Seconds} 秒"
         : $"{t.Seconds} 秒";
}
