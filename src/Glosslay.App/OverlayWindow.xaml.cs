using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glosslay.Capture;
using Glosslay.Ocr;
using Glosslay.Overlay;

namespace Glosslay;

/// <summary>
/// 訳文をゲーム画面へ重ねる透過ウィンドウ（v0.1 PoC の最小実装）。
/// </summary>
/// <remarks>
/// <para>PLAN.md P0-6。体裁は最低限でよく、確認したいのは次の 2 点。</para>
/// <list type="number">
///   <item>クリックスルーが効く（クリックが下のゲームに透過する）</item>
///   <item>ゲームのフォーカスを奪わない</item>
/// </list>
/// <para>翻訳は P0-5 で入るため、現時点では OCR の結果（原文）をそのまま重ねて表示する。</para>
/// </remarks>
public partial class OverlayWindow : Window
{
    /// <summary>フェードアウトにかける時間。</summary>
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(400));

    /// <summary>前面ウィンドウの確認間隔。人が切り替えを認識する速さに対して十分細かい。</summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _watchdog;

    private IReadOnlyList<OcrLine> _lines = [];
    private ScreenRect _bounds;
    private int _sourceWidth;
    private int _sourceHeight;
    private IReadOnlyList<nint> _keepVisibleWhileForeground = [];
    private DateTime _shownAt;
    private bool _isFading;

    public OverlayWindow()
    {
        InitializeComponent();

        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = WatchInterval };
        _watchdog.Tick += OnWatchdogTick;
    }

    /// <summary>必須の拡張スタイルが実際に効いているか。UI に出して実測する。</summary>
    public OverlayStyleState StyleState { get; private set; }

    /// <summary>
    /// FR-OVL-06: この時間が経過したら自動でフェードアウトする。
    /// </summary>
    /// <remarks>
    /// 出しっぱなしにするとゲーム画面を覆い続けてしまう。
    /// 本来は設定値。PoC では既定値のみ持つ。
    /// </remarks>
    public TimeSpan AutoHideAfter { get; set; } = TimeSpan.FromSeconds(10);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WPF が用意できるのは WS_EX_LAYERED まで。
        // クリックスルー・非アクティブ化・Alt+Tab 非表示はここで設定する。
        var handle = new WindowInteropHelper(this).Handle;
        StyleState = OverlayWindowStyles.Apply(handle);
    }

    /// <summary>
    /// 認識結果を指定した画面領域に重ねて表示する。
    /// </summary>
    /// <param name="lines">表示する行。</param>
    /// <param name="bounds">重ねる先の画面上の矩形（物理ピクセル）。</param>
    /// <param name="sourceWidth">認識に使った画像の幅。座標の対応付けに使う。</param>
    /// <param name="sourceHeight">認識に使った画像の高さ。</param>
    /// <param name="keepVisibleWhileForeground">
    /// これらのウィンドウのいずれかが前面である間だけ表示を続ける。
    /// 対象のゲームに加えて操作ウィンドウも含めること
    /// （操作中に消えてしまわないようにするため）。
    /// 空を渡すと前面の追従を行わず、自動フェードアウトのみになる
    /// （モニタ全体が対象で、どのウィンドウがゲームか特定できない場合）。
    /// </param>
    public void ShowLines(
        IReadOnlyList<OcrLine> lines,
        ScreenRect bounds,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<nint>? keepVisibleWhileForeground = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);

        _lines = lines;
        _bounds = bounds;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _keepVisibleWhileForeground = keepVisibleWhileForeground ?? [];
        _shownAt = DateTime.UtcNow;

        CancelFade();

        if (!IsVisible)
        {
            Show();
        }

        ApplyLayout();
        _watchdog.Start();
    }

    /// <summary>表示を消す。ウィンドウ自体は使い回す（再表示を速くするため）。</summary>
    public void HideOverlay()
    {
        _watchdog.Stop();
        CancelFade();
        LayerCanvas.Children.Clear();
        Hide();
    }

    /// <summary>
    /// 出しっぱなしと、別アプリの上への居座りを防ぐ見張り。
    /// </summary>
    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        // ゲームでも操作ウィンドウでもないものが前面に来たら、即座に引っ込める。
        // 無関係なアプリの上に翻訳が残り続けるのを防ぐ。
        if (_keepVisibleWhileForeground.Count > 0
            && !_keepVisibleWhileForeground.Contains(WindowFocus.Current))
        {
            HideOverlay();
            return;
        }

        // FR-OVL-06: 一定時間で自動フェードアウト。
        if (!_isFading && DateTime.UtcNow - _shownAt >= AutoHideAfter)
        {
            BeginFadeOut();
        }
    }

    private void BeginFadeOut()
    {
        _isFading = true;

        var animation = new DoubleAnimation(1.0, 0.0, FadeDuration);
        animation.Completed += (_, _) =>
        {
            if (_isFading)
            {
                HideOverlay();
            }
        };

        BeginAnimation(OpacityProperty, animation);
    }

    private void CancelFade()
    {
        _isFading = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1.0;
    }

    private void ApplyLayout()
    {
        // Win32 の座標は物理ピクセル、WPF は DIP。PerMonitorV2 なので自分で換算する。
        var dpi = VisualTreeHelper.GetDpi(this);

        Left = _bounds.X / dpi.DpiScaleX;
        Top = _bounds.Y / dpi.DpiScaleY;
        Width = _bounds.Width / dpi.DpiScaleX;
        Height = _bounds.Height / dpi.DpiScaleY;

        // 認識に使った画像と、重ねる先の実サイズの比。
        // ボーダーレスフルスクリーンなら 1:1 になる。
        var scaleX = (double)_bounds.Width / _sourceWidth / dpi.DpiScaleX;
        var scaleY = (double)_bounds.Height / _sourceHeight / dpi.DpiScaleY;

        LayerCanvas.Children.Clear();
        foreach (var line in _lines)
        {
            var label = CreateLabel(line, scaleY);
            Canvas.SetLeft(label, line.Box.X * scaleX);
            Canvas.SetTop(label, line.Box.Y * scaleY);
            LayerCanvas.Children.Add(label);
        }
    }

    private static Border CreateLabel(OcrLine line, double scaleY)
    {
        // 元の行の高さに合わせる。小さすぎると読めないので下限を設ける。
        var fontSize = Math.Max(11, line.Box.Height * scaleY * 0.62);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 12, 14, 20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 90, 200, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock
            {
                Text = line.Text,
                FontSize = fontSize,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.NoWrap,
            },
        };
    }
}
