namespace Glosslay.Ocr;

/// <summary>
/// OCR の行を、人が読む順に並べる（SPEC.md §2 の TextAggregator。FR-TRN-10 の土台）。
/// </summary>
/// <remarks>
/// <para><b>段落を崩さないことが最優先。</b>2026-09-18、Tyr の画面で次の不具合を実測した。</para>
/// <code>
/// [15] x=1439 y=220  Generate 2.00 Energy whenever you land after being   ← 右の説明パネル
/// [16] x=1167 y=249  STN-1                                                ← 左のテックツリー
/// [17] x=1439 y=244  airborne                                             ← 右の説明パネル
/// </code>
/// <para>以前は「高さがほぼ同じなら同じ行とみなし、左から並べる」だけだったため、
/// 右パネルの文の後半 <c>airborne</c> と左列の <c>STN-1</c> がたまたま同じ高さにあり、
/// <b>文の途中に別の列の文字が割り込んだ。</b>翻訳側から見ると <c>airborne</c> が孤立し、
/// 「空中」とだけ訳されて意味が抜けた（PLAN.md 問題 E）。
/// 割り込みのなかった画面では、同じ LLM が 2 行へ正しく意味を振り分けていた。</para>
/// <para>そこで 2 段階でまとめる。</para>
/// <list type="number">
///   <item><b>見た目の 1 行</b>: 同じ高さで、横の隙間が文字の高さの <see cref="SameLineGapFactor"/> 倍以内の箱。
///     OCR が 1 行を <c>It's fairy</c> / <c>sad,</c> のように割ったものを戻す</item>
///   <item><b>段落</b>: 前の行のすぐ下（隙間が高さの <see cref="ParagraphGapFactor"/> 倍以内）で、
///     <b>横位置が重なる</b>行。列が違えば横位置が重ならないため、別の段落になる</item>
/// </list>
/// <para>段落は崩さずに、上から、同じ高さなら左から並べる。
/// 「同じ高さ」は段落の<b>先頭行の中心</b>で判定する。上端だけで比べると、
/// <c>ENERGY COST:</c>（上端 510）より 2px 上にある値 <c>30</c>（上端 508）が先に来てしまう。</para>
/// </remarks>
public static class TextAggregator
{
    /// <summary>
    /// 同じ見た目の行とみなす横の隙間（文字の高さに対する倍率）。
    /// </summary>
    /// <remarks>
    /// 単語間の空白は高さの 0.5 倍程度。タブ列（Tyr の PLAY / TANKS）は 10 倍、
    /// 列をまたぐ隙間（<c>STN-1</c> と <c>airborne</c>）は約 10 倍だった。2 倍なら安全側に分かれる。
    /// </remarks>
    public const double SameLineGapFactor = 2.0;

    /// <summary>
    /// 同じ段落とみなす縦の隙間（文字の高さに対する倍率）。
    /// </summary>
    /// <remarks>
    /// OCR の枠は文字の周りに余白を含むため、段落内の行の枠はほぼ接するか重なる
    /// （<c>Generate…</c> の下端 246 と <c>airborne</c> の上端 244）。
    /// 空行 1 つ分までを同じ段落とみなす。別段落をまとめても同じ列なら並びは変わらないため害はない。
    /// </remarks>
    public const double ParagraphGapFactor = 1.0;

    /// <summary>
    /// 読み順に並べる。
    /// </summary>
    /// <param name="lines">OCR の結果。順番は問わない。</param>
    /// <returns>読み順に並べ直した行。要素そのものは変えない。</returns>
    public static List<OcrLine> InReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var visualLines = BuildVisualLines(lines);
        var paragraphs = BuildParagraphs(visualLines);

        // 先頭行が同じ高さの段落を 1 つの帯にまとめ、帯は上から、帯の中は左から並べる。
        var bands = new List<List<List<VisualLine>>>();
        foreach (var paragraph in paragraphs.OrderBy(p => p[0].Top))
        {
            var band = bands.FirstOrDefault(b => IsSameRow(b[0][0], paragraph[0]));
            if (band is null)
            {
                bands.Add([paragraph]);
            }
            else
            {
                band.Add(paragraph);
            }
        }

