using Glosslay.Capture;

namespace Glosslay.Monitoring;

/// <summary>
/// カーソルが止まったことを見つける（FR-MOD-10 / FR-MOD-11）。
/// </summary>
/// <remarks>
/// <para><b>カーソルの移動に常時追従してはならない</b>（SPEC.md のカーソル位置モードの補足 /
/// RULES.md 🟡-8）。動いている間は何も起こさず、<b>止まってから一定時間</b>で 1 回だけ起こす。</para>
///
/// <para><b>同じ場所で何度も起こさない。</b>止まり続けている間に繰り返し発動すると、
/// カードの上にカーソルを置いたまま考えているだけで呼び出しが積み上がる。
/// 一度起こしたら、<b>カーソルが動いて落ち着き直すまで</b>次は起こさない。</para>
///
/// <para>画面から切り離してあるのは、<b>時間に依存する判定を目で確かめるのが難しい</b>ため。
/// 時計を差し替えて検査する（<c>NmtBench --dwell</c>）。</para>
/// </remarks>
public sealed class CursorDwellDetector
{
    private readonly CursorModeOptions _options;
    private readonly TimeProvider _time;

    private ScreenPoint _anchor;
    private DateTimeOffset _since;
    private bool _hasAnchor;
    private bool _fired;

    public CursorDwellDetector(CursorModeOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new CursorModeOptions();
        _time = timeProvider ?? TimeProvider.System;

        ArgumentOutOfRangeException.ThrowIfNegative(_options.JitterTolerance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.DwellTime.Ticks);
    }

    /// <summary>
    /// いまのカーソル位置を渡す。<b>止まったと判定した瞬間だけ <c>true</c>。</b>
    /// </summary>
    public bool Update(ScreenPoint position)
    {
        var now = _time.GetUtcNow();

        if (!_hasAnchor || HasMoved(position))
        {
            // 動いた。数え直し、次の発動を許す。
            _anchor = position;
            _since = now;
            _hasAnchor = true;
            _fired = false;
            return false;
        }

        if (_fired || now - _since < _options.DwellTime)
        {
            return false;
        }

        _fired = true;
        return true;
    }

    /// <summary>
    /// 覚えている位置を捨てる。
    /// </summary>
    /// <remarks>
    /// モードを切り替えたときや、対象を変えたときに呼ぶ。
    /// <b>直後に即発動しない</b>よう、次の <see cref="Update"/> は必ず数え直しから始まる。
    /// </remarks>
    public void Reset()
    {
        _hasAnchor = false;
        _fired = false;
    }

    /// <summary>
    /// 止まったとみなす範囲を超えて動いたか。
    /// </summary>
    /// <remarks>
    /// 斜めの動きを正しく扱うため、縦横の差を別々ではなく<b>距離</b>で見る。
    /// 平方根は取らずに二乗のまま比べる（誤差も計算も減る）。
    /// </remarks>
    private bool HasMoved(ScreenPoint position)
    {
        var dx = position.X - _anchor.X;
        var dy = position.Y - _anchor.Y;
        var tolerance = _options.JitterTolerance;
        return ((long)dx * dx) + ((long)dy * dy) > (long)tolerance * tolerance;
    }
}
