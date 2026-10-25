using Glosslay.Capture;
using Glosslay.Imaging;

namespace Glosslay.Profiles;

/// <summary>
/// ゲームごとの設定（FR-CFG-01）。
/// </summary>
/// <remarks>
/// <para>対象・領域・前処理・動作モードをひとまとめにして、ゲームを変えるたびに
/// 手で設定し直さなくて済むようにする。</para>
/// <para>JSON でそのまま書き出せる形にしてある（FR-CFG-03 のエクスポート / インポート）。
/// 人が読んで手で直せること、他の人と交換できることを想定している。</para>
/// </remarks>
public sealed record Profile
{
    /// <summary>表示名。<b>プロファイルを見分ける鍵でもある。</b></summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// ウィンドウのクラス名（FR-CFG-02 の自動適用に使う）。
    /// </summary>
    /// <remarks>
    /// <para><b>プロセス名ではなくクラス名を使う。</b>ウィンドウからプロセス名を得るには
    /// プロセスハンドルを開く必要があり、RULES.md 🔴-1「ハンドル取得」に触れる
    /// （2026-10-11 の人間の判断）。</para>
    /// <para>クラス名はウィンドウに問い合わせるだけで取れ、<b>遊んでいる最中も変わらない。</b></para>
    /// </remarks>
    public string WindowClass { get; init; } = string.Empty;

    /// <summary>
    /// ウィンドウタイトルに含まれる文字列（部分一致・大小無視）。
    /// </summary>
    /// <remarks>
    /// <b>タイトルは遊んでいる最中に変わる</b>（「ゲーム名 - ステージ3」など）ため、
    /// 完全一致では当たらない。クラス名だけでは区別できない場合（Unity 製など、
    /// 同じクラス名を使うゲームが多い）の補助として使う。空なら見ない。
    /// </remarks>
    public string TitleContains { get; init; } = string.Empty;

    /// <summary>OCR にかける領域。<c>null</c> なら画面全体。</summary>
    public FrameRect? Region { get; init; }

    /// <summary>前処理の設定（FR-OCR-04 / 05）。</summary>
    public PreprocessOptions Preprocess { get; init; } = new();

    /// <summary>
    /// 原文の言語（FR-OCR-02 / FR-CFG-01）。BCP 47。<c>null</c> なら既定（英語。メイン言語が英語なら日本語）。
    /// </summary>
    /// <remarks>
    /// <para><b>ゲームごとに違うのは原文の方</b>なので、プロファイルに持つ（2026-10-25 人間の判断）。
    /// それまでは訳文の言語を持っていた。訳文の言語はメイン言語としてアプリ全体で 1 つ。</para>
    /// <para>以前のプロファイルにある <c>TargetLanguage</c> は読まない（メイン言語は別に決める）。</para>
    /// </remarks>
    public string? SourceLanguage { get; init; }

    /// <summary>
    /// 自動監視を使うか（FR-MOD-06）。<b>既定は OFF。</b>
    /// </summary>
    /// <remarks>
    /// <para>重いゲームや、CPU をゲームに回したい場面では切る。
    /// 対戦ゲームはその代表だが、<b>それだけのための設定ではない</b>
    /// （「PvPモード」は 2026-10-11 に廃止。PLAN.md 参照）。</para>
    /// <para><b>アプリ側の判断で強制的に無効化しない</b>（FR-MOD-07）。決めるのは利用者。</para>
    /// </remarks>
    public bool AutoWatch { get; init; }

    /// <summary>カーソルを止めたときに翻訳するか（FR-MOD-10 / 14）。</summary>
    public bool CursorDwell { get; init; }

    /// <summary>
    /// このウィンドウに当てはまるか（FR-CFG-02）。
    /// </summary>
    /// <remarks>
    /// <b>クラス名が一致し、かつタイトルの条件を満たすこと。</b>
    /// どちらも空のプロファイルは、何にでも当たってしまうので当てない。
    /// </remarks>
    public bool Matches(string windowClass, string windowTitle)
    {
        ArgumentNullException.ThrowIfNull(windowClass);
        ArgumentNullException.ThrowIfNull(windowTitle);

        if (WindowClass.Length == 0 && TitleContains.Length == 0)
        {
            return false;
        }

        if (WindowClass.Length > 0
            && !string.Equals(WindowClass, windowClass, StringComparison.Ordinal))
        {
            return false;
        }

        return TitleContains.Length == 0
            || windowTitle.Contains(TitleContains, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>表示名。</summary>
    /// <remarks>
    /// <b>レコードの既定の表記を使わせない。</b>一覧に並べると
    /// 支援技術（スクリーンリーダー）が読み上げる名前がこれになり、
    /// <c>Profile { Name = Elin, WindowClass = ... }</c> と全項目が読まれる。
    /// </remarks>
    public override string ToString() => Name;
}
