using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Glosslay.Translation;

/// <summary>
/// Marian（Opus-MT 系）モデルの分割と id の対応づけ。
/// </summary>
/// <remarks>
/// <para><b>分割は <c>.spm</c>、id は <c>vocab.json</c> で引く。</b>
/// Marian は SentencePiece を「文字列を片に割る」ためだけに使い、
/// 片から id への対応は別の表で持っている。
/// <c>EncodeToIds</c> が返す id をそのまま使うと<b>まったく別の語</b>になり、
/// 実行時エラーにはならないまま訳文が壊れる。2026-09-26 に実機で確認した
/// （<c>tools/nmt-convert/README.md</c>）。</para>
/// <para><b>同梱する <c>.spm</c> は <c>bos_id</c> を書き換えてある。</b>
/// Marian は BOS を持たず <c>bos_id = -1</c> だが、
/// <c>Microsoft.ML.Tokenizers</c> は BOS の存在を前提にしており
/// <c>IndexOutOfRangeException</c> で落ちる。
/// <c>tools/nmt-convert/patch_spm.py</c> が <c>0</c> に書き換える。
/// <b>書き換えていない <c>.spm</c> を置くと読み込みで落ちる。</b></para>
/// </remarks>
public sealed class MarianTokenizer
{
    private readonly SentencePieceTokenizer _pieces;
    private readonly Dictionary<string, int> _toId;
    private readonly string[] _toPiece;

    private MarianTokenizer(SentencePieceTokenizer pieces, Dictionary<string, int> toId, string[] toPiece)
    {
        _pieces = pieces;
        _toId = toId;
        _toPiece = toPiece;
    }

    /// <summary>語彙数。モデルの <c>vocab_size</c> と一致するはず。</summary>
    public int VocabularySize => _toPiece.Length;

    /// <summary>終端記号 <c>&lt;/s&gt;</c> の id。</summary>
    public int EndOfSequenceId { get; private init; }

    /// <summary>詰め物 <c>&lt;pad&gt;</c> の id。復号の開始記号も兼ねる。</summary>
    public int PadId { get; private init; }

    /// <summary>未知語 <c>&lt;unk&gt;</c> の id。</summary>
    public int UnknownId { get; private init; }

    /// <summary>
    /// <c>.spm</c> と <c>vocab.json</c> から読み込む。
    /// </summary>
    public static MarianTokenizer Load(string sentencePiecePath, string vocabularyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sentencePiecePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabularyPath);

        var toId = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(vocabularyPath, Encoding.UTF8))
            ?? throw new InvalidDataException($"語彙が読めません: {vocabularyPath}");

        if (toId.Count == 0)
        {
            throw new InvalidDataException($"語彙が空です: {vocabularyPath}");
        }

        var toPiece = new string[toId.Values.Max() + 1];
        foreach (var (piece, id) in toId)
        {
            // 同じ id に複数の片が割り当てられていることがある。先に来たものを採る。
            toPiece[id] ??= piece;
        }

        using var stream = File.OpenRead(sentencePiecePath);

        SentencePieceTokenizer pieces;
        try
        {
            // addEndOfSentence: true — Marian は原文の末尾に </s> を付けて符号化する。
            // addBeginningOfSentence: false — Marian に BOS は無い。
            pieces = SentencePieceTokenizer.Create(
                stream, addBeginningOfSentence: false, addEndOfSentence: true);
        }
        catch (IndexOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(sentencePiecePath)} を読み込めません。"
                + " Marian の .spm は bos_id が -1 のため、そのままでは扱えません。"
                + " tools/nmt-convert/patch_spm.py で 0 に書き換えたものを置いてください。",
                exception);
        }

        return new MarianTokenizer(pieces, toId, toPiece)
        {
            EndOfSequenceId = toId.GetValueOrDefault("</s>", 0),
            PadId = toId.GetValueOrDefault("<pad>", toId.Count - 1),
            UnknownId = toId.GetValueOrDefault("<unk>", 1),
        };
    }

    /// <summary>語彙にある片の id。無ければ <c>null</c>。</summary>
    public int? IdOf(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        return _toId.TryGetValue(piece, out var id) ? id : null;
    }

    /// <summary>
    /// 原文を id 列にする。末尾に <c>&lt;/s&gt;</c> が付く。
    /// </summary>
    /// <param name="text">原文。</param>
    /// <param name="maxTokens">上限。超えた分は切り捨て、末尾に <c>&lt;/s&gt;</c> を残す。</param>
    /// <param name="prefixId">
    /// 頭に付ける id（英→中の <c>&gt;&gt;cmn_Hans&lt;&lt;</c> など）。<c>null</c> なら付けない。
    /// </param>
    /// <remarks>
    /// <b>訳先の印は 1 つの片として付ける。</b>文字列のまま <c>.spm</c> に通すと
    /// <c>&gt;&gt;</c> や <c>cmn</c> に割れて、ただの記号として扱われる。
    /// Python 側（transformers の MarianTokenizer）も印を切り出してから残りを分割している。
    /// </remarks>
    public int[] Encode(string text, int maxTokens, int? prefixId = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 2);

        var tokens = _pieces.EncodeToTokens(text, out _);
        var ids = new List<int>(tokens.Count + 1);

        if (prefixId is { } prefix)
        {
            ids.Add(prefix);
        }

        foreach (var token in tokens)
        {
            // 片から id を引く。vocab.json に無い片は <unk>。
            ids.Add(_toId.TryGetValue(token.Value, out var id) ? id : UnknownId);
        }

        if (ids.Count > maxTokens)
        {
            ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
            ids.Add(EndOfSequenceId);
        }

        return [.. ids];
    }

    /// <summary>
    /// id 列を文字列に戻す。
    /// </summary>
    /// <remarks>
    /// <para>SentencePiece の <c>▁</c>（U+2581）が語の区切り。これを空白に戻して繋ぐ。
    /// 日本語には <c>▁</c> がほとんど現れないため、そのまま繋がる。</para>
    /// <para>特殊記号（<c>&lt;/s&gt;</c> <c>&lt;pad&gt;</c>）は落とす。
    /// <c>&lt;unk&gt;</c> は<b>残す</b> — 読めなかったことを隠すと、
    /// 訳文が自然に見えたまま情報が欠ける。</para>
    /// </remarks>
    public string Decode(IReadOnlyList<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var builder = new StringBuilder();
        foreach (var id in ids)
        {
            // <unk> も出さない。訳文に「<unk>」の文字がそのまま重なっていた（2026-10-19、Elin のヘルプ画面）。
            // Python 側（skip_special_tokens）も落としており、揃える。
            if (id == EndOfSequenceId || id == PadId || id == UnknownId || id < 0 || id >= _toPiece.Length)
            {
                continue;
            }

            var piece = _toPiece[id];
            if (piece is null)
            {
                continue;
            }

            if (piece.StartsWith(WordBoundary, StringComparison.Ordinal))
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(piece.AsSpan(1));
            }
            else
            {
                builder.Append(piece);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>SentencePiece が語の先頭に置く記号（U+2581 LOWER ONE EIGHTH BLOCK）。</summary>
    private const string WordBoundary = "▁";
}
