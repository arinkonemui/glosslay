namespace Glosslay.Input;

/// <summary>
/// キーボード（Raw Input）で翻訳を起こす（FR-INP-01 / FR-INP-02 / RULES.md 🔴-3）。
/// </summary>
/// <remarks>
/// <para><b>キーはゲームにもそのまま届く。</b>Raw Input は入力を横取りしないため、
/// ゲーム中に押しても操作を奪わない（FR-INP-02）。
/// <c>RegisterHotKey</c> はキーを奪い、<c>WH_KEYBOARD_LL</c> はキーロガーと誤検知される。
/// どちらも RULES.md 🔴-3 で禁止。</para>
///
/// <para><b>メッセージの受け取りは呼び出し側の仕事。</b>
/// ウィンドウの <c>WndProc</c> に入り込むのは UI 側の都合なので、ここでは行わない。
/// 呼び出し側は <see cref="Attach"/> で登録し、<c>WM_INPUT</c> が来るたびに
/// <see cref="ProcessMessage"/> を呼ぶ。</para>
/// </remarks>
public sealed class RawInputTrigger : IInputTrigger
{
    private readonly List<(InputAction Action, HotkeyDetector Detector)> _detectors = [];
    private readonly List<string> _descriptions = [];
    private readonly List<string> _problems = [];
    private bool _attached;
    private bool _disposed;

    /// <summary>
    /// 設定から割り当てを組み立てる。
    /// </summary>
    /// <remarks>
    /// <b>同じ組み合わせを 2 つに割り当てると、1 回押すたびに両方が走る。</b>
    /// 後から出てきた方を無効にし、理由を <see cref="Problems"/> に積む。
    /// </remarks>
    public RawInputTrigger(HotkeyOptions? options = null)
    {
        var settings = options ?? HotkeyOptions.Load();

        Add(InputAction.TranslateScreen, settings.TranslateScreen, "翻訳キー",
            "前面の画面を翻訳して、原文の位置に重ねる");
        Add(InputAction.TranslateCursor, settings.TranslateCursor, "カーソル翻訳キー",
            "カーソルの周囲だけを翻訳する");
        Add(InputAction.ToggleOverlay, settings.ToggleOverlay, "ON/OFF キー",
            "重ねた訳文を消す / 出し直す");
    }

    /// <inheritdoc />
    public string Name => "キーボード (Raw Input)";

    /// <inheritdoc />
    public bool IsActive => _detectors.Count > 0;

    /// <inheritdoc />
    public IReadOnlyList<string> Descriptions => _descriptions;

    /// <inheritdoc />
    public IReadOnlyList<string> Problems => _problems;

    /// <inheritdoc />
    public event Action<InputAction>? Fired;

    /// <summary>
    /// <c>WM_INPUT</c> を受け取るウィンドウを登録する。
    /// </summary>
    /// <remarks>使える割り当てが無ければ登録しない。打鍵を受け取る理由がないため。</remarks>
    public void Attach(nint windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_attached || !IsActive)
        {
            return;
        }

        RawKeyboard.Register(windowHandle);
        _attached = true;
    }

    /// <summary>
    /// ウィンドウメッセージを渡す。<c>WM_INPUT</c> 以外は無視する。
    /// </summary>
    /// <remarks>
    /// <para><b>キーの内容は判定に使ったらそのまま捨てる。記録もログ出力もしない。</b>
    /// Raw Input はシステム全体の打鍵を受け取るため、残せばキーロガーと変わらない。</para>
    /// <para><b>すべての検出器に同じ入力を必ず渡す。</b>修飾キーの押下状態はそれぞれが持っており、
    /// 一部にしか渡さないと、渡さなかった側の Ctrl / Shift の状態がずれる。</para>
    /// </remarks>
    public void ProcessMessage(int message, nint lParam)
    {
        if (_disposed
            || message != RawKeyboard.WmInput
            || !RawKeyboard.TryRead(lParam, out var virtualKey, out var isKeyUp))
        {
            return;
        }

        foreach (var (action, detector) in _detectors)
        {
            if (detector.Process(virtualKey, isKeyUp))
            {
                Fired?.Invoke(action);
            }
        }
    }

    private void Add(InputAction action, string text, string label, string description)
    {
        if (!HotkeyGesture.TryParse(text, out var parsed))
        {
            _problems.Add($"{HotkeyOptions.FileName} の{label}「{text}」を解釈できません。");
            return;
        }

        var gesture = parsed.Value;
        if (_detectors.Exists(d => d.Detector.Gesture == gesture))
        {
            _problems.Add($"{label}「{gesture}」は他の割り当てと重複しているため無効にしました。");
            return;
        }

        _detectors.Add((action, new HotkeyDetector(gesture)));
        _descriptions.Add($"{gesture} — {description}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Fired = null;

        if (_attached)
        {
            RawKeyboard.Unregister();
            _attached = false;
        }
    }
}
