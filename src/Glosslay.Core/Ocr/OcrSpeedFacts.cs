using static Glosslay.Localization.UiText;

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
    /// <para><b>速さだけを言わない。</b>利用者が「速度 &gt; 精度」を望んでいても、
    /// <b>数値が捏造されることまで望んでいるとは限らない</b>。
    /// 判断の材料をそろえて渡す。</para>
    /// <para><b>言語パックの話を先に置く。</b>2026-10-13 の判断で高速モードの位置づけを下げた。
    /// 日本語以外の原文では<b>利用者が自分で言語を追加しない限り使えない</b>ため、
    /// 速さの数字より先に、使えるかどうかが分かるようにする。</para>
    /// </remarks>
    public static string FastModeTradeoff =>
        T("⚠️ 原文の言語が下の一覧に無ければ、Windows に言語を追加しないと使えません。")
        + T("追加できれば全画面の読み取りは約 {0} ms → 約 {1} ms", PaddleFullScreenMs, WindowsFullScreenMs)
        + T("（{0} 枚の実測・{1}）。", SampleCount, MeasuredOn)
        + T("ただし正解との一致度が {0:0.#}% → {1:0.#}% に下がり、", PaddleSimilarity, WindowsSimilarity)
        + T("原文に無い数値が {0} 件 → {1} 件に増えます。", PaddleExtraNumbers, WindowsExtraNumbers)
        + T("セリフを読むには十分ですが、カードや数値を読むには向きません。");

    /// <summary>既定のエンジンを選んでいるときの説明。</summary>
    /// <remarks>
    /// <b>「遅い」で終わらせない。</b>既定のまま速くする手がある
    /// （検出に渡す画像の大きさ。PLAN.md「読み取りを速くする」）ことが分かっており、
    /// 利用者には範囲を絞る方法を案内する。
    /// </remarks>
    public static string DefaultModeNote =>
        T("精度を優先します。ほとんどの場合はこのままで問題ありません。")
        + T("プレビューをドラッグして範囲を絞ると、読み取りは大きく速くなります。");
}
