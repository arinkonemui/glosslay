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

    /// <summary>設定ファイル名。</summary>
    public const string FileName = "hotkeys.json";

    /// <summary>前面の画面をまとめて翻訳するキー。</summary>
    public string TranslateScreen { get; init; } = DefaultTranslateScreen;

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値を返す。</summary>
    public static HotkeyOptions Load() => JsonSettings.Load(FileName, () => new HotkeyOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
