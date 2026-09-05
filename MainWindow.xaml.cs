using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using HanguanBox.Helpers;
using HanguanBox.ViewModels;

namespace HanguanBox;

public partial class MainWindow : Window
{
    /// <summary>页面缓存：切换导航时复用实例，保留各页面的运行状态（下载进度、日志等）</summary>
    private readonly Dictionary<string, UIElement> _views = new();

    private readonly MainViewModel _vm;

    private const uint BlurTint = 0xF2161822u; // 窗口底色（AARRGGBB，深色）

    private bool _bgAVisible; // 背景交叉淡化时当前显示的图层

    public MainWindow()
    {
        InitializeComponent();

        _views["notes"] = new Views.NotesView();
        _views["mclauncher"] = new Views.McLauncherView();
        _views["hook"] = new Views.HookView();
        _views["clientinject"] = new Views.ClientInjectView();
        _views["serverforward"] = new Views.ServerForwardView();
        _views["settings"] = new Views.SettingsView();

        _vm = new MainViewModel();
        DataContext = _vm;

        // 导航请求 → 换页 + 播放滑入动画
        _vm.PageRequested += (_, key) => ShowPage(key);

        // 背景图 / 模糊半径变化 → 毛玻璃层交叉淡化
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.BackgroundPath))
                ApplyBackground(_vm.BackgroundPath);
            else if (e.PropertyName == nameof(MainViewModel.BackgroundBlurRadius))
                ApplyBlurRadius(_vm.BackgroundBlurRadius);
        };

        // 转发服务状态同步到左侧导航徽标
        NativeForward.StateChanged += running => Dispatcher.Invoke(() => UpdateForwardBadge(running));

        Loaded += (_, _) =>
        {
            BlurBackground.Enable(this, BlurTint, useAcrylic: true);
            Task.Run(NativeForward.LaunchOnStartup); // 软件启动时默认拉起转发服务
            BeginStoryboard((Storyboard)Resources["SpinnerStoryboard"]); // 常驻旋转（隐藏时无渲染开销）
            NavNotes.IsChecked = true; // 默认进入注意事项
        };
    }

    // ---------- 导航 ----------
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string key) return;
        _vm.NavigateCommand.Execute(key);
    }

    private void ShowPage(string key)
    {
        if (!_views.TryGetValue(key, out var view)) return;

        PageHost.BeginAnimation(OpacityProperty, null);
        PageTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        PageHost.Content = view;
        PageHost.BeginStoryboard((Storyboard)Resources["PageInStoryboard"], HandoffBehavior.SnapshotAndReplace);
    }

    // ---------- 背景毛玻璃 ----------
    private void ApplyBackground(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            // 无背景：淡出两个图层
            FadeBg(BgImageA, 0);
            FadeBg(BgImageB, 0);
            _bgAVisible = false;
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 释放文件句柄
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze(); // 跨线程安全 + 渲染优化

            var show = _bgAVisible ? BgImageB : BgImageA; // 双层交替 → 交叉淡化
            var hide = _bgAVisible ? BgImageA : BgImageB;
            _bgAVisible = !_bgAVisible;

            ApplyBlurRadius(_vm.BackgroundBlurRadius);
            show.Source = bmp;
            hide.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.Zero));
            FadeBg(show, 1);
        }
        catch
        {
            // 图片加载失败时忽略，保持原背景
        }
    }

    private static void FadeBg(System.Windows.Controls.Image img, double to)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
            }
        };
        img.BeginAnimation(OpacityProperty, anim);
    }

    private void ApplyBlurRadius(double radius)
    {
        if (BgImageA.Effect is System.Windows.Media.Effects.BlurEffect a) a.Radius = radius;
        if (BgImageB.Effect is System.Windows.Media.Effects.BlurEffect b) b.Radius = radius;
    }

    // ---------- 转发服务徽标 ----------
    private void UpdateForwardBadge(bool running)
    {
        TxtForwardState.Text = running ? "已启动" : "已停止";
        TxtForwardState.Foreground = new System.Windows.Media.SolidColorBrush(
            running ? System.Windows.Media.Color.FromRgb(0x7B, 0xE3, 0xA8)
                    : System.Windows.Media.Color.FromRgb(0x99, 0xFF, 0xFF));
        ForwardBadge.Background = new System.Windows.Media.SolidColorBrush(
            running ? System.Windows.Media.Color.FromArgb(0x33, 0x34, 0xC7, 0x7B)
                    : System.Windows.Media.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    }

    protected override void OnClosed(EventArgs e)
    {
        // 退出软件时一并结束转发服务进程
        NativeForward.Stop();
        base.OnClosed(e);
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
            RootBorder.CornerRadius = new CornerRadius(10);

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
