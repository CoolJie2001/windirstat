using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace WdsShell.App.Services;

/// <summary>
/// 直接按 Windows 11 的口径给窗口装系统背景材质（DWMWA_SYSTEMBACKDROP_TYPE）。
///
/// 为什么要自己设：Avalonia 11.3.5 的透明度只有两条路 —— Mica 走未公开的
/// DWMWA_MICA_EFFECT(1029)，AcrylicBlur 走 Win10 时代的 accent policy
/// （反查 Avalonia.Win32.dll 符号：SetTransparencyAcrylicBlur / AccentPolicy /
/// WCA_ACCENT_POLICY / ACCENT_ENABLE_ACRYLICBLURBEHIND）。两条都没碰
/// DWMWA_SYSTEMBACKDROP_TYPE(38)，实测本窗口在 Mica / Acrylic 两档下该属性恒读回 0(AUTO)。
/// 而 Win11 22621+ 的官方材质入口就是这个属性（DWMSBT_TRANSIENTWINDOW = Desktop Acrylic 最亮档），
/// 旧 accent 那条是 Win10 口径、带一层灰，观感比官方的淡。这里按官方口径补设一次。
/// </summary>
public static class SystemBackdrop
{
    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE；见 dwmapi.h。</summary>
    private const int SystemBackdropType = 38;

    // DWM_SYSTEMBACKDROP_TYPE：1=NONE，2=MAINWINDOW(Mica)，3=TRANSIENTWINDOW(Desktop Acrylic)。
    private const int TypeNone = 1;
    private const int TypeMica = 2;
    private const int TypeAcrylic = 3;

    /// <summary>文档标注该属性的最低客户端版本就是 Win11 build 22621，更早的系统交给 Avalonia 的旧入口。</summary>
    private const int SupportedBuild = 22621;

    /// <summary>当前系统能不能由我们直接接管材质。</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= SupportedBuild;

    /// <summary>
    /// 给任意窗口按当前设置装材质：先给 Avalonia 一条 hint 链（旧系统的唯一出路），
    /// 再在 Win11 上补设官方入口。主窗与设置窗口共用这一处，免得两个窗口口径不一致。
    /// 铺一层不透明底色会把材质整个盖掉，所以有材质时窗口背景必须是透明的；
    /// 选"无"时清掉本地值，把底色交回 FluentTheme 给 Window 的默认画刷。
    /// </summary>
    public static void ApplyTo(Window window, string mode)
    {
        window.TransparencyLevelHint = HintFor(mode);
        if (mode is "Mica" or "Acrylic") window.Background = Brushes.Transparent;
        else window.ClearValue(Window.BackgroundProperty);
        // 三档都要走：从亚克力切回"无"时，得把已经装上的材质摘掉（DWMSBT_NONE），
        // 只清 hint 不行的 —— DWM 记的是它自己那个属性。
        TryApply(window, mode);
    }

    /// <summary>把设置项翻成 Avalonia 的兜底 hint 链。</summary>
    private static WindowTransparencyLevel[] HintFor(string mode) => mode switch
    {
        "Mica" =>
        [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Transparent,
        ],
        "Acrylic" =>
        [
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Transparent,
        ],
        _ => [WindowTransparencyLevel.None],
    };

    /// <summary>把设置项的字面值翻成 DWM 的材质编号；未知值按"无材质"处理。</summary>
    private static int TypeOf(string mode) => mode switch
    {
        "Mica" => TypeMica,
        "Acrylic" => TypeAcrylic,
        _ => TypeNone,
    };

    /// <summary>
    /// 设材质。窗口还没拿到 HWND（Show 之前）或不支持的系统返回 false，
    /// 调用方保持原样即可，不算错误。
    /// </summary>
    public static bool TryApply(Window window, string mode)
    {
        if (!IsSupported) return false;
        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero) return false;

        var value = TypeOf(mode);
        return DwmSetWindowAttribute(handle.Handle, SystemBackdropType, ref value, sizeof(int)) == 0;
    }

    /// <summary>读回 DWM 现在给这个窗口装的是哪种材质（0=AUTO/未设，1=无，2=Mica，3=Acrylic）。</summary>
    public static int CurrentType(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero) return -1;
        return DwmGetWindowAttribute(handle.Handle, SystemBackdropType, out var value, sizeof(int)) == 0
            ? value
            : -2;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
