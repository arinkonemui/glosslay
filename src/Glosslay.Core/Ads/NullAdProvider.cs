namespace Glosslay.Ads;

/// <summary>
/// 何も描画しない広告（v1.0 の唯一の実装）。
/// </summary>
/// <remarks>
/// <para>v1.0 は<b>制御フローの配線だけ</b>を行い、広告 SDK を組み込まない
/// （SPEC.md FR-ADS の実装範囲）。SDK を入れると配布サイズと
/// AV 誤検知（RULES.md 🔴-2）に効き、収益が立つ前にその代償を払うことになる。</para>
/// <para><b>契約成立後はアプリを更新せず、リモート設定の切替だけで表示開始できる</b>
/// のが FR-ADS-03 の要件。そのため判断（<see cref="AdPolicy"/>）と描画（ここ）を分けてある。</para>
/// </remarks>
public sealed class NullAdProvider : IAdProvider
{
    /// <summary>製品が使う実体。</summary>
    public static IAdProvider Default { get; } = new NullAdProvider();

    /// <inheritdoc />
    public string Name => "広告なし";

    /// <inheritdoc />
    /// <remarks>常に埋められない。呼び出し側は枠ごと消す（FR-ADS-05）。</remarks>
    public bool CanFill(AdSlot slot) => false;
}
