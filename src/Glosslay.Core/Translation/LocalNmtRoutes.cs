using System.Text.RegularExpressions;

namespace Glosslay.Translation;

/// <summary>ローカル翻訳で、1 行をどのモデルに通すか。</summary>
public enum LocalNmtRoute
{
    /// <summary>訳さない（原文の言語の行ではない / ローカルでは訳せない組み合わせ）。原文のまま返す。</summary>
    None,

    /// <summary>英→日。</summary>
    EnglishToJapanese,

    /// <summary>中→英。</summary>
    ChineseToEnglish,

    /// <summary>中→英→日（2 段）。中→日を直接訳せるモデルが無いため。</summary>
    ChineseToJapanese,

    /// <summary>日→英（2026-10-25 追加）。</summary>
    JapaneseToEnglish,

    /// <summary>英→中（2026-10-25 追加）。簡体字で出す。</summary>
    EnglishToChinese,

    /// <summary>
    /// 日→英→中（2 段・2026-10-25 追加）。日→中を直接訳せるモデルで使えるものが無いため。
    /// </summary>
    /// <remarks><b>精度が大きく下がる</b>（<see cref="PairAvailability.LocalLowQuality"/>）。</remarks>
    JapaneseToChinese,
}

/// <summary>ローカル翻訳のモデル（言語ペア 1 つ）。</summary>
public enum LocalNmtModel
{
    /// <summary>英→日（<c>Mitsua/elan-mt-bt-en-ja</c>）。</summary>
    EnglishToJapanese,

    /// <summary>中→英（<c>Helsinki-NLP/opus-mt-zh-en</c>）。</summary>
    ChineseToEnglish,

    /// <summary>日→英（<c>Mitsua/elan-mt-bt-ja-en</c>）。</summary>
    JapaneseToEnglish,

    /// <summary>英→中（<c>Helsinki-NLP/opus-mt-en-zh</c>）。</summary>
    EnglishToChinese,
}

/// <summary>
/// 原文の言語とメイン言語から、行ごとのローカル翻訳の経路を決める（FR-TRN-02 / 03）。
/// </summary>
/// <remarks>
/// <para><b>2026-10-25 に、原文の言語を利用者が選ぶ形にした</b>（人間の判断）。
/// それまでは画面のかなの割合で日本語か中国語かを見分け、英語が多い画面の漢字 1 文字を雑音として捨てていた。
/// 原文の言語が決まっていれば、どちらの規則も要らない（違う言語の行は訳さない —
/// <see cref="Languages.IsWrittenIn"/>）。</para>
/// <para>組み合わせの表は <see cref="Languages.Availability"/>（RULES.md 🟡-1 が正）。
/// モデルを読まずに決める。実際に訳せるかはモデルの有無でさらに決まる（<see cref="LocalNmtTranslator"/>）。</para>
/// </remarks>
public static partial class LocalNmtRoutes
{
    /// <summary>
    /// 組み合わせの経路。ローカルで訳せない組み合わせなら <see cref="LocalNmtRoute.None"/>。
    /// </summary>
    public static LocalNmtRoute RouteFor(string sourceLanguage, string mainLanguage) =>
        (Languages.Family(sourceLanguage), Languages.Family(mainLanguage)) switch
        {
            ("en", "ja") => LocalNmtRoute.EnglishToJapanese,
            ("zh", "ja") => LocalNmtRoute.ChineseToJapanese,
            ("ja", "en") => LocalNmtRoute.JapaneseToEnglish,
            ("zh", "en") => LocalNmtRoute.ChineseToEnglish,
            ("en", "zh") => LocalNmtRoute.EnglishToChinese,
            ("ja", "zh") => LocalNmtRoute.JapaneseToChinese,
            _ => LocalNmtRoute.None,
        };

