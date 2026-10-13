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

    /// <summary>
    /// 検出に渡す画像の長辺の上限。<c>0</c> なら縮めない。
    /// </summary>
    /// <remarks>
    /// <para><b>読み取り時間の固定費はここで決まる。</b>
    /// これを入れる前は全画面 1920x1080 が縮まず 1920x1088（2.09M 画素）のまま
    /// 検出へ流れており、それが固定費 695ms の正体だった（2026-10-13 の実測）。</para>
    /// <para><b>既定 1280 の根拠</b>（全画面 16 枚・前処理なし・2026-10-13）。
    /// 利用者の手間ゼロで性能要件 1.5 秒に収まる唯一の手。</para>
    /// <list type="table">
    ///   <item><term>無制限（2.09M 画素）</term><description>1,075ms / 一致度 84.1% → 経路 1,658ms・超過</description></item>
    ///   <item><term><b>1280（0.92M 画素）</b></term><description><b>678ms / 一致度 84.4% → 経路 1,261ms・収まる</b></description></item>
    ///   <item><term>960（0.52M 画素）</term><description>554ms / 一致度 82.6% → 細かい文字で崩れる</description></item>
    /// </list>
    /// <para><b>960 まで下げない。</b>さらに速いが、文字の多い画面で一致度が落ちる
    /// （<c>tyr_tank_fuse</c> 92.2% → 82.4%）。1280 なら一致度はむしろ上がっており、
    /// <b>「原文に無い数値」も増えない</b>（41 枚で 19 → 18 件。RULES.md 🟡-7）。</para>
    /// <para>計測し直すときは <c>OcrBench --detmax=&lt;値&gt;</c>。</para>
    /// </remarks>
    public int DetectionMaxSideLength { get; init; } = 1280;

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
