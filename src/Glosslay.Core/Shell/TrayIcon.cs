using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Glosslay.Shell;

/// <summary>トレイのアイコンで起きたこと。</summary>
public enum TrayIconEvent
{
    /// <summary>何も起きていない（このアイコン宛てのメッセージではない）。</summary>
    None,

    /// <summary>左クリック、またはキーボードで選ばれた。画面を開く合図。</summary>
    Selected,

    /// <summary>右クリック、またはメニューキー。メニューを出す合図。</summary>
    MenuRequested,
}

/// <summary>トレイのメニューの 1 項目。</summary>
/// <param name="Id">選ばれたときに返す番号。1 以上。</param>
/// <param name="Text">表示する文字。</param>
public sealed record TrayMenuItem(int Id, string Text)
{
    /// <summary>区切り線。</summary>
    public static TrayMenuItem Separator { get; } = new(0, string.Empty) { IsSeparator = true };

    /// <summary>区切り線か。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>チェックを付けるか。</summary>
    public bool IsChecked { get; init; }

    /// <summary>選べるか。<c>false</c> なら灰色の説明として出す。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>太字にするか（左クリックと同じ動作の項目に付ける。Windows の慣例）。</summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// タスクトレイ（通知領域）のアイコン（FR-SYS-02）。
/// </summary>
/// <remarks>
/// <para><b>メッセージは呼び出し側のウィンドウで受け取る。</b>このクラスはウィンドウを持たず、
/// 渡されたウィンドウ宛てに届いたメッセージを <see cref="Interpret"/> で読み解くだけ。
/// 操作ウィンドウを隠している間もハンドルは生きているので、そのまま受け取れる。</para>
/// <para><b>エクスプローラーが再起動するとアイコンは消える。</b>OS が <c>TaskbarCreated</c> を
/// 全ウィンドウへ送ってくるので、受け取ったら登録し直す（<see cref="Interpret"/> が行う）。</para>
/// <para>ゲームプロセスには一切触れない（RULES.md 🔴-1）。扱うのは自分のウィンドウとアイコンだけ。</para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    /// <summary>このアイコンの番号。1 つしか置かないので固定。</summary>
    private const uint IconId = 1;

    /// <summary><c>NOTIFYICON_VERSION_4</c>。座標とイベントの渡し方がこの版の形になる。</summary>
    private const uint NotifyIconVersion4 = 4;

    // NOTIFYICON_VERSION_4 で lParam の下位 16 ビットに入るイベント。
    private const uint NinSelect = 0x0400;     // NIN_SELECT（左クリック）
    private const uint NinKeySelect = 0x0401;  // NIN_KEYSELECT（キーボードで選択）
    private const uint WmContextMenu = 0x007B; // WM_CONTEXTMENU（右クリック / メニューキー）

    /// <summary><c>WM_NULL</c>。メニューを閉じた後に送る空のメッセージ。</summary>
    private const uint WmNull = 0x0000;

    /// <summary>CreateIconFromResourceEx に渡す版。アイコンは 3.0 形式。</summary>
    private const uint IconResourceVersion = 0x00030000;

    private readonly HWND _window;
    private readonly uint _callbackMessage;
    private readonly uint _taskbarCreatedMessage;
    private readonly string _tooltip;
    private HICON _icon;
    private bool _added;
    private bool _disposed;

    /// <summary>
    /// アイコンを用意する。表示は <see cref="Show"/> で行う。
    /// </summary>
    /// <param name="windowHandle">メッセージを受け取るウィンドウ。</param>
    /// <param name="callbackMessage">
    /// アイコンの操作をウィンドウへ知らせるときのメッセージ番号。<c>WM_APP</c>（0x8000）以降を使う。
    /// </param>
    /// <param name="iconPng">アイコンの画像（PNG）。</param>
    /// <param name="tooltip">マウスを乗せたときの説明。</param>
    public TrayIcon(nint windowHandle, uint callbackMessage, byte[] iconPng, string tooltip)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        ArgumentNullException.ThrowIfNull(iconPng);
        ArgumentNullException.ThrowIfNull(tooltip);

