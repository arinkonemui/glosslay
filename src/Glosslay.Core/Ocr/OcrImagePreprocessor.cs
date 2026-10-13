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
    /// <param name="width">元の幅。</param>
    /// <param name="height">元の高さ。</param>
    /// <param name="maxSideLength">
    /// 長辺の上限。<c>0</c> なら縮めない（<c>limit_type="min"</c> のみ）。
    /// </param>
    /// <remarks>
    /// <para>短辺が <see cref="DetectionLimitSideLength"/> 未満なら、その値になるよう<b>拡大</b>する。
    /// そのうえで 32 の倍数へ丸める。小さい領域ほど拡大率が上がるため、
    /// 検出コストは元サイズに比例しない点に注意（PLAN.md P0-4 の申し送り）。</para>
    /// <para><b>縮める側の上限は既定では効かない。</b>
    /// 全画面 1920x1080 はそのまま 1920x1088（2.09M 画素）で検出に流れており、
    /// これが固定費 695ms の正体（2026-10-13 の実測）。
    /// <paramref name="maxSideLength"/> に 960 を渡せば 960x544（0.52M 画素）になる。
    /// <b>小さい文字を取りこぼすため、必ず実測してから既定を変えること。</b></para>
    /// </remarks>
    public static (int Width, int Height) ComputeDetectionSize(
        int width, int height, int maxSideLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSideLength);

        var shortSide = Math.Min(width, height);
        var ratio = shortSide < DetectionLimitSideLength
            ? (double)DetectionLimitSideLength / shortSide
            : 1.0;

        // 長辺が上限を超えるなら縮める。拡大の判断より後に見る
        // （小さい画像を拡大してから上限で切る、という順序）。
        var longSideNow = Math.Max(width, height) * ratio;
        if (maxSideLength > 0 && longSideNow > maxSideLength)
        {
            ratio *= maxSideLength / longSideNow;
        }

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
    /// <param name="image">元画像。</param>
    /// <param name="targetWidth">入力幅（<see cref="ComputeDetectionSize"/> の結果）。</param>
    /// <param name="targetHeight">入力の高さ。</param>
    /// <param name="maxDegreeOfParallelism">
    /// 行を並べて処理する数。既定は 1（並べない）。
    /// <b>呼ぶ側の CPU の上限（FR-OCR-11）を渡すこと。</b>上限を持たずに並べると、ゲームと CPU を奪い合う。
    /// </param>
    /// <remarks>
    /// <para><b>列ごとの補間係数を先に 1 回だけ作る。</b>係数は全行で同じなのに、
    /// 以前は画素ごとに計算していた。1920x1080 → 1280x704 で 18.3ms → 4.5ms（4 並列）。
    /// <b>出力は以前とビット単位で同じ</b>（同じ式を同じ順で評価している。2026-10-13 に確認）。</para>
    /// </remarks>
    public static float[] BuildDetectionTensor(
        Bgra32Image image, int targetWidth, int targetHeight, int maxDegreeOfParallelism = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegreeOfParallelism);

        var tensor = new float[3 * targetHeight * targetWidth];
        var plane = targetHeight * targetWidth;

        var scaleX = (double)image.Width / targetWidth;
        var scaleY = (double)image.Height / targetHeight;

        var columns = new BilinearTap[targetWidth];
        for (var x = 0; x < targetWidth; x++)
        {
            columns[x] = BilinearTap.At((x + 0.5) * scaleX - 0.5, image.Width);
        }

        Parallel.For(
            0,
            targetHeight,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
            y =>
            {
                var row = BilinearTap.At((y + 0.5) * scaleY - 0.5, image.Height);
                var top = row.Near * image.Stride;
                var bottom = row.Far * image.Stride;
                var pixels = image.Pixels;

                for (var x = 0; x < targetWidth; x++)
                {
                    var column = columns[x];
                    var i00 = top + (column.Near * 4);
                    var i10 = top + (column.Far * 4);
                    var i01 = bottom + (column.Near * 4);
                    var i11 = bottom + (column.Far * 4);

                    var w00 = (1 - column.Fraction) * (1 - row.Fraction);
                    var w10 = column.Fraction * (1 - row.Fraction);
                    var w01 = (1 - column.Fraction) * row.Fraction;
                    var w11 = column.Fraction * row.Fraction;

                    var b = (pixels[i00] * w00) + (pixels[i10] * w10) + (pixels[i01] * w01) + (pixels[i11] * w11);
                    var g = (pixels[i00 + 1] * w00) + (pixels[i10 + 1] * w10) + (pixels[i01 + 1] * w01) + (pixels[i11 + 1] * w11);
                    var r = (pixels[i00 + 2] * w00) + (pixels[i10 + 2] * w10) + (pixels[i01 + 2] * w01) + (pixels[i11 + 2] * w11);

                    var offset = (y * targetWidth) + x;
                    tensor[offset] = ((b / 255f) - DetectionMean[0]) / DetectionStd[0];
                    tensor[plane + offset] = ((g / 255f) - DetectionMean[1]) / DetectionStd[1];
                    tensor[(2 * plane) + offset] = ((r / 255f) - DetectionMean[2]) / DetectionStd[2];
                }
            });

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

    /// <summary>
    /// 双線形補間の 1 軸ぶんの係数（近い側・遠い側の画素位置と、遠い側の重み）。
    /// </summary>
    /// <remarks><see cref="SampleBilinear"/> と同じ式。食い違うと検出の入力が静かに変わる。</remarks>
    private readonly record struct BilinearTap(int Near, int Far, float Fraction)
    {
        public static BilinearTap At(double position, int length)
        {
            var floor = (int)Math.Floor(position);
            var fraction = (float)(position - floor);
            return new BilinearTap(
                Math.Clamp(floor, 0, length - 1),
                Math.Clamp(floor + 1, 0, length - 1),
                fraction);
        }
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
