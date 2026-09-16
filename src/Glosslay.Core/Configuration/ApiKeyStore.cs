using System.ComponentModel;
using System.Text;

namespace Glosslay.Configuration;

/// <summary>
/// APIキーの保管（FR-TRN-05「APIキーは DPAPI 等でローカル暗号化保存する」）。
/// </summary>
/// <remarks>
/// <para>設定は人が読める JSON にする（RULES.md 🔵）が、<b>キーだけは例外</b>。
/// 暗号化した別ファイルに置き、設定 JSON には一切書かない。
/// 設定ファイルを人に見せたり、不具合報告に添付したりしてもキーが漏れないようにするため。</para>
/// <para>バックエンドごとに分ける（FR-TRN-01 で Gemini / OpenAI互換を切り替えるため）。</para>
/// </remarks>
/// <param name="backendId">
/// バックエンドの識別子。ファイル名になるため英数字のみ。<c>gemini</c> など。
/// </param>
public sealed class ApiKeyStore(string backendId)
{
    /// <summary>Gemini 用（FR-TRN-04 の BYOK）。</summary>
    public static ApiKeyStore Gemini { get; } = new("gemini");

    private string FilePath => Path.Combine(GlosslayPaths.Secrets, $"{backendId}.apikey");

    /// <summary>キーが登録されているか。UI の「未登録」表示に使う（FR-TRN-02）。</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// キーを保存する。前回の値は上書きされる。
    /// </summary>
    /// <exception cref="ArgumentException">空白のみの場合。</exception>
    public void Save(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // 貼り付け時に前後の空白や改行が混ざるのはよくあるため、ここで落とす。
        var trimmed = apiKey.Trim();

        GlosslayPaths.EnsureDirectory(GlosslayPaths.Secrets);

        var plaintext = Encoding.UTF8.GetBytes(trimmed);
        try
        {
            File.WriteAllBytes(FilePath, DataProtection.Protect(plaintext));
        }
        finally
        {
            // 平文をメモリに残さない。プロセスダンプが外へ出る経路（不具合報告など）を考えると安い保険。
            Array.Clear(plaintext);
        }
    }

    /// <summary>
    /// キーを読み出す。未登録、または復号できない場合は <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 復号の失敗は例外にしない。<b>別マシンへ設定を持ち込んだときに必ず起きる</b>もので、
    /// 異常ではなく「再登録が必要な状態」。呼び出し側は未登録と同じ扱いにしてよい。
    /// </remarks>
    public string? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(DataProtection.Unprotect(File.ReadAllBytes(FilePath)));
        }
        catch (Win32Exception)
        {
            // 別ユーザー / 別マシンの暗号文。再登録してもらう。
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>キーを削除する。</summary>
    public void Delete() => File.Delete(FilePath);
}
