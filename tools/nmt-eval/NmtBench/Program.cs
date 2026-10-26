using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Glosslay.Ads;
using Glosslay.Configuration;
using Glosslay.Entitlement;
using Glosslay.Capture;
using Glosslay.Imaging;
using Glosslay.Input;
using Glosslay.Localization;
using Glosslay.Monitoring;
using Glosslay.Ocr;
using Glosslay.Profiles;
using Glosslay.Shell;
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
internal static partial class Program
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

        // --chinese: 中国語をローカルで訳す経路（中→英 / 中→英→日）を実際のモデルで確かめる。
        if (args.Contains("--chinese", StringComparer.Ordinal))
        {
            return await CheckChineseAsync(options).ConfigureAwait(false);
        }

        // --pairs: 2026-10-25 に足した組み合わせ（日→英 / 英→中 / 日→英→中）を実際のモデルで確かめる。
        if (args.Contains("--pairs", StringComparer.Ordinal))
        {
            return await CheckPairsAsync(options).ConfigureAwait(false);
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

        // --languages: メイン言語と原文の言語の扱い（FR-SYS-03 / FR-OCR-02 / FR-TRN-02・03）を確かめる。モデルを読まない。
        if (args.Contains("--languages", StringComparer.Ordinal))
        {
            return await CheckLanguagesAsync().ConfigureAwait(false);
        }

        // --ui: 画面の文言の言語（FR-SYS-03 / FR-SYS-05）を確かめる。ソースの文すべてに訳があるか。モデルを読まない。
        if (args.Contains("--ui", StringComparer.Ordinal))
        {
            return CheckUi();
        }

        // --timing: 処理時間の実測表示（FR-OVL-10）の数え方と文面を確かめる。モデルを読まない。
        if (args.Contains("--timing", StringComparer.Ordinal))
        {
            return CheckTiming();
        }

        // --tray: タスクトレイ常駐の設定と自動起動の登録（FR-SYS-02）を確かめる。モデルを読まない。
        // 本物の自動起動は書き換えない（検査用のキーに書いて最後に消す）。
        if (args.Contains("--tray", StringComparer.Ordinal))
        {
            return CheckTray();
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

        const LocalReadiness ready = LocalReadiness.Ready;
        const LocalReadiness noModel = LocalReadiness.ModelMissing;
        const LocalReadiness cloudOnlyPair = LocalReadiness.CloudOnlyPair;

        (string Label, TranslationBackendChoice Choice, Stub Cloud, LocalReadiness Readiness,
            string Active, string Fallback)[] cases =
        [
            ("自動・キーあり",                 TranslationBackendChoice.Auto,      cloud,      ready,         "クラウド", "ローカル"),
            ("自動・キーなし",                 TranslationBackendChoice.Auto,      cloudNoKey, ready,         "ローカル", "なし"),
            ("自動・モデルなし",               TranslationBackendChoice.Auto,      cloud,      noModel,       "クラウド", "なし"),
            ("自動・キーもモデルもなし",       TranslationBackendChoice.Auto,      cloudNoKey, noModel,       "ローカル", "なし"),
            ("クラウドのみ・キーあり",         TranslationBackendChoice.CloudOnly, cloud,      ready,         "クラウド", "なし"),
            ("クラウドのみ・キーなし",         TranslationBackendChoice.CloudOnly, cloudNoKey, ready,         "クラウド", "なし"),
            ("ローカルのみ・キーあり",         TranslationBackendChoice.LocalOnly, cloud,      ready,         "ローカル", "なし"),

            // 2026-10-25: 「クラウドのみ」の組み合わせ（韓国語が入るもの）。自動でもクラウドで、切り替えない。
            ("自動・キーあり・クラウドのみ組", TranslationBackendChoice.Auto,      cloud,      cloudOnlyPair, "クラウド", "なし"),
            ("自動・キーなし・クラウドのみ組", TranslationBackendChoice.Auto,      cloudNoKey, cloudOnlyPair, "クラウド", "なし"),

            // 「ローカルのみ」は外へ送らないことを選んだ人の選択。勝手にクラウドへ送らない（ローカルが理由を返す）。
            ("ローカルのみ・クラウドのみ組",   TranslationBackendChoice.LocalOnly, cloud,      cloudOnlyPair, "ローカル", "なし"),
        ];

        Console.WriteLine("=== バックエンドの選択 ===");
        Console.WriteLine($"  {"場合",-26} {"使う",-10} {"切替先",-10} 判定");

        var failures = 0;
        foreach (var (label, choice, c, readiness, wantActive, wantFallback) in cases)
        {
            var active = TranslatorSelection.Active(choice, c, local, readiness).Name;
            var fallback = TranslatorSelection.Fallback(choice, c, local, readiness)?.Name ?? "なし";
            var ok = active == wantActive && fallback == wantFallback;
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {label,-26} {active,-10} {fallback,-10} "
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
    /// メイン言語と原文の言語の扱いを確かめる（FR-SYS-03 / FR-OCR-02 / FR-TRN-02・03 / RULES.md 🟡-1）。
    /// </summary>
    /// <remarks>
    /// <para><b>一番危ないのは「訳せないのに成功して返る」こと。</b>
    /// ローカルのモデルに違う言語を入れると、<b>流暢な出鱈目が出て、しかも成功として返りうる</b>。
    /// 画面の表示だけで防ぐと、設定ファイルを手で書き換えた場合に素通りする。</para>
    /// <para><b>モデルを読まない。</b>存在しないモデル置き場を渡し、
    /// 組み合わせの判定がモデルの有無より<b>先に</b>働くことを使って確かめる。</para>
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

        Console.WriteLine("=== 選択肢（FR-SYS-03 / FR-OCR-02）===");
        Check("メイン言語の既定は日本語", Languages.MainOptions[0].Tag == Languages.DefaultMain && Languages.DefaultMain == "ja");
        Check(
            "メイン言語は 日本語 / English / 简体中文 / 한국어",
            string.Join("/", Languages.MainOptions.Select(o => o.DisplayName)) == "日本語/English/简体中文/한국어",
            $"← {string.Join("/", Languages.MainOptions.Select(o => o.DisplayName))}");
        Check("繁体字はメイン言語に無い（2026-10-24）", !Languages.MainOptions.Any(o => o.Tag.Contains("Hant", StringComparison.Ordinal)));
        Check("以前の zh-Hant は簡体字として読む", Languages.NormalizeMain("zh-Hant") == "zh-Hans");
        Check("メイン言語が空なら日本語", Languages.NormalizeMain(null) == "ja");
        Check("知らないメイン言語は捨てない", Languages.NormalizeMain("de") == "de");
        Check("原文の既定は英語", Languages.NormalizeSource(null, "ja") == "en" && Languages.NormalizeSource(null, "ko") == "en");
        Check("メイン言語が英語なら原文の既定は日本語", Languages.NormalizeSource(null, "en") == "ja");
        Check("原文がメイン言語と同じなら既定に戻す", Languages.NormalizeSource("en", "en") == "ja");
        Check("中国語の原文は簡体・繁体を問わない", Languages.NormalizeSource("zh-Hant", "ja") == "zh");

        Console.WriteLine();
        Console.WriteLine("=== 組み合わせの表（RULES.md 🟡-1）===");

        var local = PairAvailability.Local;
        var low = PairAvailability.LocalLowQuality;
        var cloudOnly = PairAvailability.CloudOnly;
        (string Source, string Main, PairAvailability Want)[] table =
        [
            ("en", "ja", local), ("zh", "ja", local), ("ko", "ja", cloudOnly),
            ("ja", "en", local), ("zh", "en", local), ("ko", "en", cloudOnly),
            ("ja", "zh-Hans", low), ("en", "zh-Hans", local), ("ko", "zh-Hans", cloudOnly),
            ("ja", "ko", cloudOnly), ("en", "ko", cloudOnly), ("zh", "ko", cloudOnly),
            ("ja", "ja", PairAvailability.Same), ("zh", "zh-Hans", PairAvailability.Same),
            ("en", "de", cloudOnly),
        ];

        foreach (var (source, main, want) in table)
        {
            var got = Languages.Availability(source, main);
            Check($"{Languages.PairName(source, main)} は {want}", got == want, got == want ? string.Empty : $"← {got}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 原文の言語の行か（違う言語の行は訳さない・クラウドにも送らない）===");
        Check("英語の行は英語", Languages.IsWrittenIn("Attack Power", "en") && !Languages.IsWrittenIn("Attack Power", "ja"));
        Check("かなの行は日本語で、中国語ではない", Languages.IsWrittenIn("ゲームを始める", "ja") && !Languages.IsWrittenIn("ゲームを始める", "zh"));
        Check("漢字だけの行は日本語でも中国語でもありうる", Languages.IsWrittenIn("設定", "ja") && Languages.IsWrittenIn("設定", "zh"));
        Check("簡体字の行は中国語で、英語ではない", Languages.IsWrittenIn("系统设置", "zh") && !Languages.IsWrittenIn("系统设置", "en"));
        Check("読み違いのカタカナ 1 文字なら中国語のまま", Languages.IsWrittenIn("攻击カ +25", "zh"));
        Check("かなが 2 文字以上なら中国語ではない", !Languages.IsWrittenIn("プログラムから開く", "zh"));
        Check("ハングルの行は韓国語で、日本語・中国語ではない",
            Languages.IsWrittenIn("공격력", "ko") && !Languages.IsWrittenIn("공격력", "ja") && !Languages.IsWrittenIn("공격력", "zh"));
        Check("英字まじりの韓国語の行も韓国語", Languages.IsWrittenIn("[길드] Player01: ㅋㅋㅋ 고마워요", "ko"));
        Check("韓国語のゲームの英語の行は韓国語ではない", !Languages.IsWrittenIn("HP 1,250 / MP 300", "ko"));
        Check("空行はどの言語でもない", !Languages.IsWrittenIn("  ", "en"));

        Console.WriteLine();
        Console.WriteLine("=== ローカルが断るか（FR-TRN-02 / 03）===");

        // 実在しない置き場。モデルを読まずに判定の順序だけを見る。
        var nowhere = Path.Combine(Path.GetTempPath(), "glosslay-no-such-model");
        var noModel = new LocalNmtOptions
        {
            ModelDirectory = nowhere,
            ChineseModelDirectory = nowhere,
            JapaneseToEnglishModelDirectory = nowhere,
            EnglishToChineseModelDirectory = nowhere,
        };

        using var translator = new LocalNmtTranslator(noModel);

        Task<TranslationResult> Ask(string text, string? source, string main) =>
            translator.TranslateAsync(new TranslationRequest(text) { SourceLanguage = source, TargetLanguage = main });

        var englishToJapanese = await Ask("Attack Power", "en", "ja").ConfigureAwait(false);
        var japaneseToKorean = await Ask("ダメージ", "ja", "ko").ConfigureAwait(false);
        var koreanToJapanese = await Ask("공격력", "ko", "ja").ConfigureAwait(false);
        var same = await Ask("ダメージ", "ja", "ja").ConfigureAwait(false);
        var noSource = await Ask("Attack Power", null, "ja").ConfigureAwait(false);
        var chineseToEnglish = await Ask("承让了！", "zh", "en").ConfigureAwait(false);

        Check(
            "ローカルの組み合わせは、モデルが無いとそう言って止まる",
            englishToJapanese.Outcome == TranslationOutcome.NotConfigured
            && englishToJapanese.Message?.Contains("英語→日本語", StringComparison.Ordinal) == true,
            $"← {englishToJapanese.Outcome} {englishToJapanese.Message}");
        Check(
            "韓国語へはモデルの有無より先に断る",
            japaneseToKorean.Outcome == TranslationOutcome.Unsupported
            && japaneseToKorean.Message?.Contains("クラウド翻訳でのみ", StringComparison.Ordinal) == true,
            $"← {japaneseToKorean.Outcome} {japaneseToKorean.Message}");
        Check("韓国語の原文も断る", koreanToJapanese.Outcome == TranslationOutcome.Unsupported, $"← {koreanToJapanese.Outcome}");
        Check("原文とメイン言語が同じなら訳さない", same.Outcome == TranslationOutcome.Unsupported, $"← {same.Outcome}");
        Check("原文の言語が無ければ訳さない", noSource.Outcome == TranslationOutcome.Unsupported, $"← {noSource.Outcome}");
        Check(
            "中→英のモデルが無ければ、そう言って止まる",
            chineseToEnglish.Outcome == TranslationOutcome.NotConfigured
            && chineseToEnglish.Message?.Contains("中国語→英語", StringComparison.Ordinal) == true,
            $"← {chineseToEnglish.Message}");

        // FR-TRN-06: 対応していない組み合わせでは切り替えない。切り替えても同じ結果になる。
        Check("対応しない組み合わせでは切り替えない", !japaneseToKorean.ShouldFallBack);
        Check(
            "クラウドのみの組み合わせは、モデルがあってもローカルでは訳せない扱い",
            translator.ReadinessFor("ja", "ko") == LocalReadiness.CloudOnlyPair
            && translator.ReadinessFor("en", "ja") == LocalReadiness.ModelMissing);

        Console.WriteLine();
        Console.WriteLine("=== 行ごとの経路（LocalNmtRoutes・モデルを読まない）===");

        LocalNmtRoute[] Plan(string source, string main, params string[] lines) => LocalNmtRoutes.Plan(lines, source, main);

        Check("英語 → 日本語は英→日", Plan("en", "ja", "Attack Power")[0] == LocalNmtRoute.EnglishToJapanese);
        Check("中国語 → 日本語は中→英→日", Plan("zh", "ja", "承让了！")[0] == LocalNmtRoute.ChineseToJapanese);
        Check("中国語 → 英語は中→英", Plan("zh", "en", "承让了！")[0] == LocalNmtRoute.ChineseToEnglish);
        Check("日本語 → 英語は日→英", Plan("ja", "en", "ダメージを与える")[0] == LocalNmtRoute.JapaneseToEnglish);
        Check("英語 → 中国語は英→中", Plan("en", "zh-Hans", "Attack Power")[0] == LocalNmtRoute.EnglishToChinese);
        Check("日本語 → 中国語は日→英→中", Plan("ja", "zh-Hans", "ダメージを与える")[0] == LocalNmtRoute.JapaneseToChinese);
        Check("韓国語が入る組み合わせは訳さない",
            Plan("ko", "ja", "공격력")[0] == LocalNmtRoute.None && Plan("ja", "ko", "ダメージ")[0] == LocalNmtRoute.None);
        Check(
            "原文の言語と違う行は訳さない（英語のゲームの日本語の行）",
            Plan("en", "ja", "Attack Power", "ゲームを始める")[1] == LocalNmtRoute.None);
        Check("原文が中国語なら漢字だけの行は中国語として訳す", Plan("zh", "ja", "設定")[0] == LocalNmtRoute.ChineseToJapanese);
        Check("原文が日本語なら漢字だけの行は日本語として訳す", Plan("ja", "en", "設定")[0] == LocalNmtRoute.JapaneseToEnglish);
        Check("中国語の画面のかなの行は訳さない", Plan("zh", "ja", "系统设置", "プログラムから開く")[1] == LocalNmtRoute.None);
        Check("空行は訳さない", Plan("en", "ja", "  ")[0] == LocalNmtRoute.None);
        Check(
            "日→英→中は日→英と英→中のモデルを使う",
            LocalNmtRoutes.ModelsOf(LocalNmtRoute.JapaneseToChinese) == (LocalNmtModel.JapaneseToEnglish, LocalNmtModel.EnglishToChinese));

        var split = LocalNmtRoutes.SplitChineseSentences("我没法捡起道具！请按下Q键。或使用鼠标滚轮？");
        Check("中国語は句点で区切る", split.Count == 3, $"← {string.Join(" | ", split)}");

        var english = LocalNmtRoutes.SplitEnglishSentences("Cooldown: 1.5 seconds. Lv.12 required! You'll be better.");
        Check("英語は文で区切り、小数点や Lv. では切らない", english.Count == 3, $"← {string.Join(" | ", english)}");
        Check(
            "区切るのは文を落とすモデル（中→英・英→中）だけ",
            LocalNmtRoutes.SentencesFor(LocalNmtModel.EnglishToJapanese, "A. B.").Count == 1
            && LocalNmtRoutes.SentencesFor(LocalNmtModel.JapaneseToEnglish, "あ。い。").Count == 1
            && LocalNmtRoutes.SentencesFor(LocalNmtModel.EnglishToChinese, "A. B. C.").Count == 3);
        Check(
            "英→中は 2 文までなら区切らない（区切ると繰り返しが出る）",
            LocalNmtRoutes.SentencesFor(LocalNmtModel.EnglishToChinese, "Your backpack is full. You cannot carry more.").Count == 1);

        Console.WriteLine();
        Console.WriteLine("=== 違う言語の行はクラウドにも送らない（LineTranslation）===");

        // クラウド（LLM）と同じ形で依頼を受け取る。依頼文の中身を見るため。
        using var echo = new EchoTranslator(lineByLine: false);
        OcrLine[] screen =
        [
            new("系统设置", 1f, new OcrBox(0, 0, 100, 20)),
            new("New Game", 1f, new OcrBox(0, 30, 100, 20)),
            new("30/100", 1f, new OcrBox(0, 60, 100, 20)),
        ];
        var sent = await LineTranslation
            .TranslateAsync(echo, screen, targetLanguage: "ja", sourceLanguage: "zh")
            .ConfigureAwait(false);
        Check(
            "中国語のゲームの英語の行は送らない",
            sent.Statuses[1] == LineTranslationStatus.OtherLanguage
            && echo.LastRequest?.Text.Contains("New Game", StringComparison.Ordinal) == false,
            $"← {string.Join(" / ", sent.Statuses)}");
        Check("数値だけの行は今までどおり送らない", sent.Statuses[2] == LineTranslationStatus.Skipped);
        Check("送った数・違う言語の数を数える", sent.SentCount == 1 && sent.OtherLanguageCount == 1, $"← {sent.SentCount} / {sent.OtherLanguageCount}");
        Check(
            "クラウドへの依頼に原文の言語を書く",
            echo.LastRequest?.Text.Contains("中国語から日本語へ", StringComparison.Ordinal) == true
            && echo.LastRequest?.SourceLanguage == "zh",
            $"← {echo.LastRequest?.Text.Split('\n')[0]}");

        var toChinese = LineTranslation.BuildRequestText(["Attack Power"], "en", "zh-Hans");
        Check(
            "訳文の中国語は簡体字と書く・日本語に決め打ちしない",
            toChinese.Contains("英語から中国語（簡体字）へ", StringComparison.Ordinal)
            && toChinese.Contains("自然な中国語（簡体字）", StringComparison.Ordinal)
            && !toChinese.Contains("自然な日本語", StringComparison.Ordinal),
            $"← {toChinese.Split('\n')[0]}");

        Console.WriteLine();
        Console.WriteLine("=== 行結合（韓国語は語を空白で区切る）===");

        var korean = LineJoiner.Join(
        [
            new("길드에 가입하면 매일 길드 출석 보상을 받을 수", 1f, new OcrBox(0, 0, 400, 20)),
            new("있습니다.", 1f, new OcrBox(0, 24, 90, 20)),
        ]);
        Check(
            "韓国語の折り返しは空白を入れて繋ぐ",
            korean.Count == 1 && korean[0].Text.EndsWith("받을 수 있습니다.", StringComparison.Ordinal),
            $"← {string.Join(" | ", korean.Select(l => l.Text))}");

        var japanese = LineJoiner.Join(
        [
            new("毎ターン最大体力の5%を回復する。その間、防御力が", 1f, new OcrBox(0, 0, 400, 20)),
            new("上昇する。", 1f, new OcrBox(0, 24, 90, 20)),
        ]);
        Check(
            "日本語の折り返しは今までどおり詰めて繋ぐ",
            japanese.Count == 1 && japanese[0].Text.EndsWith("防御力が上昇する。", StringComparison.Ordinal),
            $"← {string.Join(" | ", japanese.Select(l => l.Text))}");

        Console.WriteLine();
        Console.WriteLine("=== 行結合（大文字の略語が混ざる文は見出しにしない）===");

        // 2026-10-25 に見つけた形。「体力」なら繋がり、「HP」だと見出し扱いで繋がらなかった。
        var japaneseHp = LineJoiner.Join(
        [
            new("毎ターン最大HPの5%を回復する。その間、防御力が", 1f, new OcrBox(0, 0, 400, 20)),
            new("上昇する。", 1f, new OcrBox(0, 24, 90, 20)),
        ]);
        Check(
            "日本語の文に HP が入っても繋ぐ",
            japaneseHp.Count == 1 && japaneseHp[0].Text.EndsWith("防御力が上昇する。", StringComparison.Ordinal),
            $"← {string.Join(" | ", japaneseHp.Select(l => l.Text))}");

        var chineseHp = LineJoiner.Join(
        [
            new("每回合恢复最大HP的5%，同时防御力", 1f, new OcrBox(0, 0, 400, 20)),
            new("提升。", 1f, new OcrBox(0, 24, 60, 20)),
        ]);
        Check(
            "中国語の文に HP が入っても繋ぐ",
            chineseHp.Count == 1 && chineseHp[0].Text.EndsWith("防御力提升。", StringComparison.Ordinal),
            $"← {string.Join(" | ", chineseHp.Select(l => l.Text))}");

        var koreanHp = LineJoiner.Join(
        [
            new("매 턴 최대 HP의 5%를 회복합니다. 그동안 방어력이", 1f, new OcrBox(0, 0, 400, 20)),
            new("상승합니다.", 1f, new OcrBox(0, 24, 100, 20)),
        ]);
        Check(
            "韓国語の文に HP が入っても繋ぐ",
            koreanHp.Count == 1 && koreanHp[0].Text.EndsWith("방어력이 상승합니다.", StringComparison.Ordinal),
            $"← {string.Join(" | ", koreanHp.Select(l => l.Text))}");

        var heading = LineJoiner.Join(
        [
            new("DAILY QUEST", 1f, new OcrBox(0, 0, 120, 20)),
            new("Attach 1 Tool to your Pokémon", 1f, new OcrBox(0, 24, 300, 20)),
        ]);
        Check(
            "英語の見出しは今までどおり下の行と繋がない",
            heading.Count == 2 && heading[0].Text == "DAILY QUEST",
            $"← {string.Join(" | ", heading.Select(l => l.Text))}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>依頼をそのまま覚えて、原文を返すバックエンド。送ったものを確かめるためだけに使う。</summary>
    private sealed class EchoTranslator(bool lineByLine) : ITranslator
    {
        public TranslationRequest? LastRequest { get; private set; }

        public string Name => "検査用（そのまま返す）";

        public bool IsConfigured => true;

        public bool TranslatesLineByLine => lineByLine;

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new TranslationResult(request.Text, TranslationOutcome.Succeeded, TimeSpan.Zero));
        }

        public void Dispose()
        {
        }
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
    /// <summary>
    /// タスクトレイ常駐の設定と自動起動の登録を確かめる（FR-SYS-02）。
    /// </summary>
    /// <remarks>
    /// <b>本物の自動起動は書き換えない。</b>登録先は検査用のキー
    /// （<c>HKEY_CURRENT_USER\Software\Glosslay.BenchTest</c>）で、最後に消す。
    /// 設定ファイルも書かない（文字列にして読み戻すだけ）。
    /// </remarks>
    private static int CheckTray()
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

        Console.WriteLine("=== × の動き（tray.json）===");
        var defaults = new TrayOptions();
        Check("既定はトレイにしまう", defaults.CloseBehavior == CloseBehavior.HideToTray);
        Check("通知はまだ出していない", !defaults.HideNoticeShown);

        var json = JsonSettings.Serialize(defaults with { CloseBehavior = CloseBehavior.Exit });
        Check("動きは名前で書き出す（手で直せる）", json.Contains("\"Exit\"", StringComparison.Ordinal), $"← {json.ReplaceLineEndings(" ")}");
        var back = JsonSettings.Deserialize<TrayOptions>(json);
        Check("読み戻すと同じ", back?.CloseBehavior == CloseBehavior.Exit);

        Console.WriteLine();
        Console.WriteLine("=== 自動起動（検査用のキーに書く）===");
        const string testRoot = @"Software\Glosslay.BenchTest";
        var autoStart = new AutoStart(testRoot + @"\Run", "GlosslayTest");
        var exe = @"C:\Program Files Example\Glosslay\Glosslay.exe";
        try
        {
            Check("最初は登録されていない", !autoStart.IsEnabled);

            autoStart.Set(true, exe);
            Check("登録すると入る", autoStart.IsEnabled);
            Check("いまの実行ファイルを指す", autoStart.PointsTo(exe));
            Check("別の場所は指さない", !autoStart.PointsTo(@"D:\Other\Glosslay.exe"));

            var command = AutoStart.CommandFor(exe);
            Check("空白のあるパスを引用符で囲む", command.StartsWith("\"C:\\Program Files Example", StringComparison.Ordinal), $"← {command}");
            Check("トレイで待機する引数を付ける", command.EndsWith(" " + AutoStart.TrayArgument, StringComparison.Ordinal));

            autoStart.Set(true, exe);
            Check("2 回入れても 1 つのまま", autoStart.IsEnabled);

            autoStart.Set(false, exe);
            Check("外すと消える", !autoStart.IsEnabled);

            autoStart.Set(false, exe);
            Check("無いものを外しても落ちない", !autoStart.IsEnabled);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
        }

        using (var leftover = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testRoot))
        {
            Check("検査用のキーを片付けた", leftover is null);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 画面の文言の言語（FR-SYS-03 / FR-SYS-05）を確かめる。モデルを読まない。
    /// </summary>
    /// <remarks>
    /// <para><b>日本語の文がそのまま鍵なので、コードの文を直すと訳が黙って外れる</b>
    /// （日本語のまま出るだけで、例外にはならない）。ソースから <c>T("…")</c> と
    /// <c>{local:T '…'}</c> を拾い、どの言語の表にも訳があるかを見る。</para>
    /// <para><b>訳文の <c>{0}</c> が鍵と食い違うと、その文を出したときに落ちる</b>
    /// （<see cref="FormatException"/>）。差し込み箇所が揃っているかと、実際に差し込めるかも見る。</para>
    /// </remarks>
    private static int CheckUi()
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

        static string Head(IReadOnlyList<string> items) =>
            string.Join(" / ", items.Take(3)) + (items.Count > 3 ? $" ほか {items.Count - 3} 件" : string.Empty);

        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Glosslay.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        if (root is null)
        {
            Console.WriteLine("リポジトリが見つかりません（Glosslay.slnx を上にたどって探す）。");
            return 1;
        }

        var interpolated = new List<string>();
        var continued = new HashSet<string>(StringComparer.Ordinal);
        var used = CollectUiKeys(Path.Combine(root, "src"), interpolated, continued);

        // 変数を通して引く文。ソースを読んでも拾えない。
        // ホットキーの名前と説明（RawInputTrigger）・認識モデルの名前（PaddleOcrEngine.Name）・言語の呼び名（Languages.NameOf）。
        used.UnionWith(
        [
            "翻訳キー", "前面の画面を翻訳して、原文の位置に重ねる",
            "カーソル翻訳キー", "カーソルの周囲だけを翻訳する",
            "ON/OFF キー", "重ねた訳文を消す / 出し直す",
        ]);
        foreach (var tag in new[] { "ja", "en", "zh", "zh-Hans", "ko" })
        {
            used.Add(PaddleOcrOptions.ForSourceLanguage(tag).RecognitionModelName);
            used.Add(Languages.PromptNameOf(tag));
        }

        Console.WriteLine("=== 鍵（ソースの T(\"…\") と {local:T '…'}・変数で引く文）===");
        Console.WriteLine($"  {used.Count} 件");
        Check("差し込み済みの文を鍵にしていない（T($\"…\")）", interpolated.Count == 0, interpolated.Count == 0 ? string.Empty : $"← {Head(interpolated)}");

        foreach (var tag in UiText.Supported.Where(t => t != "ja"))
        {
            Console.WriteLine();
            Console.WriteLine($"=== {tag} ===");

            var table = UiText.TableFor(tag);
            var missing = used.Where(k => !table.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
            var stale = table.Keys.Where(k => !used.Contains(k)).Order(StringComparer.Ordinal).ToList();
            var empty = table.Where(p => p.Value.Length == 0).Select(p => p.Key).ToList();
            var kana = table.Where(p => JapaneseKana().IsMatch(p.Value)).Select(p => p.Value).ToList();
            var mismatched = table.Where(p => !SamePlaceholders(p.Key, p.Value)).Select(p => p.Value).ToList();
            var broken = table.Where(p => !Formats(p.Value)).Select(p => p.Value).ToList();

            Check("ソースの文すべてに訳がある", missing.Count == 0, missing.Count == 0 ? $"{table.Count} 件" : $"← 無い {missing.Count} 件: {Head(missing)}");
            Check("使われていない訳が無い（文を直した後の残り）", stale.Count == 0, stale.Count == 0 ? string.Empty : $"← {Head(stale)}");
            Check("空の訳が無い", empty.Count == 0, empty.Count == 0 ? string.Empty : $"← {Head(empty)}");
            Check("訳にかな・中黒「・」が残っていない", kana.Count == 0, kana.Count == 0 ? string.Empty : $"← {Head(kana)}");
            Check("差し込み箇所 {0} が鍵と同じ", mismatched.Count == 0, mismatched.Count == 0 ? string.Empty : $"← {Head(mismatched)}");
            Check("差し込んでも落ちない", broken.Count == 0, broken.Count == 0 ? string.Empty : $"← {Head(broken)}");

            if (tag == "en")
            {
                var wide = table.Where(p => FullWidth().IsMatch(p.Value)).Select(p => p.Value).ToList();
                Check("英語の訳に漢字・全角の記号が無い", wide.Count == 0, wide.Count == 0 ? string.Empty : $"← {Head(wide)}");
            }

            // 日本語・中国語は文を詰めてつなぐが、英語・韓国語は空白が要る（「….The source …」になる）。
            if (tag is "en" or "ko")
            {
                var glued = continued
                    .Where(k => table.TryGetValue(k, out var v) && !char.IsWhiteSpace(v.FirstOrDefault(' ')) && !GluedOnPurpose(k, tag))
                    .Select(k => table[k])
                    .Order(StringComparer.Ordinal)
                    .ToList();
                Check($"前の文につなぐ訳は空白で始まる（{continued.Count} 件）", glued.Count == 0, glued.Count == 0 ? string.Empty : $"← {Head(glued)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 切り替え ===");

        var changed = 0;
        void OnChanged(object? sender, EventArgs e) => changed++;
        UiText.LanguageChanged += OnChanged;
        try
        {
            UiText.SetLanguage("en");
            Check("英語にすると英語で出る", UiText.Language == "en" && UiText.T("設定") == "Settings", $"← {UiText.T("設定")}");
            Check("変わったと知らせる（画面が文言を引き直す）", changed == 1, $"← {changed} 回");
            UiText.SetLanguage("en");
            Check("同じ言語なら知らせない", changed == 1, $"← {changed} 回");
            Check("表に無い文は日本語のまま出す", UiText.T("表に無い文") == "表に無い文");
            Check("差し込みも訳文の側で", UiText.T("翻訳完了: {0:F0} ms", 812.4) == "Translation done: 812 ms", $"← {UiText.T("翻訳完了: {0:F0} ms", 812.4)}");
            Check("言語の呼び名は画面の言語で", Languages.PairName("ko", "en") == "Korean→English", $"← {Languages.PairName("ko", "en")}");

            var prompt = LineTranslation.BuildRequestText(["Attack Power"], "en", "ja");
            Check("クラウドへの指示文は日本語のまま", prompt.Contains("英語から日本語へ", StringComparison.Ordinal), $"← {prompt.Split('\n')[0]}");

            using (var trigger = new RawInputTrigger(new HotkeyOptions()))
            {
                var described = string.Join(" / ", trigger.Descriptions.Prepend(trigger.Name));
                Check("ホットキーの説明も画面の言語で", !FullWidth().IsMatch(described), $"← {described}");
            }

            var notice = AutoWatchNotice.Build(new AutoWatchOptions(), hasRegion: false);
            Check("自動監視の案内も画面の言語で", !FullWidth().IsMatch(notice), FullWidth().IsMatch(notice) ? $"← {notice.Split('\n')[0]}" : string.Empty);

            UiText.SetLanguage("ko");
            Check("韓国語", UiText.T("設定") == "설정" && Languages.NameOf("ja") == "일본어", $"← {UiText.T("設定")} / {Languages.NameOf("ja")}");
            Check("指示文の言語の呼び名は日本語のまま", Languages.PromptNameOf("ko") == "韓国語");

            UiText.SetLanguage("zh-Hant");
            Check("繁体字は簡体字の画面で出す（2026-10-24）", UiText.Language == "zh-Hans" && UiText.T("設定") == "设置", $"← {UiText.Language} / {UiText.T("設定")}");

            UiText.SetLanguage("de");
            Check("持っていない言語は英語で出す", UiText.Language == "en" && UiText.T("設定") == "Settings", $"← {UiText.Language}");

            UiText.SetLanguage("ja");
            Check("日本語に戻すと日本語", UiText.Language == "ja" && UiText.T("設定") == "設定", $"← {UiText.T("設定")}");
            Check("知らせた回数", changed == 5, $"← {changed} 回");
        }
        finally
        {
            UiText.LanguageChanged -= OnChanged;
            UiText.SetLanguage("ja");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 前の文につないでも空白で始めなくてよい鍵。
    /// </summary>
    /// <remarks>
    /// 前に来る文が空白・改行で終わっているもの（「文字 {0} / 」「…\n」「…, and 」）と、
    /// 韓国語で括弧を語に付けて書くもの。
    /// </remarks>
    private static bool GluedOnPurpose(string key, string tag) => key switch
    {
        "{0:F0} 秒で消える" or "{0}はクラウド翻訳でのみ訳せます" or "原文に無い数値が {0} 件 → {1} 件に増えます。" => true,
        "（{0}）。" => tag == "ko",
        _ => false,
    };

    /// <summary>ソースの <c>T("…")</c> と XAML の <c>{local:T '…'}</c> から鍵を集める。</summary>
    /// <param name="sourceRoot">src の場所。</param>
    /// <param name="interpolated">差し込んだ後の文を鍵にしている箇所。</param>
    /// <param name="continued">
    /// 前の文につなげて出す鍵（<c>a + T("…")</c> と <c>a + (c ? T("…") : …)</c>）。
    /// </param>
    private static HashSet<string> CollectUiKeys(string sourceRoot, List<string> interpolated, HashSet<string> continued)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories))
        {
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj"))
            {
                continue;
            }

            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // コメントの行は見ない（説明に T("…") と書いてある）。
                var text = string.Join('\n', File.ReadLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                keys.UnionWith(CsKey().Matches(text).Select(m => UnescapeCs(m.Groups[1].Value)));
                interpolated.AddRange(CsBuiltKey().Matches(text).Select(m => $"{Path.GetFileName(path)}: {m.Value}"));

                // 文字列の中の括弧で数え違えないよう、中身を伏せてから前を見る（長さは変えない）。
                var masked = CsLiteral().Replace(text, m => m.Value[0] + new string('x', m.Length - 2) + m.Value[^1]);
                continued.UnionWith(CsKey().Matches(text)
                    .Where(m => IsContinuation(masked, m.Index))
                    .Select(m => UnescapeCs(m.Groups[1].Value)));
            }
            else if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                // XML の文字参照を戻してから、マークアップ拡張の「\」を外す（WPF が読む順）。
                var text = WebUtility.HtmlDecode(File.ReadAllText(path));
                keys.UnionWith(XamlKey().Matches(text).Select(m => UnescapeXaml(m.Groups[1].Value)));
            }
        }

        return keys;
    }

    /// <summary>
    /// <paramref name="index"/> の <c>T(</c> が <c>+</c> の右か、<c>+ (c ? … : …)</c> の三項の中か。
    /// </summary>
    private static bool IsContinuation(string masked, int index)
    {
        static int Previous(string s, int i)
        {
            i--;
            while (i >= 0 && char.IsWhiteSpace(s[i]))
            {
                i--;
            }

            return i;
        }

        var at = Previous(masked, index);
        if (at < 0)
        {
            return false;
        }

        if (masked[at] == '+')
        {
            return true;
        }

        if (masked[at] is not ('?' or ':'))
        {
            return false;
        }

        // 囲んでいる ( まで戻り、その前が + かを見る。
        var depth = 0;
        for (var i = at; i >= 0; i--)
        {
            switch (masked[i])
            {
                case ')':
                    depth++;
                    break;
                case '(' when depth == 0:
                    var before = Previous(masked, i);
                    return before >= 0 && masked[before] == '+';
                case '(':
                    depth--;
                    break;
                case ';' or '{' or '}' when depth == 0:
                    return false;
            }
        }

        return false;
    }

    private static string UnescapeCs(string literal)
    {
        var builder = new StringBuilder(literal.Length);
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] != '\\' || i + 1 == literal.Length)
            {
                builder.Append(literal[i]);
                continue;
            }

            i++;
            builder.Append(literal[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '0' => '\0',
                var c => c,
            });
        }

        return builder.ToString();
    }

    private static string UnescapeXaml(string literal)
    {
        var builder = new StringBuilder(literal.Length);
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] == '\\' && i + 1 < literal.Length)
            {
                i++;
            }

            builder.Append(literal[i]);
        }

        return builder.ToString();
    }

    private static bool SamePlaceholders(string key, string translated) =>
        Placeholder().Matches(key).Select(m => m.Value).Order(StringComparer.Ordinal)
            .SequenceEqual(Placeholder().Matches(translated).Select(m => m.Value).Order(StringComparer.Ordinal));

    /// <summary>差し込み箇所があれば、数を揃えた値で実際に差し込んでみる。</summary>
    private static bool Formats(string translated)
    {
        var count = Placeholder().Matches(translated)
            .Select(m => int.Parse(m.Value.AsSpan(1, m.Value.IndexOfAny([',', ':', '}']) - 1), CultureInfo.InvariantCulture) + 1)
            .DefaultIfEmpty(0)
            .Max();
        if (count == 0)
        {
            return true;
        }

        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, translated, Enumerable.Repeat<object?>(1.5, count).ToArray());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])T\(\s*""((?:[^""\\\n]|\\.)*)""")]
    private static partial Regex CsKey();

    /// <summary><c>T($"…")</c> / <c>T(@"…")</c>。差し込んだ後の文は表のどの鍵とも一致しない。</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])T\(\s*[$@]+""")]
    private static partial Regex CsBuiltKey();

    /// <summary>文字列と文字のリテラル（逐語的文字列は使っていない）。</summary>
    [GeneratedRegex(@"""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)'")]
    private static partial Regex CsLiteral();

    [GeneratedRegex(@"\{local:T '((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"\{\d+(?:[,:][^{}]*)?\}")]
    private static partial Regex Placeholder();

    /// <summary>かなと中黒「・」（カタカナの区画）。</summary>
    [GeneratedRegex(@"\p{IsHiragana}|\p{IsKatakana}")]
    private static partial Regex JapaneseKana();

    [GeneratedRegex(@"\p{IsHiragana}|\p{IsKatakana}|\p{IsCJKUnifiedIdeographs}|\p{IsCJKSymbolsandPunctuation}|\p{IsHalfwidthandFullwidthForms}")]
    private static partial Regex FullWidth();

    /// <summary>
    /// 処理時間の実測表示（FR-OVL-10）の数え方と文面を確かめる。
    /// </summary>
    private static int CheckTiming()
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

        static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);

        Console.WriteLine("=== 処理時間の実測表示（FR-OVL-10）===");

        var stats = new ProcessingTimeStats(capacity: 3);
        Check("まだ無いときはそう言う", stats.OneLine == "処理時間: まだ翻訳していません", $"← {stats.OneLine}");

        stats.Record(Ms(500), Ms(200));
        stats.Record(Ms(600), Ms(300));
        stats.Record(Ms(700), Ms(400));
        Check("読み取りの平均", stats.ReadAverage == Ms(600));
        Check("読み取りの直近最大", stats.ReadRecentMax == Ms(700));
        Check("翻訳の平均", stats.TranslationAverage == Ms(300));
        Check(
            "承認された形の 1 行",
            stats.OneLine == "読み取り 平均 600ms / 直近最大 700ms ・ 翻訳 平均 300ms",
            $"← {stats.OneLine}");

        // 直近だけを見る。起動直後の遅い回が残り続けると、いまの速さが見えない。
        stats.Record(Ms(100), Ms(100));
        Check("古い回は外れる（直近 3 回）", stats.ReadCount == 3 && stats.ReadRecentMax == Ms(700));
        stats.Record(Ms(100), Ms(100));
        stats.Record(Ms(100), Ms(100));
        Check("遅い回が外れると最大も下がる", stats.ReadRecentMax == Ms(100));

        // 文字が無く翻訳しなかった回は、翻訳を 0 として数えない（平均が速く見えてしまう）。
        var noText = new ProcessingTimeStats();
        noText.Record(Ms(400), Ms(800));
        noText.Record(Ms(300), translation: null);
        Check("翻訳しなかった回は翻訳に数えない", noText.TranslationCount == 1 && noText.TranslationAverage == Ms(800));
        Check("読み取りは数える", noText.ReadCount == 2);

        var readOnly = new ProcessingTimeStats();
        readOnly.Record(Ms(450), translation: null);
        Check(
            "翻訳がまだ無ければ翻訳の欄を出さない",
            readOnly.OneLine == "読み取り 平均 450ms / 直近最大 450ms",
            $"← {readOnly.OneLine}");

        var defaults = new UsageOverlayOptions();
        Check("既定は出さない（録画・配信に映らない）", !defaults.ShowProcessingTime);
        Check("隅のウィンドウは、どちらかを出すときだけ要る", !defaults.AnyVisible
              && (defaults with { ShowProcessingTime = true }).AnyVisible
              && (defaults with { Enabled = true }).AnyVisible);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 中国語をローカルで訳す経路（中→英 / 中→英→日）を、実際のモデルで確かめる（2026-10-19 追加）。
    /// </summary>
    /// <remarks>
    /// <para>中→英のモデルは英→日のモデルの隣（<c>out/opus-zh-en/int8</c>）を見る。</para>
    /// <para><b>訳文の質は人が読んで判断する。</b>ここで機械的に見るのは、
    /// 数値が消えないこと・文が消えないこと・日本語のゲームの漢字だけの行を中国語として訳さないこと。</para>
    /// <para><b>Python と訳文が完全には一致しない</b>（2026-10-19。56 件中 8 件で、確率がほぼ同点の語が入れ替わる）。
    /// 分かち書きと手順は一致しており、ONNX Runtime の版の違い（製品 1.29.0 / Python 1.30.0）による計算の差と見ている。</para>
    /// </remarks>
    private static async Task<int> CheckChineseAsync(LocalNmtOptions baseOptions)
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

        var options = baseOptions with
        {
            ChineseModelDirectory = Path.GetFullPath(
                Path.Combine(baseOptions.ModelDirectory, "..", "..", "opus-zh-en", "int8")),
        };

        using var local = new LocalNmtTranslator(options);
        if (!local.HasModel(LocalNmtModel.ChineseToEnglish))
        {
            Console.WriteLine($"中→英のモデルがありません: {options.ChineseModelDirectory}");
            Console.WriteLine("tools/nmt-convert/convert.py Helsinki-NLP/opus-mt-zh-en out/opus-zh-en で変換してください。");
            return 1;
        }

        string[] chinese =
        [
            "承让了！你非常厉害，但是我更强！",
            "在手中持有斧子等道具的状态下，无法捡起地面上的物品。请按下Q键，或使用鼠标滚轮，切换到空手的状态吧。",
            "装备后，防御力提升15%，但移动速度降低10%。",
            "对目标造成120点伤害，并使其眩晕2秒。",
            "冷却时间：8秒",
            "系统设置",
            "你无法在战斗中使用此物品。",
        ];

        foreach (var target in new[] { "en", "ja" })
        {
            Console.WriteLine($"=== 中国語 → {Languages.NameOf(target)}（{(target == "ja" ? "中→英→日" : "中→英")}）===");
            var watch = Stopwatch.StartNew();
            var result = await local
                .TranslateAsync(new TranslationRequest(string.Join('\n', chinese)) { SourceLanguage = "zh", TargetLanguage = target })
                .ConfigureAwait(false);
            var elapsed = watch.Elapsed;
            var lines = result.Text.Split('\n');

            for (var i = 0; i < chinese.Length; i++)
            {
                Console.WriteLine($"  {chinese[i]}");
                Console.WriteLine($"    → {(i < lines.Length ? lines[i] : "（無し）")}");
            }

            Check("成功する", result.Outcome == TranslationOutcome.Succeeded, $"← {result.Outcome} {result.Message}");
            Check("行数が変わらない", lines.Length == chinese.Length);
            Check("どの行も原文のまま返さない", lines.Zip(chinese).All(p => p.First.Length > 0 && p.First != p.Second));

            var lost = chinese
                .Zip(lines)
                .SelectMany(p => NumberVerifier.Extract(p.First).Where(n => !p.Second.Contains(n, StringComparison.Ordinal)))
                .ToList();
            Check("数値が消えない", lost.Count == 0, lost.Count == 0 ? string.Empty : $"← 消えた: {string.Join(", ", lost)}");

            // 中→英は文が続くと途中を落とすため、区切ってから訳している。2 文目の内容が残っているかで見る。
            var second = lines.Length > 1 ? lines[1] : string.Empty;
            Check(
                "2 文続く行の後ろの文が残る",
                target == "en"
                    ? second.Contains("mouse", StringComparison.OrdinalIgnoreCase)
                    : second.Contains("マウス", StringComparison.Ordinal),
                $"← {second}");
            Console.WriteLine($"  {chinese.Length} 行 {elapsed.TotalMilliseconds:F0} ms（中→英のモデルの読み込みを含むことがある）");
            Console.WriteLine();
        }

        // 2026-10-25 から原文の言語を選ぶ。中国語のゲームの英語の行は訳さない（自動で見分けない）。
        Console.WriteLine("=== 混ざった画面（英語と中国語）・原文は中国語 → 日本語 ===");
        var mixed = await local
            .TranslateAsync(new TranslationRequest("New Game\n系统设置") { SourceLanguage = "zh", TargetLanguage = "ja" })
            .ConfigureAwait(false);
        var mixedLines = mixed.Text.Split('\n');
        Check("英語の行は訳さない（原文の言語と違う）", mixedLines.ElementAtOrDefault(0) == "New Game", $"← {mixedLines.ElementAtOrDefault(0)}");
        Check("中国語の行は中→英→日で訳す", mixedLines.Length == 2 && mixedLines[1] != "系统设置", $"← {mixedLines.ElementAtOrDefault(1)}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }

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
        CheckText(
            "実測の表示の場所を案内する（FR-MOD-08）",
            whole.Contains("処理時間を画面の隅に出す", StringComparison.Ordinal)
            && narrowed.Contains("処理時間を画面の隅に出す", StringComparison.Ordinal));

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

        var first = await LineTranslation
            .TranslateAsync(local, lines, cache: cache, sourceLanguage: "en").ConfigureAwait(false);
        var second = await LineTranslation
            .TranslateAsync(local, lines, cache: cache, sourceLanguage: "en").ConfigureAwait(false);

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

        OcrLine[] korean =
        [
            new("이 아이템은 거래할 수 없습니다.", 1f, new OcrBox(0, 0, 100, 20)),
        ];

        (string Label, TranslationOutcome Outcome, OcrLine[] Lines, string Source, bool ShouldSwitch)[] cases =
        [
            ("混雑（HTTP 503）", TranslationOutcome.Busy, english, "en", true),
            ("利用上限（HTTP 429）", TranslationOutcome.RateLimited, english, "en", true),
            ("応答なし", TranslationOutcome.Timeout, english, "en", true),
            ("キー未登録", TranslationOutcome.NotConfigured, english, "en", false),

            // 「クラウドのみ」の組み合わせ。製品では切り替え先自体を渡さない（TranslatorSelection）が、
            // 渡されてもローカルが断るので切り替わらない。
            ("混雑だが原文が韓国語", TranslationOutcome.Busy, korean, "ko", false),
        ];

        Console.WriteLine("=== FR-TRN-06 切り替えの確認 ===");
        var failures = 0;

        foreach (var (label, outcome, lines, source, shouldSwitch) in cases)
        {
            using var primary = new AlwaysFails(outcome);
            var result = await LineTranslation
                .TranslateAsync(primary, lines, local, sourceLanguage: source).ConfigureAwait(false);

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
        translator.TranslateAsync(new TranslationRequest(string.Join('\n', lines)) { SourceLanguage = "en", TargetLanguage = "ja" });

    /// <summary>
    /// 2026-10-25 に足した組み合わせ（日→英 / 英→中 / 日→英→中）を、実際のモデルで確かめる。
    /// </summary>
    /// <remarks>
    /// <para>モデルは英→日のモデルの隣（<c>out/elan-ja-en/int8</c>・<c>out/opus-en-zh/int8</c>）を見る。</para>
    /// <para><b>訳文の質は人が読んで判断する。</b>ここで機械的に見るのは、成功すること・行数が変わらないこと・
    /// 中国語が簡体字で出ること（訳先の印が効いていること）・違う言語の行を訳さないこと。
    /// 数値は数えて出すだけにする（英→中は 2 を「两」のように漢数字にすることがある。FR-TRN-08 が原文を併記する）。</para>
    /// </remarks>
    private static async Task<int> CheckPairsAsync(LocalNmtOptions baseOptions)
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

        var options = baseOptions with
        {
            JapaneseToEnglishModelDirectory = Path.GetFullPath(
                Path.Combine(baseOptions.ModelDirectory, "..", "..", "elan-ja-en", "int8")),
            EnglishToChineseModelDirectory = Path.GetFullPath(
                Path.Combine(baseOptions.ModelDirectory, "..", "..", "opus-en-zh", "int8")),
        };

        using var local = new LocalNmtTranslator(options);
        foreach (var model in new[] { LocalNmtModel.JapaneseToEnglish, LocalNmtModel.EnglishToChinese })
        {
            if (!local.HasModel(model))
            {
                Console.WriteLine($"モデルがありません: {options.DirectoryOf(model)}");
                Console.WriteLine("tools/nmt-convert/convert.py で変換してください（README の手順）。");
                return 1;
            }
        }

        string[] japanese =
        [
            "怪我はないか？",
            "毎ターン最大HPの5%を回復する。3ターン持続。",
            "装備すると防御力が15%上昇するが、移動速度が10%低下する。",
            "戦闘中はこのアイテムを使用できません。",
            "ずっと待っていたんだ。さあ、中に入って。",
            "ニューゲーム",
        ];

        string[] english =
        [
            "I can't pick up items!",
            "Your backpack is full. You cannot carry any more items.",
            "When equipped, defense increases by 15% but movement speed decreases by 10%.",
            "This item cannot be used during combat.",
            "I've been waiting for you for a long time. Come on in.",
            "System Settings",

            // 4 文続く段落。区切らずに訳すと後ろの 2 文が消えた（2026-10-25）。
            "I've opened the door to the adaptation chamber. Your printed body can borrow tricks from indigenous life. You'll feel better. You'll be better.",
        ];

        // 繁体字にしか無い字（よく出るもの）。簡体字で出るべきところに混ざっていないかを見る。
        const string traditionalOnly = "體讓劍戰鬥藥獲聲顯險離屬導裝備擊殺錢務選擇設儲說話雙應對龍門語國時會開關鍵網學習經驗寶點術傷魔獸們這個來無麼與當為過還進後間現發員動頭見長實";

        foreach (var (source, main, lines, label) in new[]
        {
            ("ja", "en", japanese, "日→英"),
            ("en", "zh-Hans", english, "英→中"),
            ("ja", "zh-Hans", japanese, "日→英→中（精度が大きく下がる組み合わせ）"),
        })
        {
            Console.WriteLine($"=== {label} ===");
            var watch = Stopwatch.StartNew();
            var result = await local
                .TranslateAsync(new TranslationRequest(string.Join('\n', lines)) { SourceLanguage = source, TargetLanguage = main })
                .ConfigureAwait(false);
            var elapsed = watch.Elapsed;
            var output = result.Text.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                Console.WriteLine($"  {lines[i]}");
                Console.WriteLine($"    → {(i < output.Length ? output[i] : "（無し）")}");
            }

            Check("成功する", result.Outcome == TranslationOutcome.Succeeded, $"← {result.Outcome} {result.Message}");
            Check("行数が変わらない", output.Length == lines.Length);
            Check("どの行も原文のまま返さない", output.Zip(lines).All(p => p.First.Length > 0 && p.First != p.Second));

            if (source == "en" && Languages.Family(main) == "zh")
            {
                // You'll feel better. You'll be better. の「好」が残っているか（区切らないと消える）。
                Check("長い段落の後ろの文が消えない", output.Length == lines.Length && output[^1].Contains('好'), $"← {output.LastOrDefault()}");
            }

            if (Languages.Family(main) == "zh")
            {
                Check("中国語で出る（訳先の印が効いている）", output.All(t => t.Any(c => c is >= '\u4E00' and <= '\u9FFF')));
                var traditional = output.Where(t => t.Any(traditionalOnly.Contains)).ToList();
                Check("繁体字が混ざらない", traditional.Count == 0, traditional.Count == 0 ? string.Empty : $"← {string.Join(" | ", traditional)}");
            }

            var lost = lines
                .Zip(output)
                .SelectMany(p => NumberVerifier.Extract(p.First).Where(n => !p.Second.Contains(n, StringComparison.Ordinal)))
                .ToList();
            Console.WriteLine($"  数値が消えた・変わった: {(lost.Count == 0 ? "なし" : string.Join(", ", lost))}（FR-TRN-08 が原文を併記する）");
            Console.WriteLine($"  {lines.Length} 行 {elapsed.TotalMilliseconds:F0} ms（モデルの読み込みを含むことがある）");
            Console.WriteLine();
        }

        Console.WriteLine("=== 違う言語の行は訳さない（原文は日本語 → 英語）===");
        var mixed = await local
            .TranslateAsync(new TranslationRequest("設定\nAttack Power\nゲームを始める") { SourceLanguage = "ja", TargetLanguage = "en" })
            .ConfigureAwait(false);
        var mixedLines = mixed.Text.Split('\n');
        Check("漢字だけの行は日本語として訳す", mixedLines.ElementAtOrDefault(0) is { } settings && settings != "設定", $"← {mixedLines.ElementAtOrDefault(0)}");
        Check("英語の行は訳さない", mixedLines.ElementAtOrDefault(1) == "Attack Power", $"← {mixedLines.ElementAtOrDefault(1)}");
        Check("かなの行は訳す", mixedLines.ElementAtOrDefault(2) is { } start && start != "ゲームを始める", $"← {mixedLines.ElementAtOrDefault(2)}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "すべて期待どおり。" : $"★ {failures} 件が期待と違う。");
        return failures == 0 ? 0 : 1;
    }
}
