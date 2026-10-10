namespace Glosslay.Input;

/// <summary>
/// トリガーが起こす動作。
/// </summary>
/// <remarks>
/// <b>TriggerAction という名前は使えない。</b>WPF の <c>System.Windows.TriggerAction</c> と
/// 衝突し、App 層で毎回あいまい参照になる（2026-10-10 に踏んだ）。
/// </remarks>
public enum InputAction
{
    /// <summary>前面の画面を翻訳する（FR-MOD-01）。</summary>
    TranslateScreen,

    /// <summary>カーソルの周囲を翻訳する（FR-MOD-09〜14）。</summary>
    TranslateCursor,

    /// <summary>オーバーレイの表示を切り替える（FR-OVL-08）。</summary>
    ToggleOverlay,
}

/// <summary>
/// 翻訳を起こす入力（RULES.md 🟡-2）。
/// </summary>
/// <remarks>
/// <para>v1.0 の実装は 2 つ。</para>
/// <list type="bullet">
///   <item><see cref="RawInputTrigger"/> — キーボード（Raw Input）</item>
///   <item><c>GamepadTrigger</c> — Windows.Gaming.Input。<b>読み取りのみ</b>。未実装</item>
/// </list>
///
/// <para>⚠️ <b>実装は RULES.md 🔴-3 を必ず守ること。</b></para>
/// <list type="bullet">
///   <item><c>RegisterHotKey</c> を使わない — キーがゲームに届かなくなる</item>
///   <item><c>WH_KEYBOARD_LL</c> の低レベルフックを使わない — キーロガーと誤検知される</item>
///   <item><b>入力の内容を判定に使ったら捨てる。記録もログ出力もしない</b></item>
/// </list>
/// </remarks>
public interface IInputTrigger : IDisposable
{
    /// <summary>UI に出す名前。</summary>
    string Name { get; }

    /// <summary>使える割り当てが 1 つでもあるか。</summary>
    bool IsActive { get; }

    /// <summary>割り当ての説明。利用者に見せる。</summary>
    IReadOnlyList<string> Descriptions { get; }

    /// <summary>
    /// 設定の誤りなど、利用者に伝えるべきこと。
    /// </summary>
    /// <remarks>
    /// <b>設定の誤りでアプリ全体を止めない。</b>使えない割り当てだけ無効にし、理由をここに積む
    /// （RULES.md 🔵「ユーザーに見せるメッセージは具体的に」）。
    /// </remarks>
    IReadOnlyList<string> Problems { get; }

    /// <summary>割り当てが発動した。</summary>
    event Action<InputAction>? Fired;
}
