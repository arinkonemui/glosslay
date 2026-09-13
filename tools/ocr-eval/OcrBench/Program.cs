using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glosslay.Imaging;
using Glosslay.Ocr;

namespace Glosslay.Bench;

/// <summary>
/// 前処理の設定を変えながら OCR 精度を比較する（PLAN.md P0-3 / P0-7）。
/// </summary>
/// <remarks>
/// <para>製品と同じコード（<c>Glosslay.Core</c>）を通して測る。
/// Python 側の <c>ocr_eval.py</c> は PaddleOCR 本家の挙動を見るためのもので、役割が違う。</para>
/// <para>サンプルの置き方: <c>samples/</c> に <c>名前.png</c> と <c>名前.truth.txt</c> を並べる。
/// 正解テキストは 1 行 1 要素。画像はゲームの著作物のため .gitignore で除外している。</para>
/// </remarks>
internal static partial class Program
{
    private static readonly (string Name, PreprocessOptions Options)[] Configurations =
    [
        ("前処理なし", PreprocessOptions.None),
        ("拡大1.5倍", new PreprocessOptions { Scale = 1.5 }),
        ("拡大2倍", new PreprocessOptions { Scale = 2 }),
        ("拡大2倍 双線形", new PreprocessOptions { Scale = 2, Resampling = ResamplingMode.Bilinear }),
        ("拡大3倍", new PreprocessOptions { Scale = 3 }),
        ("自動ｺﾝﾄﾗｽﾄ", new PreprocessOptions { Scale = 1, AutoContrast = true }),
        ("拡大2倍+自動ｺﾝﾄﾗｽﾄ", new PreprocessOptions { Scale = 2, AutoContrast = true }),
        ("拡大2倍+ｺﾝﾄﾗｽﾄ1.6", new PreprocessOptions { Scale = 2, Contrast = 1.6 }),
        ("拡大2倍+背景除去", new PreprocessOptions { Scale = 2, RemoveBackground = true }),
        ("拡大2倍+二値化", new PreprocessOptions { Scale = 2, Binarize = true }),
        ("拡大2倍+反転", new PreprocessOptions { Scale = 2, Invert = true }),
    ];

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var sampleDirectory = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "samples");
        sampleDirectory = Path.GetFullPath(sampleDirectory);

        var modelDirectory = args.Length > 1
            ? args[1]
            : Path.GetFullPath(Path.Combine(sampleDirectory, "..", "models"));

        if (!Directory.Exists(sampleDirectory))
        {
            Console.WriteLine($"サンプルのディレクトリがありません: {sampleDirectory}");
            return 1;
        }

        var samples = Directory
            .EnumerateFiles(sampleDirectory)
            .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .Where(p => File.Exists(TruthPathFor(p)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (samples.Count == 0)
        {
            Console.WriteLine($"計測できる画像がありません: {sampleDirectory}");
            Console.WriteLine("画像（*.png）と、同じ名前の正解ファイル（*.truth.txt）を置いてください。");
            return 1;
        }

        using var engine = new PaddleOcrEngine(new PaddleOcrOptions
        {
            ModelDirectory = modelDirectory,
            CpuThreads = 4,
        });

        await engine.InitializeAsync().ConfigureAwait(false);
        Console.WriteLine($"エンジン  : {engine.Name}");
        Console.WriteLine($"サンプル  : {sampleDirectory}（{samples.Count} 件）");
        Console.WriteLine();

        var totals = new Dictionary<string, Totals>();

        foreach (var samplePath in samples)
        {
            await MeasureAsync(engine, samplePath, totals).ConfigureAwait(false);
        }

        PrintSummary(totals, samples.Count);
        return 0;
    }

    private static async Task MeasureAsync(
        PaddleOcrEngine engine, string samplePath, Dictionary<string, Totals> totals)
    {
        var name = Path.GetFileNameWithoutExtension(samplePath);
        var truthLines = (await File.ReadAllLinesAsync(TruthPathFor(samplePath), Encoding.UTF8)
                                    .ConfigureAwait(false))
                         .Where(l => l.Length > 0).ToList();

        var truth = Normalize(string.Join(" ", truthLines));
        var truthNumbers = ExtractNumbers(string.Join(" ", truthLines));

        var image = LoadBgra32(samplePath);
        Console.WriteLine($"=== {name}  {image.Width}x{image.Height}  "
                          + $"正解 {truthLines.Count} 行 / 数値 {truthNumbers.Count} 種 ===");
        Console.WriteLine($"  {"設定",-20} {"一致度",6}  {"数値",7}  {"行数",5}  {"時間",7}");

        foreach (var (configName, options) in Configurations)
        {
            var processed = ImagePreprocessor.Apply(image, options);
            var result = await engine.RecognizeAsync(processed).ConfigureAwait(false);

            var recognized = Normalize(
                string.Join(" ", ReadingOrder(result.Lines).Select(l => l.Text)));

            var similarity = Similarity(truth, recognized);
            var found = truthNumbers.Count(n => recognized.Contains(n, StringComparison.Ordinal));
            var ms = result.Elapsed.TotalMilliseconds;

            Console.WriteLine($"  {Mark(similarity)}{configName,-18} {similarity * 100,5:F1}%  "
                              + $"{found,2}/{truthNumbers.Count,-4}  {result.Lines.Count,4}  {ms,5:F0}ms");

            var previous = totals.GetValueOrDefault(configName);
            totals[configName] = previous with
            {
                Similarity = previous.Similarity + similarity,
                NumbersFound = previous.NumbersFound + found,
                NumbersTotal = previous.NumbersTotal + truthNumbers.Count,
                Milliseconds = previous.Milliseconds + ms,
            };
        }

        Console.WriteLine();
    }

    private static void PrintSummary(Dictionary<string, Totals> totals, int sampleCount)
    {
        Console.WriteLine($"=== 総合（{sampleCount} 枚の平均） ===");
        Console.WriteLine($"  {"設定",-20} {"一致度",6}  {"数値",9}  {"時間",7}");

        foreach (var (configName, sum) in totals.OrderByDescending(t => t.Value.Similarity))
        {
            var similarity = sum.Similarity / sampleCount;
            Console.WriteLine($"  {Mark(similarity)}{configName,-18} {similarity * 100,5:F1}%  "
                              + $"{sum.NumbersFound,3}/{sum.NumbersTotal,-4}  "
                              + $"{sum.Milliseconds / sampleCount,5:F0}ms");
        }
    }

    private static string TruthPathFor(string imagePath) =>
        Path.Combine(
            Path.GetDirectoryName(imagePath)!,
            Path.GetFileNameWithoutExtension(imagePath) + ".truth.txt");

    private static string Mark(double similarity) =>
        similarity >= 0.95 ? "  " : similarity >= 0.80 ? "△ " : "× ";

    /// <summary>
    /// 認識結果を読み順に並べ直す。
    /// </summary>
    /// <remarks>
    /// <b>Y 座標だけで並べてはいけない。</b>同じ行の要素が 1px のずれで入れ替わり、
    /// 文字列比較が壊れて誤った結論が出る（2026-09-13 に実際に踏んだ）。
    /// </remarks>
    private static List<OcrLine> ReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();

        foreach (var line in lines.OrderBy(l => l.Box.Y))
        {
            var center = line.Box.Y + (line.Box.Height / 2.0);
            var row = rows.FirstOrDefault(candidate =>
            {
                var head = candidate[0];
                var headCenter = head.Box.Y + (head.Box.Height / 2.0);
                return Math.Abs(headCenter - center)
                       < Math.Max(head.Box.Height, line.Box.Height) * 0.5;
            });

            if (row is null)
            {
                rows.Add([line]);
            }
            else
            {
                row.Add(line);
            }
        }

        return [.. rows.SelectMany(row => row.OrderBy(l => l.Box.X))];
    }

    private static Bgra32Image LoadBgra32(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return new Bgra32Image(pixels, source.PixelWidth, source.PixelHeight, stride);
    }

    /// <summary>比較のため空白を潰し、大文字に揃える。</summary>
    private static string Normalize(string text) =>
        WhitespaceRegex().Replace(text, " ").Trim().ToUpperInvariant();

    /// <summary>正解に含まれる数値。RULES.md が最重視するため個別に数える。</summary>
    private static List<string> ExtractNumbers(string text) =>
        [.. NumberRegex().Matches(text).Select(m => m.Value).Distinct()];

    /// <summary>編集距離から求めた 0〜1 の一致度。</summary>
    private static double Similarity(string expected, string actual)
    {
        if (expected.Length == 0)
        {
            return actual.Length == 0 ? 1.0 : 0.0;
        }

        var distance = Levenshtein(expected, actual);
        return Math.Max(0.0, 1.0 - ((double)distance / Math.Max(expected.Length, actual.Length)));
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[0-9][0-9,./+]*")]
    private static partial Regex NumberRegex();

    private readonly record struct Totals(
        double Similarity, int NumbersFound, int NumbersTotal, double Milliseconds);
}
