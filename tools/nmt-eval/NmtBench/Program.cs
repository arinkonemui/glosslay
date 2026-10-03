using System.Diagnostics;
using System.Text;
using Glosslay.Ocr;
using Glosslay.Translation;

namespace Glosslay.Bench;

/// <summary>
/// ローカルNMT の訳文と速度を、製品と同じコードで測る。
/// </summary>
/// <remarks>
/// <para><b>トークナイザの id の引き方は、間違っても実行時エラーにならない。</b>
/// Marian は <c>.spm</c> で分割し <c>vocab.json</c> で id を引くため、
/// ここを取り違えると<b>意味の通らない訳文が静かに出る</b>だけになる。
/// Python 側（<c>tools/nmt-convert/compare.py</c>）の訳文と突き合わせて確かめること。</para>
/// <para>速度は性能要件 1.5 秒の判定材料（PLAN.md 課題4）。
/// 撮影 290ms + OCR 551ms（カーソル窓）に足して 1.5 秒に収まるかを見る。</para>
/// </remarks>
internal static class Program
{
    /// <summary>P0-7 のサンプルから採った実際の画面テキスト。</summary>
    /// <remarks>
    /// <c>tools/nmt-convert/bench.py</c> と同じ文を使う。Python 版と直接比べるため。
    /// <b>行結合（FR-TRN-10）の後の形</b>で入れている。製品が渡すのはこの形。
    /// </remarks>
    private static readonly (string Label, string[] Lines)[] Cases =
    [
        ("短い 2 行", ["Attack Power", "30 damage"]),

        ("カード 1 画面 11 件", [
            "STAGE 1", "Flareon ex", "HP 270", "Evolves from Eevee",
            "As long as this Pokemon is on your Bench, prevent all damage done to this Pokemon by attacks (both yours and your opponent's).",
            "Burning Charge",
            "Search your deck for up to 2 Basic Energy cards and attach them to 1 of your Pokemon. Then, shuffle your deck.",
            "Carnelian",
            "During your next turn, this Pokemon can't attack.",
            "Pokemon ex rule",
            "When your Pokemon ex is Knocked Out, your opponent takes 2 Prize cards.",
        ]),

        ("長文 3 件", [
            "You are burning in slow motion. Under 60 atmospheres of pressure, oxygen becomes a neurotoxin. Nitrogen bubbles saturate your flesh and pry your joints apart.",
            "I've opened the door to the adaptation chamber. Your printed body can borrow tricks from indigenous life. You'll feel better. You'll be better.",
            "The last pioneer didn't make it to the lifepod. I'm activating their blackbox signal. You will make it. That's why you're the QI.",
        ]),
    ];

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var modelDirectory = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "nmt-convert", "out", "elan-en-ja", "int8"));

        // --batch: まとめて 1 回で流す（既定は 1 件ずつ）。
        // 訳文が同じ画面の他の行に左右されるかを見るための切り替え。
        var options = new LocalNmtOptions
        {
            ModelDirectory = modelDirectory,
            CpuThreads = 4,
            BatchLines = args.Contains("--batch", StringComparer.Ordinal),
        };

        var missing = options.RequiredFiles().Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine($"モデルが足りません: {modelDirectory}");
            foreach (var path in missing)
            {
                Console.WriteLine($"  無い: {Path.GetFileName(path)}");
            }

            Console.WriteLine();
            Console.WriteLine("tools/nmt-convert/convert.py で変換してから実行してください。");
            return 1;
        }

        // --tokens: 分割と id だけを出す。
        // Marian は .spm で分割し vocab.json で id を引くため、ここを取り違えても
        // 例外にならず訳文が静かに壊れる。Python 側の id と突き合わせるための出口。
        if (args.Contains("--tokens", StringComparer.Ordinal))
        {
            var tokenizer = MarianTokenizer.Load(
                options.ResolveSentencePiece(), options.ResolveVocabulary());

            Console.WriteLine($"語彙 {tokenizer.VocabularySize} / </s> {tokenizer.EndOfSequenceId}"
                              + $" / <pad> {tokenizer.PadId} / <unk> {tokenizer.UnknownId}");
            Console.WriteLine();

            foreach (var text in Cases.SelectMany(c => c.Lines))
            {
                var ids = tokenizer.Encode(text, options.MaxInputTokens);
                Console.WriteLine(text);
                Console.WriteLine($"  {string.Join(' ', ids)}");
            }

            return 0;
        }

        using var translator = new LocalNmtTranslator(options);

        // --selection: 選択の規則を表のまま検査する。翻訳しない。
        if (args.Contains("--selection", StringComparer.Ordinal))
        {
            return CheckSelection();
        }

        // --fallback: FR-TRN-06 の切り替えを確かめる。
        // クラウドの失敗を待たずに済むよう、必ず失敗するバックエンドを使う。
        if (args.Contains("--fallback", StringComparer.Ordinal))
        {
            await CheckFallbackAsync(translator).ConfigureAwait(false);
            return 0;
        }

        var loading = Stopwatch.StartNew();
        await translator.InitializeAsync().ConfigureAwait(false);
        Console.WriteLine($"バックエンド : {translator.Name}");
        Console.WriteLine($"モデル       : {modelDirectory}");
        Console.WriteLine($"読み込み     : {loading.ElapsedMilliseconds} ms / スレッド {options.CpuThreads}");
        Console.WriteLine();

        // 1 回目は初期化を含むので捨てる（bench.py と同じ扱い）。
        await TranslateAsync(translator, ["warm up"]).ConfigureAwait(false);

        Console.WriteLine(options.BatchLines
            ? "行の扱い     : まとめて 1 回（--batch）"
            : "行の扱い     : 1 件ずつ（既定）");
        Console.WriteLine();

        foreach (var (label, lines) in Cases)
        {
            var result = await TranslateAsync(translator, lines).ConfigureAwait(false);

            Console.WriteLine($"=== {label}  {lines.Length} 件  "
                              + $"{result.Elapsed.TotalMilliseconds:F0} ms  "
                              + $"（1 件あたり {result.Elapsed.TotalMilliseconds / lines.Length:F0} ms）===");

            if (!result.IsSuccess)
            {
                Console.WriteLine($"  失敗: {result.Message}");
                Console.WriteLine();
                continue;
            }

            var translated = result.Text.ReplaceLineEndings("\n").Split('\n');
            if (translated.Length != lines.Length)
            {
                Console.WriteLine($"  ★ 本数が合わない: 原文 {lines.Length} / 訳文 {translated.Length}");
            }

            for (var i = 0; i < lines.Length; i++)
            {
                Console.WriteLine($"  {lines[i]}");
                Console.WriteLine($"    → {(i < translated.Length ? translated[i] : "（無し）")}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("訳文の質は人が読んで判断する。Python 側（tools/nmt-convert/compare.py）と");
        Console.WriteLine("食い違う場合は、トークナイザの id の引き方を疑うこと。");
        return 0;
    }

    /// <summary>
    /// バックエンドの選択（FR-TRN-01 / FR-TRN-06）を表のまま検査する。
    /// </summary>
    /// <remarks>
    /// <b>RULES.md 🟡-1「既定はローカル、クラウドは任意のアップグレード」がここで崩れる。</b>
    /// 選択肢が増えたので、順序が逆になっていないかを機械的に見る。
    /// </remarks>
    private static int CheckSelection()
    {
        using var cloud = new Stub("クラウド", configured: true);
        using var cloudNoKey = new Stub("クラウド", configured: false);
        using var local = new Stub("ローカル", configured: true);
        using var localNoModel = new Stub("ローカル", configured: false);

        (string Label, TranslationBackendChoice Choice, Stub Cloud, Stub Local,
            string Active, string Fallback)[] cases =
        [
            ("自動・キーあり",        TranslationBackendChoice.Auto,      cloud,      local,        "クラウド", "ローカル"),
            ("自動・キーなし",        TranslationBackendChoice.Auto,      cloudNoKey, local,        "ローカル", "なし"),
            ("自動・モデルなし",      TranslationBackendChoice.Auto,      cloud,      localNoModel, "クラウド", "なし"),
            ("クラウドのみ・キーあり", TranslationBackendChoice.CloudOnly, cloud,      local,        "クラウド", "なし"),
            ("クラウドのみ・キーなし", TranslationBackendChoice.CloudOnly, cloudNoKey, local,        "クラウド", "なし"),
            ("ローカルのみ・キーあり", TranslationBackendChoice.LocalOnly, cloud,      local,        "ローカル", "なし"),
        ];

        Console.WriteLine("=== バックエンドの選択 ===");
        Console.WriteLine($"  {"場合",-22} {"使う",-10} {"切替先",-10} 判定");

        var failures = 0;
        foreach (var (label, choice, c, l, wantActive, wantFallback) in cases)
        {
            var active = TranslatorSelection.Active(choice, c, l).Name;
            var fallback = TranslatorSelection.Fallback(choice, c, l)?.Name ?? "なし";
            var ok = active == wantActive && fallback == wantFallback;
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {label,-22} {active,-10} {fallback,-10} "
                              + (ok ? "OK" : $"★ 期待 {wantActive} / {wantFallback}"));
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>選択の検査にだけ使う、翻訳しないバックエンド。</summary>
    private sealed class Stub(string name, bool configured) : ITranslator
    {
        public string Name => name;

        public bool IsConfigured => configured;

        public bool TranslatesLineByLine => false;

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("選択の検査専用。");

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// FR-TRN-06「429 / ネットワークエラー時は静かにローカルNMTへフォールバック」の確認。
    /// </summary>
    /// <remarks>
    /// <b>切り替えるべき場合と、切り替えてはいけない場合の両方を見る。</b>
    /// 失敗したら必ず切り替える実装は、キーの設定ミスを隠してしまう（RULES.md 🟡-6）。
    /// </remarks>
    private static async Task CheckFallbackAsync(LocalNmtTranslator local)
    {
        OcrLine[] english =
        [
            new("Attack Power", 1f, new OcrBox(0, 0, 100, 20)),
            new("Evolves from Eevee", 1f, new OcrBox(0, 30, 100, 20)),
        ];

        OcrLine[] chinese =
        [
            new("使用这个技能需要消耗30点灵力。", 1f, new OcrBox(0, 0, 100, 20)),
        ];

        (string Label, TranslationOutcome Outcome, OcrLine[] Lines, bool ShouldSwitch)[] cases =
        [
            ("混雑（HTTP 503）", TranslationOutcome.Busy, english, true),
            ("利用上限（HTTP 429）", TranslationOutcome.RateLimited, english, true),
            ("応答なし", TranslationOutcome.Timeout, english, true),
            ("キー未登録", TranslationOutcome.NotConfigured, english, false),
            ("混雑だが原文が中国語", TranslationOutcome.Busy, chinese, false),
        ];

        Console.WriteLine("=== FR-TRN-06 切り替えの確認 ===");
        var failures = 0;

        foreach (var (label, outcome, lines, shouldSwitch) in cases)
        {
            using var primary = new AlwaysFails(outcome);
            var result = await LineTranslation.TranslateAsync(primary, lines, local).ConfigureAwait(false);

            var switched = result.FellBackFrom is not null;
            var ok = switched == shouldSwitch;
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "  " : "★ ")}{label,-22} "
                              + $"切替 {(switched ? "した" : "しない"),-4} "
                              + $"（期待 {(shouldSwitch ? "する" : "しない")}）"
                              + $"  状態 {result.Translation.Outcome}");

            for (var i = 0; i < lines.Length; i++)
            {
                Console.WriteLine($"      {lines[i].Text}  →  {result.Lines[i].Text}  [{result.Statuses[i]}]");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
    }

    /// <summary>必ず指定した失敗を返すバックエンド。切り替えの確認にだけ使う。</summary>
    private sealed class AlwaysFails(TranslationOutcome outcome) : ITranslator
    {
        public string Name => $"検査用（常に {outcome}）";

        public bool IsConfigured => true;

        public bool TranslatesLineByLine => false;

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResult(string.Empty, outcome, TimeSpan.Zero)
            {
                Message = "検査用",
            });

        public void Dispose()
        {
        }
    }

    private static Task<TranslationResult> TranslateAsync(LocalNmtTranslator translator, string[] lines) =>
        translator.TranslateAsync(new TranslationRequest(string.Join('\n', lines)));
}
