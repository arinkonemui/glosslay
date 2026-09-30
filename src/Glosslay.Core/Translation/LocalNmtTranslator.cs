using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Glosslay.Translation;

/// <summary>
/// 同梱したモデルで翻訳する既定のバックエンド（FR-TRN-02 / RULES.md 🟡-1）。
/// </summary>
/// <remarks>
/// <para><b>APIキーが無くても全機能が動く</b>ための実装。クラウド翻訳は
/// 「精度を上げたい人向けの任意のアップグレード」であって、こちらが既定。
/// 2026-09-27 に Gemini が 60 秒応答せず翻訳が何も出ない事象が起きており、
/// FR-TRN-06 のフォールバック先としても要る。</para>
/// <para><b>対応は英→日のみ</b>（2026-09-27 の承認済みの例外。RULES.md 🟡-1）。
/// 中国語の原文はクラウド翻訳が要る。</para>
///
/// <para>モデルは Marian（Opus-MT 系）の seq2seq を ONNX int8 にしたもの。
/// 3 つのグラフに分かれている。</para>
/// <list type="table">
///   <item><term><c>encoder_model</c></term><description>原文 → 中間表現。1 回だけ</description></item>
///   <item><term><c>decoder_model</c></term><description>1 手目。過去の状態を持たない</description></item>
///   <item><term><c>decoder_with_past_model</c></term><description>2 手目以降。前の手の状態を受け取る</description></item>
/// </list>
/// <para><b>行はまとめて 1 回で流す。</b>1 行ずつ回すと符号器と復号の初期化を行数分繰り返す。
/// 2026-09-26 の計測では 15 行まとめて 293ms だった（PLAN.md 課題4）。</para>
///
/// <para><b>探索は貪欲（ビーム幅 1）。</b>ビーム 4 は同じ 15 行で 1019ms かかり、
/// 性能要件 1.5 秒の大半を使ってしまう。設定で選べるようにはしていない
/// — 選べると「遅い既定」を踏む人が出るため、必要になった時点で見直す。</para>
/// </remarks>
public sealed class LocalNmtTranslator : ITranslator
{
    private readonly LocalNmtOptions _options;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private InferenceSession? _decoderWithPast;
    private MarianTokenizer? _tokenizer;
    private string[] _pastInputNames = [];
    private bool _disposed;

    public LocalNmtTranslator(LocalNmtOptions? options = null) =>
        _options = options ?? LocalNmtOptions.Load();

    /// <inheritdoc />
    public string Name => $"ローカルNMT ({_options.ModelName})";

    /// <inheritdoc />
    /// <remarks>キーではなく<b>モデルが置いてあるか</b>を見る。</remarks>
    public bool IsConfigured => _options.RequiredFiles().All(File.Exists);

    /// <inheritdoc />
    /// <remarks>
    /// NMT は 1 行を 1 行に訳す。番号付きの依頼文は要らないし、理解もしない。
    /// </remarks>
    public bool TranslatesLineByLine => true;

    /// <summary>読み込み済みか。</summary>
    public bool IsReady => _encoder is not null;

    /// <summary>
    /// モデルを先に読み込む。
    /// </summary>
    /// <remarks>
    /// 146MB を読むため数百 ms かかる。<b>最初の翻訳で待たせない</b>よう、
    /// 起動時に呼んでおくこと（OCR の FR-OCR-08 と同じ考え方）。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsReady)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsReady)
            {
                await Task.Run(LoadModels, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return new TranslationResult(string.Empty, TranslationOutcome.Succeeded, stopwatch.Elapsed);
        }

        var missing = _options.RequiredFiles().Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            return TranslationResult.Failure(
                TranslationOutcome.NotConfigured,
                $"翻訳モデルが見つかりません: {Path.GetFileName(missing[0])}"
                + $"（{_options.ModelDirectory}）。モデル一式を配置してください。",
                stopwatch.Elapsed);
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var lines = request.Text.ReplaceLineEndings("\n").Split('\n');
        var translated = await Task.Run(() => TranslateLines(lines, cancellationToken), cancellationToken)
                                   .ConfigureAwait(false);

        return new TranslationResult(
            string.Join('\n', translated), TranslationOutcome.Succeeded, stopwatch.Elapsed);
    }

