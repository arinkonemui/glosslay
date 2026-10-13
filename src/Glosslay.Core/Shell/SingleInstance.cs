using Windows.Win32;
using Windows.Win32.Foundation;

namespace Glosslay.Shell;

/// <summary>
/// Glosslay を同時に 2 つ動かさない（FR-SYS-02 の前提）。
/// </summary>
/// <remarks>
/// <para><b>トレイに常駐すると、閉じたつもりでもう一度起動されることが増える。</b>
/// 2 つ動くとホットキーに両方が反応し、同じ画面を 2 回翻訳する
/// （クラウド翻訳なら呼び出しも 2 倍。RULES.md 🟡-8）。トレイのアイコンも 2 つ並ぶ。</para>
/// <para><b>2 つ目は、動いている方へ「画面を出して」と知らせて終わる。</b>
/// 利用者から見ると、起動し直したら画面が出てきた、という自然な形になる。</para>
/// <para>知らせは全ウィンドウへの一斉送信で行う（隠しているウィンドウにも届く）。
/// 中身は登録したメッセージの番号だけで、情報は運ばない。</para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>同じ利用者のセッション内で 1 つに絞る名前。</summary>
    private const string MutexName = @"Local\Glosslay.SingleInstance";

    /// <summary>「画面を出して」の知らせの名前。</summary>
    private const string ShowMessageName = "Glosslay.ShowMainWindow";

    /// <summary>全ウィンドウへ送る宛先（<c>HWND_BROADCAST</c>）。</summary>
    private static readonly HWND Broadcast = new(0xFFFF);

    /// <summary>前面に出る権利を、どのプロセスにも譲る（<c>ASFW_ANY</c>）。</summary>
    private const uint AnyProcess = unchecked((uint)-1);

    private readonly Mutex _mutex;
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>
    /// 「画面を出して」の知らせのメッセージ番号。最初のプロセスはこれをウィンドウで受け取る。
    /// </summary>
    public static uint ShowMessage { get; } = PInvoke.RegisterWindowMessage(ShowMessageName);

    /// <summary>これが最初の Glosslay か。<c>false</c> なら既に別のものが動いている。</summary>
    public bool IsFirst => _owned;

    /// <summary>起動したことを宣言する。既に動いているかどうかは <see cref="IsFirst"/> で分かる。</summary>
    public static SingleInstance Claim()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    /// <summary>動いている方へ「画面を出して」と知らせる。</summary>
    public static void AskExistingToShow()
    {
        // 知らせを受けた側が前面に出られるよう、先に権利を譲る。
        // 譲らないと、画面は出てもタスクバーで点滅するだけになる。
        _ = PInvoke.AllowSetForegroundWindow(AnyProcess);
        _ = PInvoke.PostMessage(Broadcast, ShowMessage, default, default);
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
            _owned = false;
        }

        _mutex.Dispose();
    }
}
