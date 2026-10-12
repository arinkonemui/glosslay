using Glosslay.Configuration;

namespace Glosslay.Usage;

/// <summary>
/// 使用量を日ごとに数えて残す（RULES.md 🟡-8「使用量を常時表示」）。
/// </summary>
/// <remarks>
/// <para><c>%APPDATA%\Glosslay\usage.json</c> に人が読める形で置く（RULES.md 🔵）。
/// <b>自分がどれだけ使ったかを、アプリを通さずに確かめられる</b>ようにするため。</para>
/// <para><b>送信しない。</b>ここにあるのは利用者自身の記録で、
/// 外へ出す経路は作らない（RULES.md 🔵「ログは既定でローカルのみ」）。</para>
/// <para><b>翻訳のたびに書き出す。</b>落ちたときに今日のぶんが消えると、
/// 「予期しない課金」を防ぐという目的そのものが果たせない。
/// 1 回の翻訳に対して数 KB の書き込みで、翻訳自体の数百 ms に比べれば無視できる。</para>
/// </remarks>
public sealed class UsageStore
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "usage.json";

    /// <summary>
    /// 残す日数。
    /// </summary>
    /// <remarks>
    /// 月ごとの請求を見返せる程度にしてある。
    /// <b>無制限に貯めない</b> — 使用量の記録それ自体が大きくなるのは筋が通らない。
    /// </remarks>
    public const int KeepDays = 62;

    private readonly TimeProvider _time;
    private readonly string _fileName;
    private readonly List<UsageDay> _days;

    /// <param name="timeProvider">日付の取得元。<b>検査で日をまたぐために差し替える。</b></param>
    /// <param name="days">読み込み済みの記録。<c>null</c> ならファイルから読む。</param>
    /// <param name="fileName">
    /// 書き出す先。<c>null</c> なら <see cref="FileName"/>。
    /// 検査が実物の記録を壊さないために差し替える。
    /// </param>
    public UsageStore(
        TimeProvider? timeProvider = null,
        IEnumerable<UsageDay>? days = null,
        string? fileName = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _fileName = fileName ?? FileName;
        _days = [.. days ?? LoadFromFile(_fileName)];
    }

    /// <summary>残っている記録。新しい日が後ろ。</summary>
    public IReadOnlyList<UsageDay> Days => _days;

    /// <summary>
    /// 今日の記録。まだ何も使っていなければ空の記録。
    /// </summary>
    /// <remarks>
    /// <b>日付は利用者の地方時で見る。</b>UTC で切ると、日本では朝 9 時に「昨日」が終わる。
    /// </remarks>
    public UsageDay Today
    {
        get
        {
            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            return _days.Find(d => d.Date == today) ?? new UsageDay { Date = today };
        }
    }

    /// <summary>クラウド翻訳を 1 回ぶん数える。</summary>
    /// <param name="tokens">クラウドが報告したトークン数。分からなければ <c>null</c>。</param>
    /// <param name="succeeded">成功したか。<b>失敗も呼び出しには数える。</b></param>
    public void RecordCloud(int? tokens, bool succeeded) =>
        Update(day => day with
        {
            CloudCalls = day.CloudCalls + 1,
            CloudTokens = day.CloudTokens + (tokens ?? 0),
            CloudFailures = day.CloudFailures + (succeeded ? 0 : 1),
        });

    /// <summary>ローカル翻訳を 1 回ぶん数える。</summary>
    public void RecordLocal() =>
        Update(day => day with { LocalCalls = day.LocalCalls + 1 });

    /// <summary>覚えていた訳文で済ませた行を数える（FR-TRN-09）。</summary>
    public void RecordCached(int lines)
    {
        if (lines > 0)
        {
            Update(day => day with { CachedLines = day.CachedLines + lines });
        }
    }

    /// <summary>ファイルから読み直す（手で消した場合など）。</summary>
    public void Reload()
    {
        _days.Clear();
        _days.AddRange(LoadFromFile(_fileName));
    }

    /// <summary>
    /// 今日の記録を書き換えて保存する。
    /// </summary>
    /// <remarks>
    /// <b>古い日は落とす。</b>読み込んだ時点ではなく書き込む時点で落とすのは、
    /// 起動しただけで記録が減るのを避けるため。
    /// </remarks>
    private void Update(Func<UsageDay, UsageDay> change)
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var index = _days.FindIndex(d => d.Date == today);

        if (index >= 0)
        {
            _days[index] = change(_days[index]);
        }
        else
        {
            _days.Add(change(new UsageDay { Date = today }));
        }

        var limit = today.AddDays(-KeepDays);
        _days.RemoveAll(d => d.Date < limit);
        _days.Sort((a, b) => a.Date.CompareTo(b.Date));

        JsonSettings.Save(_fileName, _days);
    }

    private static List<UsageDay> LoadFromFile(string fileName) =>
        JsonSettings.Load(fileName, () => new List<UsageDay>());
}
