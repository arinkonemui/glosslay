namespace Glosslay.Translation;

/// <summary>
/// 同じ原文の訳文を使い回す（FR-TRN-09 / RULES.md 🟡-8）。
/// </summary>
/// <remarks>
/// <para><b>同じ画面でホットキーを押し直したときに、呼び出しも待ち時間もゼロにするためのもの。</b>
/// ゲーム中は押し直しが起きやすい（訳文が消えた・読み落とした・画面が変わったと思った）。
/// そのたびに課金と数百 ms を払うのは避ける。</para>
/// <para><b>行ではなくブロック単位で持つ。</b>画面が少し変わっても、変わらなかった段落は
/// そのまま使える。全文を鍵にすると 1 行変わっただけで総入れ替えになり、ほとんど当たらない。</para>
///
/// <para><b>鍵にバックエンド名を含める。</b>含めないと、クラウドが混雑してローカルへ切り替えた
/// ときの訳文（FR-TRN-06）が、クラウドが復帰した後も返り続ける。
/// 利用者から見ると「キーを登録したのに訳が良くならない」状態になる。</para>
/// <para><b>失敗は覚えない。</b>混雑や通信不良は次に成功しうる。</para>
///
/// <para>メモリ上だけに持ち、終了すると消える。1 回の遊びの間に同じ画面を見直す、
/// という使い方を想定しているため。ファイルに残すのは、古い訳が延々と返る危険と
/// 引き換えになるので採らない。</para>
/// </remarks>
public sealed class TranslationCache
{
    private readonly Dictionary<string, LinkedListNode<Entry>> _index;
    private readonly LinkedList<Entry> _order = new();
    private readonly int _capacity;
    private readonly Lock _gate = new();

    /// <summary>
    /// 既定の上限件数。
    /// </summary>
    /// <remarks>
    /// 1 画面が 30 件程度なので、数十画面分にあたる。
    /// 訳文は 1 件あたり数十〜数百バイトで、全部埋まっても 1MB に届かない。
    /// </remarks>
    public const int DefaultCapacity = 1000;

    public TranslationCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        _index = new Dictionary<string, LinkedListNode<Entry>>(capacity, StringComparer.Ordinal);
    }

    /// <summary>いま覚えている件数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>覚えていれば訳文を返す。</summary>
    public bool TryGet(string source, string targetLanguage, string backend, out string translated)
    {
        var key = KeyOf(source, targetLanguage, backend);

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                // 使ったものを末尾へ送る。あふれたときに捨てるのは先頭（最も長く使われていないもの）。
                _order.Remove(node);
                _order.AddLast(node);
                translated = node.Value.Translated;
                return true;
            }
        }

        translated = string.Empty;
        return false;
    }

    /// <summary>訳文を覚える。</summary>
    public void Set(string source, string targetLanguage, string backend, string translated)
    {
        ArgumentNullException.ThrowIfNull(translated);

        var key = KeyOf(source, targetLanguage, backend);

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _index.Remove(key);
            }

            var node = _order.AddLast(new Entry(key, translated));
            _index[key] = node;

            while (_index.Count > _capacity)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _index.Remove(oldest.Value.Key);
            }
        }
    }

    /// <summary>すべて忘れる。</summary>
    /// <remarks>翻訳の設定を変えたときなど、過去の訳を返してはいけない場合に呼ぶ。</remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _order.Clear();
        }
    }

    /// <summary>
    /// 鍵。
    /// </summary>
    /// <remarks>
    /// 区切りに改行を使うのは、原文・言語・バックエンド名のどれにも現れないため。
    /// 原文の改行は呼び出し側が空白へ潰している（<c>LineTranslation</c>）。
    /// </remarks>
    private static string KeyOf(string source, string targetLanguage, string backend)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetLanguage);
        ArgumentNullException.ThrowIfNull(backend);

        return string.Join('\n', targetLanguage, backend, source);
    }

    private readonly record struct Entry(string Key, string Translated);
}
