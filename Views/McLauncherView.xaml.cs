using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace HanguanBox.Views;

public partial class McLauncherView : UserControl
{
    // 官方落地页（返回 HTML，真实下载地址藏在页面 pc_link 变量里，非重定向）
    private const string DownloadPageUrl = "https://adl.netease.com/d/g/mc/c/pe?type=windows";
    private const string FallbackFileName = "MCLauncher.exe";

    private static readonly Regex PcLinkRegex = new(
        "var\\s+pc_link\\s*=\\s*\"(https?://[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnyExeLinkRegex = new(
        "https?://[^\\s\"']+\\.exe\\?[^\\s\"']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HttpClient Http = CreateHttp();

    private CancellationTokenSource? _cts;
    private string? _currentFile;
    private bool _downloading;

    public McLauncherView() => InitializeComponent();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            AutomaticDecompression = System.Net.DecompressionMethods.None
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return client;
    }

    // ---------- 下载入口（点击直接下载，无弹窗） ----------
    private void BtnDownload_Click(object sender, RoutedEventArgs e)
        => _ = StartDownloadAsync();

    private async Task StartDownloadAsync()
    {
        if (_downloading) return;
        _downloading = true;
        _cts = new CancellationTokenSource();

        BtnDownload.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        DoneCard.Visibility = Visibility.Collapsed;
        ErrorCard.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Visible;
        TxtFileName.Text = "正在获取官方下载地址…";
        TxtPercent.Text = string.Empty;
        TxtSize.Text = string.Empty;
        TxtSpeed.Text = "速度：--";
        TxtEta.Text = string.Empty;
        Bar.IsIndeterminate = true;
        Bar.Value = 0;

        var ct = _cts.Token;

        try
        {
            // 1. 取落地页 HTML，解析真实下载链接（页面不做重定向）
            SetStatus("正在获取官方下载地址…");
            string page = await Http.GetStringAsync(DownloadPageUrl, ct);
            string fileUrl = ExtractPcLink(page)
                ?? throw new InvalidOperationException("无法从官方下载页解析出真实下载地址，请稍后重试");

            // 2. 下载真实安装包
            using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            string fileName = ResolveFileName(response);
            string destPath = Path.Combine(AppContext.BaseDirectory, fileName);
            _currentFile = destPath;

            long? total = response.Content.Headers.ContentLength;

            TxtFileName.Text = fileName;
            Bar.IsIndeterminate = total is null or 0;
            TxtSize.Text = total is > 0 ? $"0 B / {FormatBytes(total.Value)}" : "正在下载…";

            if (File.Exists(destPath))
                File.Delete(destPath);

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long done = 0;
            long windowBytes = 0;
            var window = Stopwatch.StartNew();
            var lastUi = DateTime.UtcNow - TimeSpan.FromSeconds(1);

            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                windowBytes += read;

                if ((DateTime.UtcNow - lastUi).TotalMilliseconds < 100) continue;

                double speed = window.Elapsed.TotalSeconds > 0.05
                    ? windowBytes / window.Elapsed.TotalSeconds
                    : windowBytes * 10;
                UpdateProgress(done, total, speed);
                windowBytes = 0;
                window.Restart();
                lastUi = DateTime.UtcNow;
            }

            UpdateProgress(done, total, 0);
            ShowComplete(destPath);
        }
        catch (OperationCanceledException)
        {
            CleanupPartialFile();
            ProgressCard.Visibility = Visibility.Collapsed;
            BtnDownload.IsEnabled = true;
        }
        catch (Exception ex)
        {
            CleanupPartialFile();
            ShowError(ex.Message);
        }
        finally
        {
            _downloading = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    // ---------- 解析真实下载地址 ----------
    private static string? ExtractPcLink(string html)
    {
        string? link = PcLinkRegex.Match(html) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

        if (string.IsNullOrWhiteSpace(link))
        {
            var any = AnyExeLinkRegex.Matches(html);
            // 取最后一个匹配：页面后部通常是 PC 链接
            if (any.Count > 0)
                link = any[^1].Value.Trim();
        }

        if (string.IsNullOrWhiteSpace(link) || !link.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !link.Contains(".exe?", StringComparison.OrdinalIgnoreCase))
            return null;

        return link;
    }

    private static string ResolveFileName(HttpResponseMessage response)
    {
        string? name = null;

        ContentDispositionHeaderValue? disp = response.Content.Headers.ContentDisposition;
        if (disp is not null)
        {
            string? raw = disp.FileNameStar ?? disp.FileName;
            if (!string.IsNullOrWhiteSpace(raw))
                name = Uri.UnescapeDataString(raw.Trim('"'));
        }

        if (string.IsNullOrWhiteSpace(name)
            && response.RequestMessage?.RequestUri is { } uri
            && uri.Segments.Length > 0)
        {
            string raw = Uri.UnescapeDataString(uri.Segments[^1].TrimEnd('/'));
            if (raw.Contains('.'))
                name = raw;
        }

        if (string.IsNullOrWhiteSpace(name))
            name = FallbackFileName;

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BtnCancel.IsEnabled = false;
    }

    private void CleanupPartialFile()
    {
        if (_currentFile is null) return;
        try { if (File.Exists(_currentFile)) File.Delete(_currentFile); }
        catch { /* 文件被占用时忽略，留待下次覆盖 */ }
        _currentFile = null;
    }

    // ---------- UI 状态 ----------
    private void SetStatus(string text)
    {
        TxtFileName.Text = text;
        Bar.IsIndeterminate = true;
    }

    private void UpdateProgress(long done, long? total, double bytesPerSec)
    {
        if (total is > 0)
        {
            double pct = Math.Min(done * 100.0 / total.Value, 100);
            Bar.IsIndeterminate = false;
            Bar.Value = pct;
            TxtPercent.Text = $"{pct:0.0}%";
            TxtSize.Text = $"{FormatBytes(done)} / {FormatBytes(total.Value)}";
            TxtEta.Text = bytesPerSec > 1 && total.Value > done
                ? $"剩余约 {FormatEta(TimeSpan.FromSeconds((total.Value - done) / bytesPerSec))}"
                : string.Empty;
        }
        else
        {
            Bar.IsIndeterminate = true;
            TxtPercent.Text = string.Empty;
            TxtSize.Text = $"已下载 {FormatBytes(done)}";
            TxtEta.Text = string.Empty;
        }

        TxtSpeed.Text = $"速度：{FormatBytes(bytesPerSec)}/s";
    }

    private void ShowComplete(string path)
    {
        ProgressCard.Visibility = Visibility.Collapsed;
        ErrorCard.Visibility = Visibility.Collapsed;
        TxtSavedPath.Text = path;
        DoneCard.Visibility = Visibility.Visible;
        BtnDownload.IsEnabled = true;
    }

    private void ShowError(string message)
    {
        ProgressCard.Visibility = Visibility.Collapsed;
        TxtError.Text = $"下载失败：{message}";
        ErrorCard.Visibility = Visibility.Visible;
        BtnDownload.IsEnabled = true;
    }

    private void BtnRunInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile is null || !File.Exists(_currentFile)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_currentFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法运行安装程序：{ex.Message}", "寒竿工具箱",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{_currentFile}\"" });
        }
        catch { /* 打开失败时忽略 */ }
    }

    // ---------- 工具 ----------
    private static string FormatBytes(double b)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return $"{b:0.##} {units[i]}";
    }

    private static string FormatEta(TimeSpan t)
        => t.TotalHours >= 1 ? $"{t.Hours} 小时 {t.Minutes} 分钟"
         : t.TotalMinutes >= 1 ? $"{t.Minutes} 分钟 {t.Seconds} 秒"
         : $"{t.Seconds} 秒";
}
