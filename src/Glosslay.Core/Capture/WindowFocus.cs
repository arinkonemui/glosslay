using Windows.Win32;

namespace Glosslay.Capture;

/// <summary>
/// 前面ウィンドウの確認。
/// </summary>
/// <remarks>
/// <para>翻訳対象のゲームが前面にあるかどうかだけを見る。
/// 前面ウィンドウのハンドルを OS に問い合わせるだけで、
/// ゲームプロセスへのアタッチ・メモリ読み取り・フックは一切行わない（RULES.md 🔴-1）。</para>
/// <para>フック（<c>SetWinEventHook</c>）ではなく呼び出し側からの問い合わせにしているのは、
/// 監視系 API を増やさず、挙動を追いやすくするため。呼び出しコストは無視できる。</para>
/// </remarks>
public static class WindowFocus
{
    /// <summary>現在前面にあるウィンドウのハンドル。</summary>
    public static nint Current => PInvoke.GetForegroundWindow();

    /// <summary>指定したウィンドウが前面にあるか。</summary>
    public static bool IsForeground(nint windowHandle) =>
        windowHandle != 0 && Current == windowHandle;
}
