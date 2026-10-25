using Glosslay.Configuration;

namespace Glosslay.Translation;

/// <summary>
/// <c>LocalNmtTranslator</c> の設定。
/// </summary>
/// <remarks>
/// <para><c>%APPDATA%\Glosslay\localnmt.json</c> を読む。無ければ既定値で動く
/// （RULES.md 🔵「設定は人が読める JSON で保存」）。</para>
/// <para><b>モデル名をコードに埋めない</b>のは Gemini 側と同じ理由（FR-TRN-07 / RULES.md 🟡-3）。
/// ローカルNMT は言語ペアごとにモデルを差し替えるため、置き場所も設定で持つ。</para>
/// </remarks>
public sealed record LocalNmtOptions
{
    /// <summary>設定ファイル名。</summary>
    public const string FileName = "localnmt.json";

    /// <summary>
    /// モデル一式の置き場所。
    /// </summary>
    /// <remarks>
    /// <para>OCR と同じく実行ファイルの隣（<c>models\</c>）を既定にする。
    /// v1.0 ではオンデマンド DL（FR-TRN-03）でここへ置く。</para>
    /// <para><b>モデルは実行ファイルに埋め込まない。</b>CC BY-SA 4.0 の「並べて配る」立場を
    /// 保つため（PLAN.md の配布方針 / <c>tools/nmt-convert/README.md</c>）。</para>
    /// <para>この項目は<b>英→日</b>のモデル。名前は 1 組だけだった頃のまま（設定ファイルの互換のため）。</para>
    /// </remarks>
    public string ModelDirectory { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "models", "nmt", "en-ja");

    /// <summary>
    /// 日→英のモデル一式の置き場所（2026-10-25 追加）。
    /// </summary>
    /// <remarks>
    /// <para>日→英と、日→英→中の 1 段目に使う。<c>Mitsua/elan-mt-bt-ja-en</c>（<b>CC BY-SA 4.0</b>。
    /// 英→日と同じく帰属表示と同ライセンスでの公開が要る — <c>tools/nmt-convert/README.md</c>）。</para>
    /// <para><b>無くても動く。</b>置かれていなければ日本語の原文はローカルでは訳さず、理由を返す。</para>
    /// </remarks>
    public string JapaneseToEnglishModelDirectory { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "models", "nmt", "ja-en");

    /// <summary>
    /// 英→中のモデル一式の置き場所（2026-10-25 追加）。
    /// </summary>
    /// <remarks>
    /// <para>英→中と、日→英→中の 2 段目に使う。<c>Helsinki-NLP/opus-mt-en-zh</c>（Apache 2.0）。</para>
    /// <para><b>無くても動く。</b>置かれていなければ中国語へはローカルでは訳さず、理由を返す。</para>
    /// </remarks>
    public string EnglishToChineseModelDirectory { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "models", "nmt", "en-zh");

    /// <summary>
    /// 英→中のモデルに、どの書き方で出させるかの印。<b>原文の頭に付ける。</b>
    /// </summary>
    /// <remarks>
    /// <para>このモデルは中国語の複数の書き方（簡体字・繁体字・広東語など）を出せるため、
    /// 原文の頭に <c>&gt;&gt;id&lt;&lt;</c> の印が要る（モデルの説明書き）。<b>付けないと書き方が定まらない。</b></para>
    /// <para>既定は簡体字（<c>&gt;&gt;cmn_Hans&lt;&lt;</c>）。中国語は簡体字だけ（2026-10-24 人間の判断）。
    /// 繁体字を足すときは <c>&gt;&gt;cmn_Hant&lt;&lt;</c> にすればよく、モデルの作り直しは要らない。</para>
    /// </remarks>
    public string EnglishToChineseTargetToken { get; init; } = ">>cmn_Hans<<";

    /// <summary>
    /// 中→英のモデル一式の置き場所（2026-10-19 追加）。
    /// </summary>
    /// <remarks>
    /// <para><b>中国語の原文を訳すのに使う</b>（訳先が英語なら中→英、日本語なら中→英→日の 1 段目）。
    /// 中→日を直接訳せるモデルで使えるものが無いため（PLAN.md「中→日の手段を探し直した」）。</para>
    /// <para><b>無くても動く。</b>置かれていなければ中国語の行は訳さず、理由を返す。
    /// 中国語のゲームを遊ばない人は持たなくてよい（FR-TRN-03 の組み合わせ単位の取得）。</para>
    /// <para><c>Helsinki-NLP/opus-mt-zh-en</c>（CC BY 4.0）。帰属表示が要る（<c>tools/nmt-convert/README.md</c>）。</para>
    /// </remarks>
    public string ChineseModelDirectory { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "models", "nmt", "zh-en");

    /// <summary>
    /// 推論スレッド数。
    /// </summary>
    /// <remarks>
    /// <para>OCR（FR-OCR-11）と同じ考え方で、ゲームと CPU を奪い合わないよう抑える。</para>
    /// <para><b>1 件ずつ訳すとき（既定）は、並べる行の数として使う。</b>
    /// 1 行あたりの推論は 1 スレッドで、合計がこの値になる（<see cref="LocalNmtTranslator"/>）。</para>
    /// </remarks>
    public int CpuThreads { get; init; } = Math.Max(2, Environment.ProcessorCount / 4);

    /// <summary>
    /// 1 行あたりの入力トークン数の上限。
    /// </summary>
    /// <remarks>
    /// 超えた分は切り捨てる。OCR が拾った長すぎる塊で時間を使い切らないための歯止め。
    /// </remarks>
    public int MaxInputTokens { get; init; } = 256;

