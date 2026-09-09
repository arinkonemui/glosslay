using Glosslay.Imaging;

namespace Glosslay.Ocr;

/// <summary>
/// BGRA32 の画像を、PaddleOCR のモデルが期待する入力テンソルへ変換する。
/// </summary>
/// <remarks>
/// <para>変換規則はすべて PaddleOCR / PaddleX の実装を読んで確認したもの（2026-09-09）。
/// 推測で書くと精度が静かに落ちるため、変更するときは必ず本家の実装と突き合わせること。</para>
/// <para><b>チャンネル順は BGR。</b>モデル同梱の <c>inference.yml</c> の <c>img_mode: BGR</c> が
/// 既定の RGB を上書きする。入力が BGRA なので、そのまま B/G/R を 0/1/2 チャンネルに置けばよい。</para>
/// </remarks>
public static class OcrImagePreprocessor
{
    /// <summary>検出の入力は 32 の倍数である必要がある。</summary>
    private const int SizeAlignment = 32;

    /// <summary>検出の既定。短辺がこれ未満なら拡大する（<c>limit_type="min"</c>）。</summary>
    public const int DetectionLimitSideLength = 736;

    /// <summary>検出の入力の長辺の上限。</summary>
    public const int DetectionMaxSideLimit = 4000;

    /// <summary>認識の入力の高さ。モデル固定。</summary>
    public const int RecognitionHeight = 48;

    /// <summary>認識の入力の最小幅。<c>rec_image_shape</c> の 320 に由来する。</summary>
    public const int RecognitionMinWidth = 320;

    /// <summary>認識の入力の最大幅。</summary>
    public const int RecognitionMaxWidth = 3200;

