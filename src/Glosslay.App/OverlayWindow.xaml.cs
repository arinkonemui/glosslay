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
/// <para>「オーバーレイ表示」ボタンは OCR の結果（原文）を、
/// ホットキー（FR-MOD-01）は訳文を、それぞれ原文の位置に重ねる（FR-OVL-04 ①）。</para>
/// </remarks>
public partial class OverlayWindow : Window
{
    /// <summary>フェードアウトにかける時間。</summary>
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(400));

    /// <summary>前面ウィンドウの確認間隔。人が切り替えを認識する速さに対して十分細かい。</summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _watchdog;

    /// <summary>いま表示している内容（訳文、または「翻訳中…」などのお知らせ）。</summary>
    private OverlayContent? _current;

    /// <summary>
    /// 最後に重ねた訳文。FR-OVL-08 で消したあと、もう一度出すときに使う。
    /// </summary>
    /// <remarks>
    /// お知らせ（<see cref="ShowMessage"/>）はここに入れない。消して出し直したら
    /// 「翻訳中…」だけが戻ってくる、という状態を作らないため。
    /// </remarks>
    private OverlayContent? _lastContent;

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

        var content = new OverlayContent(lines, bounds, sourceWidth, sourceHeight, keepVisibleWhileForeground ?? []);
        _lastContent = content;
        Present(content);
    }

    /// <summary>
    /// 対象の左上に短い知らせを 1 行出す（「翻訳中…」「文字が見つかりませんでした」など）。
    /// </summary>
    /// <remarks>
    /// <para>ホットキーはゲーム中に押されるため、操作ウィンドウの表示は見えない。
    /// 押したのに数秒間なにも起きないと、効いていないと思って押し直される
    /// （そのたびに API を呼ぶことになる）。受け付けたことをゲーム画面の上で返す。</para>
    /// <para>表示の仕組み（前面の追従・自動フェード）は <see cref="ShowLines"/> と共通にしている。</para>
    /// </remarks>
    public void ShowMessage(
        string message,
        ScreenRect bounds,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<nint>? keepVisibleWhileForeground = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);

        // 1080p でおよそ 18px の文字になる高さ。解像度に比例させる。
        var height = Math.Max(24, sourceHeight / 36);
        var margin = Math.Max(8, sourceHeight / 60);

        // 出し直しの対象（_lastContent）は変えない。
        Present(new OverlayContent(
            [new OcrLine(message, 1f, new OcrBox(margin, margin, 1, height))],
            bounds, sourceWidth, sourceHeight, keepVisibleWhileForeground ?? []));
    }

    /// <summary>表示を消す。ウィンドウ自体は使い回す（再表示を速くするため）。</summary>
    /// <remarks>最後の訳文は覚えたまま。<see cref="ToggleVisibility"/> で出し直せる。</remarks>
    public void HideOverlay()
    {
        _watchdog.Stop();
        CancelFade();
        LayerCanvas.Children.Clear();
        Hide();
    }

    /// <summary>
    /// 表示を消し、最後の訳文も忘れる。
    /// </summary>
    /// <remarks>
    /// 新しい翻訳を始めるときに呼ぶ。この時点で前の訳文は古くなっている。
    /// 覚えたままにすると、翻訳中に FR-OVL-08 のキーを 2 回押しただけで
    /// <b>古い訳文が今の画面の上に戻ってくる</b>（問題 F と同じ害）。
    /// </remarks>
    public void Clear()
    {
        HideOverlay();
        _lastContent = null;
    }

    /// <summary>
    /// 表示を切り替える（FR-OVL-08「オーバーレイ全体を 1 キーで即時 ON/OFF」）。
    /// </summary>
    /// <returns>切り替えた後に表示されていれば <c>true</c>。</returns>
    /// <remarks>
    /// <para><b>OFF</b>: 表示中（フェード中を含む）ならすぐ消す。
    /// 前の訳文が次の画面の上に残る問題（PLAN.md 問題 F）への対策の本体。</para>
    /// <para><b>ON</b>: 消えていれば最後の訳文を出し直す。<b>API は呼ばない</b>ため、
    /// 読み返すのに時間も料金もかからない（RULES.md 🟡-8）。出し直した訳文も一定時間で消える（FR-OVL-06）。</para>
    /// <para>出し直すものが無ければ何もしない。</para>
    /// </remarks>
    public bool ToggleVisibility()
    {
        if (IsVisible)
        {
            HideOverlay();
            return false;
        }

        if (_lastContent is null)
        {
            return false;
        }

        Present(_lastContent);
        return true;
    }

    /// <summary>
    /// 内容を表示する。訳文とお知らせの共通の入口。
    /// </summary>
    private void Present(OverlayContent content)
    {
        _current = content;
        _shownAt = DateTime.UtcNow;

        CancelFade();

        if (!IsVisible)
        {
            Show();
        }

        ApplyLayout();
        _watchdog.Start();
    }

    /// <summary>
    /// 出しっぱなしと、別アプリの上への居座りを防ぐ見張り。
    /// </summary>
    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        // ゲームでも操作ウィンドウでもないものが前面に来たら、即座に引っ込める。
        // 無関係なアプリの上に翻訳が残り続けるのを防ぐ。
        var keepVisible = _current?.KeepVisibleWhileForeground ?? [];
        if (keepVisible.Count > 0 && !keepVisible.Contains(WindowFocus.Current))
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
        if (_current is not { } content)
        {
            return;
        }

        var bounds = content.Bounds;

        // Win32 の座標は物理ピクセル、WPF は DIP。PerMonitorV2 なので自分で換算する。
        var dpi = VisualTreeHelper.GetDpi(this);

        Left = bounds.X / dpi.DpiScaleX;
        Top = bounds.Y / dpi.DpiScaleY;
        Width = bounds.Width / dpi.DpiScaleX;
        Height = bounds.Height / dpi.DpiScaleY;

        // 認識に使った画像と、重ねる先の実サイズの比。
        // ボーダーレスフルスクリーンなら 1:1 になる。
        var scaleX = (double)bounds.Width / content.SourceWidth / dpi.DpiScaleX;
        var scaleY = (double)bounds.Height / content.SourceHeight / dpi.DpiScaleY;

        LayerCanvas.Children.Clear();
        foreach (var line in content.Lines)
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

    /// <summary>重ねる内容と、重ねる先の情報。</summary>
    /// <param name="Lines">表示する行。</param>
    /// <param name="Bounds">重ねる先の画面上の矩形（物理ピクセル）。</param>
    /// <param name="SourceWidth">認識に使った画像の幅。</param>
    /// <param name="SourceHeight">認識に使った画像の高さ。</param>
    /// <param name="KeepVisibleWhileForeground">これらのいずれかが前面である間だけ表示する。</param>
    private sealed record OverlayContent(
        IReadOnlyList<OcrLine> Lines,
        ScreenRect Bounds,
        int SourceWidth,
        int SourceHeight,
        IReadOnlyList<nint> KeepVisibleWhileForeground);
}
