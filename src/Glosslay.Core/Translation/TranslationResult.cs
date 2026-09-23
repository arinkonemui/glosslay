namespace Glosslay.Translation;

/// <summary>翻訳の依頼。</summary>
/// <param name="Text">原文。OCR の行を結合したもの（FR-TRN-10。結合自体は未実装 = PoC の範囲外）。</param>
public sealed record TranslationRequest(string Text)
{
    /// <summary>訳文の言語。BCP 47。</summary>
    public string TargetLanguage { get; init; } = "ja";

    /// <summary>原文の言語。<c>null</c> なら自動判定に任せる（FR-OCR-02 の言語自動判定と同じ思想）。</summary>
    public string? SourceLanguage { get; init; }
}

/// <summary>
/// 翻訳の結果種別。
/// </summary>
/// <remarks>
/// 失敗の理由を分けているのは、RULES.md 🟡-6 が状況ごとに違う挙動を要求しているため。
/// 「とりあえず失敗」では FR-TRN-06 のフォールバック判断ができない。
/// </remarks>
public enum TranslationOutcome
{
    /// <summary>成功。</summary>
    Succeeded,

    /// <summary>キーが未登録。<b>フォールバックの対象ではない</b>（設定を直すべき状態）。</summary>
    NotConfigured,

    /// <summary>レート上限（HTTP 429）。無料枠を使い切った場合もここ。</summary>
    RateLimited,

    /// <summary>接続できない。回線・DNS・プロキシなど、要求が相手に届かなかった場合。</summary>
    NetworkError,

    /// <summary>
    /// 制限時間内に応答が返らなかった。
    /// </summary>
    /// <remarks>
    /// <see cref="NetworkError"/> と分けているのは、<b>原因が違い、利用者が取る行動も違う</b>ため。
    /// 届いていないのか、届いたうえで遅いのかを混ぜると原因が追えない
    /// （2026-09-24、混雑による遅延を「通信できませんでした」と表示して切り分けに手間取った。PLAN.md 課題7）。
    /// </remarks>
    Timeout,

    /// <summary>
    /// 混雑していて今は応答できない（HTTP 503）。
    /// </summary>
    /// <remarks>
    /// <see cref="ServiceError"/> と分けているのは、<b>こちらに直すところが無い</b>ため。
    /// モデル名の誤りなら設定を直すが、混雑は待つか別の手段へ切り替えるしかない。
    /// </remarks>
    Busy,

    /// <summary>サービス側のエラー。モデル名の誤りやキーの失効もここに含む。</summary>
    ServiceError,

    /// <summary>安全フィルタ等で応答が返らなかった。</summary>
    Blocked,
}

/// <summary>翻訳の結果。</summary>
/// <param name="Text">訳文。失敗時は空文字。</param>
/// <param name="Outcome">結果種別。</param>
/// <param name="Elapsed">所要時間。FR-OVL-10 の実測表示と P0-7 の計測に使う。</param>
public sealed record TranslationResult(string Text, TranslationOutcome Outcome, TimeSpan Elapsed)
{
    /// <summary>
    /// 利用者に見せる説明。失敗時のみ入る。
    /// </summary>
    /// <remarks>RULES.md 🔵「ユーザーに見せるメッセージは具体的に」。</remarks>
    public string? Message { get; init; }

    /// <summary>
    /// 消費トークン数。取得できなければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// FR-TRN-12 の使用量表示は v1.0 だが、<b>PoC の時点で費用の見当をつけるために拾っておく</b>。
    /// 「予期しない費用を発生させない」は判断基準の 3 位（RULES.md ⚫）。
    /// </remarks>
    public int? TotalTokens { get; init; }

    public bool IsSuccess => Outcome == TranslationOutcome.Succeeded;

    /// <summary>
    /// ローカルNMTへ切り替えるべき状態か（FR-TRN-06 / RULES.md 🟡-6）。
    /// </summary>
    /// <remarks>
    /// キー未登録（<see cref="TranslationOutcome.NotConfigured"/>）を含めないのが要点。
    /// 設定の誤りを黙ってフォールバックで隠すと、利用者は「キーを登録したのに効いていない」ことに
    /// 気づけない。こちらは UI で知らせる。
    /// </remarks>
    public bool ShouldFallBack => Outcome
        is TranslationOutcome.RateLimited
        or TranslationOutcome.NetworkError
        or TranslationOutcome.Timeout
        or TranslationOutcome.Busy
        or TranslationOutcome.ServiceError;

    internal static TranslationResult Failure(
        TranslationOutcome outcome, string message, TimeSpan elapsed) =>
        new(string.Empty, outcome, elapsed) { Message = message };
}
