using System.Globalization;

namespace Glosslay.Monitoring;

/// <summary>
/// 自動監視を初めて ON にするときの案内（FR-MOD-08）。
/// </summary>
/// <remarks>
/// <para><b>判断の材料を、判断する人が、判断する時点で受け取れるようにする。</b>
/// もともとは「PvPモード」の一部だったが、アプリが対戦かどうかを判定できないため
/// 2026-10-11 に廃止し、<b>自動監視を初めて ON にしたとき</b>へ移した（PLAN.md）。</para>
/// <para><b>文面を画面から切り離してある。</b>数字が実測から離れていないかを
/// <c>NmtBench --watch</c> で機械的に見るため。実測値はすべてこのクラスに集める。</para>
/// </remarks>
public static class AutoWatchNotice
{
    /// <summary>撮影にかかる時間。2026-09-24 実測（PLAN.md 課題4）。</summary>
    private static readonly TimeSpan Capture = TimeSpan.FromMilliseconds(290);

    /// <summary>指定範囲（640x480）の OCR。2026-09-22 実測。</summary>
    private static readonly TimeSpan RegionOcr = TimeSpan.FromMilliseconds(551);

    /// <summary>画面全体（1920x1080）の OCR。速い側。2026-09-22 実測。</summary>
    private static readonly TimeSpan FullScreenOcrFast = TimeSpan.FromMilliseconds(1648);

    /// <summary>画面全体（1920x1080）の OCR。遅い側。2026-09-22 実測。</summary>
    private static readonly TimeSpan FullScreenOcrSlow = TimeSpan.FromMilliseconds(2119);

    /// <summary>
    /// 翻訳。ローカルNMT の 15 行で 2026-10-01 実測。
    /// </summary>
    /// <remarks>
    /// <b>クラウドはここに収まらない。</b>混雑すると 1 回に数十秒かかる（PLAN.md 課題7）。
    /// 見込みの数字に混ぜず、文面で別に断る。
    /// </remarks>
    private static readonly TimeSpan LocalTranslate = TimeSpan.FromMilliseconds(293);

    /// <summary>
    /// 翻訳 1 回にかかる見込み（撮影 + OCR + 翻訳）。
    /// </summary>
    /// <param name="hasRegion">範囲を絞っているか。絞ると OCR が大きく下がる。</param>
    public static (TimeSpan Fastest, TimeSpan Slowest) Estimate(bool hasRegion)
    {
        var fixedCost = Capture + LocalTranslate;
        return hasRegion
            ? (fixedCost + RegionOcr, fixedCost + RegionOcr)
            : (fixedCost + FullScreenOcrFast, fixedCost + FullScreenOcrSlow);
    }

    /// <summary>
    /// 案内の文面を組み立てる（FR-MOD-08）。
    /// </summary>
    /// <remarks>
    /// <b>範囲を絞っているかで中身を変える。</b>絞っていない人にとっては
    /// 「絞れば半分以下になる」ことが一番効く情報で、
    /// すでに絞っている人にその案内を出しても邪魔にしかならない。
    /// </remarks>
    public static string Build(AutoWatchOptions options, bool hasRegion)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (fastest, slowest) = Estimate(hasRegion);
        var lines = new List<string>
        {
            $"自動監視を入れると、{Seconds(options.Interval)} 秒ごとに画面を撮って見比べます。",
            "変化があったときだけ翻訳するので、画面が止まっている間は何もしません。",
            string.Empty,
        };

        if (hasRegion)
        {
            lines.Add("翻訳 1 回あたりの実測（指定した範囲）");
            lines.Add($"　撮影 {Seconds(Capture)} + 読み取り {Seconds(RegionOcr)}"
                      + $" + 翻訳 {Seconds(LocalTranslate)} = 約 {Seconds(fastest)} 秒");
        }
        else
        {
            lines.Add("翻訳 1 回あたりの実測（画面全体）");
            lines.Add($"　撮影 {Seconds(Capture)}"
                      + $" + 読み取り {Seconds(FullScreenOcrFast)}〜{Seconds(FullScreenOcrSlow)}"
                      + $" + 翻訳 {Seconds(LocalTranslate)}"
                      + $" = 約 {Seconds(fastest)}〜{Seconds(slowest)} 秒");
            lines.Add(string.Empty);
            lines.Add("プレビューをドラッグして範囲を絞ると"
                      + $"約 {Seconds(Estimate(true).Fastest)} 秒まで短くなり、");
            lines.Add("背景の動きによる誤検知も減ります。");
        }

        lines.Add(string.Empty);
        lines.Add($"1 時間あたり {options.MaxCallsPerHour.ToString(CultureInfo.CurrentCulture)} 回で"
                  + "自動的に止まります。");
        lines.Add("上の翻訳時間はローカル翻訳の実測です。"
                  + "クラウド翻訳は混雑すると 1 回に数十秒かかることがあります。");
        lines.Add(string.Empty);
        lines.Add("入れますか？");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>秒で表す。小数 1 桁。</summary>
    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture);
}
