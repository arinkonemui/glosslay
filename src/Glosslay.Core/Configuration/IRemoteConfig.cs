namespace Glosslay.Configuration;

/// <summary>
/// 遠隔から更新できる設定（FR-ADS-02 / FR-TRN-07 / RULES.md 🟡-3）。
/// </summary>
/// <param name="AdsEnabled">
/// 広告を出してよいか。<b>既定は false。</b>広告ネットワークとの契約が成立するまで、
/// 取得できなかった場合も含めて出さない（FR-ADS-02 / FR-ADS-06）。
/// </param>
/// <param name="RecommendedGeminiModel">
/// 推奨するクラウド翻訳のモデル名。<c>null</c> なら利用者の設定（<c>gemini.json</c>）をそのまま使う。
/// <b>モデル名をコードに埋めないための経路</b>（RULES.md 🟡-3）。
/// LLM は世代交代が速く、提供終了のたびに再ビルドが必要な作りにしてはいけない。
/// </param>
public sealed record RemoteSettings(bool AdsEnabled = false, string? RecommendedGeminiModel = null)
{
    /// <summary>取得できていないときの値。<b>安全側</b>に倒してある。</summary>
    public static RemoteSettings Safe { get; } = new();
}

/// <summary>
/// 署名付きの設定を遠隔から取る（RULES.md 🟡-2）。
/// </summary>
/// <remarks>
/// <para><b>失敗したら既定値に倒す</b>のが要件（FR-ADS-06「取得失敗時は広告なしにフォールバック」）。
/// 例外を投げて呼び出し側を止めない。</para>
/// <para><b>署名の検証は必須。</b>検証なしに取り込むと、途中で差し替えられた設定で
/// 広告を出したり別のモデルを使わせたりできてしまう（FR-ADS-02 / FR-LIC-03 と同じ考え方）。</para>
/// </remarks>
public interface IRemoteConfig
{
    /// <summary>いまの設定。一度も取得できていなければ <see cref="RemoteSettings.Safe"/>。</summary>
    RemoteSettings Current { get; }

    /// <summary>取り直す。<b>失敗しても例外を投げず、<see cref="Current"/> を保つ。</b></summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
