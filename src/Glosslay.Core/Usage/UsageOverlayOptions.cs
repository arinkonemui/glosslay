using Glosslay.Configuration;

namespace Glosslay.Usage;

/// <summary>画面のどの隅に出すか。</summary>
public enum OverlayCorner
{
    /// <summary>左上。</summary>
    TopLeft,

    /// <summary>右上。<b>既定。</b></summary>
    TopRight,

    /// <summary>左下。</summary>
    BottomLeft,

    /// <summary>右下。</summary>
    BottomRight,
}

/// <summary>
/// 使用量の常駐表示の設定（RULES.md 🟡-8）。
/// </summary>
/// <remarks>
/// <para><b>既定は OFF。</b>RULES.md 🟡-8 の文言は「常時表示」だが、
/// 画面の隅に出しっぱなしにすることは<b>「モニタが1枚でも邪魔されずに遊べる」というゴールと
/// 正面衝突する</b>。そのため既定では出さず、
/// 訳文オーバーレイの隅と操作ウィンドウで数字を伝える。
/// <b>文字どおりの常時表示が欲しい人のために、入れられるようにしてある</b>（2026-10-12 の人間の判断）。</para>
/// <para>出す場合もクリックスルーで、ゲームの操作は一切奪わない
/// （<c>OverlayWindowStyles</c>。FR-OVL-02 / 03 / 09）。</para>
/// </remarks>
public sealed record UsageOverlayOptions
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "usageoverlay.json";

    /// <summary>使用量の常駐表示を出すか。<b>既定は OFF。</b></summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 処理時間の実測を出すか（FR-OVL-10）。<b>既定は OFF。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>使用量と同じ隅に並べる</b>（2026-10-14・人間の判断）。隅は 1 つの設定
    /// （<see cref="Corner"/>）で両方に効く。別々の隅にすると、画面の隅が 2 か所ふさがる。</para>
    /// <para>FR-OVL-10「既定は非表示、トグル可、小サイズ」。録画・配信に映り込まないよう既定では出さない。</para>
    /// </remarks>
    public bool ShowProcessingTime { get; init; }

    /// <summary>使用量か処理時間のどちらかを出すか（隅のウィンドウが要るか）。</summary>
    public bool AnyVisible => Enabled || ShowProcessingTime;

    /// <summary>
    /// 出す隅。
    /// </summary>
    /// <remarks>
    /// <b>右上を既定にする。</b>ゲームの体力・弾数などは左上と下辺に置かれることが多く、
    /// 右上が最も当たりにくい。合わなければ変えられる。
    /// <b>使用量と処理時間の両方に効く</b>（ゲームによって邪魔にならない場所が違うため、四隅から選べる）。
    /// </remarks>
    public OverlayCorner Corner { get; init; } = OverlayCorner.TopRight;

    /// <summary>
    /// 不透明度（0〜1）。
    /// </summary>
    /// <remarks>
    /// <b>読めるが目立たない濃さ。</b>常に出ているものなので、
    /// 訳文と同じ濃さにすると視線を奪い続ける。
    /// </remarks>
    public double Opacity { get; init; } = 0.65;

    /// <summary>文字の大きさ（ピクセル）。</summary>
    public double FontSize { get; init; } = 12;

    /// <summary>画面の縁からの余白（ピクセル）。</summary>
    public double Margin { get; init; } = 12;

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static UsageOverlayOptions Load() =>
        JsonSettings.Load(FileName, () => new UsageOverlayOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
