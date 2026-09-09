namespace Glosslay.Capture;

/// <summary>
/// CPU 上に読み出した 1 フレーム。ピクセル形式は BGRA32（1画素4バイト・プリマルチプライドアルファ）。
/// </summary>
/// <remarks>
/// <para>PoC では 1 フレームごとに配列を確保している。手動トリガー（FR-MOD-01）なら問題ないが、
/// 自動監視（FR-MOD-02 / v0.5）ではフルHDで 1 枚 8MB になり GC 負荷が無視できない。
/// v0.5 でバッファ再利用に切り替えること。</para>
/// </remarks>
/// <param name="Width">画素単位の幅。</param>
/// <param name="Height">画素単位の高さ。</param>
/// <param name="Stride">1 行あたりのバイト数（<c>Width * 4</c> に詰め直し済み）。</param>
/// <param name="Pixels">BGRA32 のピクセル列。長さは <c>Stride * Height</c>。</param>
/// <param name="SystemRelativeTime">WGC が付与したフレームのタイムスタンプ。差分判定やレイテンシ計測に使う。</param>
public sealed record CapturedFrame(
    int Width,
    int Height,
    int Stride,
    byte[] Pixels,
    TimeSpan SystemRelativeTime)
{
    /// <summary>前処理・OCR へ渡すための画像として見る。ピクセル列は複製しない。</summary>
    public Imaging.Bgra32Image AsImage() => new(Pixels, Width, Height, Stride);
}