    // 検出の正規化。BGR の順に適用する。
    private static readonly float[] DetectionMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] DetectionStd = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// 検出モデルへ渡す入力サイズを求める。
    /// </summary>
    /// <remarks>
    /// 短辺が <see cref="DetectionLimitSideLength"/> 未満なら、その値になるよう<b>拡大</b>する。
    /// そのうえで 32 の倍数へ丸める。小さい領域ほど拡大率が上がるため、
    /// 検出コストは元サイズに比例しない点に注意（PLAN.md P0-4 の申し送り）。
    /// </remarks>
    public static (int Width, int Height) ComputeDetectionSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var shortSide = Math.Min(width, height);
        var ratio = shortSide < DetectionLimitSideLength
            ? (double)DetectionLimitSideLength / shortSide
            : 1.0;

        var resizedWidth = (int)(width * ratio);
        var resizedHeight = (int)(height * ratio);

        var longSide = Math.Max(resizedWidth, resizedHeight);
        if (longSide > DetectionMaxSideLimit)
        {
            var shrink = (double)DetectionMaxSideLimit / longSide;
            resizedWidth = (int)(resizedWidth * shrink);
            resizedHeight = (int)(resizedHeight * shrink);
        }

        return (Align(resizedWidth), Align(resizedHeight));

        static int Align(int value) =>
            Math.Max((int)Math.Round(value / (double)SizeAlignment) * SizeAlignment, SizeAlignment);
    }

    /// <summary>
    /// 検出モデルの入力テンソル（CHW・正規化済み）を作る。
    /// </summary>
    public static float[] BuildDetectionTensor(Bgra32Image image, int targetWidth, int targetHeight)
    {
        var tensor = new float[3 * targetHeight * targetWidth];
        var plane = targetHeight * targetWidth;

        var scaleX = (double)image.Width / targetWidth;
        var scaleY = (double)image.Height / targetHeight;

        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = (y + 0.5) * scaleY - 0.5;
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = (x + 0.5) * scaleX - 0.5;
                SampleBilinear(image, sourceX, sourceY, out var b, out var g, out var r);

                var offset = (y * targetWidth) + x;
                tensor[offset] = ((b / 255f) - DetectionMean[0]) / DetectionStd[0];
                tensor[plane + offset] = ((g / 255f) - DetectionMean[1]) / DetectionStd[1];
                tensor[(2 * plane) + offset] = ((r / 255f) - DetectionMean[2]) / DetectionStd[2];
            }
        }

        return tensor;
    }

    /// <summary>
    /// 認識モデルへ渡す入力幅を求める。
    /// </summary>
    /// <remarks>
    /// 高さ 48 に合わせたときの幅。ただし <see cref="RecognitionMinWidth"/> を下回らない。
    /// 実際の画像はこの幅の左側に置き、右側はゼロで埋める。
    /// </remarks>
    public static int ComputeRecognitionWidth(int cropWidth, int cropHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropHeight);

        var aspect = (double)cropWidth / cropHeight;
        var width = (int)(RecognitionHeight * Math.Max(aspect, (double)RecognitionMinWidth / RecognitionHeight));
        return Math.Clamp(width, RecognitionMinWidth, RecognitionMaxWidth);
    }

    /// <summary>
    /// 認識モデルの入力テンソル（CHW・[-1,1] 正規化・右側ゼロ埋め）を作る。
    /// </summary>
    public static float[] BuildRecognitionTensor(Bgra32Image crop, int targetWidth)
    {
        var tensor = new float[3 * RecognitionHeight * targetWidth];
        var plane = RecognitionHeight * targetWidth;

        // アスペクト比を保った描画幅。はみ出す場合は入力幅で頭打ちにする。
        var aspect = (double)crop.Width / crop.Height;
        var drawWidth = Math.Min(targetWidth, Math.Max(1, (int)Math.Ceiling(RecognitionHeight * aspect)));

        var scaleX = (double)crop.Width / drawWidth;
        var scaleY = (double)crop.Height / RecognitionHeight;

        for (var y = 0; y < RecognitionHeight; y++)
        {
            var sourceY = (y + 0.5) * scaleY - 0.5;
            for (var x = 0; x < drawWidth; x++)
            {
                var sourceX = (x + 0.5) * scaleX - 0.5;
                SampleBilinear(crop, sourceX, sourceY, out var b, out var g, out var r);

                var offset = (y * targetWidth) + x;
                tensor[offset] = ((b / 255f) - 0.5f) / 0.5f;
                tensor[plane + offset] = ((g / 255f) - 0.5f) / 0.5f;
                tensor[(2 * plane) + offset] = ((r / 255f) - 0.5f) / 0.5f;
            }
        }

        // drawWidth より右はゼロのまま（配列の初期値）。本家の padding と同じ。
        return tensor;
    }

    /// <summary>双線形補間で 1 画素を取り出す。</summary>
    private static void SampleBilinear(
        Bgra32Image image, double x, double y, out float b, out float g, out float r)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = (float)(x - x0);
        var fy = (float)(y - y0);

        var x1 = Math.Clamp(x0 + 1, 0, image.Width - 1);
        var y1 = Math.Clamp(y0 + 1, 0, image.Height - 1);
        x0 = Math.Clamp(x0, 0, image.Width - 1);
        y0 = Math.Clamp(y0, 0, image.Height - 1);

        var pixels = image.Pixels;
        var i00 = (y0 * image.Stride) + (x0 * 4);
        var i10 = (y0 * image.Stride) + (x1 * 4);
        var i01 = (y1 * image.Stride) + (x0 * 4);
        var i11 = (y1 * image.Stride) + (x1 * 4);

        var w00 = (1 - fx) * (1 - fy);
        var w10 = fx * (1 - fy);
        var w01 = (1 - fx) * fy;
        var w11 = fx * fy;

        b = (pixels[i00] * w00) + (pixels[i10] * w10) + (pixels[i01] * w01) + (pixels[i11] * w11);
        g = (pixels[i00 + 1] * w00) + (pixels[i10 + 1] * w10) + (pixels[i01 + 1] * w01) + (pixels[i11 + 1] * w11);
        r = (pixels[i00 + 2] * w00) + (pixels[i10 + 2] * w10) + (pixels[i01 + 2] * w01) + (pixels[i11 + 2] * w11);
    }
}
