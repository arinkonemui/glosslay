using Glosslay.Configuration;
using Glosslay.Entitlement;

namespace Glosslay.Profiles;

/// <summary>プロファイルを保存しようとした結果。</summary>
public enum ProfileSaveResult
{
    /// <summary>新しく追加した。</summary>
    Added,

    /// <summary>同じ名前のものを置き換えた。</summary>
    Replaced,

    /// <summary>名前が空。</summary>
    NameRequired,

    /// <summary>件数の上限に達している（FR-SYS-10 / RULES.md 🟡-4）。</summary>
    LimitReached,
}

/// <summary>
/// プロファイルの保存・読み込み・自動適用（FR-CFG-01 / 02 / 03）。
/// </summary>
/// <remarks>
/// <para><c>%APPDATA%\Glosslay\profiles.json</c> に人が読める形で置く（RULES.md 🔵）。
/// 手で直せること、他の人と交換できること（FR-CFG-03）を想定している。</para>
/// <para><b>件数の上限は <see cref="IEntitlementService"/> を必ず経由する</b>（RULES.md 🟡-4）。
/// v1.0 の実装は上限なしだが、判定箇所はここに作ってある。</para>
/// </remarks>
public sealed class ProfileStore
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "profiles.json";

    private readonly IEntitlementService _entitlements;
    private readonly List<Profile> _profiles;
    private readonly string _fileName;

    /// <param name="entitlements">件数の上限（FR-SYS-10）。</param>
    /// <param name="profiles">読み込み済みの一覧。<c>null</c> ならファイルから読む。</param>
    /// <param name="fileName">
    /// 書き出す先。<c>null</c> なら <see cref="FileName"/>。
    /// <b>検査（<c>NmtBench --profiles</c>）が実物の設定を壊さないために差し替える。</b>
    /// 保存の成否は上限の判定と同じ経路で確かめたいが、そのために
    /// 利用者のプロファイルを上書きするのは割に合わない。
    /// </param>
    public ProfileStore(
        IEntitlementService? entitlements = null,
        IEnumerable<Profile>? profiles = null,
        string? fileName = null)
    {
        _entitlements = entitlements ?? FreeOnlyEntitlementService.Default;
        _fileName = fileName ?? FileName;
        _profiles = [.. profiles ?? LoadFromFile(_fileName)];
    }

    /// <summary>保存されているプロファイル。</summary>
    public IReadOnlyList<Profile> Profiles => _profiles;

    /// <summary>あと何件追加できるか。</summary>
    public int Remaining => Math.Max(0, _entitlements.MaxProfiles - _profiles.Count);

    /// <summary>
    /// 保存する。同じ名前があれば置き換える。
    /// </summary>
    /// <remarks>
    /// <b>置き換えは上限に数えない。</b>上限は「持てる数」であって「保存した回数」ではない。
    /// </remarks>
    public ProfileSaveResult Save(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return ProfileSaveResult.NameRequired;
        }

        var index = _profiles.FindIndex(p => NameEquals(p.Name, profile.Name));
        if (index >= 0)
        {
            _profiles[index] = profile;
            Persist();
            return ProfileSaveResult.Replaced;
        }

        // FR-SYS-10 / RULES.md 🟡-4: 機能ゲートは必ず IEntitlementService を経由する。
        if (_profiles.Count >= _entitlements.MaxProfiles)
        {
            return ProfileSaveResult.LimitReached;
        }

        _profiles.Add(profile);
        Persist();
        return ProfileSaveResult.Added;
    }

    /// <summary>名前で消す。消したら <c>true</c>。</summary>
    public bool Remove(string name)
    {
        var index = _profiles.FindIndex(p => NameEquals(p.Name, name));
        if (index < 0)
        {
            return false;
        }

        _profiles.RemoveAt(index);
        Persist();
        return true;
    }

    /// <summary>名前で探す。</summary>
    public Profile? Find(string name) => _profiles.Find(p => NameEquals(p.Name, name));

    /// <summary>
    /// ファイルから読み直す（FR-CFG-03 のインポート）。
    /// </summary>
    /// <remarks>
    /// <b>他の人から貰った 1 件を貼り付ける経路がこれ。</b>
    /// 専用の取り込み画面を作らず、交換できる JSON をそのまま編集してもらう。
    /// 壊れていれば <see cref="JsonSettings.Load"/> が空の一覧に倒すため、例外では落ちない
    /// （RULES.md 🟡-6）。
    /// </remarks>
    public void Reload()
    {
        _profiles.Clear();
        _profiles.AddRange(LoadFromFile(_fileName));
    }

    /// <summary>
    /// このウィンドウに当てはまるプロファイルを探す（FR-CFG-02）。
    /// </summary>
    /// <remarks>
    /// <b>先に登録したものを優先する。</b>複数が当たる場合、
    /// 後から足したものが黙って勝つと、利用者には理由が分からない。
    /// </remarks>
    public Profile? FindForWindow(string windowClass, string windowTitle) =>
        _profiles.Find(p => p.Matches(windowClass, windowTitle));

    /// <summary>1 件を JSON にする（FR-CFG-03）。</summary>
    public static string Export(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSettings.Serialize(profile);
    }

    /// <summary>JSON から 1 件を読む。読めなければ <c>null</c>（FR-CFG-03）。</summary>
    /// <remarks>
    /// <b>受け取った JSON を信用しない。</b>他の人から貰うことを想定しているため、
    /// 壊れていても例外で落とさず <c>null</c> を返す（RULES.md 🟡-6）。
    /// </remarks>
    public static Profile? Import(string json) => JsonSettings.Deserialize<Profile>(json);

    private void Persist() => JsonSettings.Save(_fileName, _profiles);

    private static List<Profile> LoadFromFile(string fileName) =>
        JsonSettings.Load(fileName, () => new List<Profile>());

    private static bool NameEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
