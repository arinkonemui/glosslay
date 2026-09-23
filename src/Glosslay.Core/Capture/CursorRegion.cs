namespace Glosslay.Capture;

/// <summary>キャプチャ画像の中の矩形（画素単位）。<b>画面座標ではない。</b></summary>
/// <remarks>
/// <see cref="ScreenRect"/> と紛らわしいため型を分けている。
/// 撮れた画像のサイズとウィンドウの表示サイズは一致しないことがあり、混ぜると位置がずれる。
/// </remarks>
public readonly record struct FrameRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// カーソルの周囲だけを切り出す矩形を求める（FR-MOD-10）。
/// </summary>
/// <remarks>
/// <para>全画面を訳すと OCR も翻訳も要件の 1.5 秒に収まらない（PLAN.md 課題4）。
/// 読みたいのはカーソルが指している説明文だけなので、その周囲に絞る。</para>
/// <para>窓の大きさ 640x480 は<b>暫定値</b>。1 枚のカード画像から導いたもので、
/// 実ゲームでの見直しが要る（PLAN.md 課題3）。</para>
/// </remarks>
public static class CursorRegion
{
    /// <summary>切り出す窓の幅の既定値（暫定・課題3）。</summary>
    public const int DefaultWidth = 640;

    /// <summary>切り出す窓の高さの既定値（暫定・課題3）。</summary>
    public const int DefaultHeight = 480;

    /// <summary>既定の大きさでカーソル周辺の矩形を求める。</summary>
    public static bool TryResolve(
        ScreenPoint cursor, ScreenRect bounds, int frameWidth, int frameHeight, out FrameRect region) =>
        TryResolve(cursor, bounds, frameWidth, frameHeight, DefaultWidth, DefaultHeight, out region);

    /// <summary>
    /// カーソル周辺の矩形を、キャプチャ画像の座標で求める。
    /// </summary>
    /// <param name="cursor">カーソルの画面座標。</param>
    /// <param name="bounds">キャプチャ対象の画面上の矩形。</param>
    /// <param name="frameWidth">撮れた画像の幅。</param>
    /// <param name="frameHeight">撮れた画像の高さ。</param>
    /// <param name="windowWidth">切り出す幅。画像より大きければ画像に合わせる。</param>
    /// <param name="windowHeight">切り出す高さ。同上。</param>
    /// <param name="region">求めた矩形。</param>
    /// <returns>カーソルが対象の外にある場合や、寸法が不正な場合は <c>false</c>。</returns>
    public static bool TryResolve(
        ScreenPoint cursor, ScreenRect bounds, int frameWidth, int frameHeight,
        int windowWidth, int windowHeight, out FrameRect region)
    {
        region = default;

        if (frameWidth <= 0 || frameHeight <= 0
            || bounds.Width <= 0 || bounds.Height <= 0
            || windowWidth <= 0 || windowHeight <= 0)
        {
            return false;
        }

        // 対象の外を指しているなら訳す先がない。近い端に寄せると、見ていない場所を訳すことになる。
        if (cursor.X < bounds.X || cursor.X >= bounds.Right
            || cursor.Y < bounds.Y || cursor.Y >= bounds.Bottom)
        {
            return false;
        }

        // 画面座標 → 画像座標。ウィンドウの表示サイズと撮れた画像のサイズは一致しないことがある。
        var frameX = (int)((double)(cursor.X - bounds.X) * frameWidth / bounds.Width);
        var frameY = (int)((double)(cursor.Y - bounds.Y) * frameHeight / bounds.Height);

        var width = Math.Min(windowWidth, frameWidth);
        var height = Math.Min(windowHeight, frameHeight);

        // カーソルを中心に置く。端では画像からはみ出さないよう内側へ寄せる
        // （中心からずれるが、窓を縮めて読める文字を減らすより良い）。
        var x = Math.Clamp(frameX - (width / 2), 0, frameWidth - width);
        var y = Math.Clamp(frameY - (height / 2), 0, frameHeight - height);

        region = new FrameRect(x, y, width, height);
        return true;
    }
}
