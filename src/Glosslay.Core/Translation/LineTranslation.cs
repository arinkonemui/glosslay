using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Glosslay.Ocr;

namespace Glosslay.Translation;

/// <summary>行ごとの訳文を元の位置へ戻した結果。</summary>
/// <param name="Lines">訳文に置き換えた行。位置（Box）は原文のまま。対応が取れなかった行は原文のまま。</param>
/// <param name="IsMapped">
/// 行ごとに訳文を対応付けられたか。<see cref="Lines"/> と同じ並び。
/// </param>
/// <param name="Translation">翻訳の呼び出し結果。所要時間・状態・消費トークンはここを見る。</param>
/// <remarks>
/// 対応の有無を「訳文が原文と同じか」で推測してはいけない。
/// 数値（<c>30</c>）や固有名詞は、正しく訳しても原文と同じ文字列になる。
/// </remarks>
public sealed record LineTranslationResult(
    IReadOnlyList<OcrLine> Lines, IReadOnlyList<bool> IsMapped, TranslationResult Translation)
{
    /// <summary>訳文を対応付けられた行の数。</summary>
    public int MappedCount => IsMapped.Count(mapped => mapped);

    /// <summary>全行の対応が取れたか。</summary>
    public bool IsComplete => MappedCount == Lines.Count;
}

/// <summary>
/// OCR の行をまとめて 1 回で翻訳し、訳文を行ごとに元の位置へ戻す。
/// </summary>
/// <remarks>
/// <para><b>行ごとに API を呼ばない。</b>行数倍の呼び出しになり、RULES.md 🟡-8（予期しない課金）に反する。
/// 1 回の呼び出しに <c>[番号] 原文</c> の形で並べ、<c>[番号] 訳文</c> で返させて番号で戻す。</para>
/// <para>訳文を位置に戻すのは FR-OVL-04 ①「原文位置に重ねる」のため。
/// スキルアイコンに触れると<b>別の場所</b>に説明が出るような画面では、
/// どの訳がどこの文字か、位置でしか対応が分からない。</para>
/// <para><b>LLM が形式を守る保証はない。</b>取りこぼした行は原文のまま残し、
/// 対応付けた数を <see cref="LineTranslationResult.MappedCount"/> で返す。黙って欠落させない。</para>
/// </remarks>
public static partial class LineTranslation
{
    /// <summary>
    /// 行をまとめて翻訳する。
    /// </summary>
    /// <param name="translator">翻訳バックエンド。</param>
    /// <param name="lines">
    /// 翻訳する行。<b>読み順に並べてから渡すこと。</b>順番が崩れていると文脈が壊れて訳の質が落ちる。
    /// </param>
    /// <param name="targetLanguage">訳文の言語。</param>
    /// <param name="cancellationToken">キャンセル。</param>
    public static async Task<LineTranslationResult> TranslateAsync(
        ITranslator translator,
        IReadOnlyList<OcrLine> lines,
        string targetLanguage = "ja",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            return new LineTranslationResult(
                [], [], new TranslationResult(string.Empty, TranslationOutcome.Succeeded, TimeSpan.Zero));
        }

        var request = new TranslationRequest(BuildRequestText(lines.Select(l => l.Text).ToList()))
        {
            TargetLanguage = targetLanguage,
        };

        var result = await translator.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new LineTranslationResult(lines, new bool[lines.Count], result);
        }

        var translated = Parse(result.Text, lines.Count);

        var output = new List<OcrLine>(lines.Count);
        var isMapped = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (translated[i] is { Length: > 0 } text)
            {
                output.Add(lines[i] with { Text = text });
                isMapped[i] = true;
            }
            else
            {
                output.Add(lines[i]);
            }
        }

        return new LineTranslationResult(output, isMapped, result);
    }

    /// <summary>
    /// 翻訳に渡す文を組み立てる。
    /// </summary>
    /// <remarks>
    /// 形式の指示を<b>システム指示ではなく本文側</b>に書いている。
    /// システム指示は gemini.json で利用者が上書きできるため、そこに頼ると
    /// 古い設定ファイルを持つ環境で番号が返らなくなる。
    /// </remarks>
    public static string BuildRequestText(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var builder = new StringBuilder();
        builder.AppendLine("次の各行を翻訳してください。");
        builder.AppendLine("各行の先頭の [番号] は必ずそのまま残し、1 つの番号につき 1 行で、同じ形式で出力してください。");
        builder.AppendLine("行が文の途中で切れていても、行を結合・分割せず、番号ごとに対応する部分だけを訳してください。");
        builder.AppendLine();

        for (var i = 0; i < lines.Count; i++)
        {
            // 行の中の改行は番号の対応を壊すため空白に潰す。
            var text = lines[i].ReplaceLineEndings(" ");
            builder.Append(CultureInfo.InvariantCulture, $"[{i + 1}] {text}").AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 応答から番号ごとの訳文を取り出す。
    /// </summary>
    /// <param name="response">翻訳の応答。</param>
    /// <param name="count">元の行数。</param>
    /// <returns>長さ <paramref name="count"/> の配列。対応が取れなかった番号は <c>null</c>。</returns>
    /// <remarks>
    /// <para>日本語で出力させると括弧や数字が全角に化けることがあるため、
    /// <c>[1]</c> / <c>［１］</c> / <c>【1】</c> をすべて受け付ける。</para>
    /// <para>番号の付いていない行は、直前の番号の続きとして連結する（訳文が折り返された場合）。</para>
    /// <para>同じ番号が 2 回出たら最初を採る。範囲外の番号は捨てる。</para>
    /// </remarks>
    public static string?[] Parse(string response, int count)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var results = new string?[count];
        var current = -1;

        foreach (var rawLine in response.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = NumberedLineRegex().Match(line);
            if (match.Success)
            {
                var index = ParseNumber(match.Groups["number"].Value) - 1;
                if (index < 0 || index >= count || results[index] is not null)
                {
                    // 範囲外、または重複。以降の番号なし行をどこにも連結しない。
                    current = -1;
                    continue;
                }

                results[index] = match.Groups["text"].Value.Trim();
                current = index;
            }
            else if (current >= 0)
            {
                results[current] = $"{results[current]} {line}";
            }
        }

        return results;
    }

    /// <summary>全角数字を含む番号を整数にする。解釈できなければ 0。</summary>
    private static int ParseNumber(string digits)
    {
        var value = 0;
        foreach (var c in digits)
        {
            var digit = (int)char.GetNumericValue(c);
            if (digit is < 0 or > 9)
            {
                return 0;
            }

            value = checked((value * 10) + digit);
        }

        return value;
    }

    // [1] / ［１］ / 【1】 のいずれか。番号の後の区切り（空白・コロン・ドット）も許す。
    [GeneratedRegex(@"^[\[［【]\s*(?<number>[0-9０-９]{1,4})\s*[\]］】]\s*[:：.．]?\s*(?<text>.*)$")]
    private static partial Regex NumberedLineRegex();
}
