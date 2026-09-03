using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using HanguanBox.Helpers;

namespace HanguanBox;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UIElement> _views = new();

    private const uint BlurTint = 0xB41E1E23u; // 窗口底色（AARRGGBB）

    public MainWindow()
    {
        InitializeComponent();

        _views["mclauncher"] = new Views.McLauncherView();
        _views["hook"] = new Views.HookView();

        Loaded += (_, _) => BlurBackground.Enable(this, BlurTint, useAcrylic: true);
        NavMcLauncher.IsChecked = true;
    }

    // ---------- 导航 ----------
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string key || PageHost is null) return;
        if (!_views.TryGetValue(key, out var view)) return;

        PageHost.Content = view;
        PageTitle.Text = key switch
        {
            "mclauncher" => "下载我的世界启动器",
            "hook" => "注入 HOOK",
            _ => string.Empty
        };
    }

    // ---------- 标题栏 ----------
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null) return;

        if (e.ClickCount == 2)
        {
            ToggleMax();
            return;
        }
        try { DragMove(); } catch { /* 忽略拖动时的无效调用 */ }
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) => ToggleMax();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMax()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Maximized)
            RootBorder.CornerRadius = new CornerRadius(0);
        else
            RootBorder.CornerRadius = new CornerRadius(16);

        MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    // ---------- 最大化时限制在工作区内（不遮挡任务栏） ----------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_GETMINMAXINFO = 0x0024;

        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                // 拖动/缩放期间切换为轻量模糊，避免 Acrylic 逐帧重算导致卡顿
                BlurBackground.Enable(this, BlurTint, useAcrylic: false);
                break;
            case WM_EXITSIZEMOVE:
                BlurBackground.Enable(this, BlurTint, useAcrylic: true);
                break;
            case WM_GETMINMAXINFO:
                HandleGetMinMaxInfo(hwnd, lParam, ref handled);
                break;
        }

        return IntPtr.Zero;
    }

    private void HandleGetMinMaxInfo(IntPtr hwnd, IntPtr lParam, ref bool handled)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(mi);
        if (!GetMonitorInfo(monitor, ref mi)) return;

        mmi.ptMaxPosition.x = Math.Abs(mi.rcWork.left - mi.rcMonitor.left);
        mmi.ptMaxPosition.y = Math.Abs(mi.rcWork.top - mi.rcMonitor.top);
        mmi.ptMaxSize.x = Math.Abs(mi.rcWork.right - mi.rcWork.left);
        mmi.ptMaxSize.y = Math.Abs(mi.rcWork.bottom - mi.rcWork.top);

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
}
