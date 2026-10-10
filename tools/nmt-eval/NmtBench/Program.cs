using System.Diagnostics;
using System.Text;
using Glosslay.Ads;
using Glosslay.Configuration;
using Glosslay.Entitlement;
using Glosslay.Imaging;
using Glosslay.Input;
using Glosslay.Monitoring;
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

        // --watch: 自動監視の守り（FR-MOD-02 / 04 / 05 / FR-OCR-07）を確かめる。モデルを読まない。
        if (args.Contains("--watch", StringComparer.Ordinal))
        {
            return CheckAutoWatch();
        }

        // --services: 横断インターフェース（RULES.md 🟡-2 / 🟡-4）の配線を確かめる。モデルを読まない。
        if (args.Contains("--services", StringComparer.Ordinal))
        {
            return CheckServices();
        }

        // --numbers: FR-TRN-08 の数値照合を確かめる。モデルを読まない。
        if (args.Contains("--numbers", StringComparer.Ordinal))
        {
            return CheckNumbers();
        }

        // --cache: FR-TRN-09 の使い回しを確かめる。
        if (args.Contains("--cache", StringComparer.Ordinal))
        {
            await CheckCacheAsync(translator).ConfigureAwait(false);
            return 0;
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
    /// 自動監視の守りを確かめる（FR-MOD-02 / 04 / 05 / FR-OCR-07）。
    /// </summary>
    /// <remarks>
    /// <b>自動監視は RULES.md 🟡-8（予期しない課金）に正面からぶつかる。</b>
    /// 止まるべきときに止まることを、画面を見ずに確かめる。
    /// </remarks>
    private static int CheckAutoWatch()
    {
        var failures = 0;

        void Check(string label, AutoWatchDecision actual, AutoWatchDecision expected)
        {
            var ok = actual == expected;
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "  " : "★ ")}{label,-40} {actual,-14}（期待 {expected}）");
        }

        var black = Solid(64, 48, 0);
        var white = Solid(64, 48, 255);

        Console.WriteLine("=== 止まる条件（FR-MOD-05 / FR-OCR-07）===");
        var gate = new AutoWatchGate();
        Check("前面でなければ見もしない", gate.Evaluate(black, isForeground: false),
            AutoWatchDecision.NotForeground);
        Check("最初の 1 枚は比べる相手がない", gate.Evaluate(black, true), AutoWatchDecision.FirstFrame);
        Check("変化がなければ翻訳しない", gate.Evaluate(black, true), AutoWatchDecision.NoChange);
        Check("変化があれば翻訳する", gate.Evaluate(white, true), AutoWatchDecision.Translate);
        Check("同じ画面が続けば止まる", gate.Evaluate(white, true), AutoWatchDecision.NoChange);

        Console.WriteLine();
        Console.WriteLine("=== 呼び出し上限（FR-MOD-04）===");
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var limiter = new CallRateLimiter(maxCalls: 2, window: TimeSpan.FromHours(1), timeProvider: clock);
        var limited = new AutoWatchGate(new AutoWatchOptions(), limiter);

        limited.Evaluate(black, true);
        Check("1 回目", limited.Evaluate(white, true), AutoWatchDecision.Translate);
        Check("2 回目", limited.Evaluate(black, true), AutoWatchDecision.Translate);
        Check("上限に達したら止まる", limited.Evaluate(white, true), AutoWatchDecision.RateLimited);

        var beforeWindow = limiter.Remaining;
        clock.Advance(TimeSpan.FromMinutes(61));
        Console.WriteLine($"     残り {beforeWindow} → 61 分後 {limiter.Remaining}");
        Check("窓を過ぎれば戻る", limited.Evaluate(black, true), AutoWatchDecision.Translate);

        Console.WriteLine();
        Console.WriteLine("=== 消費しない条件 ===");
        var counted = new CallRateLimiter(maxCalls: 5, timeProvider: new FakeClock(DateTimeOffset.UnixEpoch));
        var counting = new AutoWatchGate(new AutoWatchOptions(), counted);
        counting.Evaluate(black, true);
        counting.Evaluate(black, true);
        counting.Evaluate(black, isForeground: false);
        var ok5 = counted.Remaining == 5;
        if (!ok5)
        {
            failures++;
        }

        Console.WriteLine($"  {(ok5 ? "  " : "★ ")}翻訳しなかった回は上限を消費しない    残り {counted.Remaining}/5（期待 5/5）");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>一様な色の画像。差分判定の検査にだけ使う。</summary>
    private static Bgra32Image Solid(int width, int height, byte value)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, value);
        return new Bgra32Image(pixels, width, height, stride);
    }

    /// <summary>時間を手で進められる時計。上限が窓で戻ることを確かめるために使う。</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    /// <summary>
    /// 横断インターフェースの配線を確かめる（RULES.md 🟡-2 / 🟡-4、FR-ADS、FR-SYS-10）。
    /// </summary>
    /// <remarks>
    /// <b>v1.0 の実装は上限なし・広告なしなので、放っておくと「判定を通っているのか
    /// 素通りしているのか」が区別できない。</b>上限を入れた実体を作って、ゲートが効くことを見る。
    /// </remarks>
    private static int CheckServices()
    {
        var failures = 0;

        void Check(string label, bool actual, bool expected)
        {
            var ok = actual == expected;
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "  " : "★ ")}{label,-46} {(actual ? "はい" : "いいえ"),-6}"
                              + $"（期待 {(expected ? "はい" : "いいえ")}）");
        }

        Console.WriteLine("=== 機能ゲート（IEntitlementService / RULES.md 🟡-4）===");
        var free = FreeOnlyEntitlementService.Default;
        Check("v1.0 は無料版", free.Tier == EntitlementTier.Free, true);
        Check("v1.0 の上限は実質なし（プロファイル）",
            free.MaxProfiles == FreeOnlyEntitlementService.Unlimited, true);
        Check("v1.0 は広告を出す対象でない", free.ShowsAds, false);

        var limited = new FreeOnlyEntitlementService(maxProfiles: 3, showsAds: true);
        Check("上限を入れれば効く（3 件）", limited.MaxProfiles == 3, true);

        Console.WriteLine();
        Console.WriteLine("=== 遠隔設定（IRemoteConfig / FR-ADS-06）===");
        var safe = BuiltInRemoteConfig.Default;
        Check("取得できていなければ広告は無効", safe.Current.AdsEnabled, false);
        Check("推奨モデルは未指定（利用者の設定を使う）",
            safe.Current.RecommendedGeminiModel is null, true);

        Console.WriteLine();
        Console.WriteLine("=== 広告の判断（AdPolicy / FR-ADS-02・04・05）===");
        var ads = NullAdProvider.Default;
        var enabled = new BuiltInRemoteConfig(new RemoteSettings(AdsEnabled: true));

        Check("v1.0 の既定では出さない",
            AdPolicy.ShouldShow(AdSlot.Settings, free, safe, ads), false);
        Check("遠隔設定が有効でも、描画できなければ出さない",
            AdPolicy.ShouldShow(AdSlot.Settings, limited, enabled, ads), false);

        var filling = new AlwaysFills();
        Check("条件が揃えば出す",
            AdPolicy.ShouldShow(AdSlot.Settings, limited, enabled, filling), true);
        // v1.0 は契約者を作れない（FreeOnly は常に Free）。ここで見ているのは
        // 「ShowsAds が false なら遠隔設定が有効でも出さない」という FR-ADS-04 の経路。
        Check("広告の対象でなければ出さない（遠隔設定より優先）",
            AdPolicy.ShouldShow(AdSlot.Settings, free, enabled, filling), false);
        Check("遠隔設定が無効なら出さない",
            AdPolicy.ShouldShow(AdSlot.Settings, limited, safe, filling), false);

        Console.WriteLine();
        Console.WriteLine("=== 入力（IInputTrigger / RULES.md 🔴-3）===");
        using var trigger = new RawInputTrigger(new HotkeyOptions());
        Check("既定の割り当てが有効", trigger.IsActive, true);
        Check("割り当ての説明が 3 件", trigger.Descriptions.Count == 3, true);
        Check("問題なし", trigger.Problems.Count == 0, true);

        using var duplicated = new RawInputTrigger(new HotkeyOptions
        {
            TranslateScreen = "Ctrl+Shift+T",
            TranslateCursor = "Ctrl+Shift+T",
            ToggleOverlay = "まったく解釈できない",
        });
        Check("重複した割り当ては 1 つだけ残る", duplicated.Descriptions.Count == 1, true);
        Check("重複と解釈不能の両方を伝える", duplicated.Problems.Count == 2, true);
        Check("使える割り当てが残っていれば有効", duplicated.IsActive, true);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>常に枠を埋められる広告。判断の検査にだけ使う。</summary>
    private sealed class AlwaysFills : IAdProvider
    {
        public string Name => "検査用";

        public bool CanFill(AdSlot slot) => true;
    }

    /// <summary>
    /// FR-TRN-08「原文の数値を抽出し訳文と照合」の確認。
    /// </summary>
    /// <remarks>
    /// <b>見逃しと騒ぎすぎの両方を見る。</b>
    /// 騒ぎすぎる照合は、やがて誰にも読まれなくなる。
    /// </remarks>
    private static int CheckNumbers()
    {
        (string Label, string Source, string Translated, bool Consistent)[] cases =
        [
            ("そのまま残った",        "Deal 50 damage.",            "50 ダメージを与える。",        true),
            ("書き換わった",          "Deal 50 damage.",            "60 ダメージを与える。",        false),
            ("落ちた",                "Cooldown: 15s",              "クールダウン",                 false),
            ("作られた",              "Discard all Energy.",        "エネルギーを 3 枚捨てる。",    false),
            ("桁区切りの違い",        "2,430 coins",                "2430 コイン",                  true),
            ("全角の数字",            "HP 270",                     "ＨＰ ２７０",                  true),
            ("小数と記号",            "increases by 3.50%.",        "3.50% 増加する。",             true),
            ("負の値",                "resistance -30",             "抵抗 -30",                     true),
            ("負の値が符号を失う",    "resistance -30",             "抵抗 30",                      false),
            ("型番の区切りは符号でない", "ISV-CI-DSB3 node",         "ISV-CI-DSB3 ノード",           true),
            ("分数表記",              "0/5 complete",               "0/5 完了",                     true),
            ("文末の句点を拾わない",  "It does 90 damage.",         "90 ダメージを与える",          true),
            ("数値なし",              "Attack Power",               "攻撃力",                       true),
        ];

        Console.WriteLine("=== FR-TRN-08 数値の照合 ===");
        var failures = 0;

        foreach (var (label, source, translated, want) in cases)
        {
            var check = NumberVerifier.Compare(source, translated);
            var ok = check.IsConsistent == want;
            if (!ok)
            {
                failures++;
            }

            var detail = check.IsConsistent
                ? "一致"
                : $"落ちた[{string.Join(",", check.Missing)}] 増えた[{string.Join(",", check.Added)}]";

            Console.WriteLine($"  {(ok ? "  " : "★ ")}{label,-24} {detail,-34} "
                              + $"（期待 {(want ? "一致" : "不一致")}）");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// FR-TRN-09「同一原文の翻訳結果をキャッシュする」の確認。
    /// </summary>
    /// <remarks>
    /// <b>速くなるだけでなく、同じ訳文が返ることまで見る。</b>
    /// 使い回しで別の訳が出るなら、覚えている意味がない。
    /// </remarks>
    private static async Task CheckCacheAsync(LocalNmtTranslator local)
    {
        OcrLine[] lines =
        [
            new("Attack Power", 1f, new OcrBox(0, 0, 100, 20)),
            new("Evolves from Eevee", 1f, new OcrBox(0, 30, 100, 20)),
            new("During your next turn, this Pokemon can't attack.", 1f, new OcrBox(0, 60, 400, 20)),
        ];

        var cache = new TranslationCache();

        var first = await LineTranslation.TranslateAsync(local, lines, cache: cache).ConfigureAwait(false);
        var second = await LineTranslation.TranslateAsync(local, lines, cache: cache).ConfigureAwait(false);

        Console.WriteLine("=== FR-TRN-09 使い回しの確認 ===");
        Console.WriteLine($"  1 回目  記憶から {first.CachedCount}/{first.SentCount} 件  "
                          + $"{first.Translation.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"  2 回目  記憶から {second.CachedCount}/{second.SentCount} 件  "
                          + $"{second.Translation.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"  覚えた件数: {cache.Count}");
        Console.WriteLine();

        var same = true;
        for (var i = 0; i < lines.Length; i++)
        {
            var a = first.Lines[i].Text;
            var b = second.Lines[i].Text;
            same &= string.Equals(a, b, StringComparison.Ordinal);
            Console.WriteLine($"    {lines[i].Text}");
            Console.WriteLine($"      1 回目 → {a}");
            Console.WriteLine($"      2 回目 → {b}");
        }

        Console.WriteLine();
        var ok = first.CachedCount == 0
                 && second.CachedCount == second.SentCount
                 && second.Translation.Elapsed == TimeSpan.Zero
                 && same;
        Console.WriteLine(ok
            ? "期待どおり。2 回目は呼び出さず、同じ訳文を返した。"
            : "★ 期待と違う。");
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
