using System.Globalization;

namespace Glosslay.Usage;

/// <summary>
/// 使用量の文面（RULES.md 🟡-8「使用量を常時表示」）。
/// </summary>
/// <remarks>
/// <para><b>3 か所で同じ数字を出すため、文面を 1 か所にまとめる。</b>
/// 操作ウィンドウ・訳文オーバーレイの隅・常駐オーバーレイで食い違うと、
/// どれが本当か分からなくなる。</para>
/// <para>画面から切り離してあるので、<c>NmtBench --usage</c> で文面を検査できる。</para>
/// </remarks>
public static class UsageSummary
{
    /// <summary>
    /// 自動呼び出しの残りを短い形にも出す閾値（上限に対する割合）。
    /// </summary>
    /// <remarks>
    /// <b>ふだんは出さない。</b>オーバーレイの隅は狭く、常に出すと肝心の
    /// クラウドの数字が読みにくくなる。<b>減ってきたときだけ</b>添える。
    /// </remarks>
    public const double RemainingWarningRatio = 0.2;

    /// <summary>
    /// 狭い場所に出す 1 行（訳文オーバーレイの隅・常駐オーバーレイ）。
    /// </summary>
    /// <remarks>
    /// <b>クラウドを先頭に置く。</b>お金がかかるのはそこだけなので、
    /// 1 行しか読まれなくても要点が伝わる。
    /// </remarks>
    public static string OneLine(UsageDay day, int remaining, int maxCalls)
    {
        ArgumentNullException.ThrowIfNull(day);

        var text = day.CloudCalls == 0
            ? "今日のクラウド翻訳 なし"
            : $"今日 クラウド {Count(day.CloudCalls)}回 / {Compact(day.CloudTokens)}トークン";

        // 上限に近づいたときだけ添える。止まってから気づくのでは遅い。
        if (maxCalls > 0 && remaining <= maxCalls * RemainingWarningRatio)
        {
            text += $"・自動の残り {Count(remaining)}回";
        }

        return text;
    }

    /// <summary>
    /// 操作ウィンドウに出す明細。
    /// </summary>
    /// <remarks>
    /// <b>クラウドとローカルを分けて出す。</b>合算すると、
    /// 「使った」の意味（課金されるのか否か）が分からなくなる。
    /// </remarks>
    public static string Detail(UsageDay day, int remaining, int maxCalls)
    {
        ArgumentNullException.ThrowIfNull(day);

        var lines = new List<string>
        {
            day.CloudCalls == 0
                ? "今日のクラウド翻訳: なし（費用は発生していません）"
                : $"今日のクラウド翻訳: {Count(day.CloudCalls)} 回 / {Count(day.CloudTokens)} トークン"
                    + (day.CloudFailures > 0 ? $"（うち失敗 {Count(day.CloudFailures)} 回）" : string.Empty),

            $"今日のローカル翻訳: {Count(day.LocalCalls)} 回（費用なし）",
        };

        // キャッシュが効いているかは、数字を出さないと確かめようがない（RULES.md 🟡-8）。
        if (day.CachedLines > 0)
        {
            lines.Add($"覚えていた訳で済ませた行: {Count(day.CachedLines)} 行");
        }

        if (maxCalls > 0)
        {
            lines.Add($"自動呼び出しの残り: {Count(remaining)} / {Count(maxCalls)} 回（直近1時間）");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>区切り付きの整数。</summary>
    private static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>
    /// 狭い場所むけに桁を詰める。
    /// </summary>
    /// <remarks>
    /// <b>1000 未満はそのまま出す。</b>使い始めの数百トークンを「0.3k」と出すと、
    /// 使っていないように見える。
    /// </remarks>
    private static string Compact(long value) => value switch
    {
        < 1_000 => Count(value),
        < 1_000_000 => (value / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "k",
        _ => (value / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M",
    };
}
