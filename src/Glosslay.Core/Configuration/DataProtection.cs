using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography;

namespace Glosslay.Configuration;

/// <summary>
/// DPAPI による暗号化（FR-TRN-05「APIキーは DPAPI 等でローカル暗号化保存」）。
/// </summary>
/// <remarks>
/// <para>P/Invoke をここへ集約する（RULES.md 🔵「P/Invoke は専用クラスに集約し、直接散らさない」）。</para>
/// <para><b>現在のユーザーの資格情報で暗号化する。</b>したがって暗号文は
/// 同じ Windows ユーザーでしか復号できない。別ユーザー・別マシンへ持っていくと失敗するのが正しい挙動で、
/// これは「ファイルをコピーされてもキーを取られない」という利点の裏返し。</para>
/// <para>自前で鍵を持たないため、鍵の置き場所という一番壊れやすい問題を回避できる。</para>
/// </remarks>
internal static class DataProtection
{
    /// <summary>
    /// このアプリ固有の追加エントロピー。
    /// </summary>
    /// <remarks>
    /// 同じユーザーで動く他のアプリが、暗号文を拾っただけでは復号できないようにする。
    /// 秘密ではなく（ソースに書いてある）、あくまで用途を分けるためのもの。
    /// <b>変更すると既存の暗号文が読めなくなる</b>ため、変えるときは末尾の版数を上げ、
    /// 復号失敗を「再登録が必要」として扱うこと。
    /// </remarks>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Glosslay.DataProtection.v1");

    /// <summary>暗号化する。</summary>
    public static byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, protect: true);

    /// <summary>復号する。</summary>
    /// <exception cref="Win32Exception">
    /// 復号できない場合。別ユーザー・別マシンの暗号文か、ファイルが壊れている。
    /// </exception>
    public static byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext, protect: false);

    private static unsafe byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        fixed (byte* inputPointer = input)
        fixed (byte* entropyPointer = Entropy)
        {
            var inputBlob = new CRYPT_INTEGER_BLOB
            {
                cbData = (uint)input.Length,
                pbData = inputPointer,
            };
            var entropyBlob = new CRYPT_INTEGER_BLOB
            {
                cbData = (uint)Entropy.Length,
                pbData = entropyPointer,
            };
            var output = default(CRYPT_INTEGER_BLOB);

            // CRYPTPROTECT_UI_FORBIDDEN: プロンプトを出さない。
            // ゲームの最前面にダイアログが割り込むと「ゲームの操作を妨げない」に反する。
            var succeeded = protect
                ? PInvoke.CryptProtectData(
                    &inputBlob, null, &entropyBlob, null, null,
                    PInvoke.CRYPTPROTECT_UI_FORBIDDEN, &output)
                : PInvoke.CryptUnprotectData(
                    &inputBlob, null, &entropyBlob, null, null,
                    PInvoke.CRYPTPROTECT_UI_FORBIDDEN, &output);

            if (!succeeded)
            {
                // 例外を握り潰さない（RULES.md 🔵）。呼び出し側が「再登録が必要」に変換する。
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                return new ReadOnlySpan<byte>(output.pbData, (int)output.cbData).ToArray();
            }
            finally
            {
                // DPAPI の出力は LocalAlloc で確保されている。解放は呼び出し側の責任。
                _ = PInvoke.LocalFree((HLOCAL)(nint)output.pbData);
            }
        }
    }
}
