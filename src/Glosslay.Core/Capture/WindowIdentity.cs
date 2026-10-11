using Windows.Win32;

namespace Glosslay.Capture;

/// <summary>
/// ウィンドウを見分けるための情報（FR-CFG-02）。
/// </summary>
/// <remarks>
/// <para><b>プロセスには一切触れない。</b>ウィンドウからプロセス名を得るには
/// プロセスハンドルを開く必要があり、RULES.md 🔴-1「ハンドル取得」に触れる。
/// クラス名とタイトルは<b>ウィンドウに問い合わせるだけ</b>で取れる
/// （2026-10-11 の人間の判断）。</para>
/// <para><b>クラス名は遊んでいる最中も変わらない。</b>
/// タイトルは「ゲーム名 - ステージ3」のように変わるため、補助として部分一致で使う。</para>
/// </remarks>
public static class WindowIdentity
{
    /// <summary>クラス名の上限。Win32 のクラス名は 256 文字まで。</summary>
    private const int MaxClassNameLength = 256;

    /// <summary>
    /// ウィンドウのクラス名を読む。取れなければ空文字。
    /// </summary>
    public static string GetClassName(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[MaxClassNameLength + 1];
        var length = PInvoke.GetClassName(new Windows.Win32.Foundation.HWND(windowHandle), buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }
}
