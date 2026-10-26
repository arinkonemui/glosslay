using static Glosslay.Localization.UiText;

namespace Glosslay.Translation;

/// <summary>
/// メイン言語として選べるもの 1 件（FR-SYS-03）。
/// </summary>
/// <param name="Tag">BCP 47 のタグ。設定ファイルとキャッシュの鍵に使う。</param>
/// <param name="DisplayName">一覧に出す名前。<b>その言語自身の表記で書く。</b></param>
public sealed record MainLanguageOption(string Tag, string DisplayName)
{
    /// <summary>画面に出す名前。</summary>
    /// <remarks>
    /// <b>レコードの既定の表記を使わせない。</b>一覧に並べると支援技術（スクリーンリーダー）が
    /// 読み上げる名前がこれになり、<c>MainLanguageOption { Tag = ja, ... }</c> と読まれる。
    /// </remarks>
    public override string ToString() => DisplayName;
}

/// <summary>
/// 原文の言語として選べるもの 1 件（FR-OCR-02）。
/// </summary>
/// <param name="Tag">BCP 47 の言語の部分（<c>ja</c> / <c>en</c> / <c>zh</c> / <c>ko</c>）。</param>
public sealed record SourceLanguageOption(string Tag)
{
    /// <summary>一覧に出す名前。<b>いまの画面の言語で書く</b>（<see cref="Languages.NameOf"/>）。</summary>
    public string DisplayName => Languages.NameOf(Tag);

    /// <inheritdoc cref="MainLanguageOption.ToString" />
    public override string ToString() => DisplayName;
}

/// <summary>原文の言語とメイン言語の組み合わせを、どう訳すか（RULES.md 🟡-1 の表）。</summary>
public enum PairAvailability
{
    /// <summary>原文とメイン言語が同じ。訳さない。</summary>
    Same,

    /// <summary>ローカルで訳せる。<b>キーが無くても動く。</b></summary>
    Local,

    /// <summary>
    /// ローカルで訳せるが、<b>精度が大きく下がる</b>（日→英→中）。
    /// </summary>
    /// <remarks>
    /// 選ぶ箇所に「翻訳精度が大きく下がります。クラウド機能の選択をお勧めします」と出す
    /// （2026-10-25 人間の判断）。2 段の誤りが重なり、意味が別物になる文が多いため。
    /// </remarks>
    LocalLowQuality,

    /// <summary>クラウド翻訳でのみ訳せる。<b>利用者にキーを作成・登録してもらう。</b></summary>
    CloudOnly,
}

/// <summary>
/// メイン言語と原文の言語（FR-SYS-03 / FR-OCR-02 / RULES.md 🟡-1）。
/// </summary>
/// <remarks>
/// <para><b>利用者がメイン言語と原文の言語を選び、原文をメイン言語へ訳す</b>（2026-10-25 人間の判断）。
/// それまでは訳文の言語だけを選び、原文の言語は画面のかなの割合などから自動で見分けていた。</para>
/// <para><b>組み合わせの表（<see cref="Availability"/>）は RULES.md 🟡-1 が正。</b>
/// 画面の説明・バックエンドの選択・ローカル翻訳の断り方は、すべてここを見る。
/// 3 か所に同じ表を書くと必ず食い違う。</para>
/// <para><b>中国語は簡体字だけ</b>（2026-10-24 人間の判断）。原文の側は繁体字も読んで訳せる。</para>
/// </remarks>
public static class Languages
{
    /// <summary>既定のメイン言語。</summary>
    public const string DefaultMain = "ja";

    /// <summary>
    /// メイン言語の選択肢。
    /// </summary>
    /// <remarks>
    /// <b>日本語を先頭に置く。</b>既定であり、キー無しで最も多くの原文を訳せるため。
    /// 名前はその言語自身の表記で書く（画面の言語が読めない人も自分の言語を見つけられるように）。
    /// </remarks>
    public static IReadOnlyList<MainLanguageOption> MainOptions { get; } =
    [
        new(DefaultMain, "日本語"),
        new("en", "English"),
        new("zh-Hans", "简体中文"),
        new("ko", "한국어"),
    ];

