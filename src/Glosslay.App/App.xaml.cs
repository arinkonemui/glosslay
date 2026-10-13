using System.Windows;
using Glosslay.Shell;

namespace Glosslay;

/// <summary>
/// アプリケーションのエントリポイント（App.xaml のコードビハインド）。
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;

    /// <summary>
    /// 二重起動を確かめてから操作画面を作る。
    /// </summary>
    /// <remarks>
    /// <para><b>既に動いていれば、そちらの画面を出して終わる</b>（<see cref="SingleInstance"/>）。
    /// トレイにしまったまま忘れて、もう一度起動されることが多いため。</para>
    /// <para><b>自動起動（<see cref="AutoStart.TrayArgument"/>）で 2 つ目なら何もしない。</b>
    /// ログオン時に既に動いている方の画面を勝手に開くと、ゲームの前に割り込む。</para>
    /// </remarks>
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);

        _instance = SingleInstance.Claim();
        if (!_instance.IsFirst)
        {
            if (!e.Args.Contains(AutoStart.TrayArgument, StringComparer.OrdinalIgnoreCase))
            {
                SingleInstance.AskExistingToShow();
            }

            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Windows のサインアウト・シャットダウン。
    /// </summary>
    /// <remarks>
    /// <b>× の設定が「タスクトレイにしまう」でも、ここでは閉じさせる。</b>
    /// しまおうとして閉じるのを取り消すと、Windows の終了を止めてしまう（FR-SYS-02）。
    /// </remarks>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.AllowExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _instance = null;
        base.OnExit(e);
    }
}
