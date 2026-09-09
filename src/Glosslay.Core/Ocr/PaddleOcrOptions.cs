namespace Glosslay.Ocr;

/// <summary>
/// <see cref="PaddleOcrEngine"/> の設定。
/// </summary>
public sealed record PaddleOcrOptions
{
    /// <summary>モデル一式を置いたディレクトリ。</summary>
    /// <remarks>
    /// 既定は実行ファイルの隣の <c>models</c>。
    /// 配布時はインストーラでここへ同梱する（SPEC.md §4「PaddleOCR モデル同梱で 200MB 程度まで許容」）。
    /// </remarks>
    public string ModelDirectory { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>検出モデルの相対パス。</summary>
    public string DetectionModelPath { get; init; } =
        Path.Combine("PP-OCRv6_small_det_onnx", "inference.onnx");

    /// <summary>認識モデルの相対パス。</summary>
    public string RecognitionModelPath { get; init; } =
        Path.Combine("PP-OCRv6_small_rec_onnx", "inference.onnx");

    /// <summary>文字辞書の相対パス。</summary>
    public string CharacterSetPath { get; init; } = "ppocrv6_charset.txt";

    /// <summary>
    /// 推論に使う CPU スレッド数（FR-OCR-11）。
    /// </summary>
    /// <remarks>
    /// 既定は物理コア数の半分程度。ゲームと CPU を奪い合わないための制限で、
    /// <see cref="Environment.ProcessorCount"/> は論理コア数のため 4 で割っている。
    /// 極端に少なくなりすぎないよう 2 を下限にする。
    /// </remarks>
    public int CpuThreads { get; init; } = Math.Max(2, Environment.ProcessorCount / 4);

    /// <summary>この信頼度未満の行は捨てる（FR-OCR-06）。</summary>
    public float MinConfidence { get; init; } = 0.5f;

    /// <summary>検出の後処理設定。既定値はモデル同梱の inference.yml に合わせている。</summary>
    public DbPostProcessor PostProcessor { get; init; } = new();

    /// <summary>検出モデルの絶対パス。</summary>
    public string ResolveDetectionModel() => Path.Combine(ModelDirectory, DetectionModelPath);

    /// <summary>認識モデルの絶対パス。</summary>
    public string ResolveRecognitionModel() => Path.Combine(ModelDirectory, RecognitionModelPath);

    /// <summary>文字辞書の絶対パス。</summary>
    public string ResolveCharacterSet() => Path.Combine(ModelDirectory, CharacterSetPath);
}
