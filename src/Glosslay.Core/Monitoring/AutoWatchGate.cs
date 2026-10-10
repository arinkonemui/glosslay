using Glosslay.Imaging;

namespace Glosslay.Monitoring;

/// <summary>自動監視が 1 回ぶんを見たときの判断。</summary>
public enum AutoWatchDecision
{
    /// <summary>翻訳する。</summary>
    Translate,

    /// <summary>最初のフレーム。比べる相手がないので翻訳しない。</summary>
    FirstFrame,

    /// <summary>変化がない（FR-OCR-07）。</summary>
    NoChange,

    /// <summary>ゲームが前面にない（FR-MOD-05）。</summary>
    NotForeground,

    /// <summary>呼び出し上限に達した（FR-MOD-04）。</summary>
    RateLimited,
}

/// <summary>
/// 自動監視で「いま翻訳すべきか」を決める（FR-MOD-02 / 04 / 05 / FR-OCR-07）。
/// </summary>
/// <remarks>
/// <para><b>画面から切り離してある。</b>自動監視は RULES.md 🟡-8（予期しない課金）に
/// 正面からぶつかる機能で、守りが効いているかを<b>目で見るのではなく検査で確かめたい</b>。
/// タイマーやキャプチャと絡めると確かめようがなくなる。</para>
///
/// <para><b>止める条件を先に見る。</b>前面でない・変化がない、のいずれかなら
/// 上限を消費しない。消費してから「やっぱりやめる」では歯止めが減るだけになる。</para>
/// </remarks>
public sealed class AutoWatchGate
{
    private readonly AutoWatchOptions _options;
    private readonly CallRateLimiter _limiter;
    private Bgra32Image? _previous;

    public AutoWatchGate(AutoWatchOptions? options = null, CallRateLimiter? limiter = null)
    {
        _options = options ?? new AutoWatchOptions();
        _limiter = limiter ?? new CallRateLimiter(_options.MaxCallsPerHour);
    }

    /// <summary>呼び出し上限の残り（FR-MOD-04）。</summary>
    public int Remaining => _limiter.Remaining;

    /// <summary>次に使えるようになるまでの時間。</summary>
    public TimeSpan TimeUntilNext => _limiter.TimeUntilNext;

    /// <summary>
    /// このフレームで翻訳すべきか。
    /// </summary>
    /// <param name="frame">監視している領域の画像。<b>全画面ではなく指定領域</b>（FR-MOD-02）。</param>
    /// <param name="isForeground">ゲームが前面にあるか（FR-MOD-05）。</param>
    /// <remarks>
    /// <b>翻訳すると決めたときだけ上限を 1 消費する。</b>
    /// 戻り値が <see cref="AutoWatchDecision.Translate"/> 以外なら消費していない。
    /// </remarks>
    public AutoWatchDecision Evaluate(Bgra32Image frame, bool isForeground)
    {
        ArgumentNullException.ThrowIfNull(frame.Pixels);

        if (!isForeground)
        {
            // 前面でない間は比較もしない。裏で変わった分を、戻った瞬間に 1 回で拾えばよい。
            return AutoWatchDecision.NotForeground;
        }

        if (_previous is not { } previous)
        {
            _previous = frame;
            return AutoWatchDecision.FirstFrame;
        }

        if (!FrameDifference.HasChanged(previous, frame, _options.Threshold))
        {
            return AutoWatchDecision.NoChange;
        }

        // 変化はあった。ここから先は「翻訳する」側の判断なので、覚え直す。
        _previous = frame;

        return _limiter.TryTake() ? AutoWatchDecision.Translate : AutoWatchDecision.RateLimited;
    }

    /// <summary>
    /// 覚えているフレームを捨てる。
    /// </summary>
    /// <remarks>
    /// 監視する対象や領域を変えたときに呼ぶ。
    /// 前の対象の画像と比べると、<b>切り替えた直後に必ず「変化あり」になる</b>。
    /// </remarks>
    public void Reset() => _previous = null;
}
