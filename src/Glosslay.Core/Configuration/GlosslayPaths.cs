namespace Glosslay.Configuration;

/// <summary>
/// アプリが書き込む場所（FR-CFG-00: <c>%APPDATA%\Glosslay\</c>）。
/// </summary>
/// <remarks>
/// 置き場所を各所で組み立てると必ず食い違うため、ここだけに書く。
/// <c>%APPDATA%</c>（Roaming）を使うのは、設定を持ち運べる方が利用者の利益になるため。
/// ただし <see cref="ApiKeyStore"/> が置く DPAPI の暗号文は
/// <b>別のマシンでは復号できない</b>点に注意（それが DPAPI の仕様であり、意図した挙動）。
/// </remarks>
public static class GlosslayPaths
{
    /// <summary>設定・保存物の基点。</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glosslay");

    /// <summary>キャプチャ画像の保存先。P0-7 の精度計測用サンプルを貯める場所。</summary>
    public static string Captures => Path.Combine(Root, "captures");

    /// <summary>APIキーなどの秘密情報。中身は暗号化されている（FR-TRN-05）。</summary>
    public static string Secrets => Path.Combine(Root, "secrets");

    /// <summary>
    /// 指定したディレクトリを作成し、そのパスを返す。
    /// </summary>
    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
