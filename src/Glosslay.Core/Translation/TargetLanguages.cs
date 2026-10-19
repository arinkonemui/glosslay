namespace Glosslay.Translation;

/// <summary>
/// 訳文の言語として選べるもの 1 件。
/// </summary>
/// <param name="Tag">BCP 47 のタグ。設定ファイルとキャッシュの鍵に使う。</param>
/// <param name="DisplayName">画面に出す名前。<b>その言語自身の表記で書く。</b></param>
/// <param name="LocalSupported">同梱のモデルで（どれかの原文を）訳せるか。</param>
public sealed record TargetLanguageOption(string Tag, string DisplayName, bool LocalSupported)
{
    /// <summary>
    /// 同梱のモデルで訳せる原文の言語（画面の説明に使う）。例: 「英語・中国語」。
    /// </summary>
    /// <remarks>
    /// <b>訳先ごとに訳せる原文が違う</b>（日本語へは英・中、英語へは中だけ）。
    /// 「訳せる / 訳せない」の 2 値では、英語を選んだ人に日本語の原文も訳せると誤解させる。
    /// </remarks>
    public string LocalSources { get; init; } = string.Empty;

    /// <summary>画面に出す名前。</summary>
    /// <remarks>
    /// <b>レコードの既定の表記を使わせない。</b>一覧に並べると
    /// 支援技術（スクリーンリーダー）が読み上げる名前がこれになり、
    /// <c>TargetLanguageOption { Tag = ja, ... }</c> と読まれる。
    /// </remarks>
    public override string ToString() => DisplayName;
}

/// <summary>
/// 訳文の言語の選択肢（FR-CFG-01 の「言語」）。
/// </summary>
/// <remarks>
/// <para><b>同梱のモデルで訳せるのは日本語と英語へ</b>（FR-TRN-03）。日本語へは英語・中国語の原文、
/// 英語へは中国語の原文だけ（2026-10-19 に中国語を追加。<see cref="LocalNmtRoutes"/>）。
/// それ以外はクラウド翻訳のAPIキーが要る（FR-TRN-04 の BYOK）。
/// <b>選べることと動くことは別</b>なので、一覧の時点で区別できるようにしてある。</para>
/// <para><b>最終的に断るのはバックエンド自身。</b>ここは画面に出すための一覧で、
/// 実際に訳せるかは <c>LocalNmtTranslator</c> が
/// <see cref="TranslationOutcome.Unsupported"/> で答える。
/// 2 か所で判断することになるが、<b>画面だけの判断にすると
/// 手で設定ファイルを書き換えた場合に素通りする。</b></para>
/// <para>一覧を増やすのはクラウド側だけの話で、ローカルの対応を増やすには
/// モデルの追加が要る（FR-TRN-03 の言語ペア単位のダウンロード）。</para>
/// </remarks>
public static class TargetLanguages
{
    /// <summary>既定の言語。<b>キーが無くても英語・中国語の原文を訳せる。</b></summary>
    public const string Default = "ja";

    /// <summary>
    /// 選べる言語。
    /// </summary>
    /// <remarks>
    /// <b>日本語を先頭に置く。</b>既定であり、キー無しで最も多くの原文を訳せるため。
    /// 残りは OCR が読める言語（FR-OCR-02 の英・中・日）と、
    /// UI が対応する言語（FR-SYS-03 の日本語・英語）から採った。
    /// </remarks>
    public static IReadOnlyList<TargetLanguageOption> All { get; } =
    [
        new(Default, "日本語", LocalSupported: true) { LocalSources = "英語・中国語" },
        new("en", "English", LocalSupported: true) { LocalSources = "中国語" },
        new("zh-Hans", "简体中文", LocalSupported: false),
        new("zh-Hant", "繁體中文", LocalSupported: false),
        new("ko", "한국어", LocalSupported: false),
    ];

    /// <summary>タグで探す。知らないタグなら <c>null</c>。</summary>
    /// <remarks>
    /// <b>知らないタグでも落とさない。</b>設定ファイルを手で書き換えられる以上、
    /// 一覧に無い値が来る（RULES.md 🔵 / 🟡-6）。
    /// </remarks>
    public static TargetLanguageOption? Find(string tag) =>
        All.FirstOrDefault(o => string.Equals(o.Tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 同梱のモデルで訳せる言語か。
    /// </summary>
    /// <remarks>
    /// 一覧に無いタグは <c>false</c>。<b>知らないものを「訳せる」と答えない。</b>
    /// </remarks>
    public static bool LocalSupports(string tag) => Find(tag)?.LocalSupported == true;

    /// <summary>画面に出す名前。一覧に無ければタグをそのまま返す。</summary>
    public static string DisplayNameOf(string tag) => Find(tag)?.DisplayName ?? tag;
}
