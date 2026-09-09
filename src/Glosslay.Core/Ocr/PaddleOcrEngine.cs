using System.Diagnostics;
using Glosslay.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Glosslay.Ocr;

/// <summary>
/// PP-OCRv6 を ONNX Runtime で実行する OCR エンジン（既定の実装・FR-OCR-03）。
/// </summary>
/// <remarks>
/// <para>PLAN.md P0-4 の計測で、Python/PaddlePaddle 経由より約 6 倍速く、
/// 性能要件を満たす唯一の選択肢だったため採用した。Python への依存はない。</para>
/// <para>処理の流れは 検出 → 行ごとの切り出し → 認識 → CTC デコード。</para>
/// </remarks>
public sealed class PaddleOcrEngine(PaddleOcrOptions? options = null) : IOcrEngine
{
    private readonly PaddleOcrOptions _options = options ?? new PaddleOcrOptions();
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    private InferenceSession? _detection;
    private InferenceSession? _recognition;
    private CtcDecoder? _decoder;
    private string _detectionInputName = string.Empty;
    private string _recognitionInputName = string.Empty;
    private bool _disposed;

    /// <inheritdoc />
    public string Name => "PaddleOCR (PP-OCRv6 small)";

    /// <inheritdoc />
    public bool IsReady => _detection is not null && _recognition is not null;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsReady)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return;
            }

            // モデルの読み込みは数百 ms かかるためスレッドプールで行う（FR-OCR-08）。
            await Task.Run(LoadModels, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OcrResult> RecognizeAsync(
        Bgra32Image image, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image.Pixels);

        if (image.Width <= 0 || image.Height <= 0)
        {
            return OcrResult.Empty;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Recognize(image, cancellationToken), cancellationToken)
                         .ConfigureAwait(false);
    }

    private void LoadModels()
    {
        var detectionPath = _options.ResolveDetectionModel();
        var recognitionPath = _options.ResolveRecognitionModel();
        var charsetPath = _options.ResolveCharacterSet();

        foreach (var path in (ReadOnlySpan<string>)[detectionPath, recognitionPath])
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"OCR モデルが見つかりません: {path}。"
                    + $"モデル一式を {_options.ModelDirectory} に配置してください。",
                    path);
            }
        }

        using var sessionOptions = CreateSessionOptions();
        var detection = new InferenceSession(detectionPath, sessionOptions);
        var recognition = new InferenceSession(recognitionPath, sessionOptions);

        _decoder = new CtcDecoder(CharacterSet.Load(charsetPath));
        _detectionInputName = detection.InputMetadata.Keys.First();
        _recognitionInputName = recognition.InputMetadata.Keys.First();
        _detection = detection;
        _recognition = recognition;
    }

    private SessionOptions CreateSessionOptions() => new()
    {
        // FR-OCR-11: スレッド数を制限してゲームと CPU を奪い合わない。
        IntraOpNumThreads = _options.CpuThreads,
        InterOpNumThreads = 1,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,

        // FR-OCR-12: GPU 推論は既定で無効。CPU プロバイダのみを使う。
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
    };

    private OcrResult Recognize(Bgra32Image image, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var boxes = Detect(image, cancellationToken);
        var lines = new List<OcrLine>(boxes.Count);

        foreach (var (box, _) in boxes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            var crop = image.Crop(box.X, box.Y, box.Width, box.Height);
            var (text, confidence) = RecognizeLine(crop);

            // FR-OCR-06: 信頼度が閾値未満の行は捨てる。
            if (text.Length == 0 || confidence < _options.MinConfidence)
            {
                continue;
            }

            lines.Add(new OcrLine(text, confidence, box));
        }

        return new OcrResult(lines, stopwatch.Elapsed);
    }

    private IReadOnlyList<(OcrBox Box, float Score)> Detect(
        Bgra32Image image, CancellationToken cancellationToken)
    {
        var (targetWidth, targetHeight) = OcrImagePreprocessor.ComputeDetectionSize(
            image.Width, image.Height);

        var tensorData = OcrImagePreprocessor.BuildDetectionTensor(image, targetWidth, targetHeight);
        cancellationToken.ThrowIfCancellationRequested();

        var tensor = new DenseTensor<float>(tensorData, [1, 3, targetHeight, targetWidth]);
        using var results = _detection!.Run(
            [NamedOnnxValue.CreateFromTensor(_detectionInputName, tensor)]);

        // 出力は [1, 1, H, W] の確率マップ。
        var map = results[0].AsTensor<float>();
        var mapHeight = map.Dimensions[^2];
        var mapWidth = map.Dimensions[^1];

        return _options.PostProcessor.Extract(
            map.ToArray(), mapWidth, mapHeight, image.Width, image.Height);
    }

    private (string Text, float Confidence) RecognizeLine(Bgra32Image crop)
    {
        var width = OcrImagePreprocessor.ComputeRecognitionWidth(crop.Width, crop.Height);
        var tensorData = OcrImagePreprocessor.BuildRecognitionTensor(crop, width);

        var tensor = new DenseTensor<float>(
            tensorData, [1, 3, OcrImagePreprocessor.RecognitionHeight, width]);

        using var results = _recognition!.Run(
            [NamedOnnxValue.CreateFromTensor(_recognitionInputName, tensor)]);

        // 出力は [1, timeSteps, classCount]。
        var output = results[0].AsTensor<float>();
        var timeSteps = output.Dimensions[^2];
        var classCount = output.Dimensions[^1];

        return _decoder!.Decode(output.ToArray(), timeSteps, classCount);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _detection?.Dispose();
        _recognition?.Dispose();
        _initializationLock.Dispose();
    }
}
