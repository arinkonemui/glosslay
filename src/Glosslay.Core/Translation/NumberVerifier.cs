using System.Text;
using System.Text.RegularExpressions;

namespace Glosslay.Translation;

/// <summary>原文と訳文の数値を突き合わせた結果（FR-TRN-08）。</summary>
/// <param name="Missing">原文にあって訳文に無い数値。<b>落ちた・書き換わった側。</b></param>
/// <param name="Added">訳文にあって原文に無い数値。<b>作られた側。</b></param>
public readonly record struct NumberCheck(IReadOnlyList<string> Missing, IReadOnlyList<string> Added)
{
    /// <summary>数値が食い違っていないか。</summary>
    public bool IsConsistent => Missing.Count == 0 && Added.Count == 0;

    /// <summary>突き合わせていない（訳していない行など）。</summary>
    public static NumberCheck NotChecked { get; } = new([], []);
}

/// <summary>
/// 原文の数値が訳文に残っているかを機械的に照合する（FR-TRN-08 / RULES.md 🟡-7）。
/// </summary>
/// <remarks>
/// <para><b>流暢な誤りがいちばん危ない。</b>「ダメージ50」が「ダメージ60」になっても
/// 訳文としては自然なので、読んでも気づけない。翻訳の正確さは判断基準の 4 位だが、
/// <b>数値だけは「読めない」より有害</b>になりうる（SPEC.md UC-2）。</para>
/// <para><b>両方向を見る。</b>落ちた数値（<c>Missing</c>）だけでなく、
/// 作られた数値（<c>Added</c>）も拾う。LLM は欠けた数字をもっともらしく埋めることがある。</para>
/// <para><b>これは翻訳の誤りしか見つけられない。</b>OCR がアイコンを数字と読み違えた場合
/// （課題6 の <c>7,500</c> → <c>67,500</c>）は、<b>原文側が既に間違っている</b>ため
/// 原文と訳文は一致し、ここでは検出できない。その件は利用者への明記で扱う（2026-10-07 決定）。</para>
/// </remarks>
public static partial class NumberVerifier
{
    /// <summary>
    /// 原文と訳文の数値を突き合わせる。
    /// </summary>
    public static NumberCheck Compare(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);

        var expected = Extract(source);
        var actual = Extract(translated);

        if (expected.Count == 0 && actual.Count == 0)
        {
            return NumberCheck.NotChecked;
        }

        return new NumberCheck(
            [.. expected.Where(n => !actual.Contains(n))],
            [.. actual.Where(n => !expected.Contains(n))]);
    }

    /// <summary>
    /// 文字列から数値を取り出す。
    /// </summary>
    /// <remarks>
    /// <para><b>同じ値を同じ形に揃えてから比べる。</b>そうしないと
    /// <c>2,430</c> と <c>2430</c>、<c>１０</c> と <c>10</c> が別物になり、
    /// 訳し方の違いを誤りとして騒ぐ。</para>
    /// <list type="bullet">
    ///   <item>全角の数字を半角にする</item>
    ///   <item>桁区切りの <c>,</c> を落とす（<c>2,430</c> → <c>2430</c>）</item>
    ///   <item>末尾の <c>.</c> を落とす（文末の句点を小数点と読まないため）</item>
    /// </list>
    /// <para><b>符号は、英数字に続かないときだけ取り込む。</b>
    /// <c>-30</c>（抵抗）は符号だが、<c>CL-36676</c> や <c>ISV-CI-DSB3</c> の <c>-</c> は区切り。</para>
    /// <para><b>重複は数えない。</b>同じ数値が原文に 2 回、訳文に 1 回でも誤りとは限らない
    /// （日本語は繰り返しを省くことがある）。騒ぎすぎると警告が無視されるようになる。</para>
    /// </remarks>
    public static List<string> Extract(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = ToHalfWidthDigits(text);
        var found = new List<string>();

        foreach (Match match in NumberRegex().Matches(normalized))
        {
            var value = match.Value.Replace(",", string.Empty, StringComparison.Ordinal).TrimEnd('.');
            if (value.Length > 0 && value != "-" && !found.Contains(value))
            {
                found.Add(value);
            }
        }

        return found;
    }

    /// <summary>全角の数字を半角にする。ほかの文字はそのまま。</summary>
    private static string ToHalfWidthDigits(string text)
    {
        if (!text.Any(c => c is >= '０' and <= '９'))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c);
        }

        return builder.ToString();
    }

    // 符号は英数字に続かないときだけ。数字で始まり、数字・桁区切り・小数点が続く。
    [GeneratedRegex(@"(?<![A-Za-z0-9])-?[0-9][0-9,.]*")]
    private static partial Regex NumberRegex();
}
