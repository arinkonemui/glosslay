using Glosslay.Imaging;
using Glosslay.Translation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using WinOcr = Windows.Media.Ocr;

namespace Glosslay.Ocr;

/// <summary>
/// Windows 同梱の OCR（FR-OCR-03 の「高速モード」）。
/// </summary>
/// <remarks>
/// <para><b>既定ではない。</b>既定は PaddleOCR で、こちらは
/// <b>速度を優先したい場面のための切り替え先</b>（2026-10-08 の人間の判断「速度 &gt; 精度」）。
/// PaddleOCR が初期化できない環境での逃げ道にもなる（FR-OCR-09）。</para>
///
/// <para><b>言語パックが要る。</b>認識できるのは Windows に入っている言語だけで、
/// 入っていなければ作ることすらできない。<see cref="AvailableLanguages"/> で確かめ、
/// 無ければ設定アプリへ案内する（FR-OCR-10 / <see cref="LanguageSettingsUri"/>）。</para>
///
/// <para><b>確信度を返さない。</b>Windows の OCR には行・文字どちらの確信度も無い
/// （<see cref="ProvidesConfidence"/>）。<see cref="OcrLine.Confidence"/> には 1.0 を入れるが、
/// <b>それは「自信がある」という意味ではなく「分からない」という意味</b>。
/// 画面に出すときは <see cref="ProvidesConfidence"/> を見て断ること。</para>
/// </remarks>
public sealed class WindowsOcrEngine : IOcrEngine
{
    /// <summary>言語パックを追加する設定画面（FR-OCR-10）。</summary>
    public const string LanguageSettingsUri = "ms-settings:regionlanguage";

    private readonly string? _language;
    private WinOcr.OcrEngine? _engine;

    /// <param name="language">
    /// 認識する言語（BCP 47）。<c>null</c> なら利用者の言語設定に任せる。
    /// </param>
    public WindowsOcrEngine(string? language = null) => _language = language;

