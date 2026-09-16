namespace Glosslay.Input;

/// <summary>
/// キーの押下・解放の列から、ホットキーが押された瞬間を検出する。
/// </summary>
/// <remarks>
/// <para><b>Win32 に依存しない純粋なロジック。</b>Raw Input からの入力を 1 件ずつ渡す。
/// 切り離してあるのは、実際のキーボードなしに挙動を確かめられるようにするため。</para>
/// <para><b>キーを記録しない。</b>保持するのは修飾キー 3 つの押下状態と、
/// 主キーが押されたままかどうかの 1 ビットだけ。打鍵の履歴は一切残さない。
/// Raw Input はシステム全体の打鍵を受け取るため、ここで余計な情報を持つと
/// キーロガーと同じものになってしまう（RULES.md 🔴-3 がフックを禁じた理由と同じ懸念）。</para>
/// </remarks>
public sealed class HotkeyDetector(HotkeyGesture gesture)
{
    private bool _control;
    private bool _shift;
    private bool _alt;

    /// <summary>主キーが押されたままか。キーリピートで連続発火させないために持つ。</summary>
    private bool _keyHeld;

    /// <summary>検出する組み合わせ。</summary>
    public HotkeyGesture Gesture { get; } = gesture;

    /// <summary>
    /// キー入力を 1 件処理する。
    /// </summary>
    /// <param name="virtualKey">仮想キーコード。</param>
    /// <param name="isKeyUp">解放なら <c>true</c>。</param>
    /// <returns>ホットキーが押された瞬間なら <c>true</c>。押しっぱなしのリピートでは <c>false</c>。</returns>
    public bool Process(ushort virtualKey, bool isKeyUp)
    {
        if (HotkeyGesture.IsModifier(virtualKey, out var modifier))
        {
            var pressed = !isKeyUp;
            switch (modifier)
            {
                case ModifierKey.Control:
                    _control = pressed;
                    break;
                case ModifierKey.Shift:
                    _shift = pressed;
                    break;
                case ModifierKey.Alt:
                    _alt = pressed;
                    break;
            }

            return false;
        }

        if (virtualKey != Gesture.VirtualKey)
        {
            return false;
        }

        if (isKeyUp)
        {
            _keyHeld = false;
            return false;
        }

        // 押しっぱなしにすると OS がキーリピートで押下を送り続ける。
        // それを全部翻訳の依頼にすると API を連打することになる（RULES.md 🟡-8）。
        if (_keyHeld)
        {
            return false;
        }

        _keyHeld = true;

        return _control == Gesture.Control
               && _shift == Gesture.Shift
               && _alt == Gesture.Alt;
    }

    /// <summary>
    /// 押下状態を忘れる。
    /// </summary>
    /// <remarks>
    /// 解放を取りこぼすと修飾キーが押されたままと見なされ続ける（例: Alt+Tab で別アプリへ移った直後）。
    /// 実害は「発動しない」側に倒れるだけだが、気付いたら呼べるようにしておく。
    /// </remarks>
    public void Reset()
    {
        _control = false;
        _shift = false;
        _alt = false;
        _keyHeld = false;
    }
}
