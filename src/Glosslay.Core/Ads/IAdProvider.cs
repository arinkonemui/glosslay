using Glosslay.Configuration;
using Glosslay.Entitlement;

namespace Glosslay.Ads;

/// <summary>
/// 広告を出してよい枠（FR-ADS-01）。
/// </summary>
/// <remarks>
/// <para><b>オーバーレイを列挙していないのは意図的。</b>
/// FR-ADS-01 は「ゲーム中のオーバーレイには絶対に出さない」と定めている。
/// 値として存在しなければ、うっかり渡すこともできない。
/// <b>ここにオーバーレイを足さないこと。</b></para>
/// </remarks>
public enum AdSlot
{
    /// <summary>設定画面。</summary>
    Settings,

    /// <summary>翻訳履歴の画面。</summary>
    History,
}

/// <summary>
/// 広告の描画（RULES.md 🟡-2）。
/// </summary>
/// <remarks>
/// v1.0 の実装は <see cref="NullAdProvider"/> のみで、広告 SDK は組み込まない
/// （SPEC.md「v1.0 での実装範囲: 制御フローの配線と NullAdProvider のみ」）。
/// </remarks>
public interface IAdProvider
{
    /// <summary>UI に出す名前。</summary>
    string Name { get; }

    /// <summary>
    /// この枠を埋められるか。<b>埋められないなら枠ごと消す</b>（FR-ADS-05）。
    /// </summary>
    bool CanFill(AdSlot slot);
}

/// <summary>
/// 広告を出すかどうかの判断（FR-ADS-01 / 02 / 04 / 05 / 06）。
/// </summary>
/// <remarks>
/// <para><b>判断を 1 か所に集めてある。</b>画面ごとに条件を書くと、
/// 契約者に出さない（FR-ADS-04）やリモート設定で止める（FR-ADS-02）のどれかが必ず漏れる。</para>
/// <para><b>すべての条件が揃ったときだけ出す。</b>迷ったら出さない側に倒す
/// （RULES.md 🟡-6「失敗時は安全側に倒す」）。</para>
/// </remarks>
public static class AdPolicy
{
    /// <summary>
    /// この枠に広告を出すか。
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item><b>契約者には出さない</b>（FR-ADS-04。リモート設定より優先）</item>
    ///   <item><b>リモート設定が有効でなければ出さない</b>（FR-ADS-02。取得失敗時も既定 false なので出ない = FR-ADS-06）</item>
    ///   <item><b>描画側が埋められなければ出さない</b>（FR-ADS-05。枠ごと消す）</item>
    /// </list>
    /// </remarks>
    public static bool ShouldShow(
        AdSlot slot, IEntitlementService entitlements, IRemoteConfig config, IAdProvider provider)
    {
        ArgumentNullException.ThrowIfNull(entitlements);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(provider);

        // 契約者には一切出さない。リモート設定が何と言っていても覆らない。
        if (!entitlements.ShowsAds)
        {
            return false;
        }

        if (!config.Current.AdsEnabled)
        {
            return false;
        }

        return provider.CanFill(slot);
    }
}
