using System.Text;
using Glosslay.Ocr;

namespace Glosslay.Translation;

/// <summary>
/// OCR が行に割った 1 つの文を、翻訳の前に繋ぎ直す（FR-TRN-10）。
/// </summary>
/// <remarks>
/// <para><b>これが無いとローカルNMT は実用にならない。</b>2026-09-26 の実測。</para>
/// <code>
/// 原文        Your printed body can / borrow tricks from indigenous life.
/// 切れたまま  「印刷されたボディ缶」＋ 断片      ← can を「缶」と訳した
/// 繋いでから  「あなたの印刷された身体は、先住民の生活からトリックを借りることができる。」
/// </code>
/// <para>Gemini は依頼文の「1 つの文が複数の番号に分かれていることがある」という指示で
/// 自力で繋ぎ直せていた（<c>LineTranslation.BuildRequestText</c>）。
/// <b>61M の NMT にその力は無い。</b>ここで繋いでから渡す。</para>
/// <para>切れ方は 2 通りあり、どちらも FR-TRN-10 の対象。</para>
/// <list type="number">
///   <item><b>横</b> — 1 行が複数の箱に割れる。<c>It's fairy sad,</c> →
///     <c>It's fairy</c> / <c>sad,</c>（2026-09-16・クロノクロス）</item>
///   <item><b>縦</b> — 文が折り返される。<c>Your printed body can</c> /
///     <c>borrow tricks from indigenous life.</c></item>
/// </list>
/// <para>SPEC.md §2 の図では行結合を TextAggregator に置いているが、
/// 判定に <c>LineTranslation.NeedsTranslation</c> を使うため、
/// 依存の向き（翻訳 → OCR）を保つためにこちらへ置いた。</para>
///
/// <para><b>判定は「繋がない」側へ倒す。</b>間違え方の重さが対称でない。</para>
/// <list type="table">
///   <item><term>繋ぎ落とし</term>
///     <description>いまと同じ状態。その文の訳が悪いだけ</description></item>
///   <item><term>繋ぎすぎ</term>
///     <description>無関係な文がつながり、<b>オーバーレイの枠も一体化して画面を広く覆う</b>。いまより悪い</description></item>
/// </list>
/// <para>段落のまとめ（<c>TextAggregator</c>）より条件が厳しいのは意図的。
/// 段落は<b>並び順</b>を決めるためのもので、広めにまとめても並びは変わらないため害がない。
/// こちらは<b>1 つの文かどうか</b>を決めるためのもの。</para>
///
/// <para><b>しきい値は 2026-09-27 に実サンプル 20 枚・320 行で決めた。</b>
/// OcrBench の第 3 引数で認識結果を箱の座標つきで落とし、条件を変えながら突き合わせた
/// （<c>tools/ocr-eval/README.md</c>）。</para>
/// </remarks>
public static class LineJoiner
{
    /// <summary>
    /// 折り返しとみなす縦の隙間（文字の高さに対する倍率）。
    /// </summary>
    /// <remarks>
    /// <para><b>UI の別項目と段落の折り返しを分ける、いちばんよく効く条件。</b>
    /// OCR の枠は文字の上下に余白を含むため、<b>段落の中の行はほぼ接するか重なる</b>。
    /// 別々の UI 項目には余白があって離れる。</para>
    /// <para>20 枚での実測。</para>
    /// <list type="bullet">
    ///   <item>正しい折り返し 24 件: <b>-0.26 〜 +0.16</b></item>
    ///   <item>別項目（太吾絵巻のメニュー 7 件・サクナヒメの話者名 1 件）: <b>+0.25 〜 +0.91</b></item>
    /// </list>
    /// <para>0.2 にすると、中国語のメニュー 5 項目が 1 つに繋がる誤りが消え、
    /// 正しい折り返しは 1 件も落ちなかった。
    /// <c>TextAggregator.ParagraphGapFactor</c>（1.0）とは目的が違うため値も違う。</para>
    /// </remarks>
    public const double VerticalGapFactor = 0.2;