    /// <summary>原文の言語の選択肢。</summary>
    /// <remarks>名前はいまの画面の言語で出す（<see cref="SourceLanguageOption.DisplayName"/>）。</remarks>
    public static IReadOnlyList<SourceLanguageOption> SourceOptions { get; } =
    [
        new("ja"),
        new("en"),
        new("zh"),
        new("ko"),
    ];

    /// <summary>
    /// 原文の言語の既定。<b>英語</b>（メイン言語が英語なら日本語）。
    /// </summary>
    /// <remarks>
    /// 英語版のあるゲームが最も多いため英語を既定にする。メイン言語が英語の人に
    /// 英語→英語を既定にしても意味が無いので、その場合は日本語（2026-10-25 人間の判断）。
    /// </remarks>
    public static string DefaultSourceFor(string mainLanguage) =>
        Family(mainLanguage) == "en" ? "ja" : "en";

    /// <summary>言語の部分（<c>zh-Hans</c> → <c>zh</c>）。小文字で返す。</summary>
    public static string Family(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var trimmed = tag.Trim();
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        return (dash < 0 ? trimmed : trimmed[..dash]).ToLowerInvariant();
    }

    /// <summary>同じ言語か（書き方・地域の違いは見ない）。</summary>
    public static bool SameLanguage(string a, string b) =>
        string.Equals(Family(a), Family(b), StringComparison.Ordinal);

    /// <summary>
    /// 設定ファイルの値をメイン言語として読む。
    /// </summary>
    /// <remarks>
    /// <para><b>中国語の指定はすべて簡体字に寄せる。</b>繁体字は 2026-10-24 にやめた。
    /// 以前の設定ファイルに <c>zh-Hant</c> が残っていても、近い方で動かす。</para>
    /// <para><b>知らないタグは捨てずにそのまま返す。</b>手で書き換えた値を黙って日本語に戻すと、
    /// 「書いたのに効いていない」ことに気付けない（RULES.md 🔵）。そういう組み合わせはクラウドで訳す。</para>
    /// </remarks>
    public static string NormalizeMain(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return DefaultMain;
        }

        if (Family(tag) == "zh")
        {
            return "zh-Hans";
        }

