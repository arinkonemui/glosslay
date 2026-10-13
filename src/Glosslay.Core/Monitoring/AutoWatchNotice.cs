using System.Globalization;

namespace Glosslay.Monitoring;

/// <summary>
/// 自動監視を初めて ON にするときの案内（FR-MOD-08）。
/// </summary>
/// <remarks>
/// <para><b>判断の材料を、判断する人が、判断する時点で受け取れるようにする。</b>
/// もともとは「PvPモード」の一部だったが、アプリが対戦かどうかを判定できないため
/// 2026-10-11 に廃止し、<b>自動監視を初めて ON にしたとき</b>へ移した（PLAN.md）。</para>
/// <para><b>所要時間は厳密な数値はユーザーには不要</b>（2026-10-13・人間の判断）。
/// パソコンの性能や画面の文字の量で前後するため、「1 秒程度」のように丸める。
/// 撮影・読み取り・翻訳の内訳は出さない。利用者に要るのは「どのくらい待つか」だけ。</para>
/// <para><b>実測値はこのクラスに集める。</b>文面には丸めて出すが、
/// 実測から離れていないかを <c>NmtBench --watch</c> で機械的に見るため、値そのものは持つ。</para>
/// </remarks>
public static class AutoWatchNotice
{
    /// <summary>
    /// 画面全体（1920x1080）で翻訳 1 回にかかる時間。ふだんの値（15 枚の平均・撮影込み）。
    /// </summary>
    /// <remarks>
    /// <para><b>撮影から翻訳までを 1 枚ずつ通して測った値</b>（2026-10-13・電源あり・ローカル翻訳）。
    /// 段ごとの値を足して見積もると外れる（PLAN.md 課題4 の経緯）。</para>
    /// <para><b>クラウドはここに収まらない。</b>混雑すると 1 回に数十秒かかる（PLAN.md 課題7）。
    /// 見込みの数字に混ぜず、文面で別に断る。</para>
    /// </remarks>
    private static readonly TimeSpan FullScreenTypical = TimeSpan.FromMilliseconds(820);

    /// <summary>画面全体で最も時間のかかった画面（行の多い表 <c>tyr_tank_fuse</c>）。</summary>
    private static readonly TimeSpan FullScreenWorst = TimeSpan.FromMilliseconds(1430);

    /// <summary>範囲を絞った場合（1920 未満の 25 枚の平均・撮影込み）。同じ日の実測。</summary>
    private static readonly TimeSpan RegionTypical = TimeSpan.FromMilliseconds(520);

    /// <summary>範囲を絞った場合で最も時間のかかった画面（文字の詰まった <c>elin_help_textures</c>）。</summary>
    private static readonly TimeSpan RegionWorst = TimeSpan.FromMilliseconds(1290);

    /// <summary>
    /// 翻訳 1 回にかかる見込み（撮影 + 読み取り + 翻訳）。ふだんの値と、最も遅かった値。
    /// </summary>
    /// <param name="hasRegion">範囲を絞っているか。</param>
    public static (TimeSpan Typical, TimeSpan Worst) Estimate(bool hasRegion) =>
        hasRegion
            ? (RegionTypical, RegionWorst)
            : (FullScreenTypical, FullScreenWorst);

    /// <summary>
    /// 案内の文面を組み立てる（FR-MOD-08）。
    /// </summary>
    /// <remarks>
    /// <b>範囲を絞っているかで中身を変える。</b>絞っていない人には
    /// 「絞れば短くなり、誤検知も減る」ことが一番効く情報で、
    /// すでに絞っている人にその案内を出しても邪魔にしかならない。
    /// </remarks>
    public static string Build(AutoWatchOptions options, bool hasRegion)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lines = new List<string>
        {
            $"自動監視を入れると、{Seconds(options.Interval)} 秒ごとに画面を撮って見比べます。",
            "変化があったときだけ翻訳するので、画面が止まっている間は何もしません。",
            string.Empty,
        };

        if (hasRegion)
        {
            lines.Add($"翻訳 1 回にかかる時間は、指定した範囲で {Roughly(RegionTypical)}です。");
        }
        else
        {
            lines.Add($"翻訳 1 回にかかる時間は、画面全体で {Roughly(FullScreenTypical)}です。");
            lines.Add("プレビューをドラッグして範囲を絞ると"
                      + $" {Roughly(RegionTypical)}に短くなり、");
            lines.Add("背景の動きによる誤検知も減ります。");
        }

        lines.Add("（パソコンの性能や、画面の文字の量で前後します）");
        lines.Add(string.Empty);
        lines.Add($"1 時間あたり {options.MaxCallsPerHour.ToString(CultureInfo.CurrentCulture)} 回で"
                  + "自動的に止まります。");
        lines.Add("クラウド翻訳を使う場合は、混雑すると 1 回に数十秒かかることがあります。");
        lines.Add(string.Empty);
        lines.Add("入れますか？");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 「1 秒程度」のようにぼかして表す。0.5 秒刻みに丸め、0.5 秒を下限にする。
    /// </summary>
    private static string Roughly(TimeSpan value)
    {
        var halves = Math.Max(1, Math.Round(value.TotalSeconds * 2, MidpointRounding.AwayFromZero));
        return (halves / 2).ToString("0.#", CultureInfo.CurrentCulture) + " 秒程度";
    }

    /// <summary>秒で表す。小数 1 桁。設定値（見回る間隔）に使う。</summary>
    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture);
}