    /// <summary>
    /// 同じ行の続きとみなす横の隙間（文字の高さに対する倍率）。
    /// </summary>
    /// <remarks>
    /// OCR が 1 行を複数の箱に割ったものを戻すためのしきい値。
    /// <c>TextAggregator.SameLineGapFactor</c>（2.0）より狭い。
    /// あちらは並び順を決めるだけなので広くて構わないが、ここは繋ぎすぎると害が出る。
    /// </remarks>
    public const double SameRowGapFactor = 1.0;

    /// <summary>
    /// 同じ大きさの文字とみなす高さの比。
    /// </summary>
    /// <remarks>
    /// 見出しやルビは本文と大きさが違う。
    /// 同じ段落の折り返しなら、高さの差は文字の出入り（<c>g</c> <c>y</c> の下、大文字の上）による
    /// 数 px に収まり、実測では 0.76 以上だった。
    /// </remarks>
    public const double HeightRatio = 0.7;

    /// <summary>
    /// 同じ段落の行とみなす左端のずれ（文字の高さに対する倍率）。
    /// </summary>
    /// <remarks>
    /// <para><b>折り返した行は左端が揃う。</b>これが列や見出しと分ける条件。
    /// 実測では正しい折り返しのずれは 0.00〜0.47 で、
    /// ワザ名と説明文（<c>Charged Web</c> → <c>If your opponent's…</c>）は 4.23 離れていた。</para>
    /// <para>1.0 まで許すのは、箇条書きの続きが記号の分だけ下がるため
    /// （<c>* Someone ought to teach</c> → <c>you how things work</c> で 0.89）。</para>
    /// </remarks>
    public const double LeftTolerance = 1.0;

    /// <summary>
    /// 中央揃えの行とみなす中心のずれ（文字の高さに対する倍率）。
    /// </summary>
    /// <remarks>
    /// 中央揃えの文章は<b>最後の行だけ左端が揃わない</b>。
    /// Pokémon TCG Live の「Welcome to the Learning Lab!…」では、
    /// 最後の <c>Card Game!</c> の左端が 3.89 ずれていた（中心は揃っている）。
    /// セリフを中央揃えで出すゲームは多く、落とすと文末が切れたまま訳される。
    /// </remarks>
    public const double CenterTolerance = 1.0;

    /// <summary>
    /// 続きの行を 1 つにまとめる。
    /// </summary>
    /// <param name="lines">
    /// OCR の行。<b>読み順に並べてから渡すこと</b>（<c>TextAggregator.InReadingOrder</c>）。
    /// 並びが崩れていると文の続きが隣り合わないため繋げない。
    /// </param>
    /// <returns>
    /// まとめた結果。繋がなかった行はそのまま同じ要素を返す。
    /// 繋いだ行は <c>Box</c> が元の行すべてを囲む矩形、
    /// <c>SourceLineCount</c> が元の行数になる。
    /// </returns>
    public static List<OcrLine> Join(IReadOnlyList<OcrLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var blocks = new List<List<OcrLine>>();

        // ブロックの外接矩形。折り返しの判定は直前の 1 行ではなくこちらと比べる。
        // 1 行が複数の箱に割れていると、直前の箱の左端は行の途中を指すため
        // （クロノクロスの `sad,`）、左端が揃っているかを見誤る。
        var boxes = new List<OcrBox>();

        foreach (var line in lines)
        {
            if (blocks.Count > 0 && Continues(boxes[^1], blocks[^1][^1], line))
            {
                blocks[^1].Add(line);
                boxes[^1] = Union(boxes[^1], line.Box);
            }
            else
            {
                blocks.Add([line]);
                boxes.Add(line.Box);
            }
        }

        return [.. blocks.Select(Merge)];
    }

