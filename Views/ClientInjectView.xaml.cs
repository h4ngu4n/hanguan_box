using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HanguanBox.Helpers;

namespace HanguanBox.Views;

public partial class ClientInjectView : UserControl
{
    /// <summary>Java 进程快照条目（列表绑定用）</summary>
    public sealed class JavaProcInfo
    {
        public int Pid { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool HasWindow { get; init; }
        public string Title { get; init; } = string.Empty;
        public string ExePath { get; init; } = string.Empty;
        public string WindowText => HasWindow ? "有窗口" : "无窗口";
    }

    private readonly DispatcherTimer _timer;
    private bool _busy;
    private bool _injecting;
    private int? _selectedPid;
    private string _lastFingerprint = string.Empty;

    public ClientInjectView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => _ = ScanAsync(auto: true);

        // 默认开启自动刷新：创建页面即开始扫描（XAML 勾选状态仅用于用户手动暂停）
        _timer.Start();
        _ = ScanAsync(auto: true);
    }

    // ---------- 扫描入口 ----------
    private void BtnScan_Click(object sender, RoutedEventArgs e)
        => _ = ScanAsync(auto: false);

    private void ChkAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (_timer is null) return; // XAML 初始化期间触发

        if (ChkAuto.IsChecked == true)
        {
            _ = ScanAsync(auto: true);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private async Task ScanAsync(bool auto)
    {
        if (_busy) return;
        _busy = true;
        BtnScan.IsEnabled = false;

        if (!auto) Log("正在扫描 java / javaw 进程…");

        List<JavaProcInfo> list = await Task.Run(FindJavaProcesses);

        ProcList.ItemsSource = list;

        // 尽量还原上一次选中的 PID
        if (_selectedPid is int pid)
        {
            JavaProcInfo? again = list.FirstOrDefault(x => x.Pid == pid);
            if (again is not null)
                ProcList.SelectedItem = again;
            else
                _selectedPid = null;
        }

        int windowed = list.Count(x => x.HasWindow);
        TxtCount.Text = list.Count > 0 ? $"共 {list.Count} 个进程，{windowed} 个带窗口" : string.Empty;
        TxtEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (list.Count == 0)
        {
            SetState("未捕获", "#99FFFFFF", "#26FFFFFF");
            if (!auto) Log("未发现 java / javaw 进程。请先启动我的世界 Java 版客户端。");
        }
        else
        {
            SetState($"已捕获 {list.Count} 个", "#7BE3A8", "#3334C77B");
        }

        // 自动刷新时仅在结果变化时记录日志，避免刷屏
        string fingerprint = string.Join(",", list.Select(x => x.Pid));
        if (fingerprint != _lastFingerprint)
        {
            if (list.Count == 0)
            {
                Log("未发现 java / javaw 进程。");
            }
            else
            {
                Log($"捕获 {list.Count} 个 Java 进程（{windowed} 个带窗口）：");
                foreach (var p in list)
                    Log(p.HasWindow
                        ? $"  PID {p.Pid}  {p.Name}.exe  窗口：「{p.Title}」"
                        : $"  PID {p.Pid}  {p.Name}.exe  （无窗口）");
            }
        }
        _lastFingerprint = fingerprint;

        BtnScan.IsEnabled = true;
        _busy = false;
    }

    // 枚举所有 java / javaw 进程：优先带窗口的（图形界面客户端）
    private static List<JavaProcInfo> FindJavaProcesses()
    {
        var result = new List<JavaProcInfo>();

        foreach (Process p in Process.GetProcesses())
        {
            string name;
            try { name = p.ProcessName; }
            catch { continue; }

            if (!name.Equals("java", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("javaw", StringComparison.OrdinalIgnoreCase))
            {
                p.Dispose();
                continue;
            }

            bool hasWindow = false;
            string title = string.Empty;
            string exe = string.Empty;

            try
            {
                hasWindow = p.MainWindowHandle != IntPtr.Zero;
                if (hasWindow) title = p.MainWindowTitle ?? string.Empty;
            }
            catch { /* 权限不足或已退出 */ }

            try { exe = p.MainModule?.FileName ?? string.Empty; }
            catch { /* 32/64 位不匹配或权限不足 */ }

            result.Add(new JavaProcInfo
            {
                Pid = p.Id,
                Name = name,
                HasWindow = hasWindow,
                Title = title,
                ExePath = exe
            });

            p.Dispose();
        }

        return result.OrderByDescending(x => x.HasWindow).ThenBy(x => x.Pid).ToList();
    }

    // ---------- 列表选择 ----------
    private void ProcList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcList.SelectedItem is not JavaProcInfo info)
        {
            _selectedPid = null;
            return;
        }

        _selectedPid = info.Pid;
        Log(info.HasWindow
            ? $"已选中窗口进程：PID {info.Pid}（{info.Title}）"
            : $"已选中进程：PID {info.Pid}（{info.Name}.exe，无窗口）");

        UpdateInjectState();
    }

    // ---------- 注入 ----------
    private InjectMethod? _method;

    private void InjMethod_Changed(object sender, RoutedEventArgs e)
    {
        if (BtnInject is null || RbZenLoader is null) return; // XAML 初始化期间触发

        _method = RbZenLoader.IsChecked == true ? ClientInject.ZenLoader : ClientInject.Vape421;
        TxtVersionNote.Text = _method.VersionNote;
        UpdateInjectState();
    }

    private void UpdateInjectState()
    {
        BtnInject.IsEnabled = _selectedPid is not null && !_injecting;
        TxtInjectTarget.Text = _selectedPid is int pid ? $"目标 PID：{pid}" : "尚未选择进程";
    }

    private async void BtnInject_Click(object sender, RoutedEventArgs e)
    {
        if (_method is not InjectMethod method)
        {
            Log("请先选择注入方式。");
            return;
        }

        if (_selectedPid is not int pid)
        {
            Log("请先在上方列表中选择一个 Java 进程。");
            return;
        }

        // 版本要求确认：各注入方式对客户端版本有硬性要求
        MessageBoxResult confirm = MessageBox.Show(
            $"【{method.DisplayName}】{method.VersionNote}\n\n确定要对所选进程继续注入吗？",
            "注入确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _injecting = true;
        UpdateInjectState();
        Log($"开始 {method.DisplayName} → PID {pid}（{method.CommandLine(pid)}）…");

        try
        {
            (bool ok, int code, string output) = await Task.Run(() => ClientInject.InjectAsync(method, pid));

            foreach (string line in output.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) Log("  " + line.TrimEnd());

            if (ok)
                Log("注入完成。");
            else
                Log($"注入失败（退出码 {code}）。{method.VersionNote}");
        }
        catch (Exception ex)
        {
            Log($"注入出错：{ex.Message}");
        }
        finally
        {
            _injecting = false;
            UpdateInjectState();
        }
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
}