        return
        [
            .. bands
                .SelectMany(b => b.OrderBy(p => p[0].Left))
                .SelectMany(p => p.SelectMany(v => v.Members)),
        ];
    }

    /// <summary>2 つの見た目の行が同じ高さにあるか（中心の差が高さの半分未満）。</summary>
    private static bool IsSameRow(VisualLine a, VisualLine b)
    {
        var centerA = a.Top + (a.Height / 2.0);
        var centerB = b.Top + (b.Height / 2.0);
        return Math.Abs(centerA - centerB) < Math.Max(a.Height, b.Height) * 0.5;
    }

    /// <summary>同じ高さで横に近い箱を、見た目の 1 行にまとめる。</summary>
    private static List<VisualLine> BuildVisualLines(IReadOnlyList<OcrLine> lines)
    {
        var result = new List<VisualLine>();

        foreach (var line in lines.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X))
        {
            var target = result.FirstOrDefault(v => v.Accepts(line));
            if (target is null)
            {
                result.Add(new VisualLine(line));
            }
            else
            {
                target.Add(line);
            }
        }

        return result;
    }

    /// <summary>縦に近く横位置が重なる行を、段落にまとめる。</summary>
    private static List<List<VisualLine>> BuildParagraphs(List<VisualLine> visualLines)
    {
        var paragraphs = new List<List<VisualLine>>();

        foreach (var line in visualLines.OrderBy(v => v.Top).ThenBy(v => v.Left))
        {
            // 続けられる段落のうち、いちばん近いものに付ける。
            List<VisualLine>? best = null;
            var bestGap = double.MaxValue;

            foreach (var paragraph in paragraphs)
            {
                var last = paragraph[^1];
                var gap = line.Top - last.Bottom;
                var height = Math.Max(line.Height, last.Height);

                var isBelow = line.Top >= last.Top;
                var isClose = gap <= height * ParagraphGapFactor;
                var overlaps = line.Left < last.Right && last.Left < line.Right;

                if (isBelow && isClose && overlaps && gap < bestGap)
                {
                    best = paragraph;
                    bestGap = gap;
                }
            }

            if (best is null)
            {
                paragraphs.Add([line]);
            }
            else
            {
                best.Add(line);
            }
        }

        return paragraphs;
    }

    /// <summary>見た目の 1 行。左から並んだ箱の集まり。</summary>
    private sealed class VisualLine
    {
        private readonly List<OcrLine> _members = [];

        public VisualLine(OcrLine first) => Add(first);

        public IEnumerable<OcrLine> Members => _members.OrderBy(m => m.Box.X);

        public int Left { get; private set; } = int.MaxValue;

        public int Right { get; private set; } = int.MinValue;

        public int Top { get; private set; } = int.MaxValue;

        public int Bottom { get; private set; } = int.MinValue;

        public int Height => Bottom - Top;

        public void Add(OcrLine line)
        {
            _members.Add(line);
            Left = Math.Min(Left, line.Box.X);
            Right = Math.Max(Right, line.Box.Right);
            Top = Math.Min(Top, line.Box.Y);
            Bottom = Math.Max(Bottom, line.Box.Bottom);
        }

        /// <summary>この行の続き（同じ高さで横に近い）か。</summary>
        public bool Accepts(OcrLine line)
        {
            foreach (var member in _members)
            {
                var height = Math.Max(member.Box.Height, line.Box.Height);

                var centerGap = Math.Abs(
                    (member.Box.Y + (member.Box.Height / 2.0)) - (line.Box.Y + (line.Box.Height / 2.0)));
                var sameRow = centerGap < height * 0.5;

                var horizontalGap = Math.Max(member.Box.X, line.Box.X) - Math.Min(member.Box.Right, line.Box.Right);
                var near = horizontalGap <= height * SameLineGapFactor;

                if (sameRow && near)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
