using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using static Glosslay.Localization.UiText;

namespace Glosslay.Capture;

/// <summary>
/// キャプチャ対象の候補を列挙する。
/// </summary>
/// <remarks>
/// <para>ウィンドウはタイトルとハンドルだけを見る。ゲームプロセスを開いたりハンドルを取得したりはしない
/// （RULES.md 🔴-1）。</para>
/// <para><b>FR-CAP-04 の「次回以降の自動再選択」はプロセス名を使わない</b>（2026-10-12 変更）。
/// プロセス名を得るにはプロセスハンドルを開く必要があり、RULES.md 🔴-1 に触れる。
/// 見分けるのは<b>ウィンドウのクラス名とタイトル</b>で、FR-CFG-02 と同じ仕組みを使う
/// （<see cref="WindowIdentity"/> と <c>Profile.Matches</c>）。
/// <b>判定をここに持たせない。</b>同じことを 2 か所に書くと、片方だけ直して食い違う。</para>
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
                ? T("ディスプレイ {0} ({1}x{2}・メイン)", index, width, height)
                : T("ディスプレイ {0} ({1}x{2})", index, width, height);

            results.Add(CaptureTarget.ForMonitor(hmonitor, name));
            return true;
        };

        PInvoke.EnumDisplayMonitors(default, (RECT?)null, callback, default);
        GC.KeepAlive(callback);

        return results;
    }

    /// <summary>
    /// 既知のウィンドウハンドルから対象を作る。
    /// </summary>
    /// <remarks>
    /// ホットキーで「いま見ている画面」を撮るために使う（前面ウィンドウは <see cref="WindowFocus.Current"/>）。
    /// 一覧に出すときと同じ基準で判定するため、撮っても黒くなるだけのウィンドウは弾かれる。
    /// </remarks>
    public static bool TryDescribe(nint windowHandle, out CaptureTarget target)
    {
        target = null!;
        return windowHandle != 0 && TryDescribeWindow((HWND)windowHandle, out target);
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
