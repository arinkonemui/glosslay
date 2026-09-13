namespace Glosslay.Imaging;

/// <summary>
/// 前のフレームとの差分判定（FR-OCR-07）。
/// </summary>
/// <remarks>
/// <para>差分がなければ OCR も翻訳も行わないためのもの。
/// 自動監視（FR-MOD-02）で同じ画面を何度も処理しないようにし、
/// CPU と、クラウド翻訳を使っている場合の課金を抑える（RULES.md 🟡-8）。</para>
/// <para>全画素を見る必要はないため、格子状に間引いて比較する。
/// 画面の一部だけが変わる用途（セリフ送り等）でも、間引き幅より大きい変化は捉えられる。</para>
/// </remarks>
public static class FrameDifference
{
    /// <summary>間引き幅。この画素おきに比較する。</summary>
    private const int SampleStep = 4;

    /// <summary>変化とみなす既定の閾値。0〜1 で、大きいほど鈍感になる。</summary>
    public const double DefaultThreshold = 0.02;

    /// <summary>
    /// 2 枚の画像の違いを 0.0〜1.0 で返す。0.0 は完全に同じ。
    /// </summary>
    /// <remarks>サイズが異なる場合は比較できないため 1.0（全く違う）を返す。</remarks>
    public static double Compare(Bgra32Image previous, Bgra32Image current)
    {
        ArgumentNullException.ThrowIfNull(previous.Pixels);
        ArgumentNullException.ThrowIfNull(current.Pixels);

        if (previous.Width != current.Width || previous.Height != current.Height)
        {
            return 1.0;
        }

        long totalDifference = 0;
        long samples = 0;

        for (var y = 0; y < current.Height; y += SampleStep)
        {
            var previousRow = y * previous.Stride;
            var currentRow = y * current.Stride;

            for (var x = 0; x < current.Width; x += SampleStep)
            {
                var previousOffset = previousRow + (x * 4);
                var currentOffset = currentRow + (x * 4);

                var before = Luma(
                    previous.Pixels[previousOffset],
                    previous.Pixels[previousOffset + 1],
                    previous.Pixels[previousOffset + 2]);

                var after = Luma(
                    current.Pixels[currentOffset],
                    current.Pixels[currentOffset + 1],
                    current.Pixels[currentOffset + 2]);

                totalDifference += Math.Abs(before - after);
                samples++;
            }
        }

        return samples == 0 ? 0.0 : totalDifference / (double)samples / 255.0;
    }

    /// <summary>閾値を超える変化があったか。</summary>
    public static bool HasChanged(
        Bgra32Image previous, Bgra32Image current, double threshold = DefaultThreshold) =>
        Compare(previous, current) > threshold;

    private static int Luma(byte blue, byte green, byte red) =>
        ((red * 299) + (green * 587) + (blue * 114)) / 1000;
}
