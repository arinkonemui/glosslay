using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;

namespace Glosslay.Capture;

/// <summary>画面上の矩形（物理ピクセル・仮想スクリーン座標）。</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// キャプチャ対象が画面上のどこにあるかを求める。
/// </summary>
/// <remarks>
/// オーバーレイを対象へ重ねて配置するために使う（FR-OVL-04 ①原文位置に重ねる）。
/// 座標は物理ピクセル。アプリは PerMonitorV2 を宣言しているため、
/// ここで得た値は DPI 仮想化されていない（FR-CAP-09）。
/// </remarks>
public static class CaptureTargetBounds
{
    /// <summary>対象の画面上の矩形を取得する。</summary>
    public static bool TryGet(CaptureTarget target, out ScreenRect bounds)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.Kind switch
        {
            CaptureTargetKind.Window => TryGetWindowBounds(target.Handle, out bounds),
            CaptureTargetKind.Monitor => TryGetMonitorBounds(target.Handle, out bounds),
            _ => Fail(out bounds),
        };

        static bool Fail(out ScreenRect bounds)
        {
            bounds = default;
            return false;
        }
    }

    private static unsafe bool TryGetWindowBounds(nint handle, out ScreenRect bounds)
    {
        var hwnd = new HWND(handle);

        // DWM の「見えている枠」を優先する。GetWindowRect は不可視のリサイズ余白を含み、
        // WGC が実際に撮る範囲とずれるため。
        RECT frame;
        var hr = PInvoke.DwmGetWindowAttribute(
            hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT));

        if (hr.Succeeded)
        {
            bounds = ToScreenRect(frame);
            return bounds is { Width: > 0, Height: > 0 };
        }

        if (PInvoke.GetWindowRect(hwnd, out var rect))
        {
            bounds = ToScreenRect(rect);
            return bounds is { Width: > 0, Height: > 0 };
        }

        bounds = default;
        return false;
    }

    private static unsafe bool TryGetMonitorBounds(nint handle, out ScreenRect bounds)
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (PInvoke.GetMonitorInfo(new HMONITOR(handle), ref info))
        {
            bounds = ToScreenRect(info.rcMonitor);
            return bounds is { Width: > 0, Height: > 0 };
        }

        bounds = default;
        return false;
    }

    private static ScreenRect ToScreenRect(RECT rect) =>
        new(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
}
