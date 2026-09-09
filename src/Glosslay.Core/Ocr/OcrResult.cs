namespace Glosslay.Ocr;

/// <summary>
/// 認識したテキストの位置（画素単位・軸に平行な矩形）。
/// </summary>
/// <remarks>
/// PaddleOCR は本来 4 点の四角形を返すが、ここでは外接矩形に丸めている。
/// 対象が「横書きのゲーム UI テキスト」に限られ（SPEC.md §1 で縦書きは対象外）、
/// オーバーレイの配置（FR-OVL-04 ①原文位置に重ねる）に必要なのは矩形だけのため。
/// 回転したテキストを扱う必要が出たら 4 点を持つ形へ広げること。
/// </remarks>
public readonly record struct OcrBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>OCR が認識した 1 行。</summary>
/// <param name="Text">認識した文字列。</param>
/// <param name="Confidence">信頼度 0.0〜1.0。FR-OCR-06 の閾値判定に使う。</param>
/// <param name="Box">画像内での位置。</param>
public sealed record OcrLine(string Text, float Confidence, OcrBox Box);

/// <summary>1 枚分の OCR 結果。</summary>
/// <param name="Lines">認識した行。読み順は保証しない（並べ替えは TextAggregator の仕事）。</param>
/// <param name="Elapsed">OCR にかかった時間。P0-7 の計測と FR-OVL-10 の実測表示に使う。</param>
public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, TimeSpan Elapsed)
{
    public static OcrResult Empty { get; } = new([], TimeSpan.Zero);
}
