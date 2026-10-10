using Glosslay.Capture;
using Glosslay.Configuration;

namespace Glosslay.Monitoring;

/// <summary>カーソル位置モードの発動条件（FR-MOD-10）。</summary>
public enum CursorTrigger
{
    /// <summary>
    /// カーソルが止まったら発動する。<b>SPEC.md FR-MOD-10 が定める既定。</b>
    /// </summary>
    Dwell,

    /// <summary>ホットキーを押したときだけ発動する。</summary>
    Hotkey,
}

/// <summary>
/// カーソル位置モードの設定（FR-MOD-10 / 11 / 12 / 14）。
/// </summary>
/// <remarks>
/// <c>%APPDATA%\Glosslay\cursormode.json</c> を読む（RULES.md 🔵）。
/// </remarks>
public sealed record CursorModeOptions
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "cursormode.json";

    /// <summary>
    /// カーソル位置モードを使うか（FR-MOD-14）。
    /// </summary>
    /// <remarks>
    /// <b>カーソルを使わないゲーム向けに切れるようにする</b>のが FR-MOD-14 の趣旨。
    /// パッドだけで完結する操作ではカーソルが動かないか、意味を持たない。
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 何をきっかけに発動するか（FR-MOD-10）。
    /// </summary>
    /// <remarks>
    /// <para><b>既定は <see cref="CursorTrigger.Dwell"/></b>（SPEC.md FR-MOD-10）。
    /// カードに合わせて説明を読む、という操作にそのまま乗る形。</para>
    /// <para>⚠️ <b>既定では、押さなくても翻訳が走る。</b>
    /// RULES.md 🟡-8（予期しない課金）に触れるため、SPEC.md は
    /// 「カーソルの移動に<b>常時追従してはならない</b>。静止またはホットキーを条件とし、
    /// FR-MOD-04 の上限と FR-TRN-09 のキャッシュを<b>併用する</b>」と定めている。
    /// <b>この 3 つを外さないこと。</b></para>
    /// </remarks>
    public CursorTrigger Trigger { get; init; } = CursorTrigger.Dwell;

    /// <summary>
    /// 止まったと判定するまでの時間（FR-MOD-11）。
    /// </summary>
    /// <remarks>
    /// 既定 400ms。短くすると、通り過ぎただけのカードでも翻訳が走る。
    /// 長くすると、読みたいカードの上で待たされる。
    /// </remarks>
    public TimeSpan DwellTime { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// 止まったとみなす移動量（画素）。
    /// </summary>
    /// <remarks>
    /// <b>手は完全には止まらない。</b>0 にすると永久に発動しない。
    /// 仕様には無いが、静止判定には必ず要る値。
    /// </remarks>
    public int JitterTolerance { get; init; } = 4;

    /// <summary>読み取る範囲の幅（FR-MOD-12）。</summary>
    /// <remarks>
    /// <b>既定 640x480 は暫定値</b>（PLAN.md 課題3）。1 枚のカード画像から導いたもので、
    /// 実データを取り直して決め直すことになっている。
    /// <b>幅が足りないと数値が切れる</b>（`110+` → `1` の実例）。
    /// </remarks>
    public int Width { get; init; } = CursorRegion.DefaultWidth;

    /// <summary>読み取る範囲の高さ（FR-MOD-12）。</summary>
    public int Height { get; init; } = CursorRegion.DefaultHeight;

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static CursorModeOptions Load() => JsonSettings.Load(FileName, () => new CursorModeOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