    /// <summary>
    /// <paramref name="candidate"/> が、いま組み立てているブロックの続きか。
    /// </summary>
    /// <param name="blockBox">ブロックがここまでに占めている矩形。</param>
    /// <param name="previous">ブロックの最後の行。</param>
    /// <param name="candidate">続きかどうかを調べる行。</param>
    /// <remarks>すべての条件を満たしたときだけ繋ぐ。ひとつでも怪しければ繋がない。</remarks>
    public static bool Continues(OcrBox blockBox, OcrLine previous, OcrLine candidate)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);

        // 翻訳に回さない行（数値・記号だけ）は文の一部として扱わない。
        // 繋ぐと数値が翻訳に回り、LLM が書き換える機会を作ってしまう（RULES.md 🟡-7）。
        // 判定を LineTranslation と共有しているのは、除外の規則がずれないようにするため。
        if (!LineTranslation.NeedsTranslation(previous.Text)
            || !LineTranslation.NeedsTranslation(candidate.Text))
        {
            return false;
        }

        // 文が終わっていれば繋がない。句点の後ろが別の文なら、切れていても訳は壊れない。
        if (EndsSentence(previous.Text) || StartsWithMarker(candidate.Text))
        {
            return false;
        }

        // 見出しは、前にあっても後ろにあっても繋がない。
        if (IsHeading(previous.Text) || IsHeading(candidate.Text))
        {
            return false;
        }

        var height = Math.Max(previous.Box.Height, candidate.Box.Height);

        // 文字の大きさが揃っていること（見出し・ルビと本文を分ける）。
        if (Math.Min(previous.Box.Height, candidate.Box.Height) < height * HeightRatio)
        {
            return false;
        }

        return IsSameRow(previous.Box, candidate.Box)
            ? ContinuesOnSameRow(previous.Box, candidate.Box, height)
            : ContinuesBelow(blockBox, candidate.Box, height);
    }

    /// <summary>2 つの箱が同じ高さにあるか（中心の差が高さの半分未満）。</summary>
    /// <remarks><c>TextAggregator</c> の行のまとめ方と同じ判定。</remarks>
    private static bool IsSameRow(OcrBox a, OcrBox b) =>
        Math.Abs((a.Y + (a.Height / 2.0)) - (b.Y + (b.Height / 2.0)))
        < Math.Max(a.Height, b.Height) * 0.5;

    /// <summary>同じ行の中で、<paramref name="b"/> が <paramref name="a"/> の続きか。</summary>
    private static bool ContinuesOnSameRow(OcrBox a, OcrBox b, double height)
    {
        var gap = b.X - a.Right;

        // 箱が重なることもあるため、負の隙間も許す。
        return gap >= -height && gap <= height * SameRowGapFactor;
    }

    /// <summary>
    /// <paramref name="b"/> がブロックの次の行（折り返し）か。
    /// </summary>
    /// <remarks>
    /// 左端が揃っているか、中央揃えの最終行に見えるかのどちらか。
    /// </remarks>
    private static bool ContinuesBelow(OcrBox block, OcrBox b, double height)
    {
        // 真下にあること。上にある行は続きではない。
        if (b.Y < block.Y)
        {
            return false;
        }

        // 離れていれば別の項目。
        if (b.Y - block.Bottom > height * VerticalGapFactor)
        {
            return false;
        }

        // 左端が揃っている（左揃えの段落）。
        if (Math.Abs(b.X - block.X) <= height * LeftTolerance)
        {
            return true;
        }

        // 中央揃えの最終行。ブロックより狭く、内側に収まり、中心が揃っていること。
        // 「狭く、内側」を求めるのは、見出しの下に広い説明文が来た場合
        // （Charged Web → If your opponent's…）を中心の一致だけで拾わないため。
        if (b.Width >= block.Width || b.X < block.X || b.Right > block.Right)
        {
            return false;
        }

        return Math.Abs((block.X + (block.Width / 2.0)) - (b.X + (b.Width / 2.0)))
            <= height * CenterTolerance;
    }

    /// <summary>
    /// 文がここで終わっているか。
    /// </summary>
    /// <remarks>
    /// <para>句点・感嘆符・疑問符に加えて、<b>コロンと閉じ括弧も終わり</b>として扱う。
    /// <c>ENERGY COST:</c> のようなラベルの後ろは値であって文の続きではなく、
    /// <c>(2 turns)</c> のような補足もその行で閉じている。
    /// クロノクロスの話者名 <c>Razzly:</c> がセリフに飲み込まれないのもこの条件。</para>
    /// <para><c>etc.</c> や <c>Dr.</c> のような略記も終わりと見てしまうが、
    /// 繋ぎ落としは害が小さいため放置する。</para>
    /// </remarks>
    private static bool EndsSentence(string text)
    {
        var trimmed = text.AsSpan().TrimEnd();
        return trimmed.Length > 0 && SentenceEnds.Contains(trimmed[^1]);
    }

    /// <summary>箇条書きの記号で始まるか。前の行の続きではなく次の項目。</summary>
    /// <remarks>
    /// アンダーテイルのセリフは <c>*</c> で始まる。続きの行には付かないため、
    /// <c>* Howdy!</c> と <c>* I'm FLOWEY.</c> が 1 文にまとまらない。
    /// </remarks>
    private static bool StartsWithMarker(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.Length == 0 || ItemMarkers.Contains(trimmed[0]);
    }

    /// <summary>
    /// 見出し・ラベルに見えるか（大文字だけで書かれているか）。
    /// </summary>
    /// <remarks>
    /// <para>本文と同じ大きさで描かれた見出しは高さの比では分けられないため、ここで分ける。
    /// 2026-09-27 の計測で、これが無いと次のように見出しが説明文に飲み込まれた。</para>
    /// <code>
    /// DAILY QUEST / Attach 1 Tool to your Pokémon
    /// PRESSURE TOLERANCE / Highly pressurized air is destroying your lungs.
    /// </code>
    /// <para><b>前の行でも次の行でも、見出しがあれば繋がない。</b>
    /// 見出しは訳す価値があるので落とさず、独立した 1 件として残す。</para>
    /// <para>説明文を全部大文字で描くゲームでは繋がらなくなるが、
    /// 繋ぎ落としに倒す方針どおり許容する。
    /// 漢字・かなは大文字小文字を持たないため、この判定にかからない。</para>
    /// </remarks>
    private static bool IsHeading(string text)
    {
        var hasUpper = false;
        foreach (var c in text)
        {
            if (char.IsLower(c))
            {
                return false;
            }

            hasUpper |= char.IsUpper(c);
        }

        return hasUpper;
    }

    /// <summary>行をまとめて 1 つにする。1 行だけならそのまま返す。</summary>
    private static OcrLine Merge(List<OcrLine> members)
    {
        if (members.Count == 1)
        {
            return members[0];
        }

        var builder = new StringBuilder();
        var characters = new List<RecognizedCharacter>();
        var box = members[0].Box;

        // 行の信頼度を単純に平均すると、1 文字の行と 40 文字の行が同じ重みになる。
        // 文字数で重みを付けて、ブロック全体の平均に合わせる。
        var weighted = 0.0;
        var length = 0;

        foreach (var member in members)
        {
            var text = member.Text.Trim();
            if (builder.Length > 0 && NeedsSpace(builder[^1], text[0]))
            {
                builder.Append(' ');
            }

            builder.Append(text);

            // 区切りに入れた空白は認識した文字ではないため、ここには入れない。
            characters.AddRange(member.Characters);

            weighted += member.Confidence * text.Length;
            length += text.Length;

            box = Union(box, member.Box);
        }

        return new OcrLine(builder.ToString(), length == 0 ? 0f : (float)(weighted / length), box)
        {
            Characters = characters,
            SourceLineCount = members.Count,
        };
    }

    /// <summary>2 つの矩形を囲む矩形。</summary>
    private static OcrBox Union(OcrBox a, OcrBox b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        return new OcrBox(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    /// <summary>行の継ぎ目に空白を入れるか。</summary>
    /// <remarks>
    /// 英語は単語の区切りが空白なので入れる。
    /// <b>日本語・中国語には入れない。</b>「勝利の / 条件」を「勝利の 条件」にすると、
    /// 分かち書きしない言語では不自然な入力になる。句読点や閉じ括弧の前にも入れない。
    /// <b>韓国語には入れる</b>（語を空白で区切る言語。<see cref="Scripts.JoinsWithoutSpace"/>）。
    /// </remarks>
    private static bool NeedsSpace(char left, char right) =>
        !Scripts.JoinsWithoutSpace(left) && !Scripts.JoinsWithoutSpace(right) && !NoSpaceBefore.Contains(right);

    private const string SentenceEnds = ".!?:;…。！？：；)]}）］｝」』";

    private const string ItemMarkers = "*•‣・-–—>＞";

    private const string NoSpaceBefore = ".,!?;:)]}'\"";
}
