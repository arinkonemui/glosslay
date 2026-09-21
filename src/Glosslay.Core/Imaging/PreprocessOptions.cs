namespace Glosslay.Imaging;

/// <summary>拡大時の補間方法。</summary>
public enum ResamplingMode
{
    /// <summary>双線形。速いが拡大すると輪郭が甘くなる。</summary>
    Bilinear,

    /// <summary>双三次（Catmull-Rom）。文字の拡大では輪郭が保たれやすい。</summary>
    Bicubic,
}

/// <summary>
/// OCR にかける前の画像処理の設定（FR-OCR-04 / FR-OCR-05）。
/// </summary>
/// <remarks>
/// <para>ジャンルごとに最適値が違うため、プロファイルに紐づけて保存する想定
/// （PLAN.md の主要リスク「ジャンルごとに最適な前処理が違う」）。</para>
/// <para><b>前提: 画面全体ではなく切り出した領域に適用すること。</b>
/// 全画面を 2 倍に拡大すると画素数が 4 倍になり、検出時間もそれに比例して増える。</para>
/// </remarks>
public sealed record PreprocessOptions
{
    /// <summary>何もしない設定。前処理の有無を比較するときの基準。</summary>
    public static PreprocessOptions None { get; } = new() { Scale = 1.0 };

    /// <summary>
    /// 拡大倍率（FR-OCR-04）。既定 2 倍、1〜4 倍で設定可能。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ この既定値をそのまま使わないこと（PLAN.md 課題2）。</b>
    /// 仕様どおり 2.0 にしてあるが、<b>実測では拡大しないほうが速くて精度も落ちない。</b>
    /// 10 枚平均で 1 倍 92.4% / 691ms に対し、2 倍 92.2% / 1104ms（2026-09-22 計測）。</para>
    /// <para>検出は短辺が 736 になるよう内部で拡大するため
    /// （<c>OcrImagePreprocessor.DetectionLimitSideLength</c>）、
    /// <b>拡大後の短辺が 736 を超えるまで自前の拡大は効かない。</b>
    /// 短辺 736 未満の領域では二重のリサンプルになり、精度はむしろ下がる（最大 -3.5pt）。</para>
    /// <para>仕様（FR-OCR-04）とコードは P0-7 の後に一度に変える。
    /// それまでは呼び出し側で倍率を明示すること。</para>
    /// </remarks>
    public double Scale { get; init; } = 2.0;

    /// <summary>拡大時の補間方法。</summary>
    public ResamplingMode Resampling { get; init; } = ResamplingMode.Bicubic;

    /// <summary>
    /// コントラストの倍率。1.0 で変更なし。1.0 より大きいと明暗の差が開く。
    /// </summary>
    public double Contrast { get; init; } = 1.0;

    /// <summary>
    /// ヒストグラムを引き伸ばして明暗の幅いっぱいに広げる。
    /// 全体的に眠い画面で効く。<see cref="Contrast"/> より先に適用する。
    /// </summary>
    public bool AutoContrast { get; init; }

    /// <summary>グレースケール化する。二値化を行う場合は自動的に適用される。</summary>
    public bool Grayscale { get; init; }

    /// <summary>
    /// 大津の方法で二値化する。背景が一様な場面に向く。
    /// </summary>
    public bool Binarize { get; init; }

    /// <summary>
    /// 背景除去。局所的な明るさを見て二値化する（Sauvola）。
    /// </summary>
    /// <remarks>
    /// カードのイラスト背景のように、場所によって明るさが変わる画像に向く。
    /// <see cref="Binarize"/> と同時に指定した場合はこちらを優先する。
    /// </remarks>
    public bool RemoveBackground { get; init; }

    /// <summary>背景除去の窓の大きさ（画素）。文字の高さの 2〜3 倍程度が目安。</summary>
    public int BackgroundWindow { get; init; } = 25;

    /// <summary>
    /// 明暗を反転する。暗い背景に明るい文字、という配色で効くことがある。
    /// </summary>
    public bool Invert { get; init; }

    /// <summary>何らかの処理を行う設定か。</summary>
    public bool HasAnyEffect =>
        Scale is not 1.0
        || Contrast is not 1.0
        || AutoContrast
        || Grayscale
        || Binarize
        || RemoveBackground
        || Invert;

    /// <summary>設定内容を 1 行で表す。計測ログに残すために使う（P0-7）。</summary>
    public override string ToString()
    {
        if (!HasAnyEffect)
        {
            return "前処理なし";
        }

        var parts = new List<string>();
        if (Scale is not 1.0)
        {
            parts.Add($"拡大{Scale:0.##}倍({Resampling})");
        }

        if (AutoContrast)
        {
            parts.Add("自動コントラスト");
        }

        if (Contrast is not 1.0)
        {
            parts.Add($"コントラスト{Contrast:0.##}");
        }

        if (RemoveBackground)
        {
            parts.Add($"背景除去(窓{BackgroundWindow})");
        }
        else if (Binarize)
        {
            parts.Add("二値化");
        }
        else if (Grayscale)
        {
            parts.Add("グレースケール");
        }

        if (Invert)
        {
            parts.Add("反転");
        }

        return string.Join(" / ", parts);
    }
}
