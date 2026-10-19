using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Glosslay.Translation;

/// <summary>
/// 1 つの言語の組み合わせ（英→日・中→英など）の Marian モデル。
/// </summary>
/// <remarks>
/// <para>Marian（Opus-MT 系）の seq2seq を ONNX int8 にしたもの。3 つのグラフに分かれている。</para>
/// <list type="table">
///   <item><term><c>encoder_model</c></term><description>原文 → 中間表現。1 回だけ</description></item>
///   <item><term><c>decoder_model</c></term><description>1 手目。過去の状態を持たない</description></item>
///   <item><term><c>decoder_with_past_model</c></term><description>2 手目以降。前の手の状態を受け取る</description></item>
/// </list>
/// <para><b>復号の手順は組み合わせによらず同じ。</b>2026-10-19 に <see cref="LocalNmtTranslator"/> から切り出した
/// （中→英を足すため）。手順は切り出す前と 1 文字も変えていない。</para>
/// <para><b>同時に呼んでよい。</b>状態を持たず、ONNX Runtime のセッションは並行実行に対応している。</para>
/// </remarks>
internal sealed class MarianModel : IDisposable
{
    private readonly LocalNmtOptions _options;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _decoderWithPast;
    private readonly MarianTokenizer _tokenizer;
    private readonly string[] _pastInputNames;

    private MarianModel(
        LocalNmtOptions options,
        InferenceSession encoder,
        InferenceSession decoder,
        InferenceSession decoderWithPast,
        MarianTokenizer tokenizer)
    {
        _options = options;
        _encoder = encoder;
        _decoder = decoder;
        _decoderWithPast = decoderWithPast;
        _tokenizer = tokenizer;

        // 層の数はモデルによって変わる。名前を数え上げて拾う。
        _pastInputNames =
        [
            .. decoderWithPast.InputMetadata.Keys
                .Where(k => k.StartsWith("past_key_values.", StringComparison.Ordinal)),
        ];
    }

    /// <summary>
    /// 置き場からモデルを読み込む。ファイルが足りなければ <see cref="FileNotFoundException"/>。
    /// </summary>
    /// <param name="directory">モデル一式（ONNX 3 つ・<c>source.spm</c>・<c>vocab.json</c>）の置き場。</param>
    /// <param name="options">スレッド数や長さの上限。</param>
    public static MarianModel Load(string directory, LocalNmtOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(options);

        var missing = LocalNmtOptions.RequiredFilesIn(directory).FirstOrDefault(p => !File.Exists(p));
        if (missing is not null)
        {
            throw new FileNotFoundException(
                $"翻訳モデルが見つかりません: {missing}。"
                + $"モデル一式を {directory} に配置してください。",
                missing);
        }

        using var sessionOptions = new SessionOptions
        {
            // ゲームと CPU を奪い合わない（OCR の FR-OCR-11 と同じ）。
            // 1 件ずつ訳すときは行を CpuThreads 本並べるので、1 本あたりは 1 スレッド。
            // 合計は CpuThreads のまま変わらない（LocalNmtTranslator の注記に実測）。
            IntraOpNumThreads = options.BatchLines ? options.CpuThreads : 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,

            // GPU 推論は既定で無効（RULES.md 🟠 / FR-OCR-12）。
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        var encoder = new InferenceSession(Path.Combine(directory, "encoder_model.onnx"), sessionOptions);
        var decoder = new InferenceSession(Path.Combine(directory, "decoder_model.onnx"), sessionOptions);
        var decoderWithPast = new InferenceSession(
            Path.Combine(directory, "decoder_with_past_model.onnx"), sessionOptions);
        var tokenizer = MarianTokenizer.Load(
            Path.Combine(directory, "source.spm"), Path.Combine(directory, "vocab.json"));

        return new MarianModel(options, encoder, decoder, decoderWithPast, tokenizer);
    }

    /// <summary>まとめて 1 回の推論で訳す。</summary>
    public string[] Translate(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var tokenizer = _tokenizer;
        var batch = texts.Count;

        var encoded = new int[batch][];
        for (var b = 0; b < batch; b++)
        {
            encoded[b] = tokenizer.Encode(texts[b], _options.MaxInputTokens);
        }

        var sourceLength = encoded.Max(e => e.Length);

        // 中身の無い入力をモデルに渡すと、ONNX Runtime が形の不整合で例外を投げる
        // （2026-10-19、中→英→日で 1 段目の結果が空になった行で実際に起きた）。
        // 1 件でも中身があれば、短い行は詰め物で揃うので問題ない。
        if (sourceLength == 0)
        {
            return [.. texts.Select(_ => string.Empty)];
        }

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

        using var encoderResults = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids",
                new DenseTensor<long>(inputIds, [batch, sourceLength])),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
        ]);

        cancellationToken.ThrowIfCancellationRequested();

        // 復号の開始記号。Marian は <pad> を使う（generation_config の decoder_start_token_id）。
        var step = new long[batch];
        Array.Fill(step, tokenizer.PadId);

        using var first = _decoder.Run(
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

                var next = _decoderWithPast.Run(inputs);
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

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _decoderWithPast.Dispose();
    }
}
