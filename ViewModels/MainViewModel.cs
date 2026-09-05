using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace HanguanBox.ViewModels;

/// <summary>导航项模型（扁平化：仅文字）</summary>
public class NavItem
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
}

/// <summary>主窗口 ViewModel：导航状态、页面标题、背景图、加载指示</summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>左侧主导航</summary>
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem { Key = "notes",        Label = "注意事项" },
        new NavItem { Key = "mclauncher",   Label = "下载启动器" },
        new NavItem { Key = "hook",         Label = "注入 HOOK" },
        new NavItem { Key = "clientinject", Label = "客户端注入" },
        new NavItem { Key = "settings",     Label = "设置" },
    };

    /// <summary>左下角固定项：转发服务开关与状态</summary>
    public NavItem ForwardItem { get; } = new() { Key = "serverforward", Label = "服务器转发" };

    [ObservableProperty]
    private string currentKey = "";

    [ObservableProperty]
    private string pageTitle = "注意事项";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? backgroundPath;

    [ObservableProperty]
    private double backgroundBlurRadius = 35;

    /// <summary>导航请求（由视图层订阅，负责缓存页面与切换动画）</summary>
    public event EventHandler<string>? PageRequested;

    /// <summary>切换页面：先展示加载动画，再通知视图层换页</summary>
    [RelayCommand]
    private async Task NavigateAsync(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == CurrentKey) return;

        IsBusy = true;
        await Task.Delay(300); // 展示环形加载动画，避免切换闪烁
        CurrentKey = key;
        PageTitle = NavItems.Concat(new[] { ForwardItem })
                            .FirstOrDefault(i => i.Key == key)?.Label ?? string.Empty;
        IsBusy = false;      // 先撤下加载层，再换页，保证滑入动画可见
        PageRequested?.Invoke(this, key);
    }

    /// <summary>选择自定义背景图片（毛玻璃效果由视图层 BlurEffect 实现）</summary>
    [RelayCommand]
    private void PickBackground()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true && File.Exists(dlg.FileName))
            BackgroundPath = dlg.FileName;
    }

    /// <summary>清除自定义背景</summary>
    [RelayCommand]
    private void ClearBackground() => BackgroundPath = null;
}
