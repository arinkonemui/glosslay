using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Glosslay.Capture;

/// <summary>
/// キャプチャ対象の候補を列挙する。
/// </summary>
/// <remarks>
/// <para>ウィンドウはタイトルとハンドルだけを見る。ゲームプロセスを開いたりハンドルを取得したりはしない
/// （RULES.md 🔴-1）。</para>
/// <para>FR-CAP-04 の「プロセス名で次回以降自動再選択する」はプロセス名の取得方法を決めてから実装する。
/// v0.5 のタスクであり、PoC では対象外。</para>
/// </remarks>
public static class CaptureTargetEnumerator
{
    /// <summary>MONITORINFO.dwFlags のプライマリモニタ。</summary>
    private const uint MonitorPrimary = 1;

    /// <summary>
    /// キャプチャ候補になるトップレベルウィンドウを列挙する（FR-CAP-04 の対象選択用）。
    /// </summary>
    public static IReadOnlyList<CaptureTarget> EnumerateWindows()
    {
        var results = new List<CaptureTarget>();

        WNDENUMPROC callback = (hwnd, _) =>
        {
            if (TryDescribeWindow(hwnd, out var target))
            {
                results.Add(target);
            }

            return true;
        };

        PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);

        return results;
    }

    /// <summary>
    /// 接続されているモニタを列挙する（FR-CAP-02 のモニタ全体キャプチャ用）。
    /// </summary>
    public static unsafe IReadOnlyList<CaptureTarget> EnumerateMonitors()
    {
        var results = new List<CaptureTarget>();
        var index = 0;

        MONITORENUMPROC callback = (hmonitor, _, _, _) =>
        {
            index++;

            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (!PInvoke.GetMonitorInfo(hmonitor, ref info))
            {
                return true;
            }

            var bounds = info.rcMonitor;
            var width = bounds.right - bounds.left;
            var height = bounds.bottom - bounds.top;
            var isPrimary = (info.dwFlags & MonitorPrimary) != 0;

            var name = isPrimary
                ? $"ディスプレイ {index} ({width}x{height}・メイン)"
                : $"ディスプレイ {index} ({width}x{height})";

            results.Add(CaptureTarget.ForMonitor(hmonitor, name));
            return true;
        };

        PInvoke.EnumDisplayMonitors(default, (RECT?)null, callback, default);
        GC.KeepAlive(callback);

        return results;
    }

    /// <summary>キャプチャ候補として妥当なウィンドウかを判定し、妥当なら <see cref="CaptureTarget"/> を作る。</summary>
    private static bool TryDescribeWindow(HWND hwnd, out CaptureTarget target)
    {
        target = null!;

        if (!PInvoke.IsWindowVisible(hwnd))
        {
            return false;
        }

        // タイトルのないウィンドウはツールウィンドウ等。対象にしない。
        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return false;
        }

        Span<char> buffer = length < 256 ? stackalloc char[length + 1] : new char[length + 1];
        var written = PInvoke.GetWindowText(hwnd, buffer);
        if (written <= 0)
        {
            return false;
        }

        // 中身のないウィンドウ（最小化されたシェルの残骸など）を除外する。
        if (!PInvoke.GetClientRect(hwnd, out var client) || client.right <= 0 || client.bottom <= 0)
        {
            return false;
        }

        // DWM が隠しているウィンドウ（サスペンド中の UWP、別の仮想デスクトップ）は撮っても黒くなる。
        if (IsCloaked(hwnd))
        {
            return false;
        }

        target = CaptureTarget.ForWindow(hwnd, buffer[..written].ToString());
        return true;
    }

    private static unsafe bool IsCloaked(HWND hwnd)
    {
        uint cloaked = 0;
        var hr = PInvoke.DwmGetWindowAttribute(
            hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, (uint)sizeof(uint));

        return hr.Succeeded && cloaked != 0;
    }
}
