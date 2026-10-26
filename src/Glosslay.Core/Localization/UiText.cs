using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Glosslay.Translation;

namespace Glosslay.Localization;

/// <summary>
/// 画面の文言の言語（FR-SYS-03 の「画面の言語」）。
/// </summary>
/// <remarks>
/// <para><b>今の日本語の文をそのまま鍵にする</b>（gettext と同じ方式）。コードには日本語の文を
/// <c>T("…")</c> で包んで書き、英語・簡体字・韓国語は言語別の JSON（<c>Localization/*.json</c>・
/// 「日本語の文 → 訳」）から引く。日本語の画面は何も引かずにそのまま出す。</para>
/// <para>鍵を記号（<c>Settings.MainLanguage</c> など）にしなかったのは、
/// コードを読むときに何が画面に出るかがそのまま分かるようにするため。
/// <b>日本語の文を変えると訳が外れる</b>ので、<c>NmtBench --ui</c> で全部の文に訳があるかを確かめる。</para>
/// <para><b>差し込みは <see cref="string.Format(IFormatProvider, string, object[])"/> と同じ <c>{0}</c> 形式。</b>
/// 訳文の側で語順を変えられる。<c>{0:F0}</c> のような書式も訳文に残すこと。</para>
/// <para><b>訳が無い文は日本語のまま出す</b>（落とさない）。</para>
/// </remarks>
public static class UiText
{
    /// <summary>画面の言語として持っているもの（メイン言語と同じ — FR-SYS-03）。</summary>
    public static IReadOnlyList<string> Supported { get; } = ["ja", "en", "zh-Hans", "ko"];

    private static IReadOnlyDictionary<string, string>? _table;

    /// <summary>いまの画面の言語。</summary>
    public static string Language { get; private set; } = "ja";

    /// <summary>画面の言語が変わった。画面は文言を引き直す。</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// 画面の言語を変える。<b>メイン言語と同じものを渡す</b>（FR-SYS-03）。
    /// </summary>
    /// <remarks>
    /// 持っていない言語（設定ファイルに手で書いた <c>de</c> など）は英語で出す。
    /// 日本語が読めない人の方が多いと見込むため。
    /// </remarks>
    public static void SetLanguage(string mainLanguage)
    {
        ArgumentNullException.ThrowIfNull(mainLanguage);

        var tag = Languages.NormalizeMain(mainLanguage);
        if (!Supported.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            tag = "en";
        }

        if (string.Equals(tag, Language, StringComparison.OrdinalIgnoreCase) && (_table is not null || tag == "ja"))
        {
            return;
        }

        _table = tag == "ja" ? null : TableFor(tag);
        Language = tag;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>いまの言語の文言。訳が無ければ日本語のまま。</summary>
    /// <param name="japanese">日本語の文（鍵）。</param>
    public static string T(string japanese)
    {
        ArgumentNullException.ThrowIfNull(japanese);
        return _table is not null && _table.TryGetValue(japanese, out var translated) && translated.Length > 0
            ? translated
            : japanese;
    }

    /// <summary>いまの言語の文言に値を差し込む（<c>{0}</c> 形式）。</summary>
    /// <param name="japanese">日本語の文（鍵）。</param>
    /// <param name="args">差し込む値。</param>
    public static string T(string japanese, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(japanese), args);

    /// <summary>
    /// 言語の訳の表（日本語の文 → 訳）。日本語なら空。
    /// </summary>
    /// <remarks>検査（<c>NmtBench --ui</c>）でも使う。</remarks>
    public static IReadOnlyDictionary<string, string> TableFor(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (string.Equals(tag, "ja", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>();
        }

        var name = $"Glosslay.Localization.{tag}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"画面の文言の表がありません: {name}");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? new Dictionary<string, string>();
    }
}
