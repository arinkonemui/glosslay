using System.Globalization;
using System.Diagnostics;
using System.Text;
using Glosslay.Ads;
using Glosslay.Configuration;
using Glosslay.Entitlement;
using Glosslay.Capture;
using Glosslay.Imaging;
using Glosslay.Input;
using Glosslay.Monitoring;
using Glosslay.Ocr;
using Glosslay.Profiles;
using Glosslay.Translation;
using Glosslay.Usage;

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

        // モデルの場所は最初の引数。--xxx は検査の指定なので位置引数として扱わない。
        // これを見ていないと、モデルを読まない検査（--profiles 等）が
        // 「モデルが足りません」で止まる。
        var positional = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var modelDirectory = positional is not null
            ? Path.GetFullPath(positional)
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

        // --dwell: カーソル静止の判定（FR-MOD-10 / 11）を確かめる。モデルを読まない。
        if (args.Contains("--dwell", StringComparer.Ordinal))
        {
            return CheckDwell();
        }

        // --profiles: プロファイルの当て方と上限（FR-CFG-01〜03 / FR-SYS-10）を確かめる。
        // モデルを読まない。
        if (args.Contains("--profiles", StringComparer.Ordinal))
        {
            return CheckProfiles();
        }

        // --languages: 訳文の言語の扱い（FR-CFG-01 / FR-TRN-03）を確かめる。モデルを読まない。
        if (args.Contains("--languages", StringComparer.Ordinal))
        {
            return await CheckLanguagesAsync().ConfigureAwait(false);
        }

        // --usage: 使用量の数え方と文面（RULES.md 🟡-8）を確かめる。モデルを読まない。
        if (args.Contains("--usage", StringComparer.Ordinal))
        {
            return CheckUsage();
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
    /// プロファイルの当て方・上限・交換を確かめる（FR-CFG-01〜03 / FR-SYS-10）。
    /// </summary>
    /// <remarks>
    /// <para><b>当ててはいけないものが当たることが一番危ない。</b>
    /// 自動適用は前面のウィンドウを見て黙って設定を差し替えるため、
    /// 別のゲームに当たると「設定が勝手に変わった」としか見えない。
    /// Unity 製のゲームは同じクラス名（<c>UnityWndClass</c>）を使うため、
    /// <b>クラス名だけでは足りない場合がある</b>ことをここで押さえる。</para>
    /// <para><b>実物の <c>profiles.json</c> は触らない。</b>別のファイル名を渡し、
    /// 最後に消す。検査で利用者のプロファイルを上書きしてはならない。</para>
    /// </remarks>
    private static int CheckProfiles()
    {
        var failures = 0;

        void Check(string label, bool ok, string detail = "")
        {
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "  " : "★ ")}{label,-44} {(ok ? "OK" : "NG"),-4}{detail}");
        }

        // クラス名だけのもの、タイトルでも絞るもの、何も指定しないものを並べる。
        var elin = new Profile { Name = "Elin", WindowClass = "UnityWndClass" };
        var subnautica = new Profile
        {
            Name = "Subnautica 2",
            WindowClass = "UnityWndClass",
            TitleContains = "Subnautica",
        };
        var empty = new Profile { Name = "空っぽ" };

        Console.WriteLine("=== 当てはまる判定（FR-CFG-02）===");
        Check("クラス名が一致すれば当たる", elin.Matches("UnityWndClass", "Elin"));
        Check("クラス名が違えば当たらない", !elin.Matches("UnrealWindow", "Elin"));
        Check("タイトルは部分一致で当たる", subnautica.Matches("UnityWndClass", "Subnautica 2 - Early Access"));
        Check("タイトルは大小を無視する", subnautica.Matches("UnityWndClass", "SUBNAUTICA 2"));
        Check("タイトルが違えば当たらない", !subnautica.Matches("UnityWndClass", "Elin"));

        // どちらも空のプロファイルは、放っておくと全部のウィンドウに当たる。
        Check("対象が空なら何にも当たらない", !empty.Matches("UnityWndClass", "Elin"));
        Check("対象が空なら空のウィンドウにも当たらない", !empty.Matches(string.Empty, string.Empty));

        Console.WriteLine();
        Console.WriteLine("=== 使えないクラス名（2026-10-11 実測）===");

        // WPF のウィンドウは起動ごとに変わる GUID を含む。保存しても次回は当たらない。
        Check(
            "WPF のクラス名は変わるものと判る",
            WindowIdentity.IsVolatileClassName(
                "HwndWrapper[Glosslay;;bc3063fb-5882-467e-8ca8-2c09baca2389]"));
        Check("Unity のクラス名は使える", !WindowIdentity.IsVolatileClassName("UnityWndClass"));
        Check("Unreal のクラス名は使える", !WindowIdentity.IsVolatileClassName("UnrealWindow"));
        Check("空文字は変わるものではない", !WindowIdentity.IsVolatileClassName(string.Empty));

        Console.WriteLine();
        Console.WriteLine("=== 複数が当たるとき（先に登録したものを優先）===");

        // Unity 製が 2 つ。絞っていない Elin が先にあるため、Subnautica でも Elin が当たる。
        // これは実装どおりの挙動。利用者には「先に出した方」という説明ができる。
        var ordered = new ProfileStore(profiles: [elin, subnautica], fileName: BenchProfileFile);
        Check(
            "先に登録した方が勝つ",
            ordered.FindForWindow("UnityWndClass", "Subnautica 2")?.Name == "Elin",
            "← クラス名だけの登録は後続を食う。タイトルでの絞り込みが要る");

        var narrowedFirst = new ProfileStore(
            profiles: [subnautica, elin], fileName: BenchProfileFile);
        Check(
            "絞った方を先に置けば当たる",
            narrowedFirst.FindForWindow("UnityWndClass", "Subnautica 2")?.Name == "Subnautica 2");
        Check(
            "絞った方を先に置いても他は Elin",
            narrowedFirst.FindForWindow("UnityWndClass", "Elin")?.Name == "Elin");
        Check("当てはまらなければ null", ordered.FindForWindow("Notepad", "無題") is null);

        Console.WriteLine();
        Console.WriteLine("=== 保存と上限（FR-SYS-10 / RULES.md 🟡-4）===");

        var path = JsonSettings.PathOf(BenchProfileFile);
        try
        {
            var limited = new ProfileStore(
                new CappedEntitlements(2), profiles: [], fileName: BenchProfileFile);

            Check("1 件目は保存できる", limited.Save(elin) == ProfileSaveResult.Added);
            Check("2 件目も保存できる", limited.Save(subnautica) == ProfileSaveResult.Added);
            Check(
                "上限を超えたら断る",
                limited.Save(new Profile { Name = "3 件目", WindowClass = "X" })
                    == ProfileSaveResult.LimitReached);

            // 上限は「持てる数」であって「保存した回数」ではない。
            Check(
                "置き換えは上限に数えない",
                limited.Save(elin with { TitleContains = "Elin" }) == ProfileSaveResult.Replaced);
            Check("置き換えても件数は増えない", limited.Profiles.Count == 2);
            Check("名前が空なら断る", limited.Save(new Profile()) == ProfileSaveResult.NameRequired);
            Check("上限に達しても消せば入る", limited.Remove("Elin") && limited.Remaining == 1);

            Console.WriteLine();
            Console.WriteLine("=== 交換（FR-CFG-03）===");

            var exported = ProfileStore.Export(subnautica);
            var imported = ProfileStore.Import(exported);
            Check("書き出して読み直すと同じ", imported == subnautica);
            Check("日本語がそのまま読める", exported.Contains("Subnautica", StringComparison.Ordinal));

            // 他の人から貰う前提のため、壊れていても例外で落とさない（RULES.md 🟡-6）。
            Check("壊れた JSON は null（落ちない）", ProfileStore.Import("{ これは JSON ではない") is null);
            Check("空文字も null", ProfileStore.Import(string.Empty) is null);
            Check(
                "知らない項目があっても読める",
                ProfileStore.Import("""{ "Name": "未来版", "UnknownField": 1 }""")?.Name == "未来版");

            Console.WriteLine();
            Console.WriteLine("=== 列挙型の書式（RULES.md 🔵「人が読める JSON」）===");

            // "Resampling": 1 では手で直せない。名前で書き、名前でも数字でも読めること。
            Check(
                "名前で書き出す",
                exported.Contains("\"Resampling\": \"Bicubic\"", StringComparison.Ordinal),
                exported.Contains("\"Resampling\": 1", StringComparison.Ordinal) ? "← 数字で出ている" : string.Empty);
            Check(
                "名前で読める",
                ProfileStore.Import(
                    """{ "Name": "名前", "Preprocess": { "Resampling": "Bilinear" } }""")
                    ?.Preprocess.Resampling == ResamplingMode.Bilinear);

            // 変換器を足す前に書かれた設定ファイルが読めなくなっては困る。
            Check(
                "数字でも読める（古い設定ファイル）",
                ProfileStore.Import(
                    """{ "Name": "名前", "Preprocess": { "Resampling": 0 } }""")
                    ?.Preprocess.Resampling == ResamplingMode.Bilinear);

            Console.WriteLine();
            Console.WriteLine("=== 読み直し（手で編集した場合）===");
            var onDisk = new ProfileStore(profiles: [], fileName: BenchProfileFile);
            Check("一覧を渡したらファイルは読まない", onDisk.Profiles.Count == 0);

            onDisk.Reload();
            Check(
                "Reload でファイルから読み直す",
                onDisk.Profiles.Count == limited.Profiles.Count,
                $"← {onDisk.Profiles.Count} 件");
        }
        finally
        {
            // 検査用のファイルは残さない。
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 訳文の言語の扱いを確かめる（FR-CFG-01 の「言語」/ FR-TRN-03）。
    /// </summary>
    /// <remarks>
    /// <para><b>一番危ないのは「訳せないのに成功して返る」こと。</b>
    /// 同梱のモデルは英→日のみなので、英語を選んで訳させると
    /// <b>日本語が出て、しかも成功として返りうる</b>。画面の表示だけで防ぐと、
    /// 設定ファイルを手で書き換えた場合に素通りする。</para>
    /// <para><b>モデルを読まない。</b>存在しないモデル置き場を渡し、
    /// 言語の判定がモデルの有無より<b>先に</b>働くことを使って確かめる。
    /// 日本語なら「モデルが無い」で止まり、英語なら「対応していない」で止まる。</para>
    /// </remarks>
    private static async Task<int> CheckLanguagesAsync()
    {
        var failures = 0;

        void Check(string label, bool ok, string detail = "")
        {
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "  " : "★ ")}{label,-44} {(ok ? "OK" : "NG"),-4}{detail}");
        }

        Console.WriteLine("=== 選択肢（FR-CFG-01）===");
        Check("既定は日本語", TargetLanguages.All[0].Tag == TargetLanguages.Default);
        Check("日本語はキー無しで訳せる", TargetLanguages.LocalSupports("ja"));
        Check(
            "ローカルで訳せるのは日本語だけ",
            TargetLanguages.All.Count(o => o.LocalSupported) == 1,
            $"← 全 {TargetLanguages.All.Count} 件");
        Check("大小を無視して引ける", TargetLanguages.LocalSupports("JA"));
        Check("知らないタグは訳せない扱い", !TargetLanguages.LocalSupports("de"));
        Check("知らないタグはそのまま表示する", TargetLanguages.DisplayNameOf("de") == "de");

        Console.WriteLine();
        Console.WriteLine("=== ローカルが断るか（FR-TRN-03）===");

        // 実在しない置き場。モデルを読まずに判定の順序だけを見る。
        var noModel = new LocalNmtOptions
        {
            ModelDirectory = Path.Combine(Path.GetTempPath(), "glosslay-no-such-model"),
        };

        using var local = new LocalNmtTranslator(noModel);

        var japanese = await local
            .TranslateAsync(new TranslationRequest("Attack Power") { TargetLanguage = "ja" })
            .ConfigureAwait(false);

        var english = await local
            .TranslateAsync(new TranslationRequest("ダメージ") { TargetLanguage = "en" })
            .ConfigureAwait(false);

        Check(
            "日本語は言語では断らない",
            japanese.Outcome == TranslationOutcome.NotConfigured,
            $"← {japanese.Outcome}（モデルが無いので止まる）");
        Check(
            "英語はモデルの有無より先に断る",
            english.Outcome == TranslationOutcome.Unsupported,
            $"← {english.Outcome}");
        Check(
            "断る理由を言葉で返す",
            english.Message?.Contains("APIキー", StringComparison.Ordinal) == true,
            $"← {english.Message}");

        // FR-TRN-06: 対応していない言語では切り替えない。切り替えても同じ結果になる。
        Check("対応しない言語では切り替えない", !english.ShouldFallBack);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 使用量の数え方と文面を確かめる（RULES.md 🟡-8「使用量を常時表示」）。
    /// </summary>
    /// <remarks>
    /// <para><b>課金に直結する数字なので、数え違いが一番こわい。</b>
    /// クラウドとローカルを取り違える、失敗を数えない、日をまたいでも積み続ける —
    /// どれも「予期しない課金」を防ぐという目的を裏切る。</para>
    /// <para><b>実物の <c>usage.json</c> は触らない。</b>別のファイル名を渡し、最後に消す。</para>
    /// </remarks>
    private static int CheckUsage()
    {
        var failures = 0;

        void Check(string label, bool ok, string detail = "")
        {
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "  " : "★ ")}{label,-44} {(ok ? "OK" : "NG"),-4}{detail}");
        }

        var path = JsonSettings.PathOf(BenchUsageFile);
        try
        {
            var clock = new FakeClock(DateTimeOffset.UnixEpoch);
            var store = new UsageStore(clock, days: [], fileName: BenchUsageFile);

            Console.WriteLine("=== 数え方（RULES.md 🟡-8）===");

            store.RecordCloud(tokens: 1200, succeeded: true);
            store.RecordCloud(tokens: 800, succeeded: true);
            Check("クラウドの回数を数える", store.Today.CloudCalls == 2);
            Check("トークンを足し上げる", store.Today.CloudTokens == 2000);

            // 通信は起きている。失敗したから無かったことにはならない。
            store.RecordCloud(tokens: null, succeeded: false);
            Check("失敗も呼び出しに数える", store.Today.CloudCalls == 3);
            Check("失敗の数は別に持つ", store.Today.CloudFailures == 1);
            Check("トークン不明は 0 として足す", store.Today.CloudTokens == 2000);

            // 費用がかからないものを混ぜると、見た数字の意味が分からなくなる。
            store.RecordLocal();
            Check("ローカルは別に数える", store.Today.LocalCalls == 1);
            Check("ローカルはクラウドに混ぜない", store.Today.CloudCalls == 3);

            store.RecordCached(5);
            Check("使い回した行を数える", store.Today.CachedLines == 5);
            store.RecordCached(0);
            Check("0 行は数えない", store.Today.CachedLines == 5);

            Console.WriteLine();
            Console.WriteLine("=== 日をまたぐ ===");

            clock.Advance(TimeSpan.FromDays(1));
            Check("翌日は 0 から始まる", store.Today.CloudCalls == 0);

            // 見ただけで日を作らない。使わなかった日の空の記録を残す意味がない。
            Check("見るだけでは日を作らない", store.Days.Count == 1);

            store.RecordCloud(tokens: 10, succeeded: true);
            Check("翌日のぶんは翌日に積む", store.Today.CloudCalls == 1);
            Check("前日の記録は残る", store.Days.Count == 2);

            // 使用量の記録それ自体が無制限に膨らむのは筋が通らない。
            clock.Advance(TimeSpan.FromDays(UsageStore.KeepDays + 1));
            store.RecordLocal();
            Check(
                $"{UsageStore.KeepDays} 日より古い記録は落とす",
                store.Days.Count == 1,
                $"← 残り {store.Days.Count} 日ぶん");

            Console.WriteLine();
            Console.WriteLine("=== 書き出しと読み直し ===");
            var reread = new UsageStore(clock, days: null, fileName: BenchUsageFile);
            Check("ファイルから読み直せる", reread.Days.Count == store.Days.Count);
            Check("今日のぶんが一致する", reread.Today.LocalCalls == store.Today.LocalCalls);

            Console.WriteLine();
            Console.WriteLine("=== 文面 ===");

            var busy = new UsageDay
            {
                Date = DateOnly.FromDateTime(DateTime.Today),
                CloudCalls = 42,
                CloudTokens = 12_345,
                LocalCalls = 7,
                CachedLines = 31,
            };
            var quiet = new UsageDay { Date = busy.Date };

            var oneLine = UsageSummary.OneLine(busy, remaining: 158, maxCalls: 200);
            var detail = UsageSummary.Detail(busy, remaining: 158, maxCalls: 200);

            Check("1 行はクラウドから始まる", oneLine.StartsWith("今日 クラウド", StringComparison.Ordinal));
            Check("1 行に回数が出る", oneLine.Contains("42", StringComparison.Ordinal));

            // ふだんは残りを出さない。狭い場所で肝心の数字が読みにくくなる。
            Check("余裕があれば残りを出さない", !oneLine.Contains("自動の残り", StringComparison.Ordinal));

            var low = UsageSummary.OneLine(busy, remaining: 20, maxCalls: 200);
            Check("残りが減ったら添える", low.Contains("自動の残り", StringComparison.Ordinal));

            // 使っていないことが伝わらないと、確かめる意味がない。
            var none = UsageSummary.OneLine(quiet, remaining: 200, maxCalls: 200);
            Check("使っていなければそう言う", none.Contains("なし", StringComparison.Ordinal));
            Check(
                "明細では費用が出ないと言う",
                UsageSummary.Detail(quiet, 200, 200).Contains("費用は発生していません", StringComparison.Ordinal));

            Check("明細はクラウドとローカルを分ける",
                detail.Contains("クラウド翻訳", StringComparison.Ordinal)
                && detail.Contains("ローカル翻訳", StringComparison.Ordinal));
            Check("明細に残りが出る", detail.Contains("自動呼び出しの残り", StringComparison.Ordinal));

            Console.WriteLine();
            Console.WriteLine("--- 1 行 ---");
            Console.WriteLine($"  {oneLine}");
            Console.WriteLine($"  {low}");
            Console.WriteLine($"  {none}");
            Console.WriteLine("--- 明細 ---");
            Console.WriteLine(detail);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>検査用の使用量の置き場。実物（<c>usage.json</c>）とは分ける。</summary>
    private const string BenchUsageFile = "usage.bench.json";

    /// <summary>検査用のプロファイル置き場。実物（<c>profiles.json</c>）とは分ける。</summary>
    private const string BenchProfileFile = "profiles.bench.json";

    /// <summary>件数だけを絞る資格サービス。上限の判定を通すために使う。</summary>
    private sealed class CappedEntitlements(int maxProfiles) : IEntitlementService
    {
        public EntitlementTier Tier => EntitlementTier.Free;

        public int MaxWatchedRegions => 1;

        public int MaxProfiles => maxProfiles;

        public int MaxGlossaryEntries => 0;

        public int MaxHistoryEntries => 0;

        public bool CanSearchHistory => false;

        public bool ShowsAds => true;
    }

    /// <summary>
    /// カーソル静止の判定を確かめる（FR-MOD-10 / FR-MOD-11）。
    /// </summary>
    /// <remarks>
    /// <b>カーソルの移動に常時追従してはならない</b>（RULES.md 🟡-8）。
    /// 動いている間は起こさず、止まってから 1 回だけ起こすことを、時計を進めて確かめる。
    /// </remarks>
    private static int CheckDwell()
    {
        var failures = 0;

        void Check(string label, bool actual, bool expected)
        {
            var ok = actual == expected;
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "  " : "★ ")}{label,-44} {(actual ? "発動" : "しない"),-6}"
                              + $"（期待 {(expected ? "発動" : "しない")}）");
        }

        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var options = new CursorModeOptions
        {
            DwellTime = TimeSpan.FromMilliseconds(400),
            JitterTolerance = 4,
        };
        var detector = new CursorDwellDetector(options, clock);

        Console.WriteLine("=== カーソル静止の判定（FR-MOD-10 / 11）===");

        Check("最初の 1 回は数え始めるだけ", detector.Update(new ScreenPoint(100, 100)), false);

        clock.Advance(TimeSpan.FromMilliseconds(399));
        Check("400ms に満たなければ起こさない", detector.Update(new ScreenPoint(100, 100)), false);

        clock.Advance(TimeSpan.FromMilliseconds(2));
        Check("400ms を超えたら起こす", detector.Update(new ScreenPoint(100, 100)), true);

        clock.Advance(TimeSpan.FromSeconds(10));
        Check("止まり続けても 2 度は起こさない", detector.Update(new ScreenPoint(100, 100)), false);

        Check("手ぶれ（3px）は動いたと見ない", detector.Update(new ScreenPoint(102, 102)), false);

        clock.Advance(TimeSpan.FromSeconds(10));
        Check("手ぶれのままでも起こし直さない", detector.Update(new ScreenPoint(101, 101)), false);

        Console.WriteLine();
        Console.WriteLine("=== 動かしてから止め直す ===");
        Check("動かした直後は起こさない", detector.Update(new ScreenPoint(400, 400)), false);

        clock.Advance(TimeSpan.FromMilliseconds(401));
        Check("止め直せば起こす", detector.Update(new ScreenPoint(400, 400)), true);

        Console.WriteLine();
        Console.WriteLine("=== 動かし続ける（常時追従しない）===");
        var moving = new CursorDwellDetector(options, clock);
        var firedWhileMoving = false;
        for (var i = 0; i < 50; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            firedWhileMoving |= moving.Update(new ScreenPoint(i * 20, i * 20));
        }

        Check("5 秒動かし続けても一度も起こさない", firedWhileMoving, false);

        Console.WriteLine();
        Console.WriteLine("=== 切り替え直後 ===");
        detector.Reset();
        Check("Reset の直後は起こさない", detector.Update(new ScreenPoint(400, 400)), false);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
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
        Console.WriteLine("=== 初回の案内（FR-MOD-08）===");

        void CheckText(string label, bool ok, string detail = "")
        {
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "  " : "★ ")}{label,-40} {(ok ? "OK" : "NG"),-4}{detail}");
        }

        var noticeOptions = new AutoWatchOptions();
        var whole = AutoWatchNotice.Build(noticeOptions, hasRegion: false);
        var narrowed = AutoWatchNotice.Build(noticeOptions, hasRegion: true);

        // 文面の役目は「どれだけ時間と呼び出しを使うか」を渡すこと。
        // 数字が消えていたら案内の意味がない。
        CheckText(
            "上限の回数が文面に出る",
            whole.Contains(noticeOptions.MaxCallsPerHour.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal));
        CheckText("見回りの間隔が文面に出る", whole.Contains("0.5 秒ごと", StringComparison.Ordinal));
        CheckText("やめる判断ができる文で終わる", whole.TrimEnd().EndsWith("入れますか？", StringComparison.Ordinal));
        CheckText("クラウドの遅さを断っている", whole.Contains("クラウド", StringComparison.Ordinal));

        // 絞っていない人には「絞れば半分以下」が一番効く。絞っている人には邪魔。
        CheckText("絞っていなければ絞る案内を出す", whole.Contains("範囲を絞ると", StringComparison.Ordinal));
        CheckText("絞っていれば出さない", !narrowed.Contains("範囲を絞ると", StringComparison.Ordinal));

        // 所要時間はぼかして出す（2026-10-13・人間の判断）。内訳や ms は利用者に要らない。
        CheckText("全画面の時間をぼかして出す", whole.Contains("1 秒程度", StringComparison.Ordinal));
        CheckText("絞った場合の時間をぼかして出す", narrowed.Contains("0.5 秒程度", StringComparison.Ordinal));
        CheckText(
            "内訳の数字を出さない",
            !whole.Contains("撮影", StringComparison.Ordinal) && !whole.Contains("ms", StringComparison.Ordinal)
            && !whole.Contains('+', StringComparison.Ordinal));

        var wide = AutoWatchNotice.Estimate(hasRegion: false);
        var tight = AutoWatchNotice.Estimate(hasRegion: true);
        CheckText(
            "範囲を絞った方が速い",
            tight.Typical < wide.Typical,
            $"← ふだん 約 {tight.Typical.TotalSeconds:0.##} 秒 対 {wide.Typical.TotalSeconds:0.##} 秒");

        // 性能要件は 1.5 秒（PLAN.md 課題4・2026-10-13 に解決）。全画面の最も遅い画面でも収まる。
        CheckText(
            "全画面の最も遅い画面でも 1.5 秒に収まる",
            wide.Worst <= TimeSpan.FromSeconds(1.5),
            $"← {wide.Worst.TotalSeconds:0.##} 秒");

        Console.WriteLine();
        Console.WriteLine("--- 絞っていないときの文面 ---");
        Console.WriteLine(whole);

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