    /// <summary>
    /// 行ごとに訳す。<b>入力と同じ本数を返す</b>（<see cref="TranslatesLineByLine"/> の約束）。
    /// </summary>
    private string[] TranslateLines(string[] lines, CancellationToken cancellationToken)
    {
        var output = new string[lines.Length];

        // 空行はモデルに渡さない。訳す中身が無く、バッチの長さだけ伸ばす。
        var targets = new List<int>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                output[i] = string.Empty;
            }
            else
            {
                targets.Add(i);
            }
        }

        if (targets.Count == 0)
        {
            return output;
        }

        if (_options.BatchLines)
        {
            var translated = Translate([.. targets.Select(i => lines[i])], cancellationToken);
            for (var n = 0; n < targets.Count; n++)
            {
                output[targets[n]] = translated[n];
            }

            return output;
        }

        // 既定は 1 件ずつ。詰め物が無いぶん、同じ原文がいつも同じ訳になる
        // （LocalNmtOptions.BatchLines に実測と理由）。
        foreach (var index in targets)
        {
            output[index] = Translate([lines[index]], cancellationToken)[0];
        }

        return output;
    }

    /// <summary>まとめて 1 回の推論で訳す。</summary>
    private string[] Translate(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var tokenizer = _tokenizer!;
        var batch = texts.Count;

        var encoded = new int[batch][];
        for (var b = 0; b < batch; b++)
        {
            encoded[b] = tokenizer.Encode(texts[b], _options.MaxInputTokens);
        }

        var sourceLength = encoded.Max(e => e.Length);

        // 短い行は <pad> で埋め、注意マスクを 0 にして無視させる。
        var inputIds = new long[batch * sourceLength];
        var attentionMask = new long[batch * sourceLength];
        for (var b = 0; b < batch; b++)
        {
            for (var i = 0; i < sourceLength; i++)
            {
                var inside = i < encoded[b].Length;
                inputIds[(b * sourceLength) + i] = inside ? encoded[b][i] : tokenizer.PadId;
                attentionMask[(b * sourceLength) + i] = inside ? 1 : 0;
            }
        }

        var maskTensor = new DenseTensor<long>(attentionMask, [batch, sourceLength]);

        using var encoderResults = _encoder!.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids",
                new DenseTensor<long>(inputIds, [batch, sourceLength])),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
        ]);

        cancellationToken.ThrowIfCancellationRequested();

        // 復号の開始記号。Marian は <pad> を使う（generation_config の decoder_start_token_id）。
        var step = new long[batch];
        Array.Fill(step, tokenizer.PadId);

        using var first = _decoder!.Run(
        [
            NamedOnnxValue.CreateFromTensor("encoder_attention_mask", maskTensor),
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(step, [batch, 1])),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderResults[0].AsTensor<float>()),
        ]);

        // 符号器側の状態は毎手同じ。1 手目のものを使い回す（だから first を持ち続ける）。
        var fromFirst = first.ToDictionary(v => v.Name, StringComparer.Ordinal);
        var latest = fromFirst;

        var results = new List<int>[batch];
        var finished = new bool[batch];
        for (var b = 0; b < batch; b++)
        {
            results[b] = [];
        }

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? previous = null;
        try
        {
            var limit = Math.Min(
                _options.MaxOutputTokens,
                (int)(sourceLength * _options.MaxOutputRatio) + 8);

            for (var t = 0; t < limit; t++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!PickNext(latest["logits"], batch, tokenizer, results, finished, step))
                {
                    break;
                }

                var inputs = new List<NamedOnnxValue>(_pastInputNames.Length + 2)
                {
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", maskTensor),
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(step, [batch, 1])),
                };

                foreach (var name in _pastInputNames)
                {
                    var source = name.Contains(".encoder.", StringComparison.Ordinal) ? fromFirst : latest;
                    inputs.Add(NamedOnnxValue.CreateFromTensor(
                        name, source[PresentNameOf(name)].AsTensor<float>()));
                }

                var next = _decoderWithPast!.Run(inputs);
                previous?.Dispose();
                previous = next;
                latest = next.ToDictionary(v => v.Name, StringComparer.Ordinal);
            }
        }
        finally
        {
            previous?.Dispose();
        }

        return [.. results.Select(tokenizer.Decode)];
    }

    /// <summary>
    /// 各行の次の 1 語を選ぶ。まだ続きがあるなら <c>true</c>。
    /// </summary>
    /// <remarks>
    /// <para>貪欲探索。<c>&lt;pad&gt;</c> は出力してはいけない語として除く
    /// （モデルの <c>bad_words_ids</c>）。</para>
    /// <para>終わった行にも <c>&lt;pad&gt;</c> を入れて形を揃える。出力は読まない。</para>
    /// </remarks>
    private static bool PickNext(
        DisposableNamedOnnxValue logitsValue, int batch, MarianTokenizer tokenizer,
        List<int>[] results, bool[] finished, long[] step)
    {
        var logits = AsSpan(logitsValue);
        var vocabulary = tokenizer.VocabularySize;

        // 出力は [batch, 1, vocabulary]。
        var remaining = false;
        for (var b = 0; b < batch; b++)
        {
            if (finished[b])
            {
                step[b] = tokenizer.PadId;
                continue;
            }

            var row = logits.Slice(b * vocabulary, vocabulary);
            var best = -1;
            var bestScore = float.NegativeInfinity;
            for (var v = 0; v < vocabulary; v++)
            {
                if (v != tokenizer.PadId && row[v] > bestScore)
                {
                    bestScore = row[v];
                    best = v;
                }
            }

            if (best < 0 || best == tokenizer.EndOfSequenceId)
            {
                finished[b] = true;
                step[b] = tokenizer.PadId;
                continue;
            }

            results[b].Add(best);
            step[b] = best;
            remaining = true;
        }

        return remaining;
    }

    private static ReadOnlySpan<float> AsSpan(DisposableNamedOnnxValue value)
    {
        var tensor = value.AsTensor<float>();
        return tensor is DenseTensor<float> dense ? dense.Buffer.Span : tensor.ToArray();
    }

    /// <summary><c>past_key_values.0.decoder.key</c> → <c>present.0.decoder.key</c>。</summary>
    private static string PresentNameOf(string pastInputName) =>
        string.Concat("present.", pastInputName.AsSpan("past_key_values.".Length));

    private void LoadModels()
    {
        var missing = _options.RequiredFiles().FirstOrDefault(p => !File.Exists(p));
        if (missing is not null)
        {
            throw new FileNotFoundException(
                $"翻訳モデルが見つかりません: {missing}。"
                + $"モデル一式を {_options.ModelDirectory} に配置してください。",
                missing);
        }

        using var sessionOptions = new SessionOptions
        {
            // ゲームと CPU を奪い合わない（OCR の FR-OCR-11 と同じ）。
            IntraOpNumThreads = _options.CpuThreads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,

            // GPU 推論は既定で無効（RULES.md 🟠 / FR-OCR-12）。
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        var encoder = new InferenceSession(_options.ResolveEncoder(), sessionOptions);
        var decoder = new InferenceSession(_options.ResolveDecoder(), sessionOptions);
        var decoderWithPast = new InferenceSession(_options.ResolveDecoderWithPast(), sessionOptions);

        // 層の数はモデルによって変わる。名前を数え上げて拾う。
        _pastInputNames =
        [
            .. decoderWithPast.InputMetadata.Keys
                .Where(k => k.StartsWith("past_key_values.", StringComparison.Ordinal)),
        ];

        _tokenizer = MarianTokenizer.Load(
            _options.ResolveSentencePiece(), _options.ResolveVocabulary());

        _encoder = encoder;
        _decoder = decoder;
        _decoderWithPast = decoderWithPast;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _encoder?.Dispose();
        _decoder?.Dispose();
        _decoderWithPast?.Dispose();
        _initializationLock.Dispose();
    }
}
