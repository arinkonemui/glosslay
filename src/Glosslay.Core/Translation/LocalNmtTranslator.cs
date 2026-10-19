using System.Diagnostics;

namespace Glosslay.Translation;

/// <summary>
/// 同梱したモデルで翻訳する既定のバックエンド（FR-TRN-02 / RULES.md 🟡-1）。
/// </summary>
/// <remarks>
/// <para><b>APIキーが無くても全機能が動く</b>ための実装。クラウド翻訳は
/// 「精度を上げたい人向けの任意のアップグレード」であって、こちらが既定。
/// 2026-09-27 に Gemini が 60 秒応答せず翻訳が何も出ない事象が起きており、
/// FR-TRN-06 のフォールバック先としても要る。</para>
/// <para><b>訳せる組み合わせ</b>（経路は <see cref="LocalNmtRoutes"/>）。</para>
/// <list type="table">
///   <item><term>英→日</term><description>既定のモデル（<c>elan-mt-bt-en-ja</c>）。起動時に読む</description></item>
///   <item><term>中→英</term><description>2026-10-19 追加（<c>opus-mt-zh-en</c>）。<b>初めて中国語を訳すときに読む</b>
///     （中国語を訳さない人のメモリを増やさない）</description></item>
///   <item><term>中→日</term><description>中→英→日の 2 段。直接訳せるモデルで使えるものが無いため。
///     時間は英→日のおよそ 2.3 倍（PLAN.md「中→日の手段を探し直した」）</description></item>
/// </list>
/// <para><b>行は 1 件ずつ、並べて訳す。</b>まとめて 1 回で流すと訳文が同じ画面の他の行に
/// 左右される（<see cref="LocalNmtOptions.BatchLines"/>）。1 件ずつなら行どうしが影響しないため、
/// 並べても<b>訳文は変わらない</b>（全画面 15 枚でバイト単位の一致を確認・2026-10-13）。</para>
/// <para><b>並べる数と推論のスレッド数を入れ替えてある。</b>復号は 1 語ずつの小さな計算で、
/// 1 行に 4 スレッドを当てても使い切れない。<b>同じ 4 スレッドなら 4 行を 1 スレッドずつ</b>の方が速い。
/// 全画面 15 枚の平均で 532ms → 283ms（2026-10-13）。CPU の上限（FR-OCR-11）は変わらない。</para>
///
/// <para><b>探索は貪欲（ビーム幅 1）。</b>ビーム 4 は同じ 15 行で 1019ms かかり、
/// 性能要件 1.5 秒の大半を使ってしまう。設定で選べるようにはしていない
/// — 選べると「遅い既定」を踏む人が出るため、必要になった時点で見直す。</para>
/// </remarks>
public sealed class LocalNmtTranslator : ITranslator
{
    private readonly LocalNmtOptions _options;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _chineseLock = new(1, 1);

    private MarianModel? _englishToJapanese;
    private MarianModel? _chineseToEnglish;
    private bool _disposed;

    public LocalNmtTranslator(LocalNmtOptions? options = null) =>
        _options = options ?? LocalNmtOptions.Load();

    /// <inheritdoc />
    public string Name => $"ローカルNMT ({_options.ModelName})";

    /// <inheritdoc />
    /// <remarks>
    /// キーではなく<b>既定のモデル（英→日）が置いてあるか</b>を見る。
    /// 中→英は無くても動く（中国語の行を訳さないだけ）ため、ここでは問わない。
    /// </remarks>
    public bool IsConfigured => _options.RequiredFiles().All(File.Exists);

    /// <summary>中→英のモデルが置いてあるか。</summary>
    public bool HasChineseModel => _options.ChineseRequiredFiles().All(File.Exists);

    /// <inheritdoc />
    /// <remarks>
    /// NMT は 1 行を 1 行に訳す。番号付きの依頼文は要らないし、理解もしない。
    /// </remarks>
    public bool TranslatesLineByLine => true;

    /// <summary>既定のモデル（英→日）を読み込み済みか。</summary>
    public bool IsReady => _englishToJapanese is not null;

    /// <summary>
    /// 既定のモデル（英→日）を先に読み込む。
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
                _englishToJapanese = await Task
                    .Run(() => MarianModel.Load(_options.ModelDirectory, _options), cancellationToken)
                    .ConfigureAwait(false);
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

        // 同梱のモデルで訳せる訳先は日本語と英語だけ（FR-TRN-03）。指定された言語を見ずに訳すと、
        // 韓国語を選んでいるのに日本語が出て、しかも「成功」として返ってしまう。
        // モデルの有無より先に見る。モデルを置いても、この言語は訳せない。
        if (!TargetLanguages.LocalSupports(request.TargetLanguage))
        {
            return TranslationResult.Failure(
                TranslationOutcome.Unsupported,
                $"{TargetLanguages.DisplayNameOf(request.TargetLanguage)}へ訳すには"
                + "クラウド翻訳のAPIキーが必要です（同梱の翻訳モデルは日本語と英語へのみ対応しています）。",
                stopwatch.Elapsed);
        }

