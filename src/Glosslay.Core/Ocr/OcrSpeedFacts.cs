namespace Glosslay.Ocr;

/// <summary>
/// エンジンを選ぶための実測（FR-OCR-03 / PLAN.md 課題4）。
/// </summary>
/// <remarks>
/// <para><b>「速い」「精度が落ちる」では選べない。</b>どれだけ速くて何を失うのかを
/// 数字で出すために、計測の結果をここに集める。
/// 文面を組み立てる側が数字を持つと、計測し直したときに直し漏れる。</para>
/// <para>出典: <c>OcrBench --quick</c> を同じ 41 枚に対して両エンジンで実行（2026-10-12）。
/// 詳細は PLAN.md「Windows OCR の高速モード」。</para>
/// </remarks>
public static class OcrSpeedFacts
{
    /// <summary>計測した日。</summary>
    public const string MeasuredOn = "2026-10-12";

    /// <summary>計測に使った枚数。</summary>
    public const int SampleCount = 41;

    /// <summary>PaddleOCR の全画面 1920x1080 の平均（ミリ秒）。</summary>
    public const int PaddleFullScreenMs = 1075;

    /// <summary>Windows OCR の全画面 1920x1080 の平均（ミリ秒）。</summary>
    public const int WindowsFullScreenMs = 125;

    /// <summary>PaddleOCR の正解との一致度（百分率）。</summary>
    public const double PaddleSimilarity = 86.9;

    /// <summary>Windows OCR の正解との一致度（百分率）。</summary>
    public const double WindowsSimilarity = 56.5;

    /// <summary>
    /// PaddleOCR で現れた「原文に無い数値」の件数。
    /// </summary>
    /// <remarks>
    /// <b>一致度より重く見る</b>（RULES.md 🟡-7）。原文に無い数値は、
    /// 読み落としと違って<b>訳文に嘘の数字として現れる</b>。
    /// </remarks>
    public const int PaddleExtraNumbers = 19;

    /// <summary>Windows OCR で現れた「原文に無い数値」の件数。</summary>
    public const int WindowsExtraNumbers = 147;

    /// <summary>
    /// 高速モードを選ぶときに出す説明（FR-OCR-03）。
    /// </summary>
    /// <remarks>
    /// <b>速さだけを言わない。</b>利用者が「速度 &gt; 精度」を望んでいても、
    /// <b>数値が捏造されることまで望んでいるとは限らない</b>。
    /// 判断の材料をそろえて渡す。
    /// </remarks>
    public static string FastModeTradeoff =>
        $"全画面の読み取りが約 {PaddleFullScreenMs} ms → 約 {WindowsFullScreenMs} ms になります"
        + $"（{SampleCount} 枚の実測・{MeasuredOn}）。"
        + $"ただし正解との一致度が {PaddleSimilarity:0.#}% → {WindowsSimilarity:0.#}% に下がり、"
        + $"原文に無い数値が {PaddleExtraNumbers} 件 → {WindowsExtraNumbers} 件に増えます。"
        + "セリフを読むには十分ですが、カードや数値を読むには向きません。";

    /// <summary>既定のエンジンを選んでいるときの説明。</summary>
    public static string DefaultModeNote =>
        $"精度を優先します。全画面の読み取りに約 {PaddleFullScreenMs} ms かかり、"
        + "性能要件の 1.5 秒には収まりません。範囲を絞ると大きく短くなります。";
}
