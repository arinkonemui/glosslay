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
