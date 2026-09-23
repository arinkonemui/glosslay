using Glosslay.Configuration;

namespace Glosslay.Input;

/// <summary>
/// ホットキーの設定（FR-INP-01「ホットキーは全て変更可能。既定値は衝突しにくい組み合わせ」）。
/// </summary>
/// <remarks>
/// <c>%APPDATA%\Glosslay\hotkeys.json</c> に保存する。設定 UI は v0.5 のため、PoC では JSON を直接編集する。
/// </remarks>
public sealed record HotkeyOptions
{
    /// <summary>
    /// 前面の画面をまとめて翻訳する（FR-MOD-01 手動モード）の既定値。
    /// </summary>
    /// <remarks>
    /// <para>Raw Input はキーを奪わないため、<b>押したキーはゲームにも届く</b>（FR-INP-02）。
    /// 単キーはゲームの操作と重なりやすく、修飾キーの組み合わせにしている。</para>
    /// <para>避けたもの:</para>
    /// <list type="bullet">
    ///   <item><c>F12</c> — Steam のスクリーンショット</item>
    ///   <item><c>Alt+Enter</c> — 多くのゲームで全画面の切替</item>
    ///   <item><c>Alt+Z</c> — NVIDIA のオーバーレイ</item>
    ///   <item><c>Shift+Tab</c> — Steam のオーバーレイ</item>
    ///   <item><c>`</c> — ゲーム内コンソールとして使われやすい</item>
    /// </list>
    /// <para>T は Translate の頭文字。<b>どのゲームとも絶対に衝突しない組み合わせは存在しない</b>ため、
    /// 変更できることの方が重要（FR-INP-01）。</para>
    /// </remarks>
    public const string DefaultTranslateScreen = "Ctrl+Shift+T";

    /// <summary>
    /// オーバーレイの表示を ON/OFF するキーの既定値（FR-OVL-08）。
    /// </summary>
    /// <remarks>
    /// <para>H は Hide の頭文字。翻訳キーと同じ修飾キーにしたのは、覚える組み合わせを増やさないため。</para>
    /// <para><c>Esc</c> は使えない。キーはゲームにも届く（FR-INP-02）ため、
    /// 訳文を消すつもりでゲームのメニューまで開いてしまう。</para>
    /// </remarks>
    public const string DefaultToggleOverlay = "Ctrl+Shift+H";

    /// <summary>
    /// カーソルの周囲だけを翻訳するキーの既定値（FR-MOD-09〜14）。
    /// </summary>
    /// <remarks>
    /// <para>C は Cursor の頭文字。覚える組み合わせを増やさないよう、修飾キーは他の 2 つと揃えている。</para>
    /// <para>v0.5 では<b>カーソルを止めるだけで発動する方式が既定</b>になる（FR-MOD-11）。
    /// PoC ではホットキーだけを実装し、静止判定は作っていない。</para>
    /// </remarks>
    public const string DefaultTranslateCursor = "Ctrl+Shift+C";

    /// <summary>設定ファイル名。</summary>
    public const string FileName = "hotkeys.json";

    /// <summary>前面の画面をまとめて翻訳するキー。</summary>
    public string TranslateScreen { get; init; } = DefaultTranslateScreen;

    /// <summary>
    /// オーバーレイの表示を ON/OFF するキー（FR-OVL-08）。
    /// </summary>
    /// <remarks>
    /// 前の訳文が次の画面の上に残ったとき、すぐ消すためのもの（PLAN.md 問題 F）。
    /// 消えているときに押すと、最後の訳文を API を呼ばずに出し直す。
    /// </remarks>
    public string ToggleOverlay { get; init; } = DefaultToggleOverlay;

    /// <summary>
    /// カーソルの周囲だけを翻訳するキー（FR-MOD-09〜14）。
    /// </summary>
    /// <remarks>
    /// 全画面より読む量が減るため、性能要件 1.5 秒に収まる見込みがある（PLAN.md 課題4）。
    /// </remarks>
    public string TranslateCursor { get; init; } = DefaultTranslateCursor;

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値を返す。</summary>
    public static HotkeyOptions Load() => JsonSettings.Load(FileName, () => new HotkeyOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
