using Windows.Win32;

namespace Glosslay.Capture;

/// <summary>画面上の 1 点（物理ピクセル・仮想スクリーン座標）。</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// マウスカーソルの位置を読む（FR-MOD-09）。
/// </summary>
/// <remarks>
/// <para><b>位置を問い合わせるだけで、ゲームプロセスには一切触れない</b>（RULES.md 🔴-1）。
/// 入力も奪わない（🔴-3）。<b>カーソルの動きを監視もしない</b> — ホットキーが押された瞬間の 1 点だけを読む。</para>
/// <para>座標は物理ピクセル。アプリが PerMonitorV2 を宣言しているため DPI 仮想化されておらず、
/// <see cref="CaptureTargetBounds"/> が返す矩形と同じ座標系で比べられる（FR-CAP-09）。</para>
/// </remarks>
public static class CursorPosition
{
    /// <summary>今のカーソル位置を取得する。</summary>
    /// <remarks>
    /// 失敗するのは、別デスクトップのように呼び出し元から見えない状態のとき。
    /// 例外にせず false を返し、呼び出し側が「翻訳しない」を選べるようにする。
    /// </remarks>
    public static bool TryGet(out ScreenPoint point)
    {
        if (PInvoke.GetCursorPos(out var native))
        {
            point = new ScreenPoint(native.X, native.Y);
            return true;
        }

        point = default;
        return false;
    }
}
