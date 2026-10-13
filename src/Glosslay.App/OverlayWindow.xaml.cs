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

    /// <summary>重ね方の設定（FR-OVL-05）。<see cref="Options"/> から差し替える。</summary>
    private OverlayOptions _options = new();

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

    /// <summary>
    /// 重ね方の設定（FR-OVL-05 の文字サイズ）。
    /// </summary>
    /// <remarks>
    /// 差し替えたら <see cref="Refresh"/> を呼ぶこと。出している最中でも見た目を変えられる。
    /// </remarks>
    public OverlayOptions Options
    {
        get => _options;
        set
        {
            _options = value ?? throw new ArgumentNullException(nameof(value));
            AutoHideAfter = value.AutoHideAfter;
        }
    }

    /// <summary>いま出している訳文を、今の設定で描き直す。</summary>
    /// <remarks>
    /// <b>設定を変えた結果をその場で見せるため。</b>
    /// 次の翻訳まで変化が分からないと、大きさを選び直せない。
    /// </remarks>
    public void Refresh()
    {
        if (IsVisible)
        {
            ApplyLayout();
        }
    }

    /// <summary>
    /// 訳文の隅に小さく添える使用量（RULES.md 🟡-8「使用量を常時表示」）。空なら出さない。
    /// </summary>
    /// <remarks>
    /// <para><b>モニタが1枚の人が、ゲーム中に使用量を見られる唯一の機会がここ。</b>
    /// 操作ウィンドウはゲームの裏に隠れている。</para>
    /// <para><b>呼び出した直後に出るので因果が直結する。</b>訳文が出た＝いま 1 回使った、
    /// という対応が一目で分かる。常駐表示（<c>UsageOverlayWindow</c>）と違い、
    /// <b>ゲーム画面を新たに覆わない</b> — どのみち訳文が出ている間だけ。</para>
    /// </remarks>
    public string UsageNote { get; set; } = string.Empty;

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
            var label = CreateLabel(line, scaleX, scaleY);
            Canvas.SetLeft(label, line.Box.X * scaleX);
            Canvas.SetTop(label, line.Box.Y * scaleY);
            LayerCanvas.Children.Add(label);
        }

        AddUsageNote();
    }

    /// <summary>
    /// 使用量を右下の隅に小さく添える（RULES.md 🟡-8）。
    /// </summary>
    /// <remarks>
    /// <para><b>右下に置く。</b>訳文は原文の位置に重なるため、どこに出るか決め打ちできない。
    /// 隅のうち右下は、ゲームの体力・弾数などの表示（左上と下辺中央に多い）と最も当たりにくい。</para>
    /// <para><b>訳文より薄く小さくする。</b>読みに来たときだけ読めればよく、
    /// 訳文から視線を奪ってはいけない。</para>
    /// </remarks>
    private void AddUsageNote()
    {
        if (UsageNote.Length == 0)
        {
            return;
        }

        var note = new TextBlock
        {
            Text = UsageNote,
            FontSize = Math.Max(10, Height / 90),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            Padding = new Thickness(5, 2, 5, 2),
            TextWrapping = TextWrapping.NoWrap,
        };

        // 実寸が決まらないと右下に寄せられない。
        note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var margin = Math.Max(6, Height / 120);
        Canvas.SetLeft(note, Math.Max(0, Width - note.DesiredSize.Width - margin));
        Canvas.SetTop(note, Math.Max(0, Height - note.DesiredSize.Height - margin));
        LayerCanvas.Children.Add(note);
    }

    /// <summary>
    /// 1 つ分の札を作る。
    /// </summary>
    /// <remarks>
    /// <para>文字の大きさは元の枠の高さに合わせる。ゲームが大きく描いている文字は
    /// 大きく、小さい文字は小さく重ねないと、どこの訳か分からなくなる。</para>
    /// <para><b>行を結合した札（FR-TRN-10）は枠が元の行すべてを囲む高さになっている。</b>
    /// そのまま使うと文字が数倍の大きさになるため、結合した行数で割る。</para>
    /// <para><b>結合した札は、元の段落と同じ大きさまで広げる。</b>
    /// 札は中身に合わせて縮むが、訳文は原文より短く、行数も減る。
    /// 縮んだままだと<b>原文の後ろの行が札からはみ出して残り</b>、
    /// 訳文と原文が混ざって読めなくなる（2026-09-27、実画面で確認）。
    /// 結合前は 1 行ごとに札が乗っていたため起きなかった問題。</para>
    /// </remarks>
    private Border CreateLabel(OcrLine line, double scaleX, double scaleY)
    {
        // 小さすぎると読めないので下限を設ける。
        // 倍率（FR-OVL-05 の小・中・大）は下限にもかける。
        // 下限を固定にすると、「小」を選んでも小さい文字だけが変わらない。
        var scale = _options.TextScale;
        var fontSize = Math.Max(
            11 * scale,
            line.Box.Height / (double)line.SourceLineCount * scaleY * 0.62 * scale);
        var isJoined = line.SourceLineCount > 1;

        var label = new Border
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
                TextWrapping = isJoined ? TextWrapping.Wrap : TextWrapping.NoWrap,
            },
        };

        if (isJoined)
        {
            // 元の段落を覆う大きさ。幅は折り返し位置も兼ねる。
            // 極端に細い枠で 1 文字ずつ折り返さないよう下限を置く。
            label.Width = Math.Max(fontSize * 8, line.Box.Width * scaleX);
            label.MinHeight = line.Box.Height * scaleY;
        }

        return label;
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
