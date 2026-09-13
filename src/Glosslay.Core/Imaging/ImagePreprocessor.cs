namespace Glosslay.Imaging;

/// <summary>
/// OCR にかける前の画像処理（FR-OCR-04 / FR-OCR-05）。
/// </summary>
/// <remarks>
/// <para>適用順は 拡大 → グレースケール → 自動コントラスト → コントラスト → 二値化 → 反転。
/// 拡大を先に行うのは、二値化の判定に使える画素を増やしてから境界を決めるため。
/// 先に二値化すると輪郭が階段状になり、拡大しても情報は戻らない。</para>
/// <para><b>切り出した領域に適用する前提。</b>全画面に 2 倍拡大をかけると
/// 画素数が 4 倍になり、検出時間もそれに比例して伸びる。</para>
/// </remarks>
public static class ImagePreprocessor
{
    /// <summary>Sauvola の感度。0.2 前後が一般的。</summary>
    private const double SauvolaK = 0.2;

    /// <summary>Sauvola の標準偏差の基準値。8bit 画像なら 128。</summary>
    private const double SauvolaRange = 128.0;

    /// <summary>自動コントラストで切り捨てる上下の割合。外れ値に引きずられないようにする。</summary>
    private const double ClipRatio = 0.005;

    /// <summary>設定に従って画像を加工した新しい画像を返す。元の画像は変更しない。</summary>
    public static Bgra32Image Apply(Bgra32Image source, PreprocessOptions options)
    {
        ArgumentNullException.ThrowIfNull(source.Pixels);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.HasAnyEffect)
        {
            return source;
        }

        var image = options.Scale is 1.0 ? Copy(source) : Resize(source, options);

        // 二値化・背景除去はグレースケールを前提にする。
        if (options.Grayscale || options.Binarize || options.RemoveBackground)
        {
            ToGrayscale(image);
        }

        if (options.AutoContrast)
        {
            StretchContrast(image);
        }

        if (options.Contrast is not 1.0)
        {
            AdjustContrast(image, options.Contrast);
        }

        if (options.RemoveBackground)
        {
            // 局所的に判定するため、背景の明るさが場所で変わっても文字が残る。
            BinarizeAdaptive(image, options.BackgroundWindow);
        }
        else if (options.Binarize)
        {
            BinarizeOtsu(image);
        }

        if (options.Invert)
        {
            Invert(image);
        }

