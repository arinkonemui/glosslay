using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Glosslay.Ocr;

namespace Glosslay.Translation;

/// <summary>行ごとの翻訳の結果。</summary>
public enum LineTranslationStatus
{
    /// <summary>訳文が得られ、原文と異なる。<b>オーバーレイに重ねる。</b></summary>
    Translated,

    /// <summary>訳文が原文と同じだった（固有名詞・コードなど）。重ねても情報が増えないため重ねない。</summary>
    Unchanged,

    /// <summary>数値・記号だけ等で、翻訳に回していない。重ねない。</summary>
    Skipped,

    /// <summary>翻訳に回したが、応答から対応が取れなかった（または翻訳自体が失敗した）。重ねない。</summary>
    Unmapped,
}

/// <summary>行ごとの訳文を元の位置へ戻した結果。</summary>
/// <param name="Lines">
/// 入力と同じ並びの行。<see cref="LineTranslationStatus.Translated"/> の行だけ訳文に置き換わる。
/// 位置（Box）は常に原文のまま。
/// </param>
/// <param name="Statuses">行ごとの結果。<see cref="Lines"/> と同じ並び。</param>
/// <param name="Translation">
/// 翻訳の呼び出し結果。所要時間・状態・消費トークンはここを見る。
/// 翻訳に回す行が無かった場合は呼び出していない（所要時間 0 の成功）。
/// </param>
public sealed record LineTranslationResult(
    IReadOnlyList<OcrLine> Lines,
    IReadOnlyList<LineTranslationStatus> Statuses,
    TranslationResult Translation)
{
    /// <summary>
    /// オーバーレイに重ねる行。
    /// </summary>
    /// <remarks>
    /// 重ねるのは<b>訳して文字が変わった行だけ</b>。<c>0/5</c> の上に <c>0/5</c> を重ねるような、
    /// 情報の増えない表示でゲーム画面を覆わない（判断基準の 2 位「ゲームの描画を妨げない」）。
    /// </remarks>
    public IReadOnlyList<OcrLine> LinesToOverlay =>
        [.. Lines.Where((_, i) => Statuses[i] == LineTranslationStatus.Translated)];

    /// <summary>翻訳に回した行の数。</summary>
    public int SentCount => Statuses.Count(s => s != LineTranslationStatus.Skipped);

    /// <summary>応答から対応が取れた行の数（訳文が原文と同じだった行を含む）。</summary>
    public int MappedCount =>
        Statuses.Count(s => s is LineTranslationStatus.Translated or LineTranslationStatus.Unchanged);

    /// <summary>翻訳に回した行すべての対応が取れたか。</summary>
    public bool IsComplete => MappedCount == SentCount;
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
/// <para><b>訳す必要のない行は送らない</b>（<see cref="NeedsTranslation"/>）。
/// 2026-09-17 の Tyr のテックツリー画面では 97 行中 49 行が数値・記号・段位記号だけで、
/// 送れば応答が長くなり翻訳時間が延びる（課題4）。</para>
/// <para><b>LLM が形式を守る保証はない。</b>取りこぼした行は <see cref="LineTranslationStatus.Unmapped"/> とし、
/// 黙って欠落させない。</para>
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

        var statuses = new LineTranslationStatus[lines.Count];

        // 翻訳に回す行の、元の並びでの位置。依頼文の番号はこの並びで 1 から振り直す。
        // 元の位置で番号を振ると [1] [3] [7] のような飛び番になり、応答の解釈が壊れる。
        var sent = new List<int>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (NeedsTranslation(lines[i].Text))
            {
                sent.Add(i);
            }
            else
            {
                statuses[i] = LineTranslationStatus.Skipped;
            }
        }

        if (sent.Count == 0)
        {
            // 訳すものがない。API を呼ばない（RULES.md 🟡-8）。
            return new LineTranslationResult(
                lines, statuses, new TranslationResult(string.Empty, TranslationOutcome.Succeeded, TimeSpan.Zero));
        }

        var request = new TranslationRequest(BuildRequestText([.. sent.Select(i => lines[i].Text)]))
        {
            TargetLanguage = targetLanguage,
        };

        var result = await translator.TranslateAsync(request, cancellationToken).ConfigureAwait(false);

        var translated = result.IsSuccess ? Parse(result.Text, sent.Count) : new string?[sent.Count];
        var output = lines.ToList();

        for (var n = 0; n < sent.Count; n++)
        {
            var index = sent[n];
            if (translated[n] is not { Length: > 0 } text)
            {
                statuses[index] = LineTranslationStatus.Unmapped;
            }
            else if (IsSameText(lines[index].Text, text))
            {
                statuses[index] = LineTranslationStatus.Unchanged;
            }
            else
            {
                output[index] = lines[index] with { Text = text };
                statuses[index] = LineTranslationStatus.Translated;
            }
        }

        return new LineTranslationResult(output, statuses, result);
    }

    /// <summary>
    /// 翻訳に回す必要のある行か。
    /// </summary>
    /// <remarks>
    /// <para>除外するのは次の 2 種類だけ。<b>迷うものは送る側に倒す</b>（訳し漏れの方が害が大きい）。</para>
    /// <list type="bullet">
    ///   <item><b>文字（Letter）を含まない</b> — <c>0/5</c> <c>62,778</c> <c>2.60%</c> <c>-1.30</c></item>
    ///   <item><b>英字 1 文字だけ</b> — <c>T-85</c>（段位）<c>Q</c>（キー表示）<c>15s</c> <c>x2</c></item>
    /// </list>
    /// <para>英字 2 文字以上は送る。<c>OK</c> <c>HP</c> <c>MAX</c> <c>NEW</c> のように訳す価値のある短い語がある。</para>
    /// <para>漢字・ひらがな等の 1 文字は送る。中国語のゲームでは <c>火</c> 1 文字でも意味を持つ。</para>
    /// <para>数値だけの行を送らないことは、数値の正確性（RULES.md 🟡-7）にも有利に働く。
    /// ゲームが描いた数値がそのまま見え、LLM が書き換える機会もない。</para>
    /// </remarks>
    public static bool NeedsTranslation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var letters = 0;
        var firstLetter = '\0';
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                letters++;
                firstLetter = c;
                if (letters >= 2)
                {
                    return true;
                }
            }
        }

        return letters == 1 && !char.IsAsciiLetter(firstLetter);
    }

    /// <summary>
    /// 翻訳に渡す文を組み立てる。
    /// </summary>
    /// <remarks>
    /// <para>形式の指示を<b>システム指示ではなく本文側</b>に書いている。
    /// システム指示は gemini.json で利用者が上書きできるため、そこに頼ると
    /// 古い設定ファイルを持つ環境で番号が返らなくなる。</para>
    /// <para><b>番号の数は守らせるが、意味は行をまたいで振り分けさせる。</b>
    /// 以前は「番号ごとに対応する部分だけを訳す」と指示していたため、OCR が
    /// <c>…whenever you land after being</c> / <c>airborne</c> と割った文が
    /// <c>着地するたびに…生成する</c> / <c>空中</c> になり、後ろの断片が浮いた（2026-09-18・問題 E）。
    /// 同じ日の別の回では LLM がこの指示を緩めて 2 行へ意味を振り分け、自然に読める訳を返していたため、
    /// その振る舞いを指示として明文化した。</para>
    /// <para>「どの番号も空にしない」は、空の番号が対応なし（重ねない）になるのを避けるため。
    /// 「数値を必ず残す」は、振り分けの途中で数値が落ちるのを防ぐため（RULES.md 🟡-7）。
    /// システム指示にも同じ規則があるが、gemini.json で上書きされうるので本文側でも念を押す。</para>
    /// </remarks>
    public static string BuildRequestText(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var builder = new StringBuilder();
        builder.AppendLine("次の各行を翻訳してください。");
        builder.AppendLine("各行の先頭の [番号] は必ずそのまま残し、1 つの番号につき 1 行で、同じ形式で出力してください。番号を増やしたり減らしたりしないでください。");
        builder.AppendLine("これは画面の文字を読み取ったもので、1 つの文が途中で改行され、続けて並んだ複数の番号に分かれていることがあります。");
        builder.AppendLine("その場合は文全体の意味で訳したうえで、上の番号から順に読んで自然な日本語になるよう、訳文を各番号に振り分けてください。どの番号も空にしないでください。");
        builder.AppendLine("振り分けても、原文の数値は必ずいずれかの番号の訳文に残してください。");
        builder.AppendLine("見出しと説明文のように別々の項目は、1 つの文として扱わないでください。");
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
    /// <param name="count">依頼した行数。</param>
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

    /// <summary>
    /// 訳文が原文と実質的に同じか。
    /// </summary>
    /// <remarks>
    /// 大文字小文字と空白の違いは無視する。<c>TECH  TREE</c> に <c>Tech Tree</c> が返っても、
    /// 訳していないのと同じで、重ねる意味がない。
    /// </remarks>
    private static bool IsSameText(string original, string translated) =>
        string.Equals(
            CollapseWhitespace(original), CollapseWhitespace(translated), StringComparison.OrdinalIgnoreCase);

    private static string CollapseWhitespace(string text) => WhitespaceRegex().Replace(text.Trim(), " ");

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

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
