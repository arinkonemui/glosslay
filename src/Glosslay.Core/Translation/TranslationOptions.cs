using System.Text.Json.Serialization;
using Glosslay.Configuration;

namespace Glosslay.Translation;

/// <summary>どのバックエンドで訳すか（FR-TRN-01 / FR-TRN-06）。</summary>
/// <remarks>
/// <b>「自動で切り替わってほしくない」という要望に応えるための選択肢。</b>
/// キーを登録した人にとっては、質の落ちた訳が黙って出るより
/// 「今は訳せない」と分かる方がよい場合がある。2026-10-01 の人間の要望。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TranslationBackendChoice>))]
public enum TranslationBackendChoice
{
    /// <summary>
    /// 自動（既定）。キーがあればクラウド。<b>失敗したらローカルへ切り替える</b>（FR-TRN-06）。
    /// </summary>
    Auto,

    /// <summary>
    /// クラウドのみ。<b>失敗しても切り替えない。</b>
    /// </summary>
    /// <remarks>
    /// キーが未登録なら訳せない（<see cref="TranslationOutcome.NotConfigured"/>）。
    /// <b>利用者が明示的に選んだ場合だけこうなる。</b>
    /// 既定は <see cref="Auto"/> のままで、キーが無くても動く（RULES.md 🟡-1）。
    /// </remarks>
    CloudOnly,

    /// <summary>ローカルのみ。キーが登録されていても使わない。</summary>
    /// <remarks>
    /// 通信を一切させたくない場合や、クラウドの利用量を使い切りたくない場合に選ぶ。
    /// </remarks>
    LocalOnly,
}

/// <summary>
/// 翻訳の全体設定。
/// </summary>
/// <remarks>
/// <para>バックエンドをまたぐ設定をここに置く。特定のバックエンドの設定は
/// <see cref="GeminiOptions"/> / <see cref="LocalNmtOptions"/> にある。</para>
/// <para>ユーザーデータの場所は <see cref="GlosslayPaths"/> を参照
/// （RULES.md 🔵「設定は人が読める JSON で保存」）。</para>
/// </remarks>
public sealed record TranslationOptions
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "translation.json";

    /// <summary>
    /// どのバックエンドで訳すか。
    /// </summary>
    /// <remarks>
    /// <b>既定は <see cref="TranslationBackendChoice.Auto"/>。</b>
    /// 何も設定しない利用者が、キー無しでも翻訳できて、混雑しても止まらない状態になる。
    /// </remarks>
    public TranslationBackendChoice Backend { get; init; } = TranslationBackendChoice.Auto;

    /// <summary>
    /// メイン言語（画面と訳文の言語 — FR-SYS-03）。BCP 47。<c>null</c> なら既定（日本語）。
    /// </summary>
    /// <remarks>
    /// <para><b>アプリ全体で 1 つ。</b>プロファイルでは変わらない（2026-10-25 人間の判断）。</para>
    /// <para>読むときは <see cref="Main"/> を使う（以前の設定ファイルの値の引き継ぎと、正規化が入る）。</para>
    /// </remarks>
    public string? MainLanguage { get; init; }

    /// <summary>
    /// 以前の設定ファイルにあった「訳文の言語」。<b>読むだけで書かない。</b>
    /// </summary>
    /// <remarks>
    /// 2026-10-25 までは訳文の言語だけを選んでいた。メイン言語が未設定なら、この値を引き継ぐ
    /// （<see cref="Main"/>）。書き出すときは <c>null</c> にして消す。
    /// </remarks>
    public string? TargetLanguage { get; init; }

    /// <summary>
    /// 原文の言語の「いまの値」（FR-OCR-02）。<c>null</c> なら既定（英語。メイン言語が英語なら日本語）。
    /// </summary>
    /// <remarks>
    /// <b>プロファイルを当てると上書きされる。</b>ゲームごとの値は <c>Profile.SourceLanguage</c> が持つ。
    /// 読むときは <see cref="Source"/> を使う。
    /// </remarks>
    public string? SourceLanguage { get; init; }

    /// <summary>いまのメイン言語（正規化済み）。</summary>
    [JsonIgnore]
    public string Main => Languages.NormalizeMain(MainLanguage ?? TargetLanguage);

    /// <summary>いまの原文の言語（正規化済み）。</summary>
    [JsonIgnore]
    public string Source => Languages.NormalizeSource(SourceLanguage, Main);

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static TranslationOptions Load() =>
        JsonSettings.Load(FileName, () => new TranslationOptions());

    /// <summary>設定を書き出す。</summary>
    /// <remarks>以前の「訳文の言語」はメイン言語へ移して消す（<see cref="TargetLanguage"/>）。</remarks>
    public void Save() => JsonSettings.Save(FileName, this with { MainLanguage = Main, TargetLanguage = null });
}
