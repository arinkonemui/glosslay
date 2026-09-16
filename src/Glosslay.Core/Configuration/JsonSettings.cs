using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Glosslay.Configuration;

/// <summary>
/// <c>%APPDATA%\Glosslay\</c> 直下の設定 JSON を読み書きする（RULES.md 🔵「設定は人が読める JSON で保存」）。
/// </summary>
/// <remarks>
/// 設定ファイルごとに読み書きを書くと、エンコーダの指定が漏れて日本語が <c>\u3042</c> 形式になる。
/// 2026-09-15 に gemini.json で実際に踏んだため、ここに 1 か所だけ書く。
/// </remarks>
public static class JsonSettings
{
    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 既定のエンコーダは日本語を \u3042 形式へ逃がす。手で直せなければ設定として意味がない。
        // UnsafeRelaxedJsonEscaping ではなく範囲指定にしているのは、< > & のエスケープを残すため。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// 読み込む。ファイルが無い / 壊れている場合は <paramref name="fallback"/> を返す。
    /// </summary>
    /// <remarks>
    /// 設定が読めないだけでアプリが起動しないのは割に合わないため、既定値に倒す。
    /// </remarks>
    public static T Load<T>(string fileName, Func<T> fallback)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(fallback);

        var path = PathOf(fileName);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), SerializerOptions) ?? fallback()
                : fallback();
        }
        catch (JsonException)
        {
            return fallback();
        }
        catch (IOException)
        {
            return fallback();
        }
    }

    /// <summary>書き出す。利用者が手で編集できるよう整形する。</summary>
    public static void Save<T>(string fileName, T value)
    {
        GlosslayPaths.EnsureDirectory(GlosslayPaths.Root);
        File.WriteAllText(PathOf(fileName), JsonSerializer.Serialize(value, SerializerOptions));
    }

    /// <summary>設定ファイルの絶対パス。</summary>
    public static string PathOf(string fileName) => Path.Combine(GlosslayPaths.Root, fileName);
}