        return image;
    }

    private static Bgra32Image Copy(Bgra32Image source)
    {
        var pixels = new byte[source.Stride * source.Height];
        Array.Copy(source.Pixels, pixels, pixels.Length);
        return source with { Pixels = pixels };
    }

    // ===== 拡大・縮小 =====

    private static Bgra32Image Resize(Bgra32Image source, PreprocessOptions options)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * options.Scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * options.Scale));
        var stride = width * 4;
        var pixels = new byte[stride * height];

        var scaleX = (double)source.Width / width;
        var scaleY = (double)source.Height / height;
        var bicubic = options.Resampling == ResamplingMode.Bicubic;

        for (var y = 0; y < height; y++)
        {
            var sourceY = ((y + 0.5) * scaleY) - 0.5;
            var row = y * stride;

            for (var x = 0; x < width; x++)
            {
                var sourceX = ((x + 0.5) * scaleX) - 0.5;
                var offset = row + (x * 4);

                for (var channel = 0; channel < 3; channel++)
                {
                    var value = bicubic
                        ? SampleBicubic(source, sourceX, sourceY, channel)
                        : SampleBilinear(source, sourceX, sourceY, channel);
                    pixels[offset + channel] = ClampToByte(value);
                }

                pixels[offset + 3] = 255;
            }
        }

        return new Bgra32Image(pixels, width, height, stride);
    }

    private static double SampleBilinear(Bgra32Image image, double x, double y, int channel)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;

        var p00 = GetChannel(image, x0, y0, channel);
        var p10 = GetChannel(image, x0 + 1, y0, channel);
        var p01 = GetChannel(image, x0, y0 + 1, channel);
        var p11 = GetChannel(image, x0 + 1, y0 + 1, channel);

        var top = p00 + ((p10 - p00) * fx);
        var bottom = p01 + ((p11 - p01) * fx);
        return top + ((bottom - top) * fy);
    }

    private static double SampleBicubic(Bgra32Image image, double x, double y, int channel)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;

        Span<double> columns = stackalloc double[4];
        for (var row = 0; row < 4; row++)
        {
            columns[row] = CatmullRom(
                GetChannel(image, x0 - 1, y0 - 1 + row, channel),
                GetChannel(image, x0, y0 - 1 + row, channel),
                GetChannel(image, x0 + 1, y0 - 1 + row, channel),
                GetChannel(image, x0 + 2, y0 - 1 + row, channel),
                fx);
        }

        return CatmullRom(columns[0], columns[1], columns[2], columns[3], fy);
    }

    /// <summary>Catmull-Rom スプライン。制御点を通るため、文字の輪郭が保たれやすい。</summary>
    private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;

        return (((-0.5 * t3) + t2 - (0.5 * t)) * p0)
             + (((1.5 * t3) - (2.5 * t2) + 1.0) * p1)
             + (((-1.5 * t3) + (2.0 * t2) + (0.5 * t)) * p2)
             + (((0.5 * t3) - (0.5 * t2)) * p3);
    }

    private static double GetChannel(Bgra32Image image, int x, int y, int channel)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        return image.Pixels[(y * image.Stride) + (x * 4) + channel];
    }

    // ===== 色調 =====

    private static void ToGrayscale(Bgra32Image image)
    {
        var pixels = image.Pixels;
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Stride;
            for (var x = 0; x < image.Width; x++)
            {
                var offset = row + (x * 4);
                var luma = Luma(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
                pixels[offset] = luma;
                pixels[offset + 1] = luma;
                pixels[offset + 2] = luma;
            }
        }
    }

    /// <summary>明暗の分布を 0〜255 いっぱいに引き伸ばす。色相を保つため全チャンネルに同じ変換をかける。</summary>
    private static void StretchContrast(Bgra32Image image)
    {
        var histogram = BuildLumaHistogram(image);
        var total = (long)image.Width * image.Height;
        var clip = (long)(total * ClipRatio);

        var low = 0;
        var high = 255;

        long accumulated = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            accumulated += histogram[i];
            if (accumulated > clip)
            {
                low = i;
                break;
            }
        }

        accumulated = 0;
        for (var i = histogram.Length - 1; i >= 0; i--)
        {
            accumulated += histogram[i];
            if (accumulated > clip)
            {
                high = i;
                break;
            }
        }

        if (high <= low)
        {
            return;
        }

        var scale = 255.0 / (high - low);
        var map = new byte[256];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = ClampToByte((i - low) * scale);
        }

        ApplyMap(image, map);
    }

    private static void AdjustContrast(Bgra32Image image, double factor)
    {
        var map = new byte[256];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = ClampToByte(((i - 128) * factor) + 128);
        }

        ApplyMap(image, map);
    }

    private static void Invert(Bgra32Image image)
    {
        var map = new byte[256];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = (byte)(255 - i);
        }

        ApplyMap(image, map);
    }

    private static void ApplyMap(Bgra32Image image, byte[] map)
    {
        var pixels = image.Pixels;
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Stride;
            for (var x = 0; x < image.Width; x++)
            {
                var offset = row + (x * 4);
                pixels[offset] = map[pixels[offset]];
                pixels[offset + 1] = map[pixels[offset + 1]];
                pixels[offset + 2] = map[pixels[offset + 2]];
            }
        }
    }

    // ===== 二値化 =====

    /// <summary>大津の方法。画像全体で 1 つの閾値を決める。背景が一様な場面に向く。</summary>
    private static void BinarizeOtsu(Bgra32Image image)
    {
        var histogram = BuildLumaHistogram(image);
        var total = (long)image.Width * image.Height;

        long sum = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            sum += (long)i * histogram[i];
        }

        long backgroundWeight = 0;
        long backgroundSum = 0;
        var bestVariance = -1.0;
        var threshold = 0;

        for (var i = 0; i < histogram.Length; i++)
        {
            backgroundWeight += histogram[i];
            if (backgroundWeight == 0)
            {
                continue;
            }

            var foregroundWeight = total - backgroundWeight;
            if (foregroundWeight == 0)
            {
                break;
            }

            backgroundSum += (long)i * histogram[i];

            var backgroundMean = (double)backgroundSum / backgroundWeight;
            var foregroundMean = (double)(sum - backgroundSum) / foregroundWeight;
            var difference = backgroundMean - foregroundMean;
            var variance = (double)backgroundWeight * foregroundWeight * difference * difference;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                threshold = i;
            }
        }

        var map = new byte[256];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = i > threshold ? (byte)255 : (byte)0;
        }

        ApplyMap(image, map);
    }

    /// <summary>
    /// Sauvola の適応的二値化。窓ごとに閾値を決めるため、背景の明るさが場所で変わっても文字が残る。
    /// </summary>
    /// <remarks>
    /// カードのイラスト背景対策（PLAN.md P0-3「背景除去」）。
    /// 積分画像を使うので窓の大きさによらず計算量は一定。
    /// </remarks>
    private static void BinarizeAdaptive(Bgra32Image image, int window)
    {
        var radius = Math.Max(1, window / 2);
        var width = image.Width;
        var height = image.Height;

        // 積分画像。二乗和は int を超えるため long で持つ。
        var sums = new long[(width + 1) * (height + 1)];
        var squares = new long[(width + 1) * (height + 1)];

        var pixels = image.Pixels;
        for (var y = 0; y < height; y++)
        {
            long rowSum = 0;
            long rowSquare = 0;
            var sourceRow = y * image.Stride;
            var current = (y + 1) * (width + 1);
            var previous = y * (width + 1);

            for (var x = 0; x < width; x++)
            {
                var value = pixels[sourceRow + (x * 4)];
                rowSum += value;
                rowSquare += (long)value * value;
                sums[current + x + 1] = sums[previous + x + 1] + rowSum;
                squares[current + x + 1] = squares[previous + x + 1] + rowSquare;
            }
        }

        var result = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            var top = Math.Max(0, y - radius);
            var bottom = Math.Min(height - 1, y + radius);
            var sourceRow = y * image.Stride;

            for (var x = 0; x < width; x++)
            {
                var left = Math.Max(0, x - radius);
                var right = Math.Min(width - 1, x + radius);
                var count = (long)(right - left + 1) * (bottom - top + 1);

                var areaSum = AreaSum(sums, width, left, top, right, bottom);
                var areaSquare = AreaSum(squares, width, left, top, right, bottom);

                var mean = (double)areaSum / count;
                var variance = ((double)areaSquare / count) - (mean * mean);
                var deviation = variance > 0 ? Math.Sqrt(variance) : 0.0;

                var threshold = mean * (1.0 + (SauvolaK * ((deviation / SauvolaRange) - 1.0)));

                var offset = sourceRow + (x * 4);
                var value = pixels[offset] > threshold ? (byte)255 : (byte)0;
                result[offset] = value;
                result[offset + 1] = value;
                result[offset + 2] = value;
                result[offset + 3] = 255;
            }
        }

        Array.Copy(result, pixels, pixels.Length);
    }

    private static long AreaSum(long[] integral, int width, int left, int top, int right, int bottom)
    {
        var stride = width + 1;
        var bottomRight = ((bottom + 1) * stride) + right + 1;
        var bottomLeft = ((bottom + 1) * stride) + left;
        var topRight = (top * stride) + right + 1;
        var topLeft = (top * stride) + left;

        return integral[bottomRight] - integral[bottomLeft] - integral[topRight] + integral[topLeft];
    }

    // ===== 共通 =====

    private static long[] BuildLumaHistogram(Bgra32Image image)
    {
        var histogram = new long[256];
        var pixels = image.Pixels;

        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Stride;
            for (var x = 0; x < image.Width; x++)
            {
                var offset = row + (x * 4);
                histogram[Luma(pixels[offset], pixels[offset + 1], pixels[offset + 2])]++;
            }
        }

        return histogram;
    }

    private static byte Luma(byte blue, byte green, byte red) =>
        (byte)(((red * 299) + (green * 587) + (blue * 114)) / 1000);

    private static byte ClampToByte(double value) =>
        value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)Math.Round(value);
}
