namespace Glosslay.Ocr;

/// <summary>
/// 認識モデルの出力クラス番号と文字の対応表。
/// </summary>
/// <remarks>
/// <para>PP-OCRv6_small_rec の出力は 18,710 クラス。内訳は実測で確認済み（2026-09-09）。</para>
/// <list type="table">
///   <item><term>クラス 0</term><description>CTC の blank</description></item>
///   <item><term>クラス 1〜18708</term><description>辞書ファイルの 0〜18707 行目</description></item>
///   <item><term>クラス 18709</term><description>空白文字</description></item>
/// </list>
/// <para>辞書には絵文字（BMP 外の文字）が 904 件含まれるため、
/// <c>char</c> ではなく <c>string</c> で保持する。</para>
/// <para>辞書ファイルは <c>tools/ocr-eval/extract_charset.py</c> がモデルの
/// <c>inference.yml</c> から抽出したもの。1 行 1 エントリ・UTF-8・LF 区切り。</para>
/// </remarks>
public sealed class CharacterSet
{
    /// <summary>CTC の blank に割り当てられたクラス番号。</summary>
    public const int BlankClass = 0;

    private readonly string[] _entries;

    private CharacterSet(string[] entries) => _entries = entries;

    /// <summary>辞書に載っているエントリ数（blank と空白文字を含まない）。</summary>
    public int EntryCount => _entries.Length;

    /// <summary>モデルが出力するクラス数。blank と末尾の空白文字を足した数。</summary>
    public int ClassCount => _entries.Length + 2;

    /// <summary>辞書ファイルを読み込む。</summary>
    /// <exception cref="FileNotFoundException">辞書ファイルが無い場合。</exception>
    /// <exception cref="InvalidDataException">中身が空の場合。</exception>
    public static CharacterSet Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"OCR の文字辞書が見つかりません: {path}。モデル一式が配置されているか確認してください。",
                path);
        }

        // 空白文字のエントリは空行として保存されているため、空行を捨ててはいけない。
        // 末尾の改行によって生じる最後の空要素だけを取り除く。
        var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        if (lines.Length == 0)
        {
            throw new InvalidDataException($"OCR の文字辞書が空です: {path}");
        }

        return new CharacterSet(lines);
    }

    /// <summary>
    /// クラス番号に対応する文字を返す。blank と範囲外は <c>null</c>。
    /// </summary>
    public string? TryGetCharacter(int classIndex)
    {
        if (classIndex <= BlankClass)
        {
            return null;
        }

        var position = classIndex - 1;
        if (position < _entries.Length)
        {
            return _entries[position];
        }

        // 辞書の直後のクラスが空白文字。それより後ろは未定義。
        return position == _entries.Length ? " " : null;
    }
}
