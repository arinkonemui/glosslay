using Glosslay.Configuration;

namespace Glosslay.Ocr;

/// <summary>どの OCR エンジンで読むか（FR-OCR-03）。</summary>
public enum OcrEngineChoice
{
    /// <summary>
    /// PaddleOCR（PP-OCRv6）。<b>既定。</b>
    /// </summary>
    /// <remarks>
    /// 精度で選んでいる。2026-10-12 の実測で、同じ 41 枚に対し
    /// 一致度 86.9%（高速モードは 56.5%）。
    /// </remarks>
    Paddle,

    /// <summary>
    /// Windows 同梱の OCR。<b>速いが精度は落ちる。</b>
    /// </summary>
    /// <remarks>
    /// 全画面 1920x1080 で約 125ms（PaddleOCR は約 1,075ms）。
    /// <b>性能要件 1.5 秒に収まるのはこちらだけ</b>だが、代償がある。
    /// </remarks>
    Windows,
}

/// <summary>
/// OCR エンジンの選択（FR-OCR-03 / FR-OCR-10）。
/// </summary>
/// <remarks>
/// <para><b>既定は PaddleOCR。</b>速さと引き換えに失うものが大きいため、
/// 高速モードは利用者が選んだときだけ使う。実測は <see cref="OcrSpeedFacts"/> に集めてある。</para>
/// <para>エンジンごとの細かい設定は <see cref="PaddleOcrOptions"/> にある。
/// ここに置くのは「どちらを使うか」だけ。</para>
/// </remarks>
public sealed record OcrEngineOptions
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "ocr.json";

    /// <summary>使うエンジン。<b>既定は PaddleOCR</b>（FR-OCR-03）。</summary>
    public OcrEngineChoice Engine { get; init; } = OcrEngineChoice.Paddle;

    // 高速モードで認識する言語は、2026-10-25 から原文の言語（FR-OCR-02）に合わせる
    // （WindowsOcrEngine.LanguageFor）。それまでここに持っていた WindowsLanguage は読まない。

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static OcrEngineOptions Load() =>
        JsonSettings.Load(FileName, () => new OcrEngineOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
