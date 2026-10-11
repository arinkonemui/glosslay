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
    /// 訳文の言語。BCP 47（FR-CFG-01 の「言語」）。
    /// </summary>
    /// <remarks>
    /// <para><b>プロファイルを当てると上書きされる。</b>ここにあるのは
    /// 「いまの値」で、ゲームごとの値は <c>Profile.TargetLanguage</c> が持つ。</para>
    /// <para><b>既定の日本語以外はクラウド翻訳のAPIキーが要る</b>（FR-TRN-03 / RULES.md 🟡-1）。
    /// 選択肢は <see cref="TargetLanguages"/>。</para>
    /// </remarks>
    public string TargetLanguage { get; init; } = TargetLanguages.Default;

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static TranslationOptions Load() =>
        JsonSettings.Load(FileName, () => new TranslationOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);
}
