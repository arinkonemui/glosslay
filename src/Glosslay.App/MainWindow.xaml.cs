using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glosslay.Capture;
using Glosslay.Configuration;
using Glosslay.Imaging;
using Glosslay.Input;
using Glosslay.Ocr;
using Glosslay.Translation;

namespace Glosslay;

/// <summary>
/// v0.1 PoC のキャプチャ検証用ウィンドウ。
/// </summary>
/// <remarks>
/// UI の作り込みは PoC の対象外（PLAN.md）。このウィンドウの目的は 2 つ。
/// <list type="number">
///   <item>P0-2「排他フルスクリーンのゲームで取得できるか実測する」</item>
///   <item>P0-7 の精度計測に使うサンプル画像の採取</item>
/// </list>
/// </remarks>
public partial class MainWindow : Window, IDisposable
{
    /// <summary>遅延キャプチャの待ち時間。ゲームへ切り替えるための猶予。</summary>
    private static readonly TimeSpan CaptureDelay = TimeSpan.FromSeconds(5);

    private readonly PaddleOcrEngine _ocrEngine = new();
    private readonly ApiKeyStore _apiKeyStore = ApiKeyStore.Gemini;

    /// <summary>
    /// 翻訳バックエンド。<see cref="ITranslator"/> 越しに持つ（RULES.md 🟡-2）。
    /// </summary>
    /// <remarks>
    /// gemini.json を編集したら作り直すため、フィールドは差し替え可能にしている。
    /// </remarks>
#pragma warning disable CA1859 // 具象型の方が速いが、ここは差し替え点。
                              // v1.0 で LocalNmtTranslator に切り替える前提のため
                              // インターフェースのまま持つ（RULES.md 🟡-2）。
    private ITranslator _translator = new GeminiTranslator();
#pragma warning restore CA1859

    private ScreenCapture? _capture;
    private CaptureTarget? _activeTarget;
    private bool _activeRemoveBorder;
    private bool _activeCaptureCursor;

    private CapturedFrame? _lastFrame;
    private BitmapSource? _lastBitmap;
    private CaptureTarget? _lastTarget;
    private OverlayWindow? _overlay;

    /// <summary>OCR の対象範囲（元画像の画素座標）。null なら全体。</summary>
    private Int32Rect? _region;

    /// <summary>直前の OCR 結果。翻訳ボタンが原文として使う。</summary>
    private IReadOnlyList<OcrLine> _lastOcrLines = [];

    private Point _dragOrigin;
    private bool _isDragging;

    /// <summary>画面を翻訳するキーの検出。設定が読めなければ null（そのキーなしで動く）。</summary>
    private HotkeyDetector? _translateHotkey;

    /// <summary>オーバーレイの表示を ON/OFF するキーの検出（FR-OVL-08）。</summary>
    private HotkeyDetector? _toggleHotkey;

    /// <summary><c>WM_INPUT</c> を受け取るためのフック先。</summary>
    private HwndSource? _hwndSource;