    /// <summary>
    /// この環境で認識できる言語（BCP 47）。
    /// </summary>
    /// <remarks>
    /// <b>Windows に入っている言語パックで決まる。</b>日本語版 Windows なら日本語と英語は
    /// たいてい入っているが、中国語は入っていないことが多い（FR-OCR-02 は英・中・日が対象）。
    /// </remarks>
    public static IReadOnlyList<string> AvailableLanguages { get; } =
        [.. WinOcr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag)];

    /// <summary>指定した言語を認識できるか。</summary>
    public static bool Supports(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return AvailableLanguages.Any(
            tag => tag.StartsWith(language, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public string Name => _engine is { } engine
        ? $"Windows OCR ({engine.RecognizerLanguage.DisplayName})"
        : "Windows OCR";

    /// <inheritdoc />
    public bool IsReady => _engine is not null;

    /// <summary>
    /// 確信度を返せるか。<b>このエンジンは返せない。</b>
    /// </summary>
    /// <remarks>
    /// Windows の OCR は行にも文字にも確信度を持たない。
    /// <b>FR-OCR-06「信頼度が閾値未満の行を破棄」はこのエンジンでは効かない。</b>
    /// 高速モードを選ぶとは、その切り捨ても受け入れるということ。
    /// </remarks>
    public bool ProvidesConfidence => false;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">言語パックが入っていない（FR-OCR-10）。</exception>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_engine is not null)
        {
            return Task.CompletedTask;
        }

        // 利用者の言語設定に任せる場合でも、入っていなければ null が返る。
        _engine = _language is { Length: > 0 }
            ? WinOcr.OcrEngine.TryCreateFromLanguage(new Language(_language))
            : WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();

        if (_engine is null)
        {
            // FR-OCR-10: 何が足りないかと、どこで足せるかをその場で伝える。
            var available = AvailableLanguages.Count > 0
                ? string.Join(" / ", AvailableLanguages)
                : "（ひとつも入っていません）";

            throw new NotSupportedException(
                $"Windows OCR で {_language ?? "利用者の言語"} を認識できません。"
                + $" 入っている言語: {available}。"
                + $" 設定（{LanguageSettingsUri}）から言語を追加してください。");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<OcrResult> RecognizeAsync(
        Bgra32Image image, CancellationToken cancellationToken = default)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            return OcrResult.Empty;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var engine = _engine!;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var bitmap = ToSoftwareBitmap(image);
        var recognized = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken)
            .ConfigureAwait(false);

        var joinWithoutSpace = UsesWordSeparators(engine.RecognizerLanguage.LanguageTag) is false;
        var lines = new List<OcrLine>(recognized.Lines.Count);

        foreach (var line in recognized.Lines)
        {
            if (ToLine(line, joinWithoutSpace) is { } mapped)
            {
                lines.Add(mapped);
            }
        }

        return new OcrResult(lines, stopwatch.Elapsed);
    }

    /// <summary>
    /// 1 行ぶんを変換する。語が 1 つも無ければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para><b>行の位置は語の外接矩形から組み立てる。</b>
    /// Windows の行は矩形を持たず、語だけが持っている。</para>
    /// <para><b>語の間に空白を入れるかは文字の種類で決める。</b>
    /// <c>OcrLine.Text</c> は必ず空白で繋ぐため、日本語が「こ んにちは」のように割れる。
    /// 一方で日本語の行に混ざった <c>Attack Power</c> を詰めてもいけない。
    /// 行結合（FR-TRN-10）と同じ規則（<see cref="Scripts.IsWide"/>）で判断する。</para>
    /// </remarks>
    private static OcrLine? ToLine(WinOcr.OcrLine line, bool joinWithoutSpace)
    {
        if (line.Words.Count == 0)
        {
            return null;
        }

        double left = double.MaxValue, top = double.MaxValue;
        double right = double.MinValue, bottom = double.MinValue;

        var text = new System.Text.StringBuilder();
        foreach (var word in line.Words)
        {
            var box = word.BoundingRect;
            left = Math.Min(left, box.X);
            top = Math.Min(top, box.Y);
            right = Math.Max(right, box.X + box.Width);
            bottom = Math.Max(bottom, box.Y + box.Height);

            if (text.Length > 0 && NeedsSpace(text[^1], word.Text, joinWithoutSpace))
            {
                text.Append(' ');
            }

            text.Append(word.Text);
        }

        return new OcrLine(
            text.ToString(),

            // Windows の OCR は確信度を持たない。1.0 は「分からない」の意味
            // （ProvidesConfidence を参照）。
            1f,
            new OcrBox(
                (int)Math.Floor(left),
                (int)Math.Floor(top),
                (int)Math.Ceiling(right - left),
                (int)Math.Ceiling(bottom - top)));
    }

    /// <summary>語の間に空白が要るか。</summary>
    private static bool NeedsSpace(char previous, string next, bool joinWithoutSpace)
    {
        if (next.Length == 0)
        {
            return false;
        }

        // 分かち書きをする言語なら、エンジンの区切りをそのまま信じる。
        if (!joinWithoutSpace)
        {
            return true;
        }

        // 日本語などでは、両側ともラテン文字のときだけ空白を入れる。
        return !Scripts.IsWide(previous) && !Scripts.IsWide(next[0]);
    }

    /// <summary>その言語が語を空白で区切るか。</summary>
    /// <remarks>
    /// 日本語・中国語・韓国語・タイ語は区切らない。
    /// <b>ここに無い言語は区切る側に倒す</b> — 余計な空白が入るより、
    /// 詰めてはいけないものを詰める方が害が大きい。
    /// </remarks>
    private static bool UsesWordSeparators(string languageTag) =>
        !(languageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
          || languageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
          || languageTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase)
          || languageTag.StartsWith("th", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// OCR に渡せる形へ変換する。
    /// </summary>
    /// <remarks>
    /// <b>行の詰め物（Stride）を外す。</b><c>SoftwareBitmap</c> は隙間のない並びを前提にしており、
    /// 渡すとずれた画像になる。キャプチャ結果は詰め物が付くことがある。
    /// </remarks>
    private static SoftwareBitmap ToSoftwareBitmap(Bgra32Image image)
    {
        var tight = image.Width * 4;
        var pixels = image.Pixels;

        if (image.Stride != tight || pixels.Length != tight * image.Height)
        {
            var packed = new byte[tight * image.Height];
            for (var y = 0; y < image.Height; y++)
            {
                image.Pixels.AsSpan(y * image.Stride, tight).CopyTo(packed.AsSpan(y * tight));
            }

            pixels = packed;
        }

        return SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(pixels),
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height);
    }

    /// <summary>解放するものは無い。エンジンは WinRT 側が持つ。</summary>
    public void Dispose() => _engine = null;
}