        _window = new HWND(windowHandle);
        _callbackMessage = callbackMessage;
        _taskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");
        _tooltip = tooltip;
        _icon = CreateIcon(iconPng);
    }

    /// <summary>
    /// 通知領域のアイコンの大きさ（画素）。DPI に応じて 16 / 20 / 24 / 32 などになる。
    /// </summary>
    /// <remarks>この大きさで描いた画像を渡すと、縮小でぼやけない。</remarks>
    public static int PreferredSize => PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);

    /// <summary>通知領域に出ているか。出ていなければ、画面を隠すと戻る手段が無くなる。</summary>
    public bool IsShown => _added && !_disposed;

    /// <summary>アイコンを通知領域に出す。何度呼んでもよい。</summary>
    /// <returns>出せたか。エクスプローラーが動いていないと失敗する。</returns>
    public bool Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_added)
        {
            return true;
        }

        var data = NewData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE
                      | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
                      | NOTIFY_ICON_DATA_FLAGS.NIF_TIP
                      | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        data.uCallbackMessage = _callbackMessage;
        data.hIcon = _icon;
        data.szTip = _tooltip;

        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
        {
            return false;
        }

        data.Anonymous.uVersion = NotifyIconVersion4;
        _ = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
        _added = true;
        return true;
    }

    /// <summary>
    /// アイコンから吹き出しの通知を出す。
    /// </summary>
    /// <remarks>
    /// Windows 11 では通知センターのトーストとして出る。集中モード中は出ないことがある。
    /// <b>大事な知らせをこれだけに頼らない</b>（見逃されても困らない内容に限る）。
    /// </remarks>
    public void ShowNotice(string title, string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);

        if (!_added)
        {
            return;
        }

        var data = NewData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO;
        _ = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    /// <summary>
    /// ウィンドウに届いたメッセージがこのアイコン宛てなら、何が起きたかを返す。
    /// </summary>
    /// <remarks>
    /// <c>TaskbarCreated</c>（エクスプローラーの再起動）を受け取ったら、ここで登録し直す。
    /// </remarks>
    public TrayIconEvent Interpret(int message, nint wParam, nint lParam)
    {
        if (_disposed)
        {
            return TrayIconEvent.None;
        }

        if ((uint)message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _added = false;
            Show();
            return TrayIconEvent.None;
        }

        if ((uint)message != _callbackMessage)
        {
            return TrayIconEvent.None;
        }

        // NOTIFYICON_VERSION_4: lParam の下位 16 ビットがイベント、上位がアイコンの番号。
        var notification = (uint)(lParam & 0xFFFF);
        return notification switch
        {
            NinSelect or NinKeySelect => TrayIconEvent.Selected,
            WmContextMenu => TrayIconEvent.MenuRequested,
            _ => TrayIconEvent.None,
        };
    }

    /// <summary>
    /// マウスの位置にメニューを出し、選ばれた項目の番号を返す。選ばれなければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para><b>出す前にウィンドウを前面にする必要がある</b>（Windows の決まり）。
    /// そうしないと、メニューの外をクリックしても閉じない。隠しているウィンドウでも効く。</para>
    /// <para>閉じた後に空のメッセージを送るのも同じ決まりの一部
    /// （送らないと、次にメニューを出したときにすぐ閉じることがある）。</para>
    /// </remarks>
    public unsafe int? ShowMenu(IReadOnlyList<TrayMenuItem> items)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(items);

        var menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull)
        {
            return null;
        }

        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    _ = PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, default(PCWSTR));
                    continue;
                }

                var flags = MENU_ITEM_FLAGS.MF_STRING;
                if (item.IsChecked)
                {
                    flags |= MENU_ITEM_FLAGS.MF_CHECKED;
                }

                if (!item.IsEnabled)
                {
                    flags |= MENU_ITEM_FLAGS.MF_GRAYED;
                }

                fixed (char* text = item.Text)
                {
                    _ = PInvoke.AppendMenu(menu, flags, (nuint)item.Id, new PCWSTR(text));
                }

                // 太字は追加のときの指定では付かない（AppendMenu が受け付けない）。後から既定の項目にする。
                if (item.IsDefault)
                {
                    _ = PInvoke.SetMenuDefaultItem(menu, (uint)item.Id, 0);
                }
            }

            _ = PInvoke.GetCursorPos(out var point);
            _ = PInvoke.SetForegroundWindow(_window);

            var chosen = (int)PInvoke.TrackPopupMenuEx(
                menu,
                (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD
                       | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY
                       | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON
                       | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN),
                point.X,
                point.Y,
                _window,
                null);

            _ = PInvoke.PostMessage(_window, WmNull, default, default);
            return chosen > 0 ? chosen : null;
        }
        finally
        {
            _ = PInvoke.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            var data = NewData();
            _ = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
            _added = false;
        }

        if (!_icon.IsNull)
        {
            _ = PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }

    private NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window,
        uID = IconId,
    };

    /// <summary>
    /// PNG からアイコンを作る。大きさは通知領域の標準（高 DPI ではそれに合わせて大きい）。
    /// </summary>
    /// <remarks>
    /// Windows Vista 以降、アイコンの中身は PNG のままでよい。ファイルに書き出す必要はない。
    /// </remarks>
    private static unsafe HICON CreateIcon(byte[] png)
    {
        var width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        var height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON);

        fixed (byte* bits = png)
        {
            var icon = PInvoke.CreateIconFromResourceEx(
                bits, (uint)png.Length, true, IconResourceVersion, width, height, IMAGE_FLAGS.LR_DEFAULTCOLOR);

            return icon.IsNull
                ? throw new InvalidOperationException("トレイのアイコンを作れませんでした。")
                : icon;
        }
    }
}
