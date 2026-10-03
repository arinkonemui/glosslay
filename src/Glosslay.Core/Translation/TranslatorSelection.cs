namespace Glosslay.Translation;

/// <summary>
/// どのバックエンドで訳し、失敗したらどこへ切り替えるかを決める（FR-TRN-01 / FR-TRN-06）。
/// </summary>
/// <remarks>
/// <para><b>UI から切り離してあるのは、規則が「3 つの選択 × キーの有無」で増えたため。</b>
/// 画面の中に埋めると確かめられない。<c>NmtBench --selection</c> が表をそのまま検査する。</para>
/// <para><b>RULES.md 🟡-1 の順序をここで守る。</b>
/// 既定（<see cref="TranslationBackendChoice.Auto"/>）では、キーが無ければローカルで動く。
/// クラウドは「精度を上げたい人向けの任意のアップグレード」。<b>この順序を逆にしないこと。</b></para>
/// </remarks>
public static class TranslatorSelection
{
    /// <summary>実際に使うバックエンド。</summary>
    /// <param name="choice">利用者の選択。</param>
    /// <param name="cloud">クラウド翻訳。</param>
    /// <param name="local">ローカルNMT。</param>
    public static ITranslator Active(
        TranslationBackendChoice choice, ITranslator cloud, ITranslator local)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(local);

        return choice switch
        {
            TranslationBackendChoice.LocalOnly => local,

            // 明示的に選ばれたなら、キーが無くてもクラウドのまま。
            // 「キー未登録です」と伝わる方が、黙ってローカルになるより分かりやすい。
            TranslationBackendChoice.CloudOnly => cloud,

            // 自動: キーがあればクラウド、無ければローカル（RULES.md 🟡-1）。
            _ => cloud.IsConfigured ? cloud : local,
        };
    }

    /// <summary>
    /// <see cref="Active"/> が失敗したときの切り替え先。無ければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>「自動」以外を選んでいるなら切り替えない。</b>
    /// 利用者が明示的に決めた動きを、失敗したからといって覆さない。
    /// </remarks>
    public static ITranslator? Fallback(
        TranslationBackendChoice choice, ITranslator cloud, ITranslator local)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(local);

        if (choice != TranslationBackendChoice.Auto)
        {
            return null;
        }

        // 既にローカルで動いているなら切り替え先は無い。
        // モデルが置かれていない場合も同じ（切り替えても同じ失敗になる）。
        var active = Active(choice, cloud, local);
        return ReferenceEquals(active, local) || !local.IsConfigured ? null : local;
    }
}
