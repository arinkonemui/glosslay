using System.Text;

namespace Glosslay.Ocr;

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
    /// <returns>認識文字列と、採用した時刻の平均確率。</returns>
    public (string Text, float Confidence) Decode(
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
        var scoreSum = 0.0;
        var scoreCount = 0;
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
                    scoreSum += bestScore;
                    scoreCount++;
                }
            }

            previousClass = bestClass;
        }

        var confidence = scoreCount == 0 ? 0f : (float)(scoreSum / scoreCount);
        return (builder.ToString(), confidence);
    }
}
