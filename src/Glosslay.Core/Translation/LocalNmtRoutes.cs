using System.Text.RegularExpressions;

namespace Glosslay.Translation;

/// <summary>ローカル翻訳で、1 行をどのモデルに通すか。</summary>
public enum LocalNmtRoute
{
    /// <summary>訳さない（訳す必要が無い / ローカルでは訳せない）。原文のまま返す。</summary>
    None,

    /// <summary>英→日。</summary>
    EnglishToJapanese,

    /// <summary>中→英。</summary>
    ChineseToEnglish,

    /// <summary>中→英→日（2 段）。中→日を直接訳せるモデルが無いため。</summary>
    ChineseToJapanese,
}

/// <summary>
/// 行ごとに、原文の言語と訳先からローカル翻訳の経路を決める（FR-TRN-02 / 03）。
/// </summary>
/// <remarks>
/// <para><b>2026-10-19 に中国語をローカルで訳すと決めた</b>（人間の判断）。それまでは英→日だけだった。</para>
/// <list type="table">
///   <item><term>訳先 日本語</term><description>英語 → 英→日 / 中国語 → 中→英→日 / 日本語 → 訳さない</description></item>
///   <item><term>訳先 英語</term><description>中国語 → 中→英 / 英語 → 訳さない / 日本語 → <b>ローカルでは訳せない</b>（日→英は未決定）</description></item>
///   <item><term>それ以外</term><description>ローカルでは訳せない（クラウド翻訳が要る）</description></item>
/// </list>
/// <para><b>漢字だけの行は、日本語か中国語か見分けられない</b>（「設定」「装備」）。
/// <b>画面単位で日本語か中国語かを決める。</b>行単位で決めると、日本語のゲームのメニュー語を中→英→日に通してしまう。</para>
/// <para><b>日本語の画面とみなすのは、かなが 2 文字以上あり、漢字の 1 割以上あるとき。</b>
/// OCR は中国語の漢字をよく似たカタカナに読み違える（力→カ、口→ロ、工→エ）。
/// 「かなが 1 文字でもあれば日本語」とすると、読み違い 1 文字で中国語の画面が丸ごと訳されなくなる。
/// 日本語の文はかなが 3〜6 割を占めるのが普通で、この線なら読み違いの 1〜2 文字では越えない。</para>
/// <para><b>英語が多い画面の、漢字 1 文字だけの行は訳さない。</b>アイコンを OCR が漢字と読み違えたもの
/// （「木」「日」など。PLAN.md 課題6）がほとんどで、訳すと英語の画面に *Wood* のような雑音を重ねる
/// （2026-10-19、41 枚中 2 枚で実際に起きた）。中国語の画面の「查」「是」のような 1 文字のボタンは訳す。</para>
/// <para>モデルを読まずに決める。実際に訳すかどうかはモデルの有無でさらに絞る（<see cref="LocalNmtTranslator"/>）。</para>
/// </remarks>
public static partial class LocalNmtRoutes
{
    /// <summary>
    /// 画面の行ぶんの経路を決める。<b>入力と同じ本数を返す。</b>
    /// </summary>
    /// <param name="lines">同じ画面から読んだ行（かなの有無を画面単位で見るため、まとめて渡す）。</param>
    /// <param name="targetLanguage">訳先の言語タグ。</param>
    public static LocalNmtRoute[] Plan(IReadOnlyList<string> lines, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(targetLanguage);

        var toJapanese = string.Equals(targetLanguage, "ja", StringComparison.OrdinalIgnoreCase);
        var toEnglish = string.Equals(targetLanguage, "en", StringComparison.OrdinalIgnoreCase);
        var kana = lines.Sum(l => l.Count(IsKana));
        var han = lines.Sum(l => l.Count(IsHan));
        var japaneseScreen = kana >= 2 && kana * 10 >= han;

        var routes = new LocalNmtRoute[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            routes[i] = (SourceOf(line, japaneseScreen), toJapanese, toEnglish) switch
            {
                (SourceLanguage.English, true, _) => LocalNmtRoute.EnglishToJapanese,
                (SourceLanguage.Chinese, true, _) => LocalNmtRoute.ChineseToJapanese,
                (SourceLanguage.Chinese, _, true) => LocalNmtRoute.ChineseToEnglish,
                _ => LocalNmtRoute.None,
            };
        }

        // 英語が多い画面の漢字 1 文字は、アイコンの読み違いとみなして訳さない（クラスの注記）。
        var englishLines = lines.Count(l => !string.IsNullOrWhiteSpace(l) && Scripts.IsLatinText(l));
        var chineseLines = lines.Where((l, i) => routes[i] is LocalNmtRoute.ChineseToEnglish or LocalNmtRoute.ChineseToJapanese).Count();
        if (englishLines > chineseLines)
        {
            for (var i = 0; i < routes.Length; i++)
            {
                if (routes[i] is LocalNmtRoute.ChineseToEnglish or LocalNmtRoute.ChineseToJapanese
                    && lines[i].Count(IsHan) <= 1)
                {
                    routes[i] = LocalNmtRoute.None;
                }
            }
        }

        return routes;
    }

    /// <summary>
    /// 中国語を句点で区切る。<b>中→英のモデルは 1 行に文が続くと途中の文を丸ごと落とす</b>
    /// （2026-10-16 の実測。1 文ずつ学習したモデルのため）。区切って 1 文ずつ訳せば落ちない。
    /// </summary>
    /// <remarks>英→日・日→英のモデルは落とさないため、区切らない（区切ると主語がぶれる）。</remarks>
    public static IReadOnlyList<string> SplitChineseSentences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. SentenceEnd().Split(text).Select(p => p.Trim()).Where(p => p.Length > 0)];
    }

    /// <summary>原文の言語の見立て。</summary>
    private enum SourceLanguage
    {
        Other,
        English,
        Chinese,
        Japanese,
    }

    private static SourceLanguage SourceOf(string line, bool japaneseScreen)
    {
        if (Scripts.IsLatinText(line))
        {
            return SourceLanguage.English;
        }

        if (ContainsHangul(line))
        {
            return SourceLanguage.Other;
        }

        // 日本語の画面なら、かなの行も漢字だけの行も日本語。
        if (japaneseScreen)
        {
            return line.Any(c => IsKana(c) || IsHan(c)) ? SourceLanguage.Japanese : SourceLanguage.Other;
        }

        // 中国語の画面でも、かなが 2 文字以上ある行は日本語（中国語のゲームに日本語が混ざる場合や、
        // ウィンドウの枠の日本語。2026-10-19、フォトアプリの「プログラムから開く」を中→英→日に通していた）。
        // かなが 1 文字だけなら、漢字をカタカナに読み違えたものとみなして中国語として訳す。
        if (line.Count(IsKana) >= 2)
        {
            return SourceLanguage.Japanese;
        }

        return ContainsHan(line) ? SourceLanguage.Chinese : SourceLanguage.Other;
    }

    private static bool IsKana(char c) => c is >= '\u3040' and <= '\u309F' or >= '\u30A0' and <= '\u30FF';

    private static bool IsHan(char c) => ContainsHan(c.ToString());

    private static bool ContainsHan(string text) =>
        text.Any(c => c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' or >= '\uF900' and <= '\uFAFF');

    private static bool ContainsHangul(string text) =>
        text.Any(c => c is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F');

    /// <summary>句点（。！？）と半角の ! ? の直後で区切る。記号は前の文に残す。</summary>
    [GeneratedRegex("(?<=[。！？!?])")]
    private static partial Regex SentenceEnd();
}
