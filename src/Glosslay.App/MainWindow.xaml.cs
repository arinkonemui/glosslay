using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glosslay.Capture;
using Glosslay.Ocr;

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

    private ScreenCapture? _capture;
    private CaptureTarget? _activeTarget;
    private bool _activeRemoveBorder;
    private bool _activeCaptureCursor;

    private CapturedFrame? _lastFrame;
    private BitmapSource? _lastBitmap;

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
        _ocrEngine.Dispose();
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
        _lastFrame = frame;
        _lastBitmap = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
        _lastBitmap.Freeze();

        PreviewImage.Source = _lastBitmap;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = true;
        OcrButton.IsEnabled = true;

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
            var result = await _ocrEngine.RecognizeAsync(_lastFrame.AsImage()).ConfigureAwait(true);

            PreviewImage.Source = DrawBoxes(_lastBitmap, result.Lines);
            ResultText.Text = FormatOcrResult(result);
            StatusText.Text = $"OCR 完了: {result.Lines.Count} 行 / "
                + $"{result.Elapsed.TotalMilliseconds:F0} ms（{_ocrEngine.Name}）";
        }
        finally
        {
            OcrButton.IsEnabled = true;
        }
    }

    private static string FormatOcrResult(OcrResult result)
    {
        if (result.Lines.Count == 0)
        {
            return "文字が検出されませんでした。"
                + "領域を絞る、または前処理（拡大・コントラスト）を検討してください。";
        }

        var lines = result.Lines
            .OrderBy(line => line.Box.Y)
            .ThenBy(line => line.Box.X)
            .Select(line => $"{line.Confidence:F3}  [{line.Box.X,5},{line.Box.Y,5}]  {line.Text}");

        return string.Join(Environment.NewLine, lines);
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
