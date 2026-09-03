using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HanguanBox.Helpers;

/// <summary>
/// 通过 DWM SetWindowCompositionAttribute 为窗口启用亚克力/模糊背景，
/// 效果为对窗口“后面”的桌面内容做高斯模糊（Win10 1803+ 效果最佳）。
/// </summary>
public static class BlurBackground
{
    private const int AccentStateAcrylicBlurBehind = 4; // ACCENT_ENABLE_ACRYLICBLURBEHIND
    private const int AccentStateBlurBehind = 3;        // ACCENT_ENABLE_BLURBEHIND

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <param name="tint">着色，格式 0xAARRGGBB，会被转为 ABGR</param>
    public static void Enable(Window window, uint tint, bool useAcrylic = true)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        var abgr = (tint & 0xFF00FF00u) | ((tint & 0x00FF0000u) >> 16) | ((tint & 0x000000FFu) << 16);

        var accent = new AccentPolicy
        {
            AccentState = useAcrylic ? AccentStateAcrylicBlurBehind : AccentStateBlurBehind,
            AccentFlags = 2,
            GradientColor = abgr
        };

        IntPtr accentPtr = IntPtr.Zero;
        try
        {
            accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = Marshal.SizeOf(accent)
            };

            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            if (accentPtr != IntPtr.Zero) Marshal.FreeHGlobal(accentPtr);
        }
    }
}