        var lines = request.Text.ReplaceLineEndings("\n").Split('\n');
        var routes = LocalNmtRoutes.Plan(lines, request.TargetLanguage);

        // 中→英のモデルが無ければ、中国語の行は訳さない（原文のまま返す）。
        var chineseWanted = routes.Any(IsChinese);
        if (chineseWanted && !HasChineseModel)
        {
            for (var i = 0; i < routes.Length; i++)
            {
                if (IsChinese(routes[i]))
                {
                    routes[i] = LocalNmtRoute.None;
                }
            }
        }

        // 訳せる行が 1 つも無いなら、モデルを起こさずに理由を返す。
        if (routes.All(r => r == LocalNmtRoute.None))
        {
            return TranslationResult.Failure(
                TranslationOutcome.Unsupported,
                WhyNothingToTranslate(request.TargetLanguage, chineseWanted),
                stopwatch.Elapsed);
        }

        // 英→日のモデルは、英→日と中→英→日の 2 段目で要る。
        if (routes.Any(r => r is LocalNmtRoute.EnglishToJapanese or LocalNmtRoute.ChineseToJapanese))
        {
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
        }

        if (routes.Any(IsChinese))
        {
            await EnsureChineseModelAsync(cancellationToken).ConfigureAwait(false);
        }

        var translated = await Task.Run(() => TranslateLines(lines, routes, cancellationToken), cancellationToken)
                                   .ConfigureAwait(false);

