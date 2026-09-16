using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;

namespace Glosslay.Input;

/// <summary>
/// Raw Input によるキーボード入力の受信（FR-INP-02「Raw Input で検知し、キー入力はゲームにそのまま流す」）。
/// </summary>
/// <remarks>
/// <para><b>RULES.md 🔴-3 に従う唯一の方法。</b></para>
/// <list type="table">
///   <listheader><term>方法</term><description>採否</description></listheader>
///   <item><term><c>RegisterHotKey</c></term><description>禁止。キーを奪うためゲームに届かなくなる</description></item>
///   <item><term><c>WH_KEYBOARD_LL</c></term><description>禁止。キーロガーとして誤検知される</description></item>
///   <item><term>Raw Input</term><description><b>必須。</b>通知を受けるだけで、入力の流れを変えない</description></item>
/// </list>
/// <para><c>RIDEV_INPUTSINK</c> は「前面でなくても受け取る」という指定。
/// ゲームが前面のときに押されたキーを知るために要る。入力を横取りする指定ではない。</para>
/// <para>P/Invoke はここへ集約する（RULES.md 🔵）。</para>
/// </remarks>
public static class RawKeyboard
{
    /// <summary>
    /// HID の Generic Desktop ページ。
    /// </summary>
    private const ushort UsagePageGeneric = 0x01;

    /// <summary>
    /// Generic Desktop ページ内のキーボード。
    /// </summary>
    private const ushort UsageKeyboard = 0x06;

    /// <summary>
    /// RAWKEYBOARD.VKey がこの値のときは実キーではない（キーボードのオーバーラン等）。
    /// </summary>
    private const ushort VirtualKeyInvalid = 0xFF;

    /// <summary>ウィンドウプロシージャで判定する <c>WM_INPUT</c>。</summary>
    public const int WmInput = (int)PInvoke.WM_INPUT;

    /// <summary>
    /// キーボード入力の受信を始める。
    /// </summary>
    /// <param name="windowHandle">
    /// <c>WM_INPUT</c> を受け取るウィンドウ。<c>RIDEV_INPUTSINK</c> を使う場合は必須
    /// （null にすると前面のときしか届かない）。
    /// </param>
    /// <exception cref="Win32Exception">登録に失敗した場合。</exception>
    public static unsafe void Register(nint windowHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);

        var device = new RAWINPUTDEVICE
        {
            usUsagePage = UsagePageGeneric,
            usUsage = UsageKeyboard,
            dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK,
            hwndTarget = (HWND)windowHandle,
        };

        if (!PInvoke.RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// 受信をやめる。ウィンドウを閉じる前に呼ぶ。
    /// </summary>
    /// <remarks>失敗しても例外にしない。終了処理の途中で落とす理由がないため。</remarks>
    public static unsafe void Unregister()
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = UsagePageGeneric,
            usUsage = UsageKeyboard,

            // RIDEV_REMOVE では hwndTarget を null にする決まり。
            dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE,
            hwndTarget = HWND.Null,
        };

        _ = PInvoke.RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE));
    }

    /// <summary>
    /// <c>WM_INPUT</c> の lParam からキーの状態を取り出す。
    /// </summary>
    /// <param name="lParam"><c>WM_INPUT</c> の lParam（HRAWINPUT）。</param>
    /// <param name="virtualKey">仮想キーコード。</param>
    /// <param name="isKeyUp">解放なら <c>true</c>。</param>
    /// <returns>キーボードの有効な入力だったか。</returns>
    /// <remarks>
    /// <b>ここで取り出した値を保存・記録してはいけない。</b>判定に使ったらそのまま捨てること
    /// （<see cref="HotkeyDetector"/> の説明を参照）。
    /// </remarks>
    public static unsafe bool TryRead(nint lParam, out ushort virtualKey, out bool isKeyUp)
    {
        virtualKey = 0;
        isKeyUp = false;

        var headerSize = (uint)sizeof(RAWINPUTHEADER);
        var handle = (HRAWINPUT)lParam;

        // 1 回目でサイズを問い合わせ、2 回目で読む。
        uint size = 0;
        _ = PInvoke.GetRawInputData(handle, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, null, &size, headerSize);

        // キーボードの RAWINPUT は数十バイト。これを超えるものはキーボードではない。
        if (size == 0 || size > 256)
        {
            return false;
        }

        var buffer = stackalloc byte[(int)size];
        if (PInvoke.GetRawInputData(handle, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, buffer, &size, headerSize) != size)
        {
            return false;
        }

        var input = (RAWINPUT*)buffer;
        if (input->header.dwType != (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEKEYBOARD)
        {
            return false;
        }

        var keyboard = input->data.keyboard;
        if (keyboard.VKey == VirtualKeyInvalid)
        {
            return false;
        }

        virtualKey = keyboard.VKey;
        isKeyUp = (keyboard.Flags & PInvoke.RI_KEY_BREAK) != 0;
        return true;
    }
}
