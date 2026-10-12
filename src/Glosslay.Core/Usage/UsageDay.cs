namespace Glosslay.Usage;

/// <summary>
/// 1 日ぶんの使用量（RULES.md 🟡-8「使用量を常時表示」）。
/// </summary>
/// <remarks>
/// <para><b>クラウドとローカルを分けて数える。</b>ルールの見出しは
/// 「ユーザーに予期しない<b>課金</b>を発生させない」であり、<b>お金がかかるのはクラウドだけ</b>。
/// 合算すると、見た数字の意味が分からなくなる。</para>
/// <para><b>日ごとに持つ。</b>クラウドの無料枠は 1 日単位で戻るものが多く、
/// アプリを起動し直すたびに 0 になると「今日どれだけ使ったか」が分からない。</para>
/// </remarks>
public sealed record UsageDay
{
    /// <summary>その日（利用者の地方時）。</summary>
    public DateOnly Date { get; init; }

    /// <summary>
    /// クラウド翻訳を呼んだ回数。
    /// </summary>
    /// <remarks>
    /// <b>手動・自動のどちらも数える。</b>既存の <c>CallRateLimiter</c> が数えるのは
    /// 自動ぶんだけで、あれは<b>暴走の歯止め</b>であって使用量ではない。
    /// 手動のホットキーで呼んだぶんも同じように課金される。
    /// </remarks>
    public int CloudCalls { get; init; }

    /// <summary>
    /// クラウドが報告したトークン数の合計。
    /// </summary>
    /// <remarks>
    /// <b>課金の単位はこちら。</b>回数は同じでも、全画面とカーソル窓では桁が変わる。
    /// 返ってこなかった呼び出し（失敗・モデルが返さない場合）は 0 として足す。
    /// </remarks>
    public long CloudTokens { get; init; }

    /// <summary>クラウドの呼び出しが失敗した回数。</summary>
    /// <remarks>
    /// <b>失敗も呼び出しに数える。</b>通信は発生しており、混雑や上限で失敗した回数が
    /// 見えないと「使っていないのに訳せない」理由が分からない。
    /// </remarks>
    public int CloudFailures { get; init; }

    /// <summary>ローカル翻訳を呼んだ回数。</summary>
    /// <remarks>
    /// <b>費用はかからない。</b>出すのは、クラウドを使っていないことを確かめられるようにするため。
    /// </remarks>
    public int LocalCalls { get; init; }

    /// <summary>
    /// 覚えていた訳文で済ませた行数（FR-TRN-09）。
    /// </summary>
    /// <remarks>
    /// <b>効いているかどうかが見えない対策は確かめようがない。</b>
    /// RULES.md 🟡-8 はキャッシュも対策として挙げている。
    /// </remarks>
    public int CachedLines { get; init; }

    /// <summary>何も使っていない日か。</summary>
    public bool IsEmpty =>
        CloudCalls == 0 && LocalCalls == 0 && CachedLines == 0;
}
