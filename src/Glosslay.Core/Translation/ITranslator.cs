namespace Glosslay.Translation;

/// <summary>
/// 翻訳バックエンド（RULES.md 🟡-2「外部依存はすべてインターフェース越しにする」）。
/// </summary>
/// <remarks>
/// <para>v1.0 時点の実装は 3 つ。</para>
/// <list type="bullet">
///   <item><c>LocalNmtTranslator</c> — <b>既定</b>。APIキーなしで動く（FR-TRN-02 / RULES.md 🟡-1）。
///     実装は v1.0（PLAN.md）</item>
///   <item><c>GeminiTranslator</c> — 精度を上げたい人向けの任意のアップグレード（FR-TRN-04 BYOK）</item>
///   <item><c>OpenAiCompatibleTranslator</c> — 同上</item>
/// </list>
/// <para><b>PoC では Gemini のみ実装する。</b>PoC は自分用でキー登録の障壁が問題にならないため
/// （PLAN.md P0-5）。<b>「既定はローカルNMT」という順序は変えていない</b>（RULES.md 🟡-1）。
/// 実装の順番だけが逆になっている。</para>
/// </remarks>
public interface ITranslator : IDisposable
{
    /// <summary>UI に表示する名前。どのバックエンドで動いているかを示す（FR-TRN-13 のバッジ）。</summary>
    string Name { get; }

    /// <summary>
    /// 使える状態か。クラウド翻訳ならキーが登録済みかどうか。
    /// </summary>
    /// <remarks>
    /// <c>false</c> でも <see cref="TranslateAsync"/> は呼べる。
    /// 結果が <see cref="TranslationOutcome.NotConfigured"/> になる。
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>
    /// 翻訳する。
    /// </summary>
    /// <remarks>
    /// <b>失敗を例外で返さない。</b>RULES.md 🟡-6「失敗時は安全側に倒す」に従い、
    /// 呼び出し側が <see cref="TranslationResult.ShouldFallBack"/> を見て
    /// ローカルNMTへ切り替えられるようにする（FR-TRN-06）。
    /// 想定外の例外は握り潰さずそのまま投げる（RULES.md 🔵）。
    /// </remarks>
    Task<TranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken cancellationToken = default);
}
