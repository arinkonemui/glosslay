using Glosslay.Configuration;
using Glosslay.Imaging;

namespace Glosslay.Monitoring;

/// <summary>
/// 自動監視モードの設定（FR-MOD-02 / FR-MOD-03）。
/// </summary>
/// <remarks>
/// <c>%APPDATA%\Glosslay\autowatch.json</c> を読む（RULES.md 🔵）。
/// </remarks>
public sealed record AutoWatchOptions
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "autowatch.json";

    /// <summary>
    /// 自動監視を使うか。<b>既定は OFF。</b>
    /// </summary>
    /// <remarks>
    /// <b>勝手に呼び出し続ける機能を、利用者が知らないうちに動かさない</b>（RULES.md 🟡-8）。
    /// PvP ではプロファイル単位で既定 OFF にする（FR-MOD-06）が、
    /// <b>強制はしない</b>（FR-MOD-07）。
    /// </remarks>
    public bool Enabled { get; init; }

    /// <summary>
    /// 画面を見に行く間隔。
    /// </summary>
    /// <remarks>
    /// <para>この間隔で行うのは<b>撮影と差分の比較だけ</b>。変化がなければ OCR も翻訳もしない
    /// （FR-OCR-07）。間引いた比較なので安い。</para>
    /// <para>500ms はセリフ送りに追従できる速さ。短くすると CPU をゲームと奪い合う
    /// （判断基準の 2 位）。</para>
    /// </remarks>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 変化とみなす閾値（0〜1。大きいほど鈍感）。
    /// </summary>
    /// <remarks>
    /// <c>FrameDifference.DefaultThreshold</c> と同じ 0.02。
    /// 小さくすると背景のわずかな動きでも翻訳が走る。
    /// </remarks>
    public double Threshold { get; init; } = FrameDifference.DefaultThreshold;

    /// <summary>1 時間あたりの呼び出し上限（FR-MOD-04）。</summary>
    public int MaxCallsPerHour { get; init; } = CallRateLimiter.DefaultMaxCalls;

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static AutoWatchOptions Load() => JsonSettings.Load(FileName, () => new AutoWatchOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