        return MainOptions.FirstOrDefault(o => SameLanguage(o.Tag, tag))?.Tag ?? tag.Trim();
    }

    /// <summary>
    /// 設定ファイル・プロファイルの値を原文の言語として読む。空なら既定（<see cref="DefaultSourceFor"/>）。
    /// </summary>
    /// <remarks>
    /// <b>メイン言語と同じなら既定に戻す。</b>メイン言語を変えた後や、別のメイン言語で作ったプロファイルを
    /// 当てたときに起きる（英語→英語では何も訳せない）。
    /// </remarks>
    public static string NormalizeSource(string? tag, string mainLanguage)
    {
        if (string.IsNullOrWhiteSpace(tag) || SameLanguage(tag, mainLanguage))
        {
            return DefaultSourceFor(mainLanguage);
        }

        return SourceOptions.FirstOrDefault(o => SameLanguage(o.Tag, tag))?.Tag ?? tag.Trim();
    }

    /// <summary>
    /// 組み合わせをどう訳すか（RULES.md 🟡-1 の表）。
    /// </summary>
    /// <remarks>
    /// <list type="table">
    ///   <item><term>ローカル</term><description>英→日・中→日（中→英→日）・日→英・中→英・英→中</description></item>
    ///   <item><term>ローカル（精度が大きく下がる）</term><description>日→中（日→英→中）</description></item>
    ///   <item><term>クラウドのみ</term><description>韓国語が入る組み合わせすべて。表に無い言語も</description></item>
    /// </list>
    /// </remarks>
    public static PairAvailability Availability(string sourceLanguage, string mainLanguage)
    {
        var from = Family(sourceLanguage);
        var to = Family(mainLanguage);

        if (from == to)
        {
            return PairAvailability.Same;
        }

        return (from, to) switch
        {
            ("en", "ja") or ("zh", "ja") or ("ja", "en") or ("zh", "en") or ("en", "zh") => PairAvailability.Local,
            ("ja", "zh") => PairAvailability.LocalLowQuality,
            _ => PairAvailability.CloudOnly,
        };
    }

    /// <summary>ローカルで訳せる組み合わせか（精度が下がるものを含む）。</summary>
    public static bool IsLocal(PairAvailability availability) =>
        availability is PairAvailability.Local or PairAvailability.LocalLowQuality;

    /// <summary>メイン言語の一覧から探す。知らないタグなら <c>null</c>。</summary>
    public static MainLanguageOption? FindMain(string tag) =>
        MainOptions.FirstOrDefault(o => string.Equals(o.Tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 画面に出す言語の呼び名。<b>いまの画面の言語で書く</b>（FR-SYS-03）。
    /// </summary>
    /// <remarks>
    /// <b>訳文の中国語は「中国語（簡体字）」と書く。</b>原文の中国語（<c>zh</c>）は書き方を問わない。
    /// </remarks>
    public static string NameOf(string tag)
    {
        var japanese = PromptNameOf(tag);
        return japanese == tag ? tag : T(japanese);
    }

    /// <summary>
    /// クラウドへの指示文に書く言語の呼び名。<b>画面の言語によらず日本語で書く</b>（指示文が日本語のため）。
    /// </summary>
    /// <remarks>
    /// <b>訳文の中国語は「中国語（簡体字）」と書く。</b>書き方を言わないと繁体字で返ってくることがある。
    /// </remarks>
    public static string PromptNameOf(string tag) => Family(tag) switch
    {
        "ja" => "日本語",
        "en" => "英語",
        "zh" when tag.Contains("Hans", StringComparison.OrdinalIgnoreCase) => "中国語（簡体字）",
        "zh" => "中国語",
        "ko" => "韓国語",
        _ => tag,
    };

    /// <summary>「日本語→韓国語」のような組み合わせの呼び名（画面の言語で書く）。</summary>
    public static string PairName(string sourceLanguage, string mainLanguage) =>
        $"{NameOf(sourceLanguage)}→{NameOf(mainLanguage)}";

    /// <summary>
    /// 行が原文の言語で書かれているか。<b>違う言語の行は訳さない</b>（FR-TRN-02）。
    /// </summary>
    /// <remarks>
    /// <para><b>クラウドにも送らない</b>（2026-10-25 人間の判断。利用量も減る — RULES.md 🟡-8）。
    /// 中国語のゲームにある英語のメニューは、原文の言語を中国語にしている限り訳さない。</para>
    /// <para>文字の種類だけで決める。<b>ローカルのモデルに違う言語を入れると、流暢な出鱈目が出る</b>
    /// （訳せなかったと分かる形ではなく、もっともらしい文になるため気付けない）。</para>
    /// <list type="table">
    ///   <item><term>英語</term><description>ラテン文字が主体の行</description></item>
    ///   <item><term>日本語</term><description>かなか漢字がある行（「設定」のような漢字だけの行も日本語）</description></item>
    ///   <item><term>中国語</term><description>漢字がある行。<b>かなが 2 文字以上なら日本語とみなして訳さない</b>。
    ///     1 文字だけなら、漢字をカタカナに読み違えたもの（力→カ）とみなして中国語として訳す</description></item>
    ///   <item><term>韓国語</term><description>ハングルがある行</description></item>
    /// </list>
    /// <para>表に無い言語は判定しない（訳す側に倒す）。クラウドが自分で判断する。</para>
    /// </remarks>
    public static bool IsWrittenIn(string line, string sourceLanguage)
    {
        ArgumentNullException.ThrowIfNull(sourceLanguage);

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var latin = Scripts.IsLatinText(line);
        var hangul = line.Count(Scripts.IsHangul);
        var kana = line.Count(Scripts.IsKana);
        var han = line.Count(Scripts.IsHan);

        return Family(sourceLanguage) switch
        {
            "en" => latin,
            "ja" => !latin && hangul == 0 && (kana > 0 || han > 0),
            "zh" => !latin && hangul == 0 && han > 0 && kana < 2,
            "ko" => !latin && hangul > 0,
            _ => true,
        };
    }
}