        return new TranslationResult(
            string.Join('\n', translated), TranslationOutcome.Succeeded, stopwatch.Elapsed);
    }

    /// <summary>訳せる行が無かったときの理由。何をすれば訳せるかまで言う。</summary>
    private static string WhyNothingToTranslate(string targetLanguage, bool chineseWithoutModel)
    {
        if (chineseWithoutModel)
        {
            return "中国語を訳す翻訳モデル（中→英）が入っていません。"
                   + "モデルを入れるか、クラウド翻訳のAPIキーを登録してください。";
        }

        return string.Equals(targetLanguage, "en", StringComparison.OrdinalIgnoreCase)
            ? "同梱の翻訳モデルで英語へ訳せるのは中国語の原文だけです。"
              + "日本語などを英語へ訳すにはクラウド翻訳のAPIキーが必要です。"
            : "同梱の翻訳モデルで日本語へ訳せるのは英語と中国語の原文だけです。"
              + "そのほかの言語を訳すにはクラウド翻訳のAPIキーが必要です。";
    }

    private static bool IsChinese(LocalNmtRoute route) =>
        route is LocalNmtRoute.ChineseToEnglish or LocalNmtRoute.ChineseToJapanese;

    /// <summary>中→英のモデルを、初めて要ったときに読む。</summary>
    private async Task EnsureChineseModelAsync(CancellationToken cancellationToken)
    {
        if (_chineseToEnglish is not null)
        {
            return;
        }

        await _chineseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _chineseToEnglish ??= await Task
                .Run(() => MarianModel.Load(_options.ChineseModelDirectory, _options), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _chineseLock.Release();
        }
    }

    /// <summary>
    /// 行ごとに訳す。<b>入力と同じ本数を返す</b>（<see cref="TranslatesLineByLine"/> の約束）。
    /// 訳さない行は原文のまま返す。呼び出し側は「原文と同じ」として重ねない
    /// （LineTranslation の Unchanged）。
    /// </summary>
    private string[] TranslateLines(string[] lines, LocalNmtRoute[] routes, CancellationToken cancellationToken)
    {
        var output = new string[lines.Length];
        var targets = new List<int>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (routes[i] == LocalNmtRoute.None)
            {
                // 空行・訳さない行。英→日のモデルに中国語・日本語を入れると流暢な出鱈目が出るため、
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

        if (_options.BatchLines)
        {
            // まとめて流すのは英→日の行だけ（中国語は文に区切って 1 文ずつ訳すため、まとめない）。
            var english = targets.Where(i => routes[i] == LocalNmtRoute.EnglishToJapanese).ToList();
            if (english.Count > 0)
            {
                var batch = _englishToJapanese!.Translate([.. english.Select(i => lines[i])], cancellationToken);
                for (var n = 0; n < english.Count; n++)
                {
                    output[english[n]] = batch[n];
                }
            }

            foreach (var index in targets.Where(i => routes[i] != LocalNmtRoute.EnglishToJapanese))
            {
                output[index] = TranslateOne(lines[index], routes[index], cancellationToken);
            }

            return output;
        }

        // 既定は 1 件ずつ。詰め物が無いぶん、同じ原文がいつも同じ訳になる
        // （LocalNmtOptions.BatchLines に実測と理由）。
        // 行どうし・文どうしが影響しないので、並べても訳文は変わらない。
        //
        // 1 巡目: 英→日の行と、中国語を区切った「文」を同じ列に並べて訳す。
        // 中国語は長い段落の行に仕事が偏るため、行ではなく文の単位で並べる（2026-10-19）。
        // 2 巡目: 中→英→日の 2 段目（1 巡目で揃った英語を英→日へ）。
        var englishLines = targets.Where(i => routes[i] == LocalNmtRoute.EnglishToJapanese).ToList();
        var chinese = targets.Where(i => IsChinese(routes[i])).ToList();
        var sentences = chinese
            .SelectMany(i => LocalNmtRoutes.SplitChineseSentences(lines[i]).Select(s => (Line: i, Text: s)))
            .ToList();
        var englishOfSentence = new string[sentences.Count];

        RunInParallel(englishLines.Count + sentences.Count, n =>
        {
            if (n < englishLines.Count)
            {
                output[englishLines[n]] = _englishToJapanese!.Translate([lines[englishLines[n]]], cancellationToken)[0];
                return;
            }

            var k = n - englishLines.Count;
            englishOfSentence[k] = _chineseToEnglish!.Translate([sentences[k].Text], cancellationToken)[0].Trim();
        }, cancellationToken);

        // 中→英の結果を行ごとに組み直す。空なら原文のまま返す（2 段目へ空を渡すとモデルが例外を投げる。
        // 2026-10-19、鬼谷八荒の会話の画面で実際に起きた）。
        var secondStage = new List<(int Line, string English)>();
        foreach (var index in chinese)
        {
            var joined = string.Join(
                ' ',
                sentences.Select((s, k) => (s.Line, Text: englishOfSentence[k]))
                         .Where(s => s.Line == index && s.Text.Length > 0)
                         .Select(s => s.Text));

            if (string.IsNullOrWhiteSpace(joined))
            {
                output[index] = lines[index];
            }
            else if (routes[index] == LocalNmtRoute.ChineseToEnglish)
            {
                output[index] = joined;
            }
            else
            {
                secondStage.Add((index, joined));
            }
        }

        RunInParallel(secondStage.Count, n =>
            output[secondStage[n].Line] =
                _englishToJapanese!.Translate([secondStage[n].English], cancellationToken)[0],
            cancellationToken);

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

    /// <summary>1 行を経路どおりに訳す。</summary>
    /// <remarks>
    /// <b>中→英の結果が空なら、原文のまま返す</b>（訳さない行と同じ扱い）。
    /// 空のまま 2 段目へ渡すとモデルが例外を投げ、画面全体の翻訳が止まる
    /// （2026-10-19、鬼谷八荒の会話の画面で実際に起きた）。
    /// </remarks>
    private string TranslateOne(string line, LocalNmtRoute route, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case LocalNmtRoute.EnglishToJapanese:
                return _englishToJapanese!.Translate([line], cancellationToken)[0];

            case LocalNmtRoute.ChineseToEnglish:
            {
                var english = ChineseToEnglish(line, cancellationToken);
                return string.IsNullOrWhiteSpace(english) ? line : english;
            }

            case LocalNmtRoute.ChineseToJapanese:
            {
                var english = ChineseToEnglish(line, cancellationToken);
                return string.IsNullOrWhiteSpace(english)
                    ? line
                    : _englishToJapanese!.Translate([english], cancellationToken)[0];
            }

            default:
                return line;
        }
    }

    /// <summary>
    /// 中→英。<b>句点で区切って 1 文ずつ訳す</b>（区切らないと途中の文を丸ごと落とす）。
    /// </summary>
    /// <remarks>
    /// 文ごとに別々に流す。まとめて 1 回で流すと短い文が詰め物で埋められ、
    /// 訳が変わる（<see cref="LocalNmtOptions.BatchLines"/> と同じ理由）。
    /// </remarks>
    private string ChineseToEnglish(string line, CancellationToken cancellationToken) =>
        string.Join(
            ' ',
            LocalNmtRoutes.SplitChineseSentences(line)
                .Select(sentence => _chineseToEnglish!.Translate([sentence], cancellationToken)[0].Trim()));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _englishToJapanese?.Dispose();
        _chineseToEnglish?.Dispose();
        _initializationLock.Dispose();
        _chineseLock.Dispose();
    }
}
