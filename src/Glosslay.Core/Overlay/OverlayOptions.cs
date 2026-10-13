using Glosslay.Configuration;

namespace Glosslay.Overlay;

/// <summary>訳文の文字の大きさ（FR-OVL-05）。</summary>
public enum OverlayTextSize
{
    /// <summary>小。原文に近い大きさで、画面を覆う面積が最も小さい。</summary>
    Small,

    /// <summary>中。<b>既定。</b></summary>
    Medium,

    /// <summary>大。小さい文字が読みにくい場合に。</summary>
    Large,
}

/// <summary>
/// 訳文の重ね方の設定（FR-OVL-05 / FR-OVL-06）。
/// </summary>
/// <remarks>
/// <para><b>文字の大きさは原文の枠から決める</b>のが基本（大きく描かれた文字には大きく重ねる）。
/// ここで持つのはその<b>倍率</b>で、絶対値ではない。
/// 絶対値にすると、ゲームごとに文字の大きさが違うため必ず合わなくなる。</para>
/// </remarks>
public sealed record OverlayOptions
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "overlay.json";

    /// <summary>文字の大きさ（FR-OVL-05）。</summary>
    public OverlayTextSize TextSize { get; init; } = OverlayTextSize.Medium;

    /// <summary>
    /// 自動で消えるまでの時間（FR-OVL-06）。
    /// </summary>
    /// <remarks>
    /// <b>出しっぱなしにしない。</b>訳文は押した瞬間の画面のもので、
    /// 画面が変われば嘘になる。消える方が安全側。
    /// </remarks>
    public TimeSpan AutoHideAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <see cref="TextSize"/> に対応する倍率。
    /// </summary>
    /// <remarks>
    /// <b>幅を控えめにしてある。</b>大きくすると画面を覆う面積がそのぶん増え、
    /// 「ゲームの描画を妨げない」（RULES.md の判断基準 2 位）とぶつかる。
    /// </remarks>
    public double TextScale => TextSize switch
    {
        OverlayTextSize.Small => 0.85,
        OverlayTextSize.Large => 1.25,
        _ => 1.0,
    };

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static OverlayOptions Load() => JsonSettings.Load(FileName, () => new OverlayOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
