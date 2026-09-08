namespace Glosslay.Capture;

/// <summary>
/// キャプチャ方式。
/// FR-CAP-02: アンチチート更新で片方が塞がれた場合の保険として、
/// 「ウィンドウ指定」と「モニタ全体」を切り替えられることが要件。
/// </summary>
public enum CaptureTargetKind
{
    /// <summary>指定したウィンドウだけをキャプチャする。</summary>
    Window,

    /// <summary>モニタ全体をキャプチャする。</summary>
    Monitor,
}

/// <summary>
/// キャプチャ対象。ウィンドウなら HWND、モニタなら HMONITOR を保持する。
/// </summary>
/// <remarks>
/// ここで保持するのは「どこを撮るか」を示すウィンドウ/モニタのハンドルのみ。
/// ゲームプロセスへのアタッチやプロセスハンドルの取得は一切行わない（RULES.md 🔴-1）。
/// </remarks>
public sealed record CaptureTarget
{
    private CaptureTarget(CaptureTargetKind kind, nint handle, string displayName)
    {
        Kind = kind;
        Handle = handle;
        DisplayName = displayName;
    }

    /// <summary>ウィンドウ指定かモニタ全体か。</summary>
    public CaptureTargetKind Kind { get; }

    /// <summary><see cref="CaptureTargetKind.Window"/> なら HWND、<see cref="CaptureTargetKind.Monitor"/> なら HMONITOR。</summary>
    public nint Handle { get; }

    /// <summary>UI に表示する名前（ウィンドウタイトル、またはモニタ名）。</summary>
    public string DisplayName { get; }

    /// <summary>ウィンドウを対象にする。</summary>
    public static CaptureTarget ForWindow(nint hwnd, string title)
    {
        ArgumentOutOfRangeException.ThrowIfZero(hwnd);
        return new CaptureTarget(CaptureTargetKind.Window, hwnd, title);
    }

    /// <summary>モニタを対象にする。</summary>
    public static CaptureTarget ForMonitor(nint hmonitor, string name)
    {
        ArgumentOutOfRangeException.ThrowIfZero(hmonitor);
        return new CaptureTarget(CaptureTargetKind.Monitor, hmonitor, name);
    }

    public override string ToString() => $"[{Kind}] {DisplayName}";
}
