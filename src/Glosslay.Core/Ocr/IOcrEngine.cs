using Glosslay.Imaging;

namespace Glosslay.Ocr;

/// <summary>
/// OCR エンジン。実装を差し替えられるようにする（RULES.md「外部依存はすべてインターフェース越しにする」）。
/// </summary>
/// <remarks>
/// v1.0 時点の実装は 2 つ。
/// <list type="bullet">
///   <item><c>PaddleOcrEngine</c> — 既定。PP-OCRv6 を ONNX Runtime で実行する</item>
///   <item><c>WindowsOcrEngine</c> — 高速モード、および PaddleOCR 初期化失敗時のフォールバック
///     （FR-OCR-03 / FR-OCR-09）</item>
/// </list>
/// </remarks>
public interface IOcrEngine : IDisposable
{
    /// <summary>UI に表示する名前。どちらのエンジンで動いているかを示すのに使う（FR-OCR-09）。</summary>
    string Name { get; }

    /// <summary>モデルの読み込みが完了しているか。</summary>
    bool IsReady { get; }

    /// <summary>
    /// <see cref="OcrLine.Confidence"/> に意味のある値を入れられるか。
    /// </summary>
    /// <remarks>
    /// <para><b>Windows の OCR は確信度を持たない</b>（<c>WindowsOcrEngine</c>）。
    /// 値としては 1.0 が入るが、それは「自信がある」ではなく「分からない」という意味。</para>
    /// <para><b>画面や計測に出すときは必ずここを見る。</b>
    /// 1.00 が並んでいるのを「完璧に読めた」と読み違えると、
    /// エンジンを比べる判断そのものが狂う。</para>
    /// <para>FR-OCR-06「信頼度が閾値未満の行を破棄」も、
    /// <c>false</c> のエンジンでは効かない。</para>
    /// </remarks>
    bool ProvidesConfidence { get; }

    /// <summary>
    /// モデルを読み込む。
    /// </summary>
    /// <remarks>
    /// FR-OCR-08: 初回 OCR で待たせないよう、アプリ起動時に非同期で呼ぶこと。
    /// 2 回目以降の呼び出しは何もしない。
    /// </remarks>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 画像から文字列とその位置を取得する（FR-OCR-01）。
    /// </summary>
    /// <param name="image">対象画像。実運用では領域を切り出したもの（FR-CAP-05）。</param>
    /// <remarks>
    /// 未初期化の場合はこの中で初期化してから実行する。
    /// 信頼度が閾値未満の行は実装側で除外済み（FR-OCR-06）。
    /// </remarks>
    Task<OcrResult> RecognizeAsync(Bgra32Image image, CancellationToken cancellationToken = default);
}
