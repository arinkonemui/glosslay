namespace Glosslay.Imaging;

/// <summary>
/// BGRA32（1画素4バイト）の画像。
/// </summary>
/// <remarks>
/// キャプチャ結果（<see cref="Capture.CapturedFrame"/>）と前処理結果を、
/// OCR へ渡すときの共通の型として使う。
/// SPEC.md §2 の Capture → Preprocess → IOcrEngine の受け渡しがこれにあたる。
/// </remarks>
/// <param name="Pixels">BGRA32 のピクセル列。長さは <c>Stride * Height</c> 以上。</param>
/// <param name="Width">画素単位の幅。</param>
/// <param name="Height">画素単位の高さ。</param>
/// <param name="Stride">1 行あたりのバイト数。</param>
public readonly record struct Bgra32Image(byte[] Pixels, int Width, int Height, int Stride)
{
    /// <summary>指定位置の画素を返す。</summary>
    /// <remarks>戻り値は BGRA の順。呼び出し側で範囲を保証すること。</remarks>
    public ReadOnlySpan<byte> GetPixel(int x, int y) =>
        Pixels.AsSpan((y * Stride) + (x * 4), 4);

    /// <summary>矩形で切り出した新しい画像を返す。</summary>
    /// <remarks>FR-CAP-05 の「翻訳対象領域」を OCR に渡すときに使う。</remarks>
    public Bgra32Image Crop(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(x + width, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(y + height, Height);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            Pixels.AsSpan(((y + row) * Stride) + (x * 4), stride)
                  .CopyTo(pixels.AsSpan(row * stride));
        }

        return new Bgra32Image(pixels, width, height, stride);
    }
}