    /// <summary>
    /// 画面の翻訳が進行中か。
    /// </summary>
    /// <remarks>
    /// 全画面だと数秒かかる（課題4）。その間に押し直されても受け付けない。
    /// 受け付けると待ち行列ができ、同じ画面に API を何度も払うことになる（RULES.md 🟡-8）。
    /// </remarks>
    private bool _isTranslatingScreen;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnWindowClosed;
    }

    /// <summary>
    /// ウィンドウハンドルができた時点でホットキーの受信を始める（FR-MOD-01 / FR-INP-02）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        StartHotkey();
    }

    private void StartHotkey()
    {
        var options = HotkeyOptions.Load();
        var problems = new List<string>();

        // 設定の誤りでアプリ全体を止めない。使えないキーだけ無効にして理由を見せる。
        HotkeyGesture? translate = HotkeyGesture.TryParse(options.TranslateScreen, out var t) ? t : null;
        HotkeyGesture? toggle = HotkeyGesture.TryParse(options.ToggleOverlay, out var o) ? o : null;

        if (translate is null)
        {
            problems.Add($"hotkeys.json の翻訳キー「{options.TranslateScreen}」を解釈できません。");
        }

        if (toggle is null)
        {
            problems.Add($"hotkeys.json の ON/OFF キー「{options.ToggleOverlay}」を解釈できません。");
        }
        else if (toggle == translate)
        {
            // 同じ組み合わせだと、1 回押すたびに翻訳と ON/OFF が同時に走る。
            toggle = null;
            problems.Add("ON/OFF キーが翻訳キーと同じ組み合わせのため、ON/OFF キーを無効にしました。");
        }

        if (translate is null && toggle is null)
        {
            HotkeyText.Text = "ホットキー無効: " + string.Join(" ", problems);
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource.AddHook(OnWindowMessage);

        RawKeyboard.Register(handle);
        _translateHotkey = translate is { } translateGesture ? new HotkeyDetector(translateGesture) : null;
        _toggleHotkey = toggle is { } toggleGesture ? new HotkeyDetector(toggleGesture) : null;

        var descriptions = new List<string>();
        if (translate is { } tg)
        {
            descriptions.Add($"{tg} — 前面の画面を翻訳して、原文の位置に重ねる");
        }

        if (toggle is { } og)
        {
            descriptions.Add($"{og} — 訳文を消す / 最後の訳文を出し直す");
        }

        HotkeyText.Text = string.Join(Environment.NewLine, [.. descriptions, .. problems]);
    }

    /// <summary>
    /// <c>WM_INPUT</c> を拾ってホットキーを判定する。
    /// </summary>
    /// <remarks>
    /// <para><b>キーの内容は判定に使ったらそのまま捨てる。記録・ログ出力をしないこと。</b>
    /// Raw Input はシステム全体の打鍵を受け取るため、残せばキーロガーと変わらない。</para>
    /// <para><c>handled</c> は立てない。<c>WM_INPUT</c> は <c>DefWindowProc</c> を通して
    /// OS に後始末させる決まりがある。そもそも Raw Input は入力を横取りしないため、
    /// ここで何をしてもゲームにはキーが届く（FR-INP-02）。</para>
    /// </remarks>
    private nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != RawKeyboard.WmInput || !RawKeyboard.TryRead(lParam, out var virtualKey, out var isKeyUp))
        {
            return 0;
        }

        // 両方の検出器に同じ入力を必ず渡す。修飾キーの押下状態はそれぞれが持っているため、
        // 片方にしか渡さないと、もう片方の Ctrl / Shift の状態がずれる。
        if (_translateHotkey?.Process(virtualKey, isKeyUp) == true)
        {
            _ = RunGuardedAsync(TranslateScreenAsync);
        }

        if (_toggleHotkey?.Process(virtualKey, isKeyUp) == true)
        {
            _ = RunGuardedAsync(ToggleOverlayAsync);
        }

        return 0;
    }

    /// <summary>キャプチャ画像の保存先（FR-CFG-00）。</summary>
    private static string CaptureDirectory => GlosslayPaths.Captures;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ScreenCapture.IsSupported)
        {
            StatusText.Text = "この環境では Windows.Graphics.Capture が利用できません。Windows 11 か確認してください。";
            CaptureNowButton.IsEnabled = false;
            CaptureDelayedButton.IsEnabled = false;
            return;
        }

        RefreshTargets();
        RefreshTranslatorState();

        // FR-OCR-08: 初回 OCR で待たせないよう、起動時に非同期でモデルを読み込んでおく。
        _ = WarmUpOcrAsync();
    }

    private async Task WarmUpOcrAsync()
    {
        try
        {
            await _ocrEngine.InitializeAsync().ConfigureAwait(true);
            StatusText.Text = $"準備完了（{_ocrEngine.Name}）";
        }
        catch (FileNotFoundException ex)
        {
            // モデル未配置は想定内。キャプチャ自体は使えるので落とさない。
            StatusText.Text = $"OCR は使えません: {ex.Message}";
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    /// <summary>キャプチャセッションと OCR エンジンを解放する。ウィンドウを閉じたときに呼ぶ。</summary>
    public void Dispose()
    {
        if (_hwndSource is not null)
        {
            RawKeyboard.Unregister();
            _hwndSource.RemoveHook(OnWindowMessage);
            _hwndSource = null;
        }

        ReleaseCapture();
        _overlay?.Close();
        _ocrEngine.Dispose();
        _translator.Dispose();
        GC.SuppressFinalize(this);
    }

    // ===== 対象の選択 =====

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshTargets();

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        RefreshTargets();
    }

    /// <summary>枠の有無・カーソルの有無はセッション生成時に決まるため、変更したら作り直す。</summary>
    private void OnOptionChanged(object sender, RoutedEventArgs e) => ReleaseCapture();

    private void OnTargetSelectionChanged(object sender, SelectionChangedEventArgs e) => ReleaseCapture();

    private void RefreshTargets()
    {
        var targets = MonitorModeRadio.IsChecked == true
            ? CaptureTargetEnumerator.EnumerateMonitors()
            : CaptureTargetEnumerator.EnumerateWindows();

        TargetList.ItemsSource = targets;
        if (targets.Count > 0)
        {
            TargetList.SelectedIndex = 0;
        }

        StatusText.Text = $"対象 {targets.Count} 件を検出しました。";
    }

    // ===== キャプチャ =====

    private async void OnCaptureNowClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(() => CaptureAsync(TimeSpan.Zero)).ConfigureAwait(true);

    private async void OnCaptureDelayedClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(() => CaptureAsync(CaptureDelay)).ConfigureAwait(true);

    private async Task CaptureAsync(TimeSpan delay)
    {
        if (TargetList.SelectedItem is not CaptureTarget target)
        {
            StatusText.Text = "対象が選択されていません。";
            return;
        }

        SetBusy(true);
        try
        {
            for (var remaining = (int)delay.TotalSeconds; remaining > 0; remaining--)
            {
                StatusText.Text = $"{remaining} 秒後にキャプチャします… 対象のゲームへ切り替えてください。";
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            }

            StatusText.Text = "キャプチャ中…";

            var startWatch = Stopwatch.StartNew();
            var capture = await EnsureCaptureAsync(target).ConfigureAwait(true);
            var startMs = startWatch.Elapsed.TotalMilliseconds;

            var frameWatch = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await capture.GetNextFrameAsync(timeout.Token).ConfigureAwait(true);
            var frameMs = frameWatch.Elapsed.TotalMilliseconds;

            ShowFrame(target, capture, frame, startMs, frameMs);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "フレームが 5 秒以内に届きませんでした。"
                + "排他フルスクリーンの場合は「モニタ全体」へ切り替えて試してください（FR-CAP-03）。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// セッションは開始したまま保持する。作り直すと初回フレームまで数百 ms かかるため。
    /// </summary>
    private async Task<ScreenCapture> EnsureCaptureAsync(CaptureTarget target)
    {
        var removeBorder = RemoveBorderCheck.IsChecked == true;
        var captureCursor = CaptureCursorCheck.IsChecked == true;

        if (_capture is not null
            && _activeTarget == target
            && _activeRemoveBorder == removeBorder
            && _activeCaptureCursor == captureCursor)
        {
            return _capture;
        }

        ReleaseCapture();

        var capture = await ScreenCapture.StartAsync(target, removeBorder, captureCursor).ConfigureAwait(true);
        capture.Closed += OnCaptureClosed;

        _capture = capture;
        _activeTarget = target;
        _activeRemoveBorder = removeBorder;
        _activeCaptureCursor = captureCursor;
        return capture;
    }

    private void OnCaptureClosed(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "対象が閉じられました。一覧を更新してください。";
            ReleaseCapture();
        });

    private void ShowFrame(
        CaptureTarget target, ScreenCapture capture, CapturedFrame frame, double startMs, double frameMs)
    {
        UpdatePreview(target, frame);

        var nonBlackRatio = CalculateNonBlackRatio(frame);
        var blackWarning = nonBlackRatio < 0.001
            ? "  ← ほぼ黒。取得に失敗している可能性が高い（FR-CAP-03）"
            : string.Empty;

        ResultText.Text = string.Join(Environment.NewLine,
            $"対象           : {target}",
            $"サイズ         : {frame.Width} x {frame.Height} (stride={frame.Stride})",
            $"枠を消せたか   : {(capture.IsBorderRemoved ? "はい" : "いいえ（OS が許可しなかった）")}",
            $"セッション開始 : {startMs:F1} ms",
            $"フレーム取得   : {frameMs:F1} ms",
            $"非黒ピクセル   : {nonBlackRatio * 100:F1} %{blackWarning}");

        StatusText.Text = "キャプチャしました。";
    }

    /// <summary>プレビューを最新のフレームに差し替える。結果欄は書き換えない。</summary>
    private void UpdatePreview(CaptureTarget target, CapturedFrame frame)
    {
        _lastFrame = frame;
        _lastTarget = target;
        _lastBitmap = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
        _lastBitmap.Freeze();

        PreviewImage.Source = _lastBitmap;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = true;
        OcrButton.IsEnabled = true;
        OverlayButton.IsEnabled = true;
    }

    /// <summary>取得できたのが黒画面でないかの判定材料（FR-CAP-03）。</summary>
    private static double CalculateNonBlackRatio(CapturedFrame frame)
    {
        long nonBlack = 0;
        var pixels = frame.Pixels;
        for (var i = 0; i + 2 < pixels.Length; i += 4)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
            {
                nonBlack++;
            }
        }

        var total = (long)frame.Width * frame.Height;
        return total == 0 ? 0 : (double)nonBlack / total;
    }

    // ===== 前処理と範囲指定（P0-3） =====

    /// <summary>UI の設定から前処理のオプションを組み立てる。</summary>
    private PreprocessOptions BuildPreprocessOptions() => new()
    {
        Scale = ComboValue(ScaleCombo, 1.0),
        Contrast = ComboValue(ContrastCombo, 1.0),
        AutoContrast = AutoContrastCheck.IsChecked == true,
        RemoveBackground = RemoveBackgroundCheck.IsChecked == true,
        Binarize = BinarizeCheck.IsChecked == true,
        Invert = InvertCheck.IsChecked == true,
    };

    private static double ComboValue(System.Windows.Controls.ComboBox combo, double fallback) =>
        combo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag }
        && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>
    /// フレームを OCR にかけられる形にする。範囲指定があれば切り出し、前処理を適用する。
    /// </summary>
    /// <param name="frame">撮った画面。</param>
    /// <param name="useRegion">
    /// プレビュー上でドラッグした範囲を使うか。ホットキーでは <c>false</c> にする。
    /// 範囲は一覧で選んだ対象の画像に対して引いたもので、前面の別ウィンドウに当てると
    /// 見当違いの場所を切り抜くため。
    /// </param>
    /// <returns>加工後の画像と、元画像へ座標を戻すための情報。</returns>
    private (Bgra32Image Image, PreprocessOptions Options, int OffsetX, int OffsetY)
        PrepareForOcr(CapturedFrame frame, bool useRegion = true)
    {
        var image = frame.AsImage();
        var offsetX = 0;
        var offsetY = 0;

        if (useRegion && _region is { } region)
        {
            // 範囲を絞ると検出コストが大きく下がる。全画面の 1/3 程度になる。
            image = image.Crop(region.X, region.Y, region.Width, region.Height);
            offsetX = region.X;
            offsetY = region.Y;
        }

        var options = BuildPreprocessOptions();
        return (ImagePreprocessor.Apply(image, options), options, offsetX, offsetY);
    }

    /// <summary>OCR の結果を、加工後の座標から元画像の座標へ戻す。</summary>
    private static IReadOnlyList<OcrLine> MapToSource(
        IReadOnlyList<OcrLine> lines, double scale, int offsetX, int offsetY)
    {
        if (scale is 1.0 && offsetX == 0 && offsetY == 0)
        {
            return lines;
        }

        return [.. lines.Select(line => line with
        {
            Box = new OcrBox(
                (int)(line.Box.X / scale) + offsetX,
                (int)(line.Box.Y / scale) + offsetY,
                (int)(line.Box.Width / scale),
                (int)(line.Box.Height / scale)),
        })];
    }

    private void OnClearRegionClick(object sender, RoutedEventArgs e)
    {
        _region = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        StatusText.Text = "範囲の指定を解除しました。画像全体を OCR します。";
    }

    private void OnSelectionStart(object sender, MouseButtonEventArgs e)
    {
        if (_lastBitmap is null)
        {
            return;
        }

        _isDragging = true;
        _dragOrigin = e.GetPosition(SelectionCanvas);
        SelectionCanvas.CaptureMouse();

        SelectionRectangle.Visibility = Visibility.Visible;
        UpdateSelectionRectangle(_dragOrigin, _dragOrigin);
    }

    private void OnSelectionMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            UpdateSelectionRectangle(_dragOrigin, e.GetPosition(SelectionCanvas));
        }
    }

    private void OnSelectionEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        SelectionCanvas.ReleaseMouseCapture();

        var end = e.GetPosition(SelectionCanvas);
        UpdateSelectionRectangle(_dragOrigin, end);

        _region = ToSourceRegion(_dragOrigin, end);
        if (_region is { } region)
        {
            StatusText.Text = $"範囲を指定しました: {region.X},{region.Y} "
                + $"{region.Width}x{region.Height}（元画像の画素）";
        }
        else
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            StatusText.Text = "範囲が小さすぎます。指定を解除しました。";
        }
    }

    private void UpdateSelectionRectangle(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);

        System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, x);
        System.Windows.Controls.Canvas.SetTop(SelectionRectangle, y);
        SelectionRectangle.Width = Math.Abs(a.X - b.X);
        SelectionRectangle.Height = Math.Abs(a.Y - b.Y);
    }

    /// <summary>
    /// プレビュー上の座標を元画像の画素座標へ変換する。
    /// </summary>
    /// <remarks>
    /// プレビューは Stretch="Uniform" のため上下または左右に余白ができる。
    /// その余白を除いてから比率を掛けないと、指定した位置とずれる。
    /// </remarks>
    private Int32Rect? ToSourceRegion(Point a, Point b)
    {
        if (_lastBitmap is null)
        {
            return null;
        }

        var controlWidth = SelectionCanvas.ActualWidth;
        var controlHeight = SelectionCanvas.ActualHeight;
        if (controlWidth <= 0 || controlHeight <= 0)
        {
            return null;
        }

        var imageAspect = (double)_lastBitmap.PixelWidth / _lastBitmap.PixelHeight;
        var controlAspect = controlWidth / controlHeight;

        double displayWidth, displayHeight;
        if (imageAspect > controlAspect)
        {
            displayWidth = controlWidth;
            displayHeight = controlWidth / imageAspect;
        }
        else
        {
            displayHeight = controlHeight;
            displayWidth = controlHeight * imageAspect;
        }

        var marginX = (controlWidth - displayWidth) / 2;
        var marginY = (controlHeight - displayHeight) / 2;

        var left = ToSource(Math.Min(a.X, b.X), marginX, displayWidth, _lastBitmap.PixelWidth);
        var top = ToSource(Math.Min(a.Y, b.Y), marginY, displayHeight, _lastBitmap.PixelHeight);
        var right = ToSource(Math.Max(a.X, b.X), marginX, displayWidth, _lastBitmap.PixelWidth);
        var bottom = ToSource(Math.Max(a.Y, b.Y), marginY, displayHeight, _lastBitmap.PixelHeight);

        var width = right - left;
        var height = bottom - top;

        // 極端に小さい範囲は誤操作とみなす。
        return width >= 16 && height >= 16 ? new Int32Rect(left, top, width, height) : null;

        static int ToSource(double value, double margin, double display, int sourceSize) =>
            Math.Clamp((int)Math.Round((value - margin) / display * sourceSize), 0, sourceSize);
    }

    // ===== OCR =====

    private async void OnOcrClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(RunOcrAsync).ConfigureAwait(true);

    private async Task RunOcrAsync()
    {
        if (_lastFrame is null || _lastBitmap is null)
        {
            StatusText.Text = "先にキャプチャしてください。";
            return;
        }

        OcrButton.IsEnabled = false;
        StatusText.Text = "OCR 実行中…";
        try
        {
            var (image, options, offsetX, offsetY) = PrepareForOcr(_lastFrame);
            var result = await _ocrEngine.RecognizeAsync(image).ConfigureAwait(true);
            var lines = MapToSource(result.Lines, options.Scale, offsetX, offsetY);

            _lastOcrLines = lines;
            TranslateButton.IsEnabled = lines.Count > 0;

            PreviewImage.Source = DrawBoxes(_lastBitmap, lines);
            ResultText.Text = string.Join(Environment.NewLine,
                $"前処理     : {options}",
                $"OCR 入力   : {image.Width}x{image.Height}"
                    + (_region is { } r ? $"（範囲 {r.X},{r.Y} {r.Width}x{r.Height} を切り出し）" : "（全体）"),
                $"処理時間   : {result.Elapsed.TotalMilliseconds:F0} ms",
                string.Empty,
                FormatOcrResult(lines));

            StatusText.Text = $"OCR 完了: {lines.Count} 行 / "
                + $"{result.Elapsed.TotalMilliseconds:F0} ms（{_ocrEngine.Name}）";
        }
        finally
        {
            OcrButton.IsEnabled = true;
        }
    }

    // ===== 翻訳（P0-5） =====

    private void RefreshTranslatorState()
    {
        var configured = _translator.IsConfigured;

        TranslatorStateText.Text = configured
            ? $"{_translator.Name} — キー登録済み"
            : $"{_translator.Name} — キー未登録";

        DeleteApiKeyButton.IsEnabled = configured;
    }

    private void OnSaveApiKeyClick(object sender, RoutedEventArgs e)
    {
        // Password は読んだ直後に使い切り、変数にも UI にも残さない。
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            StatusText.Text = "APIキーが空です。";
            return;
        }

        _apiKeyStore.Save(ApiKeyBox.Password);
        ApiKeyBox.Clear();

        RefreshTranslatorState();
        StatusText.Text = "APIキーを暗号化して保存しました。「翻訳を実行」で試せます。";
    }

    private void OnDeleteApiKeyClick(object sender, RoutedEventArgs e)
    {
        _apiKeyStore.Delete();
        ApiKeyBox.Clear();

        RefreshTranslatorState();
        StatusText.Text = "APIキーを削除しました。";
    }

    /// <summary>
    /// モデル名などの設定ファイルを開く（FR-TRN-07 / RULES.md 🟡-3）。
    /// </summary>
    /// <remarks>
    /// 無ければ既定値で作ってから開く。ファイルが存在しないと「どこを直せばいいのか」が分からない。
    /// 閉じた後に読み直すのではなく、開くたびに翻訳側を作り直して反映させる。
    /// </remarks>
    private void OnOpenGeminiSettingsClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(GlosslayPaths.Root, "gemini.json");
        if (!File.Exists(path))
        {
            GeminiOptions.Load().Save();
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

        // 編集後に「更新」を押させるのは忘れるため、次の翻訳で必ず読み直す形にする。
        _translator.Dispose();
        _translator = new GeminiTranslator();
        RefreshTranslatorState();

        StatusText.Text = "gemini.json を開きました。保存すると次の翻訳から反映されます。";
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(RunTranslateAsync).ConfigureAwait(true);

    private async Task RunTranslateAsync()
    {
        if (_lastOcrLines.Count == 0)
        {
            StatusText.Text = "先に OCR を実行してください。";
            return;
        }

        // 読み順に並べ、改行で繋いで 1 回の呼び出しにまとめる。
        // FR-TRN-10 の「行分割で切れた文の結合」は未実装（PoC の範囲外）。
        // 行をまとめず 1 行ずつ投げると呼び出し回数が行数倍になり、RULES.md 🟡-8 に反する。
        var source = string.Join(
            Environment.NewLine, TextAggregator.InReadingOrder(_lastOcrLines).Select(line => line.Text));

        TranslateButton.IsEnabled = false;
        StatusText.Text = "翻訳中…";
        try
        {
            var result = await _translator
                .TranslateAsync(new TranslationRequest(source))
                .ConfigureAwait(true);

            ResultText.Text = string.Join(Environment.NewLine,
                $"バックエンド : {_translator.Name}",
                $"処理時間     : {result.Elapsed.TotalMilliseconds:F0} ms"
                    + (result.TotalTokens is { } tokens ? $" / {tokens} トークン" : string.Empty),
                $"状態         : {DescribeOutcome(result)}",
                string.Empty,
                "--- 原文 ---",
                source,
                string.Empty,
                "--- 訳文 ---",
                result.IsSuccess ? result.Text : "（なし）");

            StatusText.Text = result.IsSuccess
                ? $"翻訳完了: {result.Elapsed.TotalMilliseconds:F0} ms"
                : $"翻訳失敗: {result.Message}";
        }
        finally
        {
            TranslateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 結果を利用者向けの文にする。
    /// </summary>
    /// <remarks>
    /// フォールバック対象かどうかを明示している。v1.0 ではここでローカルNMTへ切り替わる
    /// （FR-TRN-06）が、PoC はローカルNMT未実装のため「切り替え先がない」と伝えるに留める。
    /// </remarks>
    private static string DescribeOutcome(TranslationResult result)
    {
        var state = result.Outcome switch
        {
            TranslationOutcome.Succeeded => "成功",
            TranslationOutcome.NotConfigured => "キー未登録",
            TranslationOutcome.RateLimited => "利用上限（HTTP 429）",
            TranslationOutcome.NetworkError => "通信エラー",
            TranslationOutcome.ServiceError => "サービスエラー",
            TranslationOutcome.Blocked => "応答が拒否された",
            _ => result.Outcome.ToString(),
        };

        if (result.Message is { Length: > 0 } message)
        {
            state += $" — {message}";
        }

        return result.ShouldFallBack
            ? state + "（v1.0 ではローカルNMTへ自動フォールバックする箇所。PoC は未実装）"
            : state;
    }

    private static string FormatOcrResult(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0)
        {
            return "文字が検出されませんでした。"
                + "範囲を絞る、または前処理（拡大・コントラスト）を試してください。";
        }

        return string.Join(Environment.NewLine, TextAggregator.InReadingOrder(lines)
            .Select(line => $"{line.Confidence:F3}  [{line.Box.X,5},{line.Box.Y,5}]  {line.Text}"));
    }

    /// <summary>認識できた位置を確認できるよう、プレビューに枠を重ねる。</summary>
    private static RenderTargetBitmap DrawBoxes(BitmapSource source, IReadOnlyList<OcrLine> lines)
    {
        var pen = new Pen(Brushes.OrangeRed, 2);
        pen.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            foreach (var line in lines)
            {
                context.DrawRectangle(
                    null, pen, new Rect(line.Box.X, line.Box.Y, line.Box.Width, line.Box.Height));
            }
        }

        var target = new RenderTargetBitmap(
            source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    // ===== オーバーレイ（P0-6） =====

    private async void OnOverlayClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(ShowOverlayAsync).ConfigureAwait(true);

    private async Task ShowOverlayAsync()
    {
        if (TargetList.SelectedItem is not CaptureTarget target)
        {
            StatusText.Text = "対象が選択されていません。";
            return;
        }

        if (!CaptureTargetBounds.TryGet(target, out var bounds))
        {
            StatusText.Text = "対象の画面上の位置を取得できませんでした。";
            return;
        }

        OverlayButton.IsEnabled = false;
        try
        {
            // 押した時点の画面を撮り直す。前のキャプチャを使い回すと、
            // ゲーム内で画面を切り替えたときに古い内容を表示してしまう（FR-MOD-01）。
            StatusText.Text = "キャプチャ中…";
            var capture = await EnsureCaptureAsync(target).ConfigureAwait(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await capture.GetNextFrameAsync(timeout.Token).ConfigureAwait(true);
            UpdatePreview(target, frame);

            StatusText.Text = "OCR 実行中…";
            var (image, options, offsetX, offsetY) = PrepareForOcr(frame);
            var result = await _ocrEngine.RecognizeAsync(image).ConfigureAwait(true);
            var lines = MapToSource(result.Lines, options.Scale, offsetX, offsetY);

            var overlay = EnsureOverlay();

            // ウィンドウ指定なら、ゲームか操作ウィンドウが前面の間だけ表示する。
            // モニタ全体はどれがゲームか特定できないため追従しない（自動フェードのみ）。
            IReadOnlyList<nint>? keepVisibleFor = target.Kind == CaptureTargetKind.Window
                ? [target.Handle, new WindowInteropHelper(this).Handle]
                : null;

            overlay.ShowLines(
                lines, bounds, frame.Width, frame.Height, keepVisibleFor);
            OverlayHideButton.IsEnabled = true;

            // P0-6 の検証項目。設定したつもりで終わらせず、実際の値を出す。
            var style = overlay.StyleState;
            ResultText.Text = string.Join(Environment.NewLine,
                $"重ねた先       : {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}（物理px）",
                $"認識画像       : {frame.Width}x{frame.Height}（このボタンで撮り直したもの）",
                $"表示した行     : {lines.Count}",
                $"前処理         : {options}",
                $"自動で消える   : {overlay.AutoHideAfter.TotalSeconds:F0} 秒後（FR-OVL-06）",
                $"前面の追従     : {(keepVisibleFor is null ? "なし（モニタ全体のため）" : "ゲームと操作画面のみ表示")}",
                string.Empty,
                "拡張スタイルの実測値（RULES.md が必須と定める4つ）",
                $"  WS_EX_LAYERED     (透過)          : {Mark(style.IsLayered)}",
                $"  WS_EX_TRANSPARENT (クリックスルー): {Mark(style.IsClickThrough)}",
                $"  WS_EX_NOACTIVATE  (フォーカス維持): {Mark(style.DoesNotActivate)}",
                $"  WS_EX_TOOLWINDOW  (Alt+Tab非表示) : {Mark(style.IsHiddenFromAltTab)}",
                string.Empty,
                style.IsComplete
                    ? "4つとも設定済み。実際に効くかはゲーム上で操作して確認すること。"
                    : "★ 不足があります。要件を満たしていません。");

            StatusText.Text = $"オーバーレイ表示中: {lines.Count} 行 / "
                + $"{result.Elapsed.TotalMilliseconds:F0} ms";
        }
        finally
        {
            OverlayButton.IsEnabled = true;
        }

        static string Mark(bool value) => value ? "OK" : "未設定";
    }

    /// <summary>
    /// オーバーレイを用意する。
    /// </summary>
    /// <remarks>
    /// <para><b><c>Owner</c> を設定してはいけない。</b>WPF では所有元を最小化すると
    /// 所有ウィンドウも一緒に最小化される。ホットキーは「操作ウィンドウを最小化してゲームをする」
    /// 使い方で押されるため、所有させると<b>訳文が黙って出なくなる</b>
    /// （最小化状態でも IsVisible は true のため、ShowLines の再表示も効かない）。</para>
    /// <para>所有させない代わりに、閉じるのは <see cref="Dispose"/> で明示的に行う。
    /// 最前面は Topmost が、Alt+Tab に出ないことは WS_EX_TOOLWINDOW が担うため、所有関係は要らない。</para>
    /// </remarks>
    private OverlayWindow EnsureOverlay() => _overlay ??= new OverlayWindow();

    // ===== ホットキーで画面を翻訳（FR-MOD-01 手動モード） =====

    /// <summary>
    /// 前面の画面を撮り、まとめて翻訳して、訳文を原文の位置に重ねる。
    /// </summary>
    /// <remarks>
    /// <para>想定している場面: スキルアイコンに触れると<b>別の場所</b>に説明が出て、
    /// カーソルを外すと消える。カーソル位置モード（FR-MOD-09）では説明の場所を撮れないため、
    /// キーを押した瞬間の画面全体を翻訳する。</para>
    /// <para><b>撮影を最優先にする。</b>「翻訳中…」が出た時点で撮り終えているので、
    /// そこから先はカーソルを動かしてよい。</para>
    /// </remarks>
    private async Task TranslateScreenAsync()
    {
        if (_isTranslatingScreen)
        {
            return;
        }

        _isTranslatingScreen = true;
        try
        {
            var target = ResolveHotkeyTarget();
            if (target is null)
            {
                StatusText.Text = "ホットキー: 翻訳する画面を特定できませんでした。";
                return;
            }

            if (!CaptureTargetBounds.TryGet(target, out var bounds))
            {
                StatusText.Text = $"ホットキー: {target.DisplayName} の画面上の位置を取得できませんでした。";
                return;
            }

            // モニタ全体を撮るときに、前回の訳文を自分で読み取ってしまわないよう先に消す。
            // 前回の訳文は出し直しの対象からも外す。新しい翻訳を頼んだ時点で古くなっているため
            // （覚えたままだと、翻訳中に ON/OFF キーを 2 回押すと古い訳文が戻ってくる = 問題 F）。
            _overlay?.Clear();

            var total = Stopwatch.StartNew();
            var capture = await EnsureCaptureAsync(target).ConfigureAwait(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await capture.GetNextFrameAsync(timeout.Token).ConfigureAwait(true);
            var captureMs = total.Elapsed.TotalMilliseconds;
            UpdatePreview(target, frame);

            var overlay = EnsureOverlay();

            // ゲームか操作ウィンドウが前面の間だけ出す。モニタ全体はどれがゲームか分からないため追従しない。
            IReadOnlyList<nint>? keepVisibleFor = target.Kind == CaptureTargetKind.Window
                ? [target.Handle, new WindowInteropHelper(this).Handle]
                : null;

            // ここで撮り終えている。受け付けたことをゲーム画面の上で返す。
            overlay.ShowMessage("翻訳中…", bounds, frame.Width, frame.Height, keepVisibleFor);

            var (image, options, _, _) = PrepareForOcr(frame, useRegion: false);
            var ocr = await _ocrEngine.RecognizeAsync(image).ConfigureAwait(true);

            // 読み順に並べてから渡す。崩れた順で渡すと文脈が壊れて訳が悪くなる。
            var lines = TextAggregator.InReadingOrder(MapToSource(ocr.Lines, options.Scale, 0, 0));
            _lastOcrLines = lines;
            TranslateButton.IsEnabled = lines.Count > 0;
            PreviewImage.Source = DrawBoxes(_lastBitmap!, lines);

            if (lines.Count == 0)
            {
                overlay.ShowMessage("文字が見つかりませんでした", bounds, frame.Width, frame.Height, keepVisibleFor);
                StatusText.Text = $"ホットキー: {target.DisplayName} に文字が見つかりませんでした。";
                return;
            }

            var translated = await LineTranslation.TranslateAsync(_translator, lines).ConfigureAwait(true);

            if (!translated.Translation.IsSuccess)
            {
                overlay.ShowMessage(
                    $"翻訳できませんでした: {ShortOutcome(translated.Translation.Outcome)}",
                    bounds, frame.Width, frame.Height, keepVisibleFor);
            }
            else if (translated.LinesToOverlay.Count == 0)
            {
                // 数値だけの画面や、既に日本語の画面。何も重ねないと「効いていない」と区別できない。
                overlay.ShowMessage(
                    "訳す必要のある文字はありませんでした", bounds, frame.Width, frame.Height, keepVisibleFor);
            }
            else
            {
                // 訳して文字が変わった行だけを重ねる。数値だけの行や原文と同じ訳は重ねない。
                overlay.ShowLines(translated.LinesToOverlay, bounds, frame.Width, frame.Height, keepVisibleFor);
            }

            OverlayHideButton.IsEnabled = true;
            ShowScreenTranslationReport(target, frame, lines, translated, captureMs,
                ocr.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "ホットキー: 画面が 5 秒以内に取得できませんでした。";
        }
        finally
        {
            _isTranslatingScreen = false;
        }
    }

    /// <summary>
    /// ホットキーで翻訳する画面を決める。
    /// </summary>
    /// <remarks>
    /// <para>ゲーム中に押されたなら、前面にあるのがゲーム。一覧から選ぶ必要がない。</para>
    /// <para>一覧の選択を使うのは<b>操作ウィンドウが前面のときだけ</b>（PoC の検証用）。
    /// デスクトップなど撮れないものが前面のときに一覧へ黙って切り替えると、
    /// 見ていない画面を翻訳することになり予測しにくい。その場合は何もしない。</para>
    /// </remarks>
    private CaptureTarget? ResolveHotkeyTarget()
    {
        var foreground = WindowFocus.Current;
        if (foreground == new WindowInteropHelper(this).Handle)
        {
            return TargetList.SelectedItem as CaptureTarget;
        }

        return CaptureTargetEnumerator.TryDescribe(foreground, out var target) ? target : null;
    }

    /// <summary>オーバーレイに出す短い失敗理由。詳細は操作ウィンドウに出す。</summary>
    private static string ShortOutcome(TranslationOutcome outcome) => outcome switch
    {
        TranslationOutcome.NotConfigured => "APIキーが未登録です",
        TranslationOutcome.RateLimited => "利用上限に達しました（しばらく待つと戻ります）",
        TranslationOutcome.NetworkError => "通信できませんでした",
        TranslationOutcome.ServiceError => "翻訳サービスがエラーを返しました",
        TranslationOutcome.Blocked => "翻訳が拒否されました",
        _ => outcome.ToString(),
    };

    /// <summary>
    /// 操作ウィンドウに内訳を出す。
    /// </summary>
    /// <remarks>
    /// <para>工程ごとの時間を分けて出すのは課題4（全画面が 1.5 秒に収まらない）の計測のため。</para>
    /// <para>対応付けの数を出すのは、LLM が番号の形式を守らなかったときに黙って欠落させないため。</para>
    /// <para>翻訳に回さなかった行も一覧に残す。除外のルールが訳すべき行まで落としていないか、
    /// 実画面で目で確かめられるようにするため。</para>
    /// </remarks>
    private void ShowScreenTranslationReport(
        CaptureTarget target, CapturedFrame frame, List<OcrLine> source,
        LineTranslationResult translated, double captureMs, double ocrMs, double totalMs)
    {
        var translation = translated.Translation;
        var statuses = translated.Statuses;
        var skipped = source.Where((_, i) => statuses[i] == LineTranslationStatus.Skipped).Select(l => l.Text);

        var pairs = source
            .Select((from, i) => statuses[i] switch
            {
                LineTranslationStatus.Translated => $"  {from.Text}  →  {translated.Lines[i].Text}",
                LineTranslationStatus.Unchanged => $"  {from.Text}  →  （原文と同じ・重ねない）",
                LineTranslationStatus.Unmapped => $"  {from.Text}  →  （対応なし・重ねない）",
                _ => null,
            })
            .OfType<string>();

        ResultText.Text = string.Join(Environment.NewLine,
            [
                $"ホットキー     : 前面の画面を翻訳（FR-MOD-01）",
                $"対象           : {target}",
                $"画面           : {frame.Width}x{frame.Height}（範囲指定は使わない）",
                $"バックエンド   : {_translator.Name}",
                $"状態           : {DescribeOutcome(translation)}",
                $"検出           : {source.Count} 行",
                $"翻訳に回した   : {translated.SentCount} 行（数値・記号だけの {source.Count - translated.SentCount} 行は除外）",
                $"対応付け       : {translated.MappedCount}/{translated.SentCount} 行",
                $"重ねた         : {translated.LinesToOverlay.Count} 行（訳して文字が変わった行のみ）",
                string.Empty,
                $"撮影           : {captureMs,6:F0} ms",
                $"OCR            : {ocrMs,6:F0} ms",
                $"翻訳           : {translation.Elapsed.TotalMilliseconds,6:F0} ms"
                    + (translation.TotalTokens is { } tokens ? $"（{tokens} トークン）" : string.Empty),
                $"合計           : {totalMs,6:F0} ms（要件 1.5 秒）",
                string.Empty,
                "--- 原文 → 訳文 ---",
                .. pairs,
                string.Empty,
                $"--- 翻訳に回さなかった行 ---",
                $"  {string.Join("  |  ", skipped)}",
            ]);

        StatusText.Text = translation.IsSuccess
            ? $"ホットキー: {translated.LinesToOverlay.Count} 行を重ねた"
              + $"（{source.Count} 行中 {translated.SentCount} 行を翻訳）/ 合計 {totalMs:F0} ms"
            : $"ホットキー: 翻訳失敗 — {translation.Message}";
    }

    /// <summary>
    /// オーバーレイの表示を切り替える（FR-OVL-08）。
    /// </summary>
    /// <remarks>
    /// <para>前の訳文が次の画面の上に残ったとき（PLAN.md 問題 F）、すぐ消すためのもの。
    /// 消えているときに押すと、最後の訳文を <b>API を呼ばずに</b>出し直す。</para>
    /// <para>翻訳中に押すと「翻訳中…」を消す。翻訳が終われば訳文は表示される
    /// （翻訳は利用者が明示的に頼んだものなので、結果は見せる）。</para>
    /// </remarks>
    private Task ToggleOverlayAsync()
    {
        if (_overlay is null)
        {
            StatusText.Text = "ホットキー: まだ訳文を表示していません。";
            return Task.CompletedTask;
        }

        var wasVisible = _overlay.IsVisible;
        var isVisible = _overlay.ToggleVisibility();
        OverlayHideButton.IsEnabled = isVisible;

        StatusText.Text = (wasVisible, isVisible) switch
        {
            (true, _) => "ホットキー: 訳文を消しました。もう一度押すと出し直します。",
            (false, true) => "ホットキー: 最後の訳文を出し直しました（API は呼んでいません）。",
            (false, false) => "ホットキー: 出し直せる訳文がありません。",
        };

        return Task.CompletedTask;
    }

    private void OnOverlayHideClick(object sender, RoutedEventArgs e)
    {
        _overlay?.HideOverlay();
        OverlayHideButton.IsEnabled = false;
        StatusText.Text = "オーバーレイを消しました。";
    }

    // ===== 保存 =====

    private async void OnSaveClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(SaveAsync).ConfigureAwait(true);

    private Task SaveAsync()
    {
        if (_lastBitmap is null || _lastFrame is null)
        {
            StatusText.Text = "保存できる画像がありません。";
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(CaptureDirectory);
        var name = $"{DateTime.Now:yyyyMMdd_HHmmss}_{_lastFrame.Width}x{_lastFrame.Height}.png";
        var path = Path.Combine(CaptureDirectory, name);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_lastBitmap));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        StatusText.Text = $"保存しました: {path}";
        return Task.CompletedTask;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(CaptureDirectory);
        using (Process.Start(new ProcessStartInfo(CaptureDirectory) { UseShellExecute = true }))
        {
        }
    }

    // ===== 後始末・共通処理 =====

    private void ReleaseCapture()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.Closed -= OnCaptureClosed;
        _capture.Dispose();
        _capture = null;
        _activeTarget = null;
    }

    private void SetBusy(bool busy)
    {
        CaptureNowButton.IsEnabled = !busy;
        CaptureDelayedButton.IsEnabled = !busy;
        TargetList.IsEnabled = !busy;
    }

    /// <summary>
    /// UI の最上位ハンドラ。失敗の原因を具体的に見せる（RULES.md 🔵「例外を握り潰さない」）。
    /// </summary>
    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 最上位のハンドラ。ここで捕捉しないとアプリごと落ちる。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText.Text = $"失敗: {ex.Message}";
            ResultText.Text = ex.ToString();
        }
    }
}
