using System.Diagnostics;
using System.Text;
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

    private static Task<TranslationResult> TranslateAsync(LocalNmtTranslator translator, string[] lines) =>
        translator.TranslateAsync(new TranslationRequest(string.Join('\n', lines)));
}
