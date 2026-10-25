using System.Collections.Concurrent;
using System.Diagnostics;

namespace Glosslay.Translation;

/// <summary>
/// 同梱したモデルで翻訳する既定のバックエンド（FR-TRN-02 / RULES.md 🟡-1）。
/// </summary>
/// <remarks>
/// <para><b>RULES.md 🟡-1 の表で「ローカル」の組み合わせは、APIキーが無くても訳せる</b>ための実装。
/// クラウド翻訳は「精度を上げたい人向けの任意のアップグレード」であって、こちらが既定。
/// 2026-09-27 に Gemini が 60 秒応答せず翻訳が何も出ない事象が起きており、
/// FR-TRN-06 のフォールバック先としても要る。</para>
/// <para><b>訳せる組み合わせ</b>（経路は <see cref="LocalNmtRoutes"/>、表は <see cref="Languages.Availability"/>）。</para>
/// <list type="table">
///   <item><term>英→日</term><description><c>elan-mt-bt-en-ja</c></description></item>
///   <item><term>中→英 / 中→英→日</term><description><c>opus-mt-zh-en</c>（2026-10-19 追加）。<b>句点で区切って 1 文ずつ訳す</b>。
///     中→日は直接訳せるモデルで使えるものが無いため 2 段</description></item>
///   <item><term>日→英</term><description><c>elan-mt-bt-ja-en</c>（2026-10-25 追加）</description></item>
///   <item><term>英→中</term><description><c>opus-mt-en-zh</c>（2026-10-25 追加）。原文の頭に簡体字の印を付ける</description></item>
///   <item><term>日→英→中</term><description>2 段（2026-10-25 追加）。<b>精度が大きく下がる</b>
///     （<see cref="PairAvailability.LocalLowQuality"/>）</description></item>
/// </list>
/// <para><b>韓国語が入る組み合わせは訳さない</b>（クラウドのみ — 2026-10-21 人間の判断）。
/// 理由を <see cref="TranslationOutcome.Unsupported"/> で返す。</para>
/// <para><b>モデルは要ったときに読む</b>（使わない組み合わせの分だけメモリを増やさない）。
/// 最初の翻訳で待たせないよう、いまの組み合わせのモデルは <see cref="PrepareAsync"/> で先に読んでおく。</para>
/// <para><b>行は 1 件ずつ、並べて訳す。</b>まとめて 1 回で流すと訳文が同じ画面の他の行に
/// 左右される（<see cref="LocalNmtOptions.BatchLines"/>）。1 件ずつなら行どうしが影響しないため、
/// 並べても<b>訳文は変わらない</b>（全画面 15 枚でバイト単位の一致を確認・2026-10-13）。</para>
/// <para><b>並べる数と推論のスレッド数を入れ替えてある。</b>復号は 1 語ずつの小さな計算で、
/// 1 行に 4 スレッドを当てても使い切れない。<b>同じ 4 スレッドなら 4 行を 1 スレッドずつ</b>の方が速い。
/// 全画面 15 枚の平均で 532ms → 283ms（2026-10-13）。CPU の上限（FR-OCR-11）は変わらない。</para>
/// <para><b>探索は貪欲（ビーム幅 1）。</b>ビーム 4 は同じ 15 行で 1019ms かかり、
/// 性能要件 1.5 秒の大半を使ってしまう。設定で選べるようにはしていない
/// — 選べると「遅い既定」を踏む人が出るため、必要になった時点で見直す。</para>
/// </remarks>
public sealed class LocalNmtTranslator : ITranslator
{
    private readonly LocalNmtOptions _options;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<LocalNmtModel, MarianModel> _models = new();
    private bool _disposed;

    public LocalNmtTranslator(LocalNmtOptions? options = null) =>
        _options = options ?? LocalNmtOptions.Load();

    /// <inheritdoc />
    /// <remarks>
    /// 組み合わせによってモデルが違うため、モデルの名前は入れない。
    /// <b>名前は訳文の使い回し（FR-TRN-09）の鍵にも使う</b>ので、組み合わせで変えない。
    /// </remarks>
    public string Name => "ローカルNMT";

