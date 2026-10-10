namespace Glosslay.Entitlement;

/// <summary>
/// 常に無料版を返す実装（v1.0。FR-SYS-10 / RULES.md 🟡-4）。
/// </summary>
/// <remarks>
/// <para><b>上限は実質無制限。</b>v1.0 は配線を通すことが目的で、絞ることが目的ではない。
/// SPEC.md の「無料 / サブスクの差」は案の段階（v1.1 で確定）。</para>
/// <para>上限を指定して作れるのは<b>ゲートが効くことを検査するため</b>
/// （<c>NmtBench --entitlement</c>）。無制限のままでは、判定箇所を通っているのか
/// 素通りしているのか区別がつかない。<b>製品は既定の無制限で使う。</b></para>
/// </remarks>
public sealed class FreeOnlyEntitlementService : IEntitlementService
{
    /// <summary>上限なしを表す値。</summary>
    public const int Unlimited = int.MaxValue;

    /// <summary>製品が使う実体。上限なし。</summary>
    public static IEntitlementService Default { get; } = new FreeOnlyEntitlementService();

    /// <summary>
    /// 上限を指定して作る。<b>検査用。</b>既定はすべて無制限。
    /// </summary>
    public FreeOnlyEntitlementService(
        int maxWatchedRegions = Unlimited,
        int maxProfiles = Unlimited,
        int maxGlossaryEntries = Unlimited,
        int maxHistoryEntries = Unlimited,
        bool canSearchHistory = true,
        bool showsAds = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWatchedRegions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProfiles);
        ArgumentOutOfRangeException.ThrowIfNegative(maxGlossaryEntries);
        ArgumentOutOfRangeException.ThrowIfNegative(maxHistoryEntries);

        MaxWatchedRegions = maxWatchedRegions;
        MaxProfiles = maxProfiles;
        MaxGlossaryEntries = maxGlossaryEntries;
        MaxHistoryEntries = maxHistoryEntries;
        CanSearchHistory = canSearchHistory;
        ShowsAds = showsAds;
    }

    /// <inheritdoc />
    public EntitlementTier Tier => EntitlementTier.Free;

    /// <inheritdoc />
    public int MaxWatchedRegions { get; }

    /// <inheritdoc />
    public int MaxProfiles { get; }

    /// <inheritdoc />
    public int MaxGlossaryEntries { get; }

    /// <inheritdoc />
    public int MaxHistoryEntries { get; }

    /// <inheritdoc />
    public bool CanSearchHistory { get; }

    /// <inheritdoc />
    /// <remarks>
    /// <b>v1.0 は広告そのものが無い</b>（FR-ADS の実装範囲は配線と <c>NullAdProvider</c> のみ）。
    /// 既定で <c>false</c> なのはそのため。v1.2 で広告を入れるときに無料版を <c>true</c> にする。
    /// </remarks>
    public bool ShowsAds { get; }
}
