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

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnWindowClosed;
    }

    /// <summary>キャプチャ画像の保存先。FR-CFG-00 に合わせて %APPDATA%\Glosslay\ 配下に置く。</summary>
    private static string CaptureDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glosslay", "captures");

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
    /// <returns>加工後の画像と、元画像へ座標を戻すための情報。</returns>
    private (Bgra32Image Image, PreprocessOptions Options, int OffsetX, int OffsetY)
        PrepareForOcr(CapturedFrame frame)
    {
        var image = frame.AsImage();
        var offsetX = 0;
        var offsetY = 0;

        if (_region is { } region)
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
            Environment.NewLine, InReadingOrder(_lastOcrLines).Select(line => line.Text));

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

        return string.Join(Environment.NewLine, InReadingOrder(lines)
            .Select(line => $"{line.Confidence:F3}  [{line.Box.X,5},{line.Box.Y,5}]  {line.Text}"));
    }

    /// <summary>
    /// 認識結果を読み順に並べ直す。
    /// </summary>
    /// <remarks>
    /// 縦位置が近いものを同じ行にまとめ、行内は左から右へ並べる。
    /// Y 座標だけで並べると、同じ行の要素が 1px のずれで入れ替わる。
    /// SPEC.md §2 の TextAggregator が本来担う処理の最小版。
    /// </remarks>
    private static List<OcrLine> InReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();

        foreach (var line in lines.OrderBy(l => l.Box.Y))
        {
            var center = line.Box.Y + (line.Box.Height / 2.0);
            var row = rows.FirstOrDefault(candidate =>
            {
                var head = candidate[0];
                var headCenter = head.Box.Y + (head.Box.Height / 2.0);
                var tolerance = Math.Max(head.Box.Height, line.Box.Height) * 0.5;
                return Math.Abs(headCenter - center) < tolerance;
            });

            if (row is null)
            {
                rows.Add([line]);
            }
            else
            {
                row.Add(line);
            }
        }

        return [.. rows.SelectMany(row => row.OrderBy(l => l.Box.X))];
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

            _overlay ??= new OverlayWindow { Owner = this };

            // ウィンドウ指定なら、ゲームか操作ウィンドウが前面の間だけ表示する。
            // モニタ全体はどれがゲームか特定できないため追従しない（自動フェードのみ）。
            IReadOnlyList<nint>? keepVisibleFor = target.Kind == CaptureTargetKind.Window
                ? [target.Handle, new WindowInteropHelper(this).Handle]
                : null;

            _overlay.ShowLines(
                lines, bounds, frame.Width, frame.Height, keepVisibleFor);
            OverlayHideButton.IsEnabled = true;

            // P0-6 の検証項目。設定したつもりで終わらせず、実際の値を出す。
            var style = _overlay.StyleState;
            ResultText.Text = string.Join(Environment.NewLine,
                $"重ねた先       : {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}（物理px）",
                $"認識画像       : {frame.Width}x{frame.Height}（このボタンで撮り直したもの）",
                $"表示した行     : {lines.Count}",
                $"前処理         : {options}",
                $"自動で消える   : {_overlay.AutoHideAfter.TotalSeconds:F0} 秒後（FR-OVL-06）",
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
