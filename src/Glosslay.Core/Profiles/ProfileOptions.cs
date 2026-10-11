using Glosslay.Configuration;

namespace Glosslay.Profiles;

/// <summary>
/// プロファイルの自動適用の設定（FR-CFG-02）。
/// </summary>
/// <remarks>
/// <para>プロファイル本体（<c>profiles.json</c>）とは分けてある。
/// <c>profiles.json</c> は<b>他の人と交換する前提</b>（FR-CFG-03）なので、
/// 「自分の環境で自動適用を使うか」という設定を混ぜると、貰った側の設定を書き換えてしまう。</para>
/// </remarks>
public sealed record ProfileOptions
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "autoprofile.json";

    /// <summary>
    /// 前面のウィンドウに合わせて自動で切り替えるか。<b>既定は ON。</b>
    /// </summary>
    /// <remarks>
    /// <para>自動監視（<c>AutoWatchOptions.Enabled</c>）が既定 OFF なのに対して、
    /// <b>こちらは既定 ON にしてある。</b>切り替え自体は呼び出しを使わないため、
    /// RULES.md 🟡-8「知らないうちに呼び出し続けない」に触れない。
    /// 利用者がクラス名まで登録したプロファイルを持っているなら、
    /// それを当てないほうが驚きが大きい。</para>
    /// <para><b>ただし適用したプロファイルが自動監視 ON を含む場合は呼び出しが始まる。</b>
    /// そのため適用時は必ず画面に出して知らせる（<c>MainWindow.ApplyProfile</c>）。</para>
    /// </remarks>
    public bool AutoApply { get; init; } = true;

    /// <summary>
    /// 前面のウィンドウを見に行く間隔。
    /// </summary>
    /// <remarks>
    /// ウィンドウの切り替えは人の操作なので、1 秒でも遅いと感じない。
    /// 行うのは「前面のハンドルを問い合わせる」だけで、変わっていなければ何もしない。
    /// </remarks>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static ProfileOptions Load() => JsonSettings.Load(FileName, () => new ProfileOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
