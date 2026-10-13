using Glosslay.Configuration;

namespace Glosslay.Shell;

/// <summary>操作画面の × を押したときの動き（FR-SYS-02）。</summary>
public enum CloseBehavior
{
    /// <summary>
    /// タスクトレイにしまって動き続ける。<b>既定。</b>
    /// </summary>
    /// <remarks>
    /// ふだんは操作画面を出さず、ホットキーと訳文だけで使うのが本来の使い方のため。
    /// 終了するとホットキーも自動監視も止まる。
    /// </remarks>
    HideToTray,

    /// <summary>アプリを終了する。</summary>
    Exit,
}

/// <summary>
/// タスクトレイ常駐の設定（FR-SYS-02）。
/// </summary>
/// <remarks>
/// <b>自動起動の ON / OFF はここに持たない。</b>本当の状態は Windows の登録（<see cref="AutoStart"/>）で、
/// 利用者はタスクマネージャーの「スタートアップ アプリ」からも外せる。
/// 設定ファイルにも持つと、片方だけ変わって食い違う。
/// </remarks>
public sealed record TrayOptions
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "tray.json";

    /// <summary>× を押したときの動き。</summary>
    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.HideToTray;

    /// <summary>
    /// 「タスクトレイで動いています」の通知を出したか。
    /// </summary>
    /// <remarks>
    /// <b>1 回だけ出す。</b>× で閉じたのにアプリが動き続けていると、
    /// 終わったつもりの利用者を驚かせる。毎回出すと邪魔になる。
    /// </remarks>
    public bool HideNoticeShown { get; init; }

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static TrayOptions Load() => JsonSettings.Load(FileName, () => new TrayOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
