namespace Glosslay.Translation;

/// <summary>文字の種類の判定。</summary>
/// <remarks>
/// 行結合の継ぎ目（空白を入れるか）と、ローカルNMT が扱える原文かの判定で使う。
/// 同じ範囲表を 2 か所に置くと必ず食い違うため、ここだけに書く。
/// </remarks>
internal static class Scripts
{
    /// <summary>分かち書きをしない文字か（漢字・かな・ハングル・全角記号）。</summary>
    internal static bool IsWide(char c) => c
        is >= '\u1100' and <= '\u11FF'      // ハングル字母
        or >= '\u3000' and <= '\u303F'      // CJK の記号・句読点
        or >= '\u3040' and <= '\u30FF'      // ひらがな・カタカナ
        or >= '\u3130' and <= '\u318F'      // ハングル互換字母
        or >= '\u3400' and <= '\u4DBF'      // CJK 統合漢字拡張A
        or >= '\u4E00' and <= '\u9FFF'      // CJK 統合漢字
        or >= '\uAC00' and <= '\uD7AF'      // ハングル音節
        or >= '\uF900' and <= '\uFAFF'      // CJK 互換漢字
        or >= '\uFF00' and <= '\uFFEF';     // 全角・半角形

    /// <summary>ハングルか（音節・字母・互換字母）。</summary>
    internal static bool IsHangul(char c) => c
        is >= '\uAC00' and <= '\uD7AF'      // ハングル音節
        or >= '\u1100' and <= '\u11FF'      // ハングル字母
        or >= '\u3130' and <= '\u318F';     // ハングル互換字母

    /// <summary>かなか（ひらがな・カタカナ）。</summary>
    internal static bool IsKana(char c) => c is >= '\u3040' and <= '\u309F' or >= '\u30A0' and <= '\u30FF';

    /// <summary>漢字か（CJK 統合漢字・拡張A・互換漢字）。</summary>
    internal static bool IsHan(char c) =>
        c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' or >= '\uF900' and <= '\uFAFF';

    /// <summary>
    /// 前後に空白を入れずに繋ぐ文字か（漢字・かな・全角記号）。
    /// </summary>
    /// <remarks>
    /// <b>ハングルは含めない。</b>韓国語は語を空白で区切る言語で、詰めると単語がくっつく
    /// （「받을 수」＋「있습니다」→「받을 수있습니다」）。<see cref="IsWide"/> はハングルを含む
    /// （英語の行かを数えるため）ので、継ぎ目の判断には使わない（2026-10-25・SPEC.md FR-OCR-02）。
    /// </remarks>
    internal static bool JoinsWithoutSpace(char c) => IsWide(c) && !IsHangul(c);

    /// <summary>
    /// 英→日のモデルに渡せる行か（ラテン文字が主体か）。
    /// </summary>
    /// <remarks>
    /// <para><b>中国語・日本語の行を英→日のモデルに入れると、流暢な出鱈目が出る。</b>
    /// 訳せなかったと分かる形ではなく、もっともらしい日本語になるため気付けない。
    /// RULES.md 🟡-1 の承認済みの例外（中国語はAPIキーが要る）を実装で守るための判定。</para>
    /// <para>文字だけを数え、<b>数値・記号しかない行は「訳せる」側に倒す</b>
    /// （そういう行はそもそも翻訳に回らない — <c>LineTranslation.NeedsTranslation</c>）。</para>
    /// </remarks>
    internal static bool IsLatinText(string text)
    {
        var latin = 0;
        var wide = 0;

        foreach (var c in text)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            if (IsWide(c))
            {
                wide++;
            }
            else
            {
                // アクセント付きラテン文字（Pokémon の é など）もこちら。
                latin++;
            }
        }

        return wide == 0 || latin > wide;
    }
}
