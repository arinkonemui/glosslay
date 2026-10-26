using System.Windows;
using Glosslay.Localization;
using Glosslay.Shell;
using Glosslay.Translation;

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

        ChooseLanguage(e.Args.Contains(AutoStart.TrayArgument, StringComparer.OrdinalIgnoreCase));

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// 画面の言語を決める（FR-SYS-03）。<b>まだ選んでいなければ、ここで選んでもらう</b>（FR-SYS-05）。
    /// </summary>
    /// <remarks>
    /// <para>メイン言語が画面の言語を兼ねる。操作画面を作る前に決めておくと、最初から選んだ言語で出る。</para>
    /// <para><b>自動起動（トレイで待機）のときは聞かない。</b>ログオン時に画面を出すと、ゲームの前に割り込む。
    /// その回は既定（日本語）で動き、次に画面から起動したときに聞く。</para>
    /// <para>選ぶ画面を閉じただけなら保存しない（<see cref="LanguageSetupWindow"/>）。</para>
    /// </remarks>
    private void ChooseLanguage(bool startInTray)
    {
        var options = TranslationOptions.Load();
        if (options.MainLanguage is null && options.TargetLanguage is null && !startInTray)
        {
            // 選ぶ画面だけが開いている間に閉じられても、アプリごと終わらせない。
            var mode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var chooser = new LanguageSetupWindow();
            if (chooser.ShowDialog() == true && chooser.Selected is { } selected)
            {
                options = options with { MainLanguage = selected };
                options.Save();
            }

            ShutdownMode = mode;
        }

        UiText.SetLanguage(options.Main);
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
