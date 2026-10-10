namespace Glosslay.Entitlement;

/// <summary>利用資格の区分。</summary>
public enum EntitlementTier
{
    /// <summary>無料版。v1.0 は常にこちら。</summary>
    Free,

    /// <summary>サブスク契約者。v1.1 以降。</summary>
    Subscriber,
}

/// <summary>
/// 機能ゲートの判定（FR-SYS-10 / FR-LIC-01 / RULES.md 🟡-4）。
/// </summary>
/// <remarks>
/// <para><b>機能ゲートは必ずここを経由する。</b>
/// 「無料版ならこの数まで」という判断をコードのあちこちに直接書くと、
/// 有料版を入れるときに UI 全体へ手が入る（RULES.md 🟡-4）。</para>
///
/// <para><b>v1.0 の実装（<see cref="FreeOnlyEntitlementService"/>）は上限を実質無制限で返す。</b>
/// 配線だけを先に通し、<b>実際に絞るのは v1.1 で差を確定してから</b>。
/// SPEC.md の「無料 / サブスクの差」は案の段階で、まだ確定していない。</para>
///
/// <para>⚠️ <b>v1.0 で無料提供したものを、後から有料側へ移してはならない</b>（RULES.md 🟡-5）。
/// ここで上限を入れるときは、<b>新しい機能を足す形</b>でのみ行うこと。
/// いま無制限で出しているものを後から 3 件に絞る、という変更はできない。</para>
/// </remarks>
public interface IEntitlementService
{
    /// <summary>いまの区分。</summary>
    EntitlementTier Tier { get; }

    /// <summary>同時に自動監視できる領域の数（FR-MOD-02）。</summary>
    int MaxWatchedRegions { get; }

    /// <summary>保存できるプロファイルの数。</summary>
    int MaxProfiles { get; }

    /// <summary>登録できる用語集の件数（FR-TRN-11）。</summary>
    int MaxGlossaryEntries { get; }

    /// <summary>残す翻訳履歴の件数（FR-LOG-03）。</summary>
    int MaxHistoryEntries { get; }

    /// <summary>翻訳履歴を検索できるか。</summary>
    bool CanSearchHistory { get; }

    /// <summary>広告を表示する対象か（FR-ADS-04。契約者には出さない）。</summary>
    bool ShowsAds { get; }
}
