using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Glosslay.Overlay;

/// <summary>
/// オーバーレイウィンドウに必要な拡張スタイルの適用と検証。
/// </summary>
/// <remarks>
/// <para>RULES.md は次の 4 つを<b>すべて必須</b>と定めている。1 つでも欠けると要件を満たさない。</para>
/// <list type="table">
///   <item><term>WS_EX_LAYERED</term><description>透過表示</description></item>
///   <item><term>WS_EX_TRANSPARENT</term><description>クリックスルー（FR-OVL-02）</description></item>
///   <item><term>WS_EX_NOACTIVATE</term><description>ゲームのフォーカスを奪わない（FR-OVL-03）</description></item>
///   <item><term>WS_EX_TOOLWINDOW</term><description>Alt+Tab の一覧に出さない（FR-OVL-09）</description></item>
/// </list>
/// <para><b>WS_EX_TOOLWINDOW は Alt+Tab 経由でフォーカスを奪われる経路を塞ぐためのもの。</b>
/// WS_EX_NOACTIVATE は自分から前面に出ないことしか保証せず、
/// 利用者が Alt+Tab でオーバーレイを選んでしまうとゲームがフォアグラウンドを失う。
/// ボーダーレスフルスクリーンのゲームはフォーカス喪失で FPS を落とす実装が多く、
/// 「ゲームの操作を妨げない」（RULES.md の判断基準 2 位）が破れる。</para>
/// <para>副作用としてタスクバーにも出なくなるため、アプリの操作導線は
/// タスクトレイ常駐（FR-SYS-02）と別ウィンドウで用意すること。</para>
/// </remarks>
public static class OverlayWindowStyles
{
    private const WINDOW_EX_STYLE Required =
        WINDOW_EX_STYLE.WS_EX_LAYERED
        | WINDOW_EX_STYLE.WS_EX_TRANSPARENT
        | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
        | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;

    /// <summary>
    /// 必須の拡張スタイルを追加する。既存のスタイルは維持する。
    /// </summary>
    /// <param name="windowHandle">対象ウィンドウのハンドル。</param>
    /// <returns>適用後に実際に設定されていたスタイル。</returns>
    /// <remarks>
    /// WPF の <c>AllowsTransparency</c> が既に WS_EX_LAYERED を立てているが、
    /// 冪等な操作なので重ねて設定して構わない。
    /// </remarks>
    public static OverlayStyleState Apply(nint windowHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);

        var hwnd = new HWND(windowHandle);
        var current = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(
            hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        _ = PInvoke.SetWindowLongPtr(
            hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)(current | Required));

        return Inspect(windowHandle);
    }

    /// <summary>
    /// 実際に設定されているスタイルを読み取る。
    /// </summary>
    /// <remarks>
    /// 「設定したつもり」で終わらせないための確認用。
    /// PoC ではこの結果を UI に出して実測する（PLAN.md P0-6）。
    /// </remarks>
    public static OverlayStyleState Inspect(nint windowHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);

        var style = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(
            new HWND(windowHandle), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        return new OverlayStyleState(
            IsLayered: style.HasFlag(WINDOW_EX_STYLE.WS_EX_LAYERED),
            IsClickThrough: style.HasFlag(WINDOW_EX_STYLE.WS_EX_TRANSPARENT),
            DoesNotActivate: style.HasFlag(WINDOW_EX_STYLE.WS_EX_NOACTIVATE),
            IsHiddenFromAltTab: style.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW));
    }
}

/// <summary>オーバーレイに必要な拡張スタイルが実際に効いているか。</summary>
/// <param name="IsLayered">透過表示（WS_EX_LAYERED）。</param>
/// <param name="IsClickThrough">クリックスルー（WS_EX_TRANSPARENT / FR-OVL-02）。</param>
/// <param name="DoesNotActivate">フォーカスを奪わない（WS_EX_NOACTIVATE / FR-OVL-03）。</param>
/// <param name="IsHiddenFromAltTab">Alt+Tab に出ない（WS_EX_TOOLWINDOW / FR-OVL-09）。</param>
public readonly record struct OverlayStyleState(
    bool IsLayered,
    bool IsClickThrough,
    bool DoesNotActivate,
    bool IsHiddenFromAltTab)
{
    /// <summary>4 つすべてが設定されているか。1 つでも欠けたら要件を満たさない。</summary>
    public bool IsComplete =>
        IsLayered && IsClickThrough && DoesNotActivate && IsHiddenFromAltTab;

    public override string ToString() =>
        $"LAYERED={IsLayered} TRANSPARENT={IsClickThrough} "
        + $"NOACTIVATE={DoesNotActivate} TOOLWINDOW={IsHiddenFromAltTab}";
}
