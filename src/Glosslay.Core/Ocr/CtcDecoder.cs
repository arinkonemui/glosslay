using System.Text;

namespace Glosslay.Ocr;

/// <summary>認識した 1 文字と、その文字を選んだときの確率。</summary>
/// <param name="Text">文字。辞書に BMP 外の文字が含まれるため <c>char</c> では持てない。</param>
/// <param name="Confidence">選んだ文字の確率 0.0〜1.0。</param>
/// <remarks>
/// <para>行全体の平均は、正しく読めた周囲の文字に引っ張られて高いままになる。
/// どこが怪しいかは文字ごとの値でしか見えないため、計測用に残す（PLAN.md 課題6）。</para>
/// <para><b>この値で誤読を機械的に判定することはできない。</b>
/// 正常な文字が 0.39 まで下がる一方、アイコンを誤読した「4」は前処理次第で 0.94 まで上がり、
/// 分布が重なる。2026-09-21 に 13,878 文字で計測した結果。</para>
/// </remarks>
public readonly record struct RecognizedCharacter(string Text, float Confidence);

/// <summary>
/// 認識モデルの出力（クラスごとの確率列）を文字列へ変換する。
/// </summary>
/// <remarks>
/// PaddleOCR の <c>CTCLabelDecode</c> と同じ貪欲デコード。
/// 各時刻で最大確率のクラスを取り、連続する同一クラスを 1 つに畳み、blank を捨てる。
/// この手順が正しいことは Python 側の出力と突き合わせて確認済み
/// （<c>tools/ocr-eval/verify_rec_contract.py</c>）。
/// </remarks>
public sealed class CtcDecoder(CharacterSet characterSet)
{
    private readonly CharacterSet _characterSet =
        characterSet ?? throw new ArgumentNullException(nameof(characterSet));

    /// <summary>
    /// 1 行分の出力をデコードする。
    /// </summary>
    /// <param name="logits">形状 <c>[timeSteps, classCount]</c> の確率列。</param>
    /// <param name="timeSteps">時刻数。</param>
    /// <param name="classCount">クラス数。</param>
    /// <returns>認識文字列、採用した時刻の平均確率、文字ごとの確率。</returns>
    public (string Text, float Confidence, IReadOnlyList<RecognizedCharacter> Characters) Decode(
        ReadOnlySpan<float> logits, int timeSteps, int classCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeSteps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(classCount);

        if (logits.Length < (long)timeSteps * classCount)
        {
            throw new ArgumentException(
                $"出力の要素数が足りません。期待 {(long)timeSteps * classCount}、実際 {logits.Length}。",
                nameof(logits));
        }

        var builder = new StringBuilder(timeSteps);
        var characters = new List<RecognizedCharacter>(timeSteps);
        var scoreSum = 0.0;
        var previousClass = -1;

        for (var t = 0; t < timeSteps; t++)
        {
            var row = logits.Slice(t * classCount, classCount);

            var bestClass = 0;
            var bestScore = row[0];
            for (var c = 1; c < classCount; c++)
            {
                if (row[c] > bestScore)
                {
                    bestScore = row[c];
                    bestClass = c;
                }
            }

            // 連続する同一クラスは 1 文字に畳む。blank は出力しない。
            if (bestClass != previousClass && bestClass != CharacterSet.BlankClass)
            {
                var character = _characterSet.TryGetCharacter(bestClass);
                if (character is not null)
                {
                    builder.Append(character);
                    characters.Add(new RecognizedCharacter(character, bestScore));
                    scoreSum += bestScore;
                }
            }

            previousClass = bestClass;
        }

        var confidence = characters.Count == 0 ? 0f : (float)(scoreSum / characters.Count);
        return (builder.ToString(), confidence, characters);
    }
}