    /// <inheritdoc />
    /// <remarks>
    /// <b>どれか 1 つでもモデルが置いてあるか</b>を見る。いまの組み合わせで訳せるかは
    /// <see cref="ReadinessFor"/> で見ること（モデルは組み合わせごとに取得する — FR-TRN-03）。
    /// </remarks>
    public bool IsConfigured => Enum.GetValues<LocalNmtModel>().Any(HasModel);

    /// <inheritdoc />
    /// <remarks>
    /// NMT は 1 行を 1 行に訳す。番号付きの依頼文は要らないし、理解もしない。
    /// </remarks>
    public bool TranslatesLineByLine => true;

    /// <summary>モデルが置いてあるか。</summary>
    public bool HasModel(LocalNmtModel model) => _options.RequiredFilesOf(model).All(File.Exists);

    /// <summary>
    /// いまの組み合わせを、ローカルで訳せる状態か（<see cref="TranslatorSelection"/> に渡す）。
    /// </summary>
    public LocalReadiness ReadinessFor(string sourceLanguage, string mainLanguage)
    {
        var route = LocalNmtRoutes.RouteFor(sourceLanguage, mainLanguage);
        if (route == LocalNmtRoute.None)
        {
            return LocalReadiness.CloudOnlyPair;
        }

        var (first, second) = LocalNmtRoutes.ModelsOf(route);
        return HasModel(first) && (second is not { } next || HasModel(next))
            ? LocalReadiness.Ready
            : LocalReadiness.ModelMissing;
    }

    /// <summary>
    /// 英→日のモデルを先に読み込む。
    /// </summary>
    /// <remarks>組み合わせを選ぶ前の形。画面からは <see cref="PrepareAsync"/> を使う。</remarks>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        EnsureModelAsync(LocalNmtModel.EnglishToJapanese, cancellationToken);

