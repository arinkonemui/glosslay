namespace Glosslay.Translation;

/// <summary>いまの組み合わせを、ローカルで訳せる状態か。</summary>
public enum LocalReadiness
{
    /// <summary>
    /// <b>「クラウドのみ」の組み合わせ</b>（RULES.md 🟡-1 の表）。モデルがあっても訳せない。
    /// </summary>
    CloudOnlyPair,

    /// <summary>ローカルで訳せる組み合わせだが、モデルが置かれていない。</summary>
    ModelMissing,

    /// <summary>ローカルで訳せる。</summary>
    Ready,
}

/// <summary>
/// どのバックエンドで訳し、失敗したらどこへ切り替えるかを決める（FR-TRN-01 / FR-TRN-06 / FR-TRN-17）。
/// </summary>
/// <remarks>
/// <para><b>UI から切り離してあるのは、規則が「3 つの選択 × キーの有無 × 組み合わせ」で増えたため。</b>
/// 画面の中に埋めると確かめられない。<c>NmtBench --selection</c> が表をそのまま検査する。</para>
/// <para><b>RULES.md 🟡-1 の順序をここで守る。</b>
/// 既定（<see cref="TranslationBackendChoice.Auto"/>）では、キーが無ければローカルで動く。
/// クラウドは「精度を上げたい人向けの任意のアップグレード」。<b>この順序を逆にしないこと。</b></para>
/// <para><b>「クラウドのみ」の組み合わせ（2026-10-25）</b>では、自動でもクラウドで訳し、
/// 失敗してもローカルへ切り替えない（切り替えても訳せない）。
/// 「ローカルのみ」のときは<b>勝手にクラウドへ送らない</b> — 外へ送らないことを選んだ人の選択のため。
/// ローカルが理由を返す（RULES.md 🟡-6 / SPEC.md FR-TRN-17）。</para>
/// </remarks>
public static class TranslatorSelection
{
    /// <summary>実際に使うバックエンド。</summary>
    /// <param name="choice">利用者の選択。</param>
    /// <param name="cloud">クラウド翻訳。</param>
    /// <param name="local">ローカルNMT。</param>
    /// <param name="readiness">いまの組み合わせをローカルで訳せるか。</param>
    public static ITranslator Active(
        TranslationBackendChoice choice, ITranslator cloud, ITranslator local, LocalReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(local);

        return choice switch
        {
            // 「クラウドのみ」の組み合わせでも、クラウドへは送らない。ローカルが理由を返す。
            TranslationBackendChoice.LocalOnly => local,

            // 明示的に選ばれたなら、キーが無くてもクラウドのまま。
            // 「キー未登録です」と伝わる方が、黙ってローカルになるより分かりやすい。
            TranslationBackendChoice.CloudOnly => cloud,

            // 自動: 「クラウドのみ」の組み合わせはクラウド（キーが無ければ、キーが要ると伝わる）。
            _ when readiness == LocalReadiness.CloudOnlyPair => cloud,

            // 自動: キーがあればクラウド、無ければローカル（RULES.md 🟡-1）。
            // モデルが無い場合もローカルのまま — 「モデルが無い」と伝わる方が、キーを求めるより正しい。
            _ => cloud.IsConfigured ? cloud : local,
        };
    }

    /// <summary>
    /// <see cref="Active"/> が失敗したときの切り替え先。無ければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>「自動」以外を選んでいるなら切り替えない。</b>
    /// 利用者が明示的に決めた動きを、失敗したからといって覆さない。
    /// 「クラウドのみ」の組み合わせとモデルが無い場合も切り替えない（切り替えても同じ失敗になる）。
    /// </remarks>
    public static ITranslator? Fallback(
        TranslationBackendChoice choice, ITranslator cloud, ITranslator local, LocalReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(local);

        if (choice != TranslationBackendChoice.Auto || readiness != LocalReadiness.Ready)
        {
            return null;
        }

        // 既にローカルで動いているなら切り替え先は無い。
        var active = Active(choice, cloud, local, readiness);
        return ReferenceEquals(active, local) ? null : local;
    }
}