    /// <summary>
    /// 経路が使うモデル。1 段目と、2 段目（無ければ <c>null</c>）。
    /// </summary>
    public static (LocalNmtModel First, LocalNmtModel? Second) ModelsOf(LocalNmtRoute route) => route switch
    {
        LocalNmtRoute.EnglishToJapanese => (LocalNmtModel.EnglishToJapanese, null),
        LocalNmtRoute.ChineseToEnglish => (LocalNmtModel.ChineseToEnglish, null),
        LocalNmtRoute.ChineseToJapanese => (LocalNmtModel.ChineseToEnglish, LocalNmtModel.EnglishToJapanese),
        LocalNmtRoute.JapaneseToEnglish => (LocalNmtModel.JapaneseToEnglish, null),
        LocalNmtRoute.EnglishToChinese => (LocalNmtModel.EnglishToChinese, null),
        LocalNmtRoute.JapaneseToChinese => (LocalNmtModel.JapaneseToEnglish, LocalNmtModel.EnglishToChinese),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "訳さない経路にモデルは無い"),
    };

    /// <summary>
    /// 画面の行ぶんの経路を決める。<b>入力と同じ本数を返す。</b>
    /// </summary>
    /// <param name="lines">同じ画面から読んだ行。</param>
    /// <param name="sourceLanguage">原文の言語（利用者が選んだもの）。</param>
    /// <param name="mainLanguage">メイン言語（訳文の言語）。</param>
    /// <remarks>
    /// <b>原文の言語で書かれていない行は訳さない</b>（<see cref="Languages.IsWrittenIn"/>）。
    /// 呼び出し側（<c>LineTranslation</c>）でも除いているが、ここでも見る。
    /// ローカルのモデルに違う言語を入れると、流暢な出鱈目が出て気付けないため。
    /// </remarks>
    public static LocalNmtRoute[] Plan(IReadOnlyList<string> lines, string sourceLanguage, string mainLanguage)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(mainLanguage);

        var route = RouteFor(sourceLanguage, mainLanguage);
        var routes = new LocalNmtRoute[lines.Count];
        if (route == LocalNmtRoute.None)
        {
            return routes;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (Languages.IsWrittenIn(lines[i], sourceLanguage))
            {
                routes[i] = route;
            }
        }

        return routes;
    }

    /// <summary>
    /// 中国語を句点で区切る。<b>中→英のモデルは 1 行に文が続くと途中の文を丸ごと落とす</b>
    /// （2026-10-16 の実測。1 文ずつ学習したモデルのため）。区切って 1 文ずつ訳せば落ちない。
    /// </summary>
    /// <remarks>
    /// 英→日・日→英のモデルは落とさないため、区切らない（日→英は区切ると主語がぶれる — 2026-10-16）。
    /// 英→中は落とす（<see cref="SplitEnglishSentences"/>）。
    /// </remarks>
    public static IReadOnlyList<string> SplitChineseSentences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. SentenceEnd().Split(text).Select(p => p.Trim()).Where(p => p.Length > 0)];
    }

    /// <summary>
    /// 英語を文で区切る。<b>英→中のモデルは 3 文以上続く段落の文を丸ごと落とす</b>（2026-10-25 の実測）。
    /// </summary>
    /// <remarks>
    /// <para><b>文末の記号の後ろに空白があるときだけ区切る。</b>「1.5」「Lv.12」の点で切らない。</para>
    /// <para>いつ区切るかは <see cref="SentencesFor"/>（3 文以上のときだけ）。</para>
    /// </remarks>
    public static IReadOnlyList<string> SplitEnglishSentences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. EnglishSentenceEnd().Split(text).Select(p => p.Trim()).Where(p => p.Length > 0)];
    }

    /// <summary>
    /// モデルに入れる前に、原文を文で区切るか（区切るなら区切った文、区切らないなら 1 件）。
    /// </summary>
    /// <remarks>
    /// <para>文を丸ごと落とすモデル（中→英・英→中）だけ区切る。2 段の 2 段目（日→英→中の英→中）も同じ。</para>
    /// <para><b>英→中は 3 文以上のときだけ区切る</b>（2026-10-25 の実測。英語 11 件）。</para>
    /// <list type="table">
    ///   <item><term>3 文以上・区切らない</term><description>5 件中 3 件で文が消えた
    ///     （Subnautica 2 の段落で「You'll feel better. You'll be better.」が無くなる等）</description></item>
    ///   <item><term>3 文以上・区切る</term><description>全部残る</description></item>
    ///   <item><term>2 文・区切らない</term><description>7 件とも両方の文が残った</description></item>
    ///   <item><term>2 文・区切る</term><description>同じ内容の繰り返し（我等你很久了 已经很久了）や、
    ///     無い文の付け足し（很高兴见到你）が出た</description></item>
    /// </list>
    /// <para>中→英は 2 文でも途中の文を落とすため、常に区切る（2026-10-16）。</para>
    /// </remarks>
    public static IReadOnlyList<string> SentencesFor(LocalNmtModel model, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        switch (model)
        {
            case LocalNmtModel.ChineseToEnglish:
                return SplitChineseSentences(text);

            case LocalNmtModel.EnglishToChinese:
                var sentences = SplitEnglishSentences(text);
                return sentences.Count >= 3 ? sentences : [text];

            default:
                return [text];
        }
    }

    /// <summary>句点（。！？）と半角の ! ? の直後で区切る。記号は前の文に残す。</summary>
    [GeneratedRegex("(?<=[。！？!?])")]
    private static partial Regex SentenceEnd();

    /// <summary>半角の . ! ? の後ろの空白で区切る。記号は前の文に残す。</summary>
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex EnglishSentenceEnd();
}