    /// <summary>
    /// 生成するトークン数の上限。
    /// </summary>
    /// <remarks>
    /// <b>終端記号が出ないまま延々と生成する事故を止める。</b>
    /// NMT は稀に同じ語を繰り返して止まらなくなる。ゲーム中に固まるのが最悪なので必ず切る。
    /// </remarks>
    public int MaxOutputTokens { get; init; } = 256;

    /// <summary>
    /// 行をまとめて 1 回の推論で流すか。<b>既定は false（1 件ずつ）。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>まとめると、訳文が同じ画面にある他の行に左右される。</b>
    /// 短い行はバッチの最長行に合わせて詰め物で埋められ、その分だけ数値がわずかに動く。
    /// 貪欲探索は僅差の語を選び直すため、結果が変わる。
    /// <b>これは実装の誤りではなく、Python 側でも同じように変化する</b>（2026-10-01 に確認）。</para>
    /// <para>速度の利点は、実際の画面では<b>ほぼ無い</b>。2026-10-01 の実測。</para>
    /// <list type="table">
    ///   <item><term>カード 1 画面 11 件（長短が混ざる）</term>
    ///     <description>まとめて 493ms / 1 件ずつ 510ms — <b>差 3%</b></description></item>
    ///   <item><term>長文 3 件（長さが揃う）</term>
    ///     <description>まとめて 222ms / 1 件ずつ 331ms</description></item>
    /// </list>
    /// <para>まとめた場合は<b>全行が終わるまで回る</b>ため、長さが混ざると
    /// 終わった行の分まで計算し続けて利点が消える。
    /// 3% と引き換えに「同じ原文がいつも同じ訳になる」ことを選ぶ
    /// （PLAN.md 問題 D の訳の揺れ / FR-TRN-09 のキャッシュと整合する）。</para>
    /// <para><b>1 件ずつなら Python の実装と訳文が完全に一致する。</b>
    /// 回帰を機械的に確かめられる利点もある（<c>tools/nmt-eval/README.md</c>）。</para>
    /// </remarks>
    public bool BatchLines { get; init; }

    /// <summary>
    /// 訳文の長さの上限（原文のトークン数に対する倍率）。
    /// </summary>
    /// <remarks>
    /// <see cref="MaxOutputTokens"/> とは別に、<b>短い原文に対する暴走</b>を止める。
    /// <c>Attack Power</c> の訳に 256 トークン使わせない。
    /// </remarks>
    public double MaxOutputRatio { get; init; } = 3.0;

    /// <summary>設定を読み込む。ファイルが無い / 壊れている場合は既定値。</summary>
    public static LocalNmtOptions Load() => JsonSettings.Load(FileName, () => new LocalNmtOptions());

    /// <summary>設定を書き出す。</summary>
    public void Save() => JsonSettings.Save(FileName, this);

    /// <summary>符号器。</summary>
    public string ResolveEncoder() => Path.Combine(ModelDirectory, "encoder_model.onnx");

    /// <summary>復号器（1 手目。過去の状態を持たない）。</summary>
    public string ResolveDecoder() => Path.Combine(ModelDirectory, "decoder_model.onnx");

    /// <summary>復号器（2 手目以降。過去の状態を受け取る）。</summary>
    public string ResolveDecoderWithPast() =>
        Path.Combine(ModelDirectory, "decoder_with_past_model.onnx");

    /// <summary>分割用の SentencePiece モデル（原文側）。</summary>
    public string ResolveSentencePiece() => Path.Combine(ModelDirectory, "source.spm");

    /// <summary>id の対応表。<b>分割は .spm、id は vocab.json</b>（<c>tools/nmt-convert/README.md</c>）。</summary>
    public string ResolveVocabulary() => Path.Combine(ModelDirectory, "vocab.json");

    /// <summary>英→日に必要なファイルの一覧。存在確認とエラーメッセージに使う。</summary>
    public IEnumerable<string> RequiredFiles() => RequiredFilesIn(ModelDirectory);

    /// <summary>中→英に必要なファイルの一覧。</summary>
    public IEnumerable<string> ChineseRequiredFiles() => RequiredFilesIn(ChineseModelDirectory);

    /// <summary>モデルの置き場所。</summary>
    public string DirectoryOf(LocalNmtModel model) => model switch
    {
        LocalNmtModel.EnglishToJapanese => ModelDirectory,
        LocalNmtModel.ChineseToEnglish => ChineseModelDirectory,
        LocalNmtModel.JapaneseToEnglish => JapaneseToEnglishModelDirectory,
        LocalNmtModel.EnglishToChinese => EnglishToChineseModelDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, null),
    };

    /// <summary>
    /// 原文の頭に付ける印（無ければ <c>null</c>）。英→中だけが要る（<see cref="EnglishToChineseTargetToken"/>）。
    /// </summary>
    public string? SourcePrefixTokenOf(LocalNmtModel model) =>
        model == LocalNmtModel.EnglishToChinese ? EnglishToChineseTargetToken : null;

    /// <summary>モデルに必要なファイルの一覧。</summary>
    public IEnumerable<string> RequiredFilesOf(LocalNmtModel model) => RequiredFilesIn(DirectoryOf(model));

    /// <summary>モデル一式に必要なファイル。組み合わせによらず同じ形。</summary>
    public static IEnumerable<string> RequiredFilesIn(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        yield return Path.Combine(directory, "encoder_model.onnx");
        yield return Path.Combine(directory, "decoder_model.onnx");
        yield return Path.Combine(directory, "decoder_with_past_model.onnx");
        yield return Path.Combine(directory, "source.spm");
        yield return Path.Combine(directory, "vocab.json");
    }
}
