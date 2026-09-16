using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Glosslay.Configuration;

namespace Glosslay.Translation;

/// <summary>
/// <see cref="GeminiTranslator"/> の設定。
/// </summary>
/// <remarks>
/// <para><b>モデル名をコードに埋めないための型</b>（FR-TRN-07 / RULES.md 🟡-3）。
/// LLM の世代交代が速く、提供終了が来るたびに再ビルドが必要になる作りにしてはいけない。</para>
/// <para><see cref="Load"/> が <c>%APPDATA%\Glosslay\gemini.json</c> を読む。
/// ファイルが無ければ既定値で動き、<see cref="Save"/> で人が読める JSON として書き出せる
/// （RULES.md 🔵「設定は人が読める JSON で保存」）。
/// v1.0 では <c>IRemoteConfig</c> が推奨モデル名を上書きできるようにする。</para>
/// <para><b>APIキーはここに入れない。</b>暗号化して別に置く（<see cref="ApiKeyStore"/> / FR-TRN-05）。</para>
/// </remarks>
public sealed record GeminiOptions
{
    /// <summary>
    /// 既定のモデル。
    /// </summary>
    /// <remarks>
    /// <para><b>2026-09-15、実機で 404 を踏んで確定した値。</b>
    /// 当初は思考（thinking）が既定 OFF である <c>gemini-2.5-flash-lite</c> を選んだが、
    /// 新規に作ったプロジェクトから呼ぶと次が返る。</para>
    /// <code>
    /// 404 NOT_FOUND: This model models/gemini-2.5-flash-lite is no longer available
    /// to new users. Please update your code to use models/gemini-3.5-flash-lite
    /// </code>
    /// <para><b>公式の deprecations ページは「終了日の告知なし」と書いていたが実態と違った。</b>
    /// 既存利用者には残っているが新規は不可、という状態はページに現れない。
    /// <b>モデルの可用性はドキュメントではなく実機で確かめること。</b></para>
    /// <para>3.5 系は思考を完全には切れないが、<c>flash-lite</c> は既定が既に minimal のため
    /// <see cref="ThinkingLevel"/> を指定しても変わらない。よって既定では送らない。</para>
    /// </remarks>
    public const string DefaultModel = "gemini-3.5-flash-lite";

    /// <summary>使用するモデル名。</summary>
    public string Model { get; init; } = DefaultModel;

    /// <summary>API の基点。</summary>
    public string Endpoint { get; init; } = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>
    /// 思考の量（<c>minimal</c> / <c>low</c> / <c>medium</c> / <c>high</c>）。
    /// <c>null</c> なら指定しない。
    /// </summary>
    /// <remarks>
    /// <c>gemini-3.5-flash-lite</c> の既定は既に minimal のため、
    /// 速度目的で <c>"minimal"</c> を入れても変わらない。<b>既定では送らない</b>。
    /// 精度を上げたい場合に <c>"low"</c> 以上を試す余地を残してある。
    /// 3.x 系以外は解釈しないため、モデルを戻すときは外すこと。
    /// </remarks>
    public string? ThinkingLevel { get; init; }

    /// <summary>
    /// 生成のばらつき。翻訳では 0 にする。
    /// </summary>
    /// <remarks>
    /// 同じ原文から毎回同じ訳文が出る方が、FR-TRN-09 のキャッシュと相性が良い。
    /// 数値を書き換える余地も減る（RULES.md 🟡-7）。
    /// </remarks>
    public double Temperature { get; init; }

    /// <summary>1 回の応答の上限トークン数。暴走による課金を防ぐ（RULES.md 🟡-8）。</summary>
    public int MaxOutputTokens { get; init; } = 2048;

    /// <summary>
    /// 応答を待つ上限。
    /// </summary>
    /// <remarks>
    /// 超えたら <see cref="TranslationOutcome.NetworkError"/> にしてフォールバックへ回す。
    /// ゲーム中に無言で固まるのが最悪の挙動なので、長く待たない。
    /// </remarks>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// システム指示。<c>{target}</c> は訳文の言語名に置き換わる。
    /// </summary>
    /// <remarks>
    /// PLAN.md P0-5 が要求する「ゲームのスキル説明」「数値と固有名詞を変更しない」を明示している。
    /// 数値については <b>補完も推測もさせない</b>のが要点（RULES.md 🟡-7）。
    /// LLM は欠けた数字を「もっともらしく」埋めるため、そちらの方が誤読より有害になる。
    /// </remarks>
    public string SystemInstruction { get; init; } =
        """
        あなたは PC ゲームの画面テキストを翻訳します。
        原文はゲーム画面を OCR で読み取ったもので、スキルやアイテムの説明・セリフ・
        クエスト説明・メニュー項目・カードテキストなどが含まれます。

        規則:
        - {target} の訳文だけを出力してください。前置き・解説・注釈を書かないでください。
        - 数値は絶対に変更しないでください。桁・単位・記号をそのまま保ってください（例: 50、12秒、+3、100%、2,430）。
        - 数値を補完・推測しないでください。読み取れていない数値は、そこだけ原文のまま残してください。
        - 固有名詞（キャラクター名・カード名・スキル名・地名）は変更しないでください。
          定訳が分からない場合は原文のまま残してください。
        - OCR の誤字が含まれることがあります。文脈から補って訳して構いませんが、数値には適用しないでください。
        - 原文が既に {target} の場合は、そのまま出力してください。
        - 意味が取れない断片は、その断片だけ原文のまま残してください。
        """;

    private static string FilePath => Path.Combine(GlosslayPaths.Root, "gemini.json");

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 既定のエンコーダは日本語を あ 形式へ逃がす。
        // プロンプトを手で直せなければ設定として意味がない（RULES.md 🔵「人が読める JSON」）。
        // UnsafeRelaxedJsonEscaping ではなく範囲指定にしているのは、
        // < > & のエスケープを残しておくため。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// 設定を読み込む。ファイルが無い / 壊れている場合は既定値を返す。
    /// </summary>
    /// <remarks>
    /// 設定が読めないだけでアプリが起動しないのは割に合わないため、ここは既定値に倒す。
    /// </remarks>
    public static GeminiOptions Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<GeminiOptions>(File.ReadAllText(FilePath), SerializerOptions)
                  ?? new GeminiOptions()
                : new GeminiOptions();
        }
        catch (JsonException)
        {
            return new GeminiOptions();
        }
        catch (IOException)
        {
            return new GeminiOptions();
        }
    }

    /// <summary>設定を書き出す。利用者が手で編集できるようにするため整形する。</summary>
    public void Save()
    {
        GlosslayPaths.EnsureDirectory(GlosslayPaths.Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
    }
}
