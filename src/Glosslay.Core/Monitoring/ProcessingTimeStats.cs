using System.Globalization;
using static Glosslay.Localization.UiText;

namespace Glosslay.Monitoring;

/// <summary>
/// 翻訳 1 回ごとにかかった時間を覚えておき、直近の平均と最大を出す（FR-OVL-10）。
/// </summary>
/// <remarks>
/// <para><b>利用者が自分のパソコンで判断するための実測値。</b>自動監視を入れるかどうかは利用者が決める
/// （FR-MOD-07）。判断の材料は文章の警告ではなく、その人の環境で測った数字で渡す（SPEC.md FR-MOD-08）。</para>
/// <para><b>利用者向けの文に計測の数字を出さない方針の例外</b>（2026-10-14・人間の判断）。
/// 利用者が自分で入れたときだけ出る、その人の環境の実測値なので、案内文の一致度や件数とは性質が違う。</para>
/// <para><b>直近の回数だけを見る。</b>起動直後はモデルの読み込みで遅く、ずっと平均すると
/// いまの速さが見えない。</para>
/// </remarks>
public sealed class ProcessingTimeStats
{
    /// <summary>覚えておく回数の既定。</summary>
    public const int DefaultCapacity = 10;

    private readonly Queue<TimeSpan> _reads = new();
    private readonly Queue<TimeSpan> _translations = new();

    /// <summary>直近何回分を見るか。</summary>
    /// <param name="capacity">回数。1 以上。</param>
    public ProcessingTimeStats(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    /// <summary>直近何回分を見るか。</summary>
    public int Capacity { get; }

    /// <summary>覚えている読み取りの回数。</summary>
    public int ReadCount => _reads.Count;

    /// <summary>覚えている翻訳の回数。</summary>
    public int TranslationCount => _translations.Count;

    /// <summary>読み取りの平均。まだ無ければ <c>null</c>。</summary>
    public TimeSpan? ReadAverage => Average(_reads);

    /// <summary>直近の読み取りで最も遅かったもの。</summary>
    public TimeSpan? ReadRecentMax => _reads.Count == 0 ? null : _reads.Max();

    /// <summary>翻訳の平均。まだ無ければ <c>null</c>。</summary>
    public TimeSpan? TranslationAverage => Average(_translations);

    /// <summary>
    /// 1 回分を記録する。
    /// </summary>
    /// <param name="read">読み取り（OCR）にかかった時間。</param>
    /// <param name="translation">
    /// 翻訳にかかった時間。文字が見つからず翻訳しなかった場合は <c>null</c>
    /// （0 として数えると平均が実際より速く見える）。
    /// </param>
    public void Record(TimeSpan read, TimeSpan? translation)
    {
        Push(_reads, read);
        if (translation is { } value)
        {
            Push(_translations, value);
        }
    }

    /// <summary>
    /// 画面の隅に出す 1 行。例: <c>読み取り 平均 560ms / 直近最大 900ms ・ 翻訳 平均 280ms</c>。
    /// </summary>
    public string OneLine
    {
        get
        {
            if (ReadAverage is not { } readAverage || ReadRecentMax is not { } readMax)
            {
                return T("処理時間: まだ翻訳していません");
            }

            var line = T("読み取り 平均 {0} / 直近最大 {1}", Milliseconds(readAverage), Milliseconds(readMax));
            return TranslationAverage is { } translation
                ? line + T(" ・ 翻訳 平均 {0}", Milliseconds(translation))
                : line;
        }
    }

    private void Push(Queue<TimeSpan> queue, TimeSpan value)
    {
        queue.Enqueue(value < TimeSpan.Zero ? TimeSpan.Zero : value);
        while (queue.Count > Capacity)
        {
            queue.Dequeue();
        }
    }

    private static TimeSpan? Average(Queue<TimeSpan> queue) =>
        queue.Count == 0 ? null : TimeSpan.FromTicks((long)queue.Average(t => t.Ticks));

    private static string Milliseconds(TimeSpan value) =>
        value.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms";
}