    /// <summary>
    /// 組み合わせに要るモデルを先に読み込む。置かれていないモデルは読まない。
    /// </summary>
    /// <remarks>
    /// 1 つ 146〜228MB あり、数百 ms かかる。<b>最初の翻訳で待たせない</b>よう、
    /// 起動時と組み合わせを変えたときに呼んでおくこと（OCR の FR-OCR-08 と同じ考え方）。
    /// </remarks>
    public async Task PrepareAsync(
        string sourceLanguage, string mainLanguage, CancellationToken cancellationToken = default)
    {
        var route = LocalNmtRoutes.RouteFor(sourceLanguage, mainLanguage);
        if (route == LocalNmtRoute.None)
        {
            return;
        }

        var (first, second) = LocalNmtRoutes.ModelsOf(route);
        foreach (var model in second is { } next ? new[] { first, next } : [first])
        {
            if (HasModel(model))
            {
                await EnsureModelAsync(model, cancellationToken).ConfigureAwait(false);
            }
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

        if (request.SourceLanguage is not { Length: > 0 } source)
        {
            return TranslationResult.Failure(
                TranslationOutcome.Unsupported,
                "原文の言語が指定されていません。設定で原文の言語を選んでください。",
                stopwatch.Elapsed);
        }

        var main = request.TargetLanguage;

        // 組み合わせはモデルの有無より先に見る。モデルを置いても、この組み合わせは訳せない。
        // 見ずに訳すと、韓国語を選んでいるのに日本語が出て、しかも「成功」として返ってしまう。
        switch (Languages.Availability(source, main))
        {
            case PairAvailability.Same:
                return TranslationResult.Failure(
                    TranslationOutcome.Unsupported,
                    $"原文の言語とメイン言語が同じ（{Languages.NameOf(main)}）ため、訳しません。",
                    stopwatch.Elapsed);

            case PairAvailability.CloudOnly:
                return TranslationResult.Failure(
                    TranslationOutcome.Unsupported, CloudOnlyMessage(source, main), stopwatch.Elapsed);
        }

        var route = LocalNmtRoutes.RouteFor(source, main);
        var (first, second) = LocalNmtRoutes.ModelsOf(route);
        foreach (var model in second is { } next ? new[] { first, next } : [first])
        {
            if (_options.RequiredFilesOf(model).FirstOrDefault(p => !File.Exists(p)) is { } missing)
            {
                return TranslationResult.Failure(
                    TranslationOutcome.NotConfigured,
                    $"{Languages.PairName(source, main)}の翻訳モデルが見つかりません: {Path.GetFileName(missing)}"
                    + $"（{_options.DirectoryOf(model)}）。モデルを入れるか、クラウド翻訳のAPIキーを登録してください。",
                    stopwatch.Elapsed);
            }
        }

        var lines = request.Text.ReplaceLineEndings("\n").Split('\n');
        var routes = LocalNmtRoutes.Plan(lines, source, main);

        // 訳せる行が 1 つも無いなら、モデルを起こさずに理由を返す。
        if (routes.All(r => r == LocalNmtRoute.None))
        {
            return TranslationResult.Failure(
                TranslationOutcome.Unsupported,
                $"{Languages.NameOf(source)}の行がありませんでした。"
                + $"原文の言語（{Languages.NameOf(source)}）がゲームの言語と合っているか確かめてください。",
                stopwatch.Elapsed);
        }

        await EnsureModelAsync(first, cancellationToken).ConfigureAwait(false);
        if (second is { } secondModel)
        {
            await EnsureModelAsync(secondModel, cancellationToken).ConfigureAwait(false);
        }

        var translated = await Task
            .Run(() => TranslateLines(lines, routes, first, second, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        return new TranslationResult(
            string.Join('\n', translated), TranslationOutcome.Succeeded, stopwatch.Elapsed);
    }

    /// <summary>
    /// 「クラウドのみ」の組み合わせを断る理由。何をすれば訳せるかまで言う。
    /// </summary>
    public static string CloudOnlyMessage(string sourceLanguage, string mainLanguage) =>
        $"{Languages.PairName(sourceLanguage, mainLanguage)}はクラウド翻訳でのみ訳せます（同梱の翻訳モデルは対応していません）。"
        + "APIキーを登録し、使うバックエンドを「自動」か「クラウドのみ」にしてください。";

    /// <summary>モデルを、初めて要ったときに読む。</summary>
    private async Task EnsureModelAsync(LocalNmtModel model, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_models.ContainsKey(model))
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_models.ContainsKey(model))
            {
                var loaded = await Task
                    .Run(
                        () => MarianModel.Load(_options.DirectoryOf(model), _options, _options.SourcePrefixTokenOf(model)),
                        cancellationToken)
                    .ConfigureAwait(false);
                _models[model] = loaded;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// 行ごとに訳す。<b>入力と同じ本数を返す</b>（<see cref="TranslatesLineByLine"/> の約束）。
    /// 訳さない行は原文のまま返す。呼び出し側は「原文と同じ」として重ねない
    /// （LineTranslation の Unchanged）。
    /// </summary>
    /// <remarks>
    /// <para>1 段目: 行（文を落とすモデルでは文で区切ったもの — <see cref="LocalNmtRoutes.SentencesFor"/>）を並べて訳す。
    /// 長い段落の行に仕事が偏るため、行ではなく文の単位で並べる（2026-10-19）。</para>
    /// <para>2 段目（中→英→日・日→英→中）: 1 段目で揃った英語を、同じように並べて訳す。</para>
    /// </remarks>
    private string[] TranslateLines(
        string[] lines, LocalNmtRoute[] routes, LocalNmtModel first, LocalNmtModel? second,
        CancellationToken cancellationToken)
    {
        var output = new string[lines.Length];
        var targets = new List<int>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (routes[i] == LocalNmtRoute.None)
            {
                // 空行・訳さない行。モデルに違う言語を入れると流暢な出鱈目が出るため、
                // 経路の無い行は決してモデルに渡さない。
                output[i] = string.IsNullOrWhiteSpace(lines[i]) ? string.Empty : lines[i];
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

        var firstOutput = TranslateStage(first, [.. targets.Select(i => (i, lines[i]))], cancellationToken);

        // 1 段目の結果が空なら原文のまま返す（2 段目へ空を渡すとモデルが例外を投げる。
        // 2026-10-19、鬼谷八荒の会話の画面で実際に起きた）。
        var secondStage = new List<(int Line, string Text)>();
        foreach (var index in targets)
        {
            var text = firstOutput[index];
            if (string.IsNullOrWhiteSpace(text))
            {
                output[index] = lines[index];
            }
            else if (second is null)
            {
                output[index] = text;
            }
            else
            {
                secondStage.Add((index, text));
            }
        }

        if (second is { } secondModel && secondStage.Count > 0)
        {
            var secondOutput = TranslateStage(secondModel, secondStage, cancellationToken);
            foreach (var (index, english) in secondStage)
            {
                // 2 段目が空になったら、1 段目の英語より原文を出す（訳さない行と同じ扱い）。
                output[index] = string.IsNullOrWhiteSpace(secondOutput[index]) ? lines[index] : secondOutput[index];
            }
        }

        return output;
    }

    /// <summary>
    /// 1 つのモデルで、行ごとの文を訳して行ごとに組み直す。
    /// </summary>
    /// <remarks>
    /// 文を落とすモデルでは文で区切って 1 文ずつ訳し（<see cref="LocalNmtRoutes.SentencesFor"/>）、空白で繋ぐ。
    /// 行ではなく文の単位で並べるので、長い段落の行に仕事が偏らない。
    /// </remarks>
    /// <returns>行の番号 → 訳文（空の文は除いて繋いだもの。全部空なら空文字）。</returns>
    private Dictionary<int, string> TranslateStage(
        LocalNmtModel model, IReadOnlyList<(int Line, string Text)> items, CancellationToken cancellationToken)
    {
        var units = items
            .SelectMany(item => LocalNmtRoutes.SentencesFor(model, item.Text).Select(text => (item.Line, Text: text)))
            .ToList();

        var translated = Run(_models[model], [.. units.Select(u => u.Text)], cancellationToken);

        return items.ToDictionary(
            item => item.Line,
            item => string.Join(
                ' ',
                units.Select((u, k) => (u.Line, Text: translated[k].Trim()))
                     .Where(u => u.Line == item.Line && u.Text.Length > 0)
                     .Select(u => u.Text)));
    }

    /// <summary>
    /// 1 つのモデルで訳す。既定は 1 件ずつ並べて、<see cref="LocalNmtOptions.BatchLines"/> ならまとめて 1 回で。
    /// </summary>
    /// <remarks>
    /// 1 件ずつなら詰め物が無いぶん、同じ原文がいつも同じ訳になる
    /// （<see cref="LocalNmtOptions.BatchLines"/> に実測と理由）。行どうしが影響しないので、並べても訳文は変わらない。
    /// </remarks>
    private string[] Run(MarianModel model, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (_options.BatchLines)
        {
            return model.Translate(texts, cancellationToken);
        }

        var output = new string[texts.Count];
        RunInParallel(texts.Count, n => output[n] = model.Translate([texts[n]], cancellationToken)[0], cancellationToken);
        return output;
    }

    /// <summary>
    /// <paramref name="count"/> 件の仕事を、CPU の上限（<see cref="LocalNmtOptions.CpuThreads"/>）まで並べて行う。
    /// </summary>
    private void RunInParallel(int count, Action<int> work, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return;
        }

        try
        {
            Parallel.For(
                0,
                count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.CpuThreads,
                    CancellationToken = cancellationToken,
                },
                work);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // 行の中で止まった場合も、呼び出し側には普通のキャンセルとして返す。
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var model in _models.Values)
        {
            model.Dispose();
        }

        _models.Clear();
        _loadLock.Dispose();
    }
}
