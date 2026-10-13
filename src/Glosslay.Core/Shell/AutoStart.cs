using Microsoft.Win32;

namespace Glosslay.Shell;

/// <summary>
/// Windows の起動時に自動で起動する（FR-SYS-02「自動起動（任意）」）。
/// </summary>
/// <remarks>
/// <para><b>現在のユーザーの <c>Run</c> キーに登録する。</b>管理者権限は要らない
/// （RULES.md 🟠「管理者権限を要求しない」）。タスクスケジューラやサービスは使わない。</para>
/// <para><b>既定は OFF。</b>利用者が設定で入れたときだけ登録する。</para>
/// <para>起動時は画面を出さず、タスクトレイで待機する（<see cref="TrayArgument"/>）。
/// ログオンのたびに画面が開くと、ゲームを始める前に閉じる手間が増えるだけになる。</para>
/// </remarks>
public sealed class AutoStart
{
    /// <summary>現在のユーザーの自動起動の登録先。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>登録する名前。</summary>
    public const string DefaultValueName = "Glosslay";

    /// <summary>自動起動のときに渡す引数。これがあると画面を出さずにトレイで待機する。</summary>
    public const string TrayArgument = "--tray";

    private readonly string _keyPath;
    private readonly string _valueName;

    /// <summary>
    /// 登録先を決める。
    /// </summary>
    /// <param name="keyPath">
    /// <c>HKEY_CURRENT_USER</c> からの位置。既定は <see cref="RunKeyPath"/>。
    /// <b>検査では別の場所を渡す</b>（本物の自動起動を書き換えないため）。
    /// </param>
    /// <param name="valueName">登録する名前。</param>
    public AutoStart(string keyPath = RunKeyPath, string valueName = DefaultValueName)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        ArgumentException.ThrowIfNullOrEmpty(valueName);
        _keyPath = keyPath;
        _valueName = valueName;
    }

    /// <summary>自動起動に登録されているか（どの実行ファイルかは問わない）。</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            return key?.GetValue(_valueName) is string { Length: > 0 };
        }
    }

    /// <summary>
    /// 登録されているのが、いま動いている実行ファイルか。
    /// </summary>
    /// <remarks>
    /// 置き場所を移した（ZIP 版を別のフォルダへ展開し直した等）と、古い場所を指したまま残る。
    /// その場合は画面で入れ直してもらえば直る。
    /// </remarks>
    public bool PointsTo(string executablePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        return key?.GetValue(_valueName) is string command
               && string.Equals(command, CommandFor(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>自動起動に登録する / 外す。</summary>
    /// <param name="enabled">登録するか。</param>
    /// <param name="executablePath">起動する実行ファイル。</param>
    public void Set(bool enabled, string executablePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);

        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            key.SetValue(_valueName, CommandFor(executablePath), RegistryValueKind.String);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        existing?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 登録するコマンド。パスに空白があっても動くよう引用符で囲む。
    /// </summary>
    public static string CommandFor(string executablePath) =>
        $"\"{executablePath}\" {TrayArgument}";
}
