namespace Glosslay.Monitoring;

/// <summary>
/// 一定時間あたりの呼び出し回数に上限をかける（FR-MOD-04 / RULES.md 🟡-8）。
/// </summary>
/// <remarks>
/// <para><b>自動監視のための歯止め。</b>手動のホットキーは利用者が押した回数だけなので数えない。
/// 自動監視は<b>勝手に呼び出し続ける</b>ため、暴走したときに止まるものが要る
/// （RULES.md 🟡-8「自動監視の呼び出し回数に上限（既定で有効）」）。</para>
///
/// <para><b>キャッシュに当たった回も数える。</b>当たれば費用はかからないが、
/// <b>上限の目的は暴走を止めること</b>であって費用の精算ではない。
/// 区別すると「キャッシュに当たり続ける限り無限に回る」状態を許すことになる。</para>
///
/// <para><b>直近 1 時間の移動窓</b>で数える。1 時間ごとに一斉に戻る形にすると、
/// 境目で一気に使い切れてしまう。</para>
/// </remarks>
public sealed class CallRateLimiter
{
    private readonly Queue<DateTimeOffset> _taken = new();
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    /// <summary>
    /// 既定の上限（1 時間あたり）。
    /// </summary>
    /// <remarks>
    /// <para><b>根拠のある数ではなく、暴走を止めるための歯止め。</b>
    /// SPEC.md §13 が「Gemini の現行モデル名と無料枠のレート制限」を<b>未確認</b>としており、
    /// 合わせるべき外部の数値がまだ無い。</para>
    /// <para>200 回/時 は平均して 18 秒に 1 回。セリフを追う用途には十分で、
    /// 1 秒ごとに呼び出す暴走（3600 回/時）は確実に止まる。
    /// <b>無料枠の実測が取れたら見直すこと。</b></para>
    /// </remarks>
    public const int DefaultMaxCalls = 200;

    /// <summary>既定の窓。</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    /// <param name="maxCalls">窓あたりの上限。</param>
    /// <param name="window">数える幅。</param>
    /// <param name="timeProvider">時刻の取得元。<b>検査で時間を進めるために差し替える。</b></param>
    public CallRateLimiter(
        int maxCalls = DefaultMaxCalls, TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCalls);

        MaxCalls = maxCalls;
        Window = window ?? DefaultWindow;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Window.Ticks);

        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>窓あたりの上限。</summary>
    public int MaxCalls { get; }

    /// <summary>数える幅。</summary>
    public TimeSpan Window { get; }

    /// <summary>いま使える残り回数。</summary>
    public int Remaining
    {
        get
        {
            lock (_gate)
            {
                Trim(_time.GetUtcNow());
                return MaxCalls - _taken.Count;
            }
        }
    }

    /// <summary>
    /// 1 回ぶん使う。上限に達していれば <c>false</c> を返し、<b>消費しない</b>。
    /// </summary>
    public bool TryTake()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Trim(now);

            if (_taken.Count >= MaxCalls)
            {
                return false;
            }

            _taken.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// 次に 1 回使えるようになるまでの時間。いま使えるなら <see cref="TimeSpan.Zero"/>。
    /// </summary>
    /// <remarks>利用者に「あと何分で戻る」と伝えるため（RULES.md 🔵）。</remarks>
    public TimeSpan TimeUntilNext
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                Trim(now);

                if (_taken.Count < MaxCalls)
                {
                    return TimeSpan.Zero;
                }

                var oldest = _taken.Peek();
                var wait = oldest + Window - now;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }
    }

    /// <summary>数えた記録を捨てる。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _taken.Clear();
        }
    }

    /// <summary>窓から外れた記録を落とす。<b>呼び出し側で lock 済みであること。</b></summary>
    private void Trim(DateTimeOffset now)
    {
        var limit = now - Window;
        while (_taken.Count > 0 && _taken.Peek() <= limit)
        {
            _taken.Dequeue();
        }
    }
}
