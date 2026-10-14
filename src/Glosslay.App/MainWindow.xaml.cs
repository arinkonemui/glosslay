using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glosslay.Ads;
using Glosslay.Capture;
using Glosslay.Entitlement;
using Glosslay.Configuration;
using Glosslay.Imaging;
using Glosslay.Input;
using Glosslay.Monitoring;
using Glosslay.Ocr;
using Glosslay.Overlay;
using Glosslay.Profiles;
using Glosslay.Shell;
using Glosslay.Translation;
using Glosslay.Usage;

namespace Glosslay;

/// <summary>
/// v0.1 PoC のキャプチャ検証用ウィンドウ。
/// </summary>
/// <remarks>
/// UI の作り込みは PoC の対象外（PLAN.md）。このウィンドウの目的は 2 つ。
/// <list type="number">
///   <item>P0-2「排他フルスクリーンのゲームで取得できるか実測する」</item>
///   <item>P0-7 の精度計測に使うサンプル画像の採取</item>
/// </list>
/// </remarks>
public partial class MainWindow : Window, IDisposable
{
    /// <summary>遅延キャプチャの待ち時間。ゲームへ切り替えるための猶予。</summary>
    private static readonly TimeSpan CaptureDelay = TimeSpan.FromSeconds(5);

    /// <summary>範囲指定として扱う最小の辺（画素）。これ未満は誤操作とみなす。</summary>
    private const int MinRegionSize = 16;

    /// <summary>
    /// OCR エンジン（FR-OCR-03）。<b>差し替え点</b>のためインターフェース越しに持つ。
    /// </summary>
#pragma warning disable CA1859 // 具象型の方が速いが、ここは高速モードとの差し替え点。
    private IOcrEngine _ocrEngine = new PaddleOcrEngine();
#pragma warning restore CA1859

    /// <summary>どちらのエンジンを使うか（FR-OCR-03）。</summary>
    private OcrEngineOptions _ocrOptions = OcrEngineOptions.Load();
    private readonly ApiKeyStore _apiKeyStore = ApiKeyStore.Gemini;

    /// <summary>
    /// 翻訳バックエンド。<see cref="ITranslator"/> 越しに持つ（RULES.md 🟡-2）。
    /// </summary>
    /// <remarks>
    /// gemini.json を編集したら作り直すため、フィールドは差し替え可能にしている。
    /// </remarks>
#pragma warning disable CA1859 // 具象型の方が速いが、ここは差し替え点（RULES.md 🟡-2）。
    private ITranslator _cloudTranslator;
#pragma warning restore CA1859

    /// <summary>
    /// キー登録なしで動く既定のバックエンド（FR-TRN-02 / RULES.md 🟡-1）。
    /// </summary>
    private readonly LocalNmtTranslator _localTranslator = new();

    /// <summary>
    /// 機能ゲートの判定（FR-SYS-10 / RULES.md 🟡-4）。v1.0 は常に無料版・上限なし。
    /// </summary>
    private readonly IEntitlementService _entitlements = FreeOnlyEntitlementService.Default;

    /// <summary>
    /// 遠隔から更新できる設定（FR-TRN-07 / FR-ADS-02 / RULES.md 🟡-3）。
    /// </summary>
    /// <remarks>v1.0 は既定値のみ。署名付き取得は未実装。</remarks>
    private readonly IRemoteConfig _remoteConfig = BuiltInRemoteConfig.Default;

    /// <summary>広告（FR-ADS）。v1.0 は何も描画しない。</summary>
    private readonly IAdProvider _adProvider = NullAdProvider.Default;

    /// <summary>どのバックエンドを使うかの選択（FR-TRN-01 / FR-TRN-06）。</summary>
    private TranslationOptions _translationOptions = TranslationOptions.Load();

    /// <summary>
    /// 訳文の使い回し（FR-TRN-09 / RULES.md 🟡-8）。
    /// </summary>
    /// <remarks>
    /// 同じ画面でホットキーを押し直したときに、呼び出しも待ち時間もゼロにする。
    /// バックエンドを変えたら忘れる（<see cref="OnBackendChanged"/>）。
    /// </remarks>
    private readonly TranslationCache _translationCache = new();

    /// <summary>
    /// 実際に使うバックエンド。
    /// </summary>
    /// <remarks>
    /// <b>キーが登録されていればクラウド、無ければローカル。</b>
    /// RULES.md 🟡-1 の「既定はローカルNMT、クラウドは精度を上げたい人向けの任意の
    /// アップグレード」をそのまま表している。<b>この順序を逆にしないこと。</b>
    /// </remarks>
    private ITranslator ActiveTranslator =>
        TranslatorSelection.Active(_translationOptions.Backend, _cloudTranslator, _localTranslator);

    /// <summary>
    /// <see cref="ActiveTranslator"/> が失敗したときの切り替え先（FR-TRN-06）。
    /// </summary>
    /// <remarks>
    /// <b>「自動」以外を選んでいるなら切り替えない。</b>利用者が明示的に決めた動きを、
    /// 失敗したからといって覆さない。
    /// 既にローカルで動いている場合と、モデルが置かれていない場合も <c>null</c>
    /// （切り替えても同じ失敗になる）。
    /// </remarks>
    private ITranslator? FallbackTranslator =>
        TranslatorSelection.Fallback(_translationOptions.Backend, _cloudTranslator, _localTranslator);

    /// <summary>自動監視の設定（FR-MOD-02 / 03 / 04）。</summary>
    private AutoWatchOptions _watchOptions = AutoWatchOptions.Load();

    /// <summary>カーソル位置モードの設定（FR-MOD-10〜12 / 14）。</summary>
    private CursorModeOptions _cursorOptions = CursorModeOptions.Load();

    /// <summary>
    /// 自動で起こす翻訳の呼び出し上限（FR-MOD-04 / FR-MOD-13）。
    /// </summary>
    /// <remarks>
    /// <b>自動監視とカーソル静止で 1 つを共有する。</b>別々に持つと、
    /// 両方を使ったときに合算で 2 倍まで呼べてしまい、歯止めの意味が薄れる。
    /// <b>手動のホットキーは数えない</b>（利用者が押した回数だけなので暴走しない）。
    /// </remarks>
    private readonly CallRateLimiter _autoCallLimiter = new(AutoWatchOptions.Load().MaxCallsPerHour);

    /// <summary>カーソル静止の判定（FR-MOD-10 / 11）。</summary>
    private CursorDwellDetector? _dwellDetector;

    /// <summary>カーソルの見回り。</summary>
    private DispatcherTimer? _dwellTimer;

    /// <summary>自動監視の判断。止める条件と呼び出し上限を持つ。</summary>
    private AutoWatchGate? _watchGate;

    /// <summary>自動監視の見回り。</summary>
    private DispatcherTimer? _watchTimer;

    /// <summary>使用量の記録（RULES.md 🟡-8）。</summary>
    private readonly UsageStore _usage = new();

    /// <summary>使用量の常駐表示の設定。<b>既定は OFF。</b></summary>
    private UsageOverlayOptions _usageOverlayOptions = UsageOverlayOptions.Load();

    /// <summary>タスクトレイ常駐の設定（FR-SYS-02）。</summary>
    private TrayOptions _trayOptions = TrayOptions.Load();

    /// <summary>Windows 起動時の自動起動（FR-SYS-02）。状態は Windows の登録が正。</summary>
    private readonly AutoStart _autoStart = new();

    /// <summary>タスクトレイのアイコン。ウィンドウのハンドルができた時点で作る。</summary>
    private TrayIcon? _trayIcon;

    /// <summary>
    /// 本当に終了してよいか。<c>false</c> の間は、× を押してもトレイにしまうだけ（設定による）。
    /// </summary>
    private bool _exitRequested;

    /// <summary>自動起動から立ち上がった。画面を出さずにトレイで待機する。</summary>
    private readonly bool _startInTray =
        Environment.GetCommandLineArgs().Contains(AutoStart.TrayArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>設定画面へ値を反映している間、変更の処理を止める。</summary>
    private bool _isRefreshingTraySettings;

    /// <summary>トレイのアイコンの操作を知らせてもらうメッセージ番号（WM_APP + 1）。</summary>
    private const uint TrayCallbackMessage = 0x8001;

    // トレイのメニューの項目番号。
    private const int TrayMenuOpen = 1;
    private const int TrayMenuAutoWatch = 2;
    private const int TrayMenuExit = 9;

    /// <summary>使用量の常駐表示。使うときだけ作る。</summary>
    private UsageOverlayWindow? _usageOverlay;

    /// <summary>
    /// 翻訳 1 回ごとの時間（FR-OVL-10）。画面の隅に直近の平均と最大を出す。
    /// </summary>
    /// <remarks>
    /// <b>記録するのは本番の経路（ホットキー・カーソル・自動監視・「画面を翻訳」）だけ。</b>
    /// 「工程ごとに実行する」は動作確認用で、待ち方が違うため混ぜない。
    /// </remarks>
    private readonly ProcessingTimeStats _processingTimes = new();

    /// <summary>使用量の 1 行（訳文の隅と常駐表示で同じ文言を使う）。</summary>
    private string _usageOneLine = string.Empty;

    /// <summary>
    /// 自動呼び出しの残りを見直す間隔。
    /// </summary>
    /// <remarks>
    /// <b>残りは翻訳しなくても増える</b>（直近 1 時間の移動窓から外れるため）。
    /// 翻訳のたびの更新だけでは「戻ったこと」が画面に出ない。
    /// 30 秒は、200 回/時 の目盛りに対して十分細かい。
    /// </remarks>
    private static readonly TimeSpan UsageRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>使用量の表示を定期的に見直す。</summary>
    private DispatcherTimer? _usageTimer;

    /// <summary>ゲームごとの設定（FR-CFG-01）。上限は資格サービスを経由する。</summary>
    private readonly ProfileStore _profileStore;

    /// <summary>自動適用の設定（FR-CFG-02）。</summary>
    private ProfileOptions _profileOptions = ProfileOptions.Load();

    /// <summary>前面ウィンドウの見回り。自動適用のためだけに回す。</summary>
    private DispatcherTimer? _profileTimer;

    /// <summary>いま当たっているプロファイルの名前。何も当たっていなければ空。</summary>
    private string _appliedProfileName = string.Empty;

    /// <summary>直前に見た前面ウィンドウ。変わっていなければ何もしない。</summary>
    private nint _lastForegroundWindow;

    /// <summary>ラジオへ選択を反映している間、Checked の処理を止める。</summary>
    private bool _suppressBackendChange;

    /// <summary>一覧へ選択を反映している間、SelectionChanged の処理を止める。</summary>
    private bool _suppressLanguageChange;

    /// <summary>ラジオへ選択を反映している間、Checked の処理を止める。</summary>
    private bool _suppressOcrEngineChange;

    /// <summary>一覧へ値を反映している間、SelectionChanged の処理を止める。</summary>
    private bool _suppressTuningChange;

    /// <summary>訳文の重ね方（FR-OVL-05 / 06）。</summary>
    private OverlayOptions _overlayOptions = OverlayOptions.Load();

    private ScreenCapture? _capture;

    /// <summary>
    /// セッションの作成を 1 本に絞る。前倒しの作成とホットキーが重なると 2 本作られ、片方が漏れる。
    /// </summary>
    private readonly SemaphoreSlim _captureStartLock = new(1, 1);

    private CaptureTarget? _activeTarget;
    private bool _activeRemoveBorder;
    private bool _activeCaptureCursor;

    private CapturedFrame? _lastFrame;
    private BitmapSource? _lastBitmap;
    private CaptureTarget? _lastTarget;
    private OverlayWindow? _overlay;

    /// <summary>OCR の対象範囲（元画像の画素座標）。null なら全体。</summary>
    private Int32Rect? _region;

    /// <summary>
    /// 前処理のうち画面に操作子が無い項目を保持する土台。
    /// </summary>
    /// <remarks>
    /// 補間方法（<c>Resampling</c>）と背景除去の窓（<c>BackgroundWindow</c>）には
    /// 操作子が無い。プロファイルを当てたときにここへ入れておかないと、
    /// 次の <see cref="BuildPreprocessOptions"/> で既定へ戻ってしまう。
    /// </remarks>
    private PreprocessOptions _preprocessBase = new();

    /// <summary>直前の OCR 結果。翻訳ボタンが原文として使う。</summary>
    private IReadOnlyList<OcrLine> _lastOcrLines = [];

    private Point _dragOrigin;
    private bool _isDragging;

    /// <summary>
    /// 翻訳を起こす入力（FR-INP-01 / RULES.md 🟡-2）。
    /// </summary>
    /// <remarks>
    /// v0.5 でゲームパッド（Windows.Gaming.Input・読み取りのみ）を足すときは、
    /// ここを差し替えるか、複数のトリガーを並べる。
    /// </remarks>
#pragma warning disable CA1859 // 具象型の方が速いが、ここは差し替え点（RULES.md 🟡-2）。
    private IInputTrigger? _inputTrigger;
#pragma warning restore CA1859

    /// <summary><c>WM_INPUT</c> を受け取るためのフック先。</summary>
    private HwndSource? _hwndSource;

    /// <summary>
    /// 画面の翻訳が進行中か。
    /// </summary>
    /// <remarks>
    /// 全画面だと数秒かかる（課題4）。その間に押し直されても受け付けない。
    /// 受け付けると待ち行列ができ、同じ画面に API を何度も払うことになる（RULES.md 🟡-8）。
    /// </remarks>
    private bool _isTranslatingScreen;

    public MainWindow()
    {
        InitializeComponent();
        _cloudTranslator = CreateCloudTranslator();

        // FR-SYS-10 / RULES.md 🟡-4: 件数の上限は必ず資格サービスを経由する。
        _profileStore = new ProfileStore(_entitlements);

        Loaded += OnLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;

        // 自動起動のときは画面を出さない。Loaded で隠すまでの間も、
        // 前面を奪わず、タスクバーにも出さない（ゲームを始めている最中かもしれない）。
        if (_startInTray)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
    }

    /// <summary>
    /// クラウド翻訳を作る。
    /// </summary>
    /// <remarks>
    /// <b>モデル名はコードに埋めない</b>（FR-TRN-07 / RULES.md 🟡-3）。
    /// 利用者の <c>gemini.json</c> を基本とし、<b>遠隔設定に推奨モデルがあればそちらを使う。</b>
    /// LLM は世代交代が速く、提供終了のたびに再ビルドが必要な作りにしてはいけない。
    /// </remarks>
    private GeminiTranslator CreateCloudTranslator()
    {
        var options = GeminiOptions.Load();
        if (_remoteConfig.Current.RecommendedGeminiModel is { Length: > 0 } recommended)
        {
            options = options with { Model = recommended };
        }

        return new GeminiTranslator(options);
    }

    /// <summary>
    /// ウィンドウハンドルができた時点でホットキーの受信を始める（FR-MOD-01 / FR-INP-02）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // ホットキーとトレイの両方が、このウィンドウでメッセージを受け取る。
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource.AddHook(OnWindowMessage);

        Icon = AppIcon.CreateBitmap(64);
        StartTrayIcon(handle);
        StartHotkey();
    }

    /// <summary>
    /// タスクトレイにアイコンを出す（FR-SYS-02）。
    /// </summary>
    /// <remarks>
    /// <b>出せなくてもアプリは動かす。</b>エクスプローラーが動いていない等で出せないときは、
    /// × で隠さずに終了する（隠すと戻る手段が無くなる。<see cref="OnWindowClosing"/>）。
    /// </remarks>
    private void StartTrayIcon(nint handle)
    {
        try
        {
            _trayIcon = new TrayIcon(
                handle, TrayCallbackMessage, AppIcon.CreatePng(TrayIcon.PreferredSize), "Glosslay");
            _trayIcon.Show();
        }
        catch (InvalidOperationException)
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
    }

private void StartHotkey()
    {
        // FR-INP-01 / RULES.md 🟡-2: 入力はインターフェース越しに扱う。
        // 設定の誤りでアプリ全体を止めない。使えない割り当てだけ無効にして理由を見せる。
        var trigger = new RawInputTrigger();
        _inputTrigger = trigger;

        if (!trigger.IsActive)
        {
            HotkeyText.Text = "ホットキー無効: " + string.Join(" ", trigger.Problems);
            return;
        }

        trigger.Fired += OnTriggerFired;
        trigger.Attach(new WindowInteropHelper(this).Handle);

        HotkeyText.Text = string.Join(
            Environment.NewLine, [.. trigger.Descriptions, .. trigger.Problems]);
    }

    /// <summary>割り当てが発動した。</summary>
    private void OnTriggerFired(InputAction action) => _ = RunGuardedAsync(action switch
    {
        InputAction.TranslateCursor => TranslateCursorAsync,
        InputAction.ToggleOverlay => ToggleOverlayAsync,
        _ => TranslateScreenAsync,
    });

    
    /// <summary>
    /// <c>WM_INPUT</c> を拾ってホットキーを判定する。
    /// </summary>
    /// <remarks>
    /// <para><b>キーの内容は判定に使ったらそのまま捨てる。記録・ログ出力をしないこと。</b>
    /// Raw Input はシステム全体の打鍵を受け取るため、残せばキーロガーと変わらない。</para>
    /// <para><c>handled</c> は立てない。<c>WM_INPUT</c> は <c>DefWindowProc</c> を通して
    /// OS に後始末させる決まりがある。そもそも Raw Input は入力を横取りしないため、
    /// ここで何をしてもゲームにはキーが届く（FR-INP-02）。</para>
    /// </remarks>
    private nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // トレイのアイコン宛て。メニューはこの関数から戻った後で出す
        // （ウィンドウの処理の途中でメニューの待ち受けに入ると、WPF の処理が入れ子になる）。
        // もう一度起動された（SingleInstance）。2 つ目は終わるので、こちらが画面を出す。
        if (msg != 0 && (uint)msg == SingleInstance.ShowMessage)
        {
            Dispatcher.BeginInvoke(OpenFromTray);
            return 0;
        }

        switch (_trayIcon?.Interpret(msg, wParam, lParam))
        {
            case TrayIconEvent.Selected:
                Dispatcher.BeginInvoke(OpenFromTray);
                return 0;

            case TrayIconEvent.MenuRequested:
                Dispatcher.BeginInvoke(ShowTrayMenu);
                return 0;

            default:
                break;
        }

        // 判定はトリガー側の仕事。ここはメッセージを渡すだけ。
        // ウィンドウメッセージを要るのはキーボードだけなので、型で絞る。
        // ゲームパッド（Windows.Gaming.Input）に差し替えたときは null になり、何もしないのが正しい。
        (_inputTrigger as RawInputTrigger)?.ProcessMessage(msg, lParam);
        return 0;
    }

    /// <summary>
    /// 自動監視を始める / 止める（FR-MOD-02）。
    /// </summary>
    /// <remarks>
    /// <b>既定は OFF。</b>勝手に呼び出し続ける機能を、利用者が知らないうちに動かさない
    /// （RULES.md 🟡-8）。
    /// </remarks>
    private void SetAutoWatch(bool enabled)
    {
        // FR-MOD-08: 初めて ON にするときだけ、処理時間を示して確認する。
        // 承諾の記録より先に問う。順序を逆にすると、断っても「案内済み」になる。
        if (enabled && !_watchOptions.FirstEnableNoticeShown && !ConfirmFirstAutoWatch())
        {
            // ここで SetAutoWatch(false) が入れ子で走り、タイマーと設定が片付く。
            AutoWatchCheck.IsChecked = false;
            StatusText.Text = "自動監視: 取りやめました（いつでも入れ直せます）。";
            return;
        }

        _watchOptions = _watchOptions with { Enabled = enabled };
        _watchOptions.Save();

        _watchTimer?.Stop();
        _watchTimer = null;
        _watchGate = null;

        if (!enabled)
        {
            StatusText.Text = "自動監視: 停止しました。";
            return;
        }

        // FR-SYS-10 / RULES.md 🟡-4: 同時に監視できる領域の数はエンタイトルメントで決まる。
        // いまの実装は 1 領域だけなので、上限が 1 以上であれば足りる。
        if (_entitlements.MaxWatchedRegions < 1)
        {
            StatusText.Text = "自動監視: このプランでは利用できません。";
            AutoWatchCheck.IsChecked = false;
            return;
        }

        _watchGate = new AutoWatchGate(_watchOptions, _autoCallLimiter);

        _watchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = _watchOptions.Interval,
        };
        _watchTimer.Tick += OnWatchTick;
        _watchTimer.Start();

        StatusText.Text = _region is null
            ? "自動監視: 開始（画面全体）。プレビューをドラッグして範囲を絞ると誤検知が減ります。"
            : "自動監視: 開始（指定範囲）。";
    }

    /// <summary>
    /// 見回り 1 回分。
    /// </summary>
    /// <remarks>
    /// <para><b>この間隔で行うのは撮影と差分の比較だけ。</b>
    /// 変化がなければ OCR も翻訳もしない（FR-OCR-07）。</para>
    /// <para>翻訳中は何もしない。重ねて走らせると、前の訳文が出る前に次が始まる。</para>
    /// </remarks>
    private async void OnWatchTick(object? sender, EventArgs e)
    {
        if (_watchGate is null || _isTranslatingScreen)
        {
            return;
        }

        try
        {
            var target = ResolveHotkeyTarget();
            if (target is null)
            {
                return;
            }

            var capture = await EnsureCaptureAsync(target).ConfigureAwait(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await capture.GetNextFrameAsync(timeout.Token).ConfigureAwait(true);

            // 比べるのは監視している範囲だけ（FR-MOD-02「指定領域を監視」）。
            var image = frame.AsImage();
            if (_region is { } region
                && ClipRegion(region, image.Width, image.Height) is { } fitted)
            {
                image = image.Crop(fitted.X, fitted.Y, fitted.Width, fitted.Height);
            }

            // FR-MOD-05: ゲームが前面にない間は止める。
            var isForeground = target.Kind != CaptureTargetKind.Window
                               || WindowFocus.IsForeground(target.Handle);

            switch (_watchGate.Evaluate(image, isForeground))
            {
                case AutoWatchDecision.Translate:
                    await TranslateAsync(useCursorWindow: false).ConfigureAwait(true);
                    break;

                case AutoWatchDecision.RateLimited:
                    // FR-MOD-04。黙って止まると「効いていない」と見えるので理由を出す。
                    StatusText.Text =
                        $"自動監視: 1 時間あたり {_watchOptions.MaxCallsPerHour} 回の上限に達しました。"
                        + $"約 {_watchGate.TimeUntilNext.TotalMinutes:F0} 分後に再開します。";
                    break;

                default:
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // 画面が取れなかっただけ。次の見回りで拾う。
        }
    }

    /// <summary>
    /// 初めて自動監視を ON にするときの案内（FR-MOD-08）。入れてよければ <c>true</c>。
    /// </summary>
    /// <remarks>
    /// <para><b>読まずに始められない形にする。</b>自動監視は利用者が押していない翻訳を
    /// 起こし続けるため、RULES.md 🟡-8（予期しない課金）に正面からぶつかる。
    /// 文面に実測の処理時間を入れるのが FR-MOD-08 の趣旨で、
    /// <b>やめる選択肢を同時に出す</b>ことで初めて判断になる。</para>
    /// <para>文面は <see cref="AutoWatchNotice"/> にある。数字が実測から離れていないかを
    /// <c>NmtBench --watch</c> で見るため、画面から切り離してある。</para>
    /// </remarks>
    private bool ConfirmFirstAutoWatch()
    {
        var answer = MessageBox.Show(
            this,
            AutoWatchNotice.Build(_watchOptions, _region is not null),
            "自動監視について",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.OK)
        {
            return false;
        }

        // 承諾は 1 回だけ記録する。次からは黙って始めてよい。
        _watchOptions = _watchOptions with { FirstEnableNoticeShown = true };
        _watchOptions.Save();
        return true;
    }

    private void OnAutoWatchChanged(object sender, RoutedEventArgs e) =>
        SetAutoWatch(AutoWatchCheck.IsChecked == true);

    /// <summary>
    /// カーソル静止での発動を始める / 止める（FR-MOD-10 / 14）。
    /// </summary>
    /// <remarks>
    /// <b>見回るのはカーソルの座標だけ。</b>撮影も OCR もしない。
    /// 止まったと判定したときに初めて、ホットキーと同じ経路を通す。
    /// </remarks>
    private void SetCursorDwell(bool enabled)
    {
        _cursorOptions = _cursorOptions with
        {
            Enabled = enabled,
            Trigger = enabled ? CursorTrigger.Dwell : CursorTrigger.Hotkey,
        };
        _cursorOptions.Save();

        _dwellTimer?.Stop();
        _dwellTimer = null;
        _dwellDetector = null;

        if (!enabled)
        {
            StatusText.Text = "カーソル静止: 停止しました（ホットキーでの翻訳は使えます）。";
            return;
        }

        _dwellDetector = new CursorDwellDetector(_cursorOptions);

        // 静止の判定そのものより細かく見ないと、止まった瞬間を取り逃す。
        var interval = TimeSpan.FromMilliseconds(
            Math.Max(50, _cursorOptions.DwellTime.TotalMilliseconds / 4));

        _dwellTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _dwellTimer.Tick += OnDwellTick;
        _dwellTimer.Start();

        StatusText.Text =
            $"カーソル静止: 開始。{_cursorOptions.DwellTime.TotalMilliseconds:F0} ms 止めると"
            + $" 周囲 {_cursorOptions.Width}x{_cursorOptions.Height} を翻訳します。";
    }

    /// <summary>カーソルの見回り 1 回分。</summary>
    private async void OnDwellTick(object? sender, EventArgs e)
    {
        if (_dwellDetector is null || _isTranslatingScreen)
        {
            return;
        }

        if (!CursorPosition.TryGet(out var cursor) || !_dwellDetector.Update(cursor))
        {
            return;
        }

        // ここから先は「止まった」と決まった後。止める条件を先に見る。
        var target = ResolveHotkeyTarget();
        if (target is null)
        {
            return;
        }

        // ゲームが前面にないなら起こさない。別のアプリを触っている間に
        // カーソルを止めただけで翻訳が走るのは予期しない呼び出しになる。
        if (target.Kind == CaptureTargetKind.Window && !WindowFocus.IsForeground(target.Handle))
        {
            return;
        }

        // FR-MOD-13: カーソル位置モードにも呼び出し上限を必ず適用する。
        if (!_autoCallLimiter.TryTake())
        {
            StatusText.Text =
                $"カーソル静止: 1 時間あたり {_watchOptions.MaxCallsPerHour} 回の上限に達しました。"
                + $"約 {_autoCallLimiter.TimeUntilNext.TotalMinutes:F0} 分後に再開します。";
            return;
        }

        await RunGuardedAsync(TranslateCursorAsync).ConfigureAwait(true);
    }

    private void OnCursorDwellChanged(object sender, RoutedEventArgs e) =>
        SetCursorDwell(CursorDwellCheck.IsChecked == true);

    // ===== プロファイル（FR-CFG-01〜03） =====

    /// <summary>
    /// 一覧を作り直す。
    /// </summary>
    /// <remarks>
    /// <b>毎回新しい配列を渡す。</b><see cref="ProfileStore.Profiles"/> は同じ実体を返すため、
    /// そのまま入れ直しても WPF は変化に気づかない。
    /// </remarks>
    private void RefreshProfiles(string? select = null)
    {
        ProfileCombo.ItemsSource = _profileStore.Profiles.ToArray();

        var wanted = select ?? _appliedProfileName;
        if (wanted.Length > 0 && _profileStore.Find(wanted) is { } found)
        {
            ProfileCombo.SelectedItem = ProfileCombo.Items
                .OfType<Profile>()
                .FirstOrDefault(p => p.Name == found.Name);
        }
        else if (ProfileCombo.Items.Count > 0)
        {
            ProfileCombo.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// 保存するときに使うクラス名とタイトルを見せる。
    /// </summary>
    /// <remarks>
    /// <b>タイトルの絞り込みは利用者に書いてもらう</b>ため、材料を出しておく。
    /// 「ゲーム名 - ステージ3」のように変わる部分まで保存すると、次回は当たらない。
    /// </remarks>
    private void UpdateProfileTargetText()
    {
        if (TargetList.SelectedItem is not CaptureTarget target)
        {
            ProfileTargetText.Text = "対象: 未選択";
            return;
        }

        if (target.Kind != CaptureTargetKind.Window)
        {
            ProfileTargetText.Text = "対象: モニタ全体（クラス名が無いため自動では切り替わりません）";
            return;
        }

        var className = WindowIdentity.GetClassName(target.Handle);
        ProfileTargetText.Text = WindowIdentity.IsVolatileClassName(className)
            ? $"対象: タイトル {target.DisplayName}"
                + "（クラス名は起動ごとに変わるため使えません。タイトルで絞ってください）"
            : $"対象: クラス名 {className} / タイトル {target.DisplayName}";
    }

    /// <summary>
    /// いまの設定を 1 件のプロファイルにする（FR-CFG-01）。
    /// </summary>
    /// <remarks>
    /// <b>APIキーと翻訳バックエンドの選択は入れない。</b>キーは DPAPI の別の場所にあり
    /// （FR-TRN-05）、バックエンドはゲームごとではなくアプリ全体の設定
    /// （<c>translation.json</c>）。交換する前提のファイルに秘密を混ぜない。
    /// </remarks>
    private Profile BuildProfile(string name, string windowClass, string titleContains) => new()
    {
        Name = name,
        WindowClass = windowClass,
        TitleContains = titleContains,
        Region = _region is { } region
            ? new FrameRect(region.X, region.Y, region.Width, region.Height)
            : null,
        Preprocess = BuildPreprocessOptions(),
        TargetLanguage = _translationOptions.TargetLanguage,
        AutoWatch = AutoWatchCheck.IsChecked == true,
        CursorDwell = CursorDwellCheck.IsChecked == true,
    };

    private void OnSaveProfileClick(object sender, RoutedEventArgs e)
    {
        var selected = TargetList.SelectedItem as CaptureTarget;
        var readClass = selected is { Kind: CaptureTargetKind.Window }
            ? WindowIdentity.GetClassName(selected.Handle)
            : string.Empty;

        // 起動ごとに変わるクラス名は残さない。保存できても次回は当たらない。
        var isVolatile = readClass.Length > 0 && WindowIdentity.IsVolatileClassName(readClass);
        var windowClass = isVolatile ? string.Empty : readClass;

        var name = ProfileNameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = selected?.DisplayName.Trim() ?? string.Empty;
        }

        var profile = BuildProfile(name, windowClass, ProfileTitleBox.Text.Trim());
        var note = isVolatile
            ? "（このアプリのクラス名は起動ごとに変わるため使いません。"
                + "タイトルに含まれる文字列を入れてください）"
            : string.Empty;

        switch (_profileStore.Save(profile))
        {
            case ProfileSaveResult.NameRequired:
                StatusText.Text = "プロファイル: 名前を入れてください（対象を選べばタイトルを使います）。";
                return;

            // FR-SYS-10 / RULES.md 🟡-4。v1.0 は上限なしだが、理由は必ず伝える。
            case ProfileSaveResult.LimitReached:
                StatusText.Text =
                    $"プロファイル: 保存できるのは {_entitlements.MaxProfiles} 件までです。"
                    + "どれかを削除してください。";
                return;

            case ProfileSaveResult.Replaced:
                StatusText.Text =
                    $"プロファイル「{profile.Name}」を上書きしました。{MatchSummary(profile)}{note}";
                break;

            default:
                StatusText.Text =
                    $"プロファイル「{profile.Name}」を保存しました。{MatchSummary(profile)}{note}";
                break;
        }

        _appliedProfileName = profile.Name;
        ProfileNameBox.Text = profile.Name;
        RefreshProfiles(profile.Name);
    }

    /// <summary>
    /// 保存したプロファイルが自動適用の対象になるかを言葉にする。
    /// </summary>
    /// <remarks>
    /// <b>モニタ全体で保存するとクラス名が取れない。</b>自動では当たらないが、
    /// 範囲と前処理は「適用」で使えるため、保存は断らずに理由だけ伝える。
    /// </remarks>
    private static string MatchSummary(Profile profile)
    {
        if (profile.WindowClass.Length == 0 && profile.TitleContains.Length == 0)
        {
            return "対象のウィンドウが分からないため、自動では切り替わりません（「適用」で使えます）。";
        }

        var title = profile.TitleContains.Length > 0
            ? $" / タイトルに「{profile.TitleContains}」"
            : string.Empty;

        return profile.WindowClass.Length > 0
            ? $"自動適用の条件: クラス名「{profile.WindowClass}」{title}"
            : $"自動適用の条件: タイトルに「{profile.TitleContains}」";
    }

    private void OnApplyProfileClick(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not Profile profile)
        {
            StatusText.Text = "プロファイル: 適用するものを選んでください。";
            return;
        }

        ApplyProfile(profile, automatic: false);
    }

    /// <summary>
    /// プロファイルを画面へ反映する（FR-CFG-01 / 02）。
    /// </summary>
    /// <remarks>
    /// <para><b>一覧の選択は揃えるだけで、撮る対象は変えない。</b>ホットキーも自動監視も
    /// 撮るのは前面のウィンドウ（<see cref="ResolveHotkeyTarget"/>）。一覧は操作ウィンドウが
    /// 前面のときに使う検証用の経路なので、当てはまるものが無くても困らない。</para>
    /// <para><b>自動監視を含むプロファイルは、当たった時点で呼び出しが始まる。</b>
    /// 黙って始めないため（RULES.md 🟡-8）、何が ON になったかを必ず画面に出す。</para>
    /// </remarks>
    private void ApplyProfile(Profile profile, bool automatic)
    {
        _appliedProfileName = profile.Name;

        // 画面に出していない項目（補間方法・背景除去の窓）を落とさないため、
        // プロファイルの値を土台にする。画面の 6 項目はこの後で上書きされる。
        _preprocessBase = profile.Preprocess;

        SelectComboValue(ScaleCombo, profile.Preprocess.Scale, "0.0 倍");
        SelectComboValue(ContrastCombo, profile.Preprocess.Contrast, "0.0#");
        AutoContrastCheck.IsChecked = profile.Preprocess.AutoContrast;
        RemoveBackgroundCheck.IsChecked = profile.Preprocess.RemoveBackground;
        BinarizeCheck.IsChecked = profile.Preprocess.Binarize;
        InvertCheck.IsChecked = profile.Preprocess.Invert;

        _region = profile.Region is { } region
            ? new Int32Rect(region.X, region.Y, region.Width, region.Height)
            : null;
        ShowRegionRectangle();

        // 訳文の言語はゲームごとに変わりうる（FR-CFG-01）。覚えている訳は
        // 言語ごとに分かれているが、切り替えた直後に前の言語の訳が出ると紛らわしい。
        if (!string.Equals(_translationOptions.TargetLanguage, profile.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            _translationOptions = _translationOptions with { TargetLanguage = profile.TargetLanguage };
            _translationOptions.Save();
            _translationCache.Clear();
        }

        SelectTargetLanguage(profile.TargetLanguage);

        // FR-MOD-08 の案内がまだなら、自動で当たったプロファイルの自動監視は入れない。
        // ゲームが前面のときに問いかけても、こちらのウィンドウは背面で見えない。
        // 貰った / 手で書いたプロファイル（FR-CFG-03）はこの経路で来る。
        var deferWatch = automatic && profile.AutoWatch && !_watchOptions.FirstEnableNoticeShown;

        // チェックボックス経由で通す。タイマーを起こす / 止めるのは
        // SetAutoWatch と SetCursorDwell にあり、同じ値なら WPF がイベントを出さない。
        AutoWatchCheck.IsChecked = profile.AutoWatch && !deferWatch;
        CursorDwellCheck.IsChecked = profile.CursorDwell;

        ProfileNameBox.Text = profile.Name;
        ProfileTitleBox.Text = profile.TitleContains;
        SelectTargetFor(profile);

        StatusText.Text = $"プロファイル「{profile.Name}」を{(automatic ? "自動で適用" : "適用")}しました。"
            + AppliedSummary(profile, watchEnabled: profile.AutoWatch && !deferWatch)
            + (deferWatch
                ? "／自動監視は初回だけ確認が要ります。この画面でチェックを入れてください。"
                : string.Empty);
    }

    /// <summary>適用した内容のうち、見えないと困るものを並べる。</summary>
    /// <remarks>
    /// <b>ON になった自動の機能は必ず挙げる。</b>範囲と前処理は結果を見れば分かるが、
    /// 自動監視とカーソル静止は<b>翻訳の呼び出しを自分で起こす</b>（RULES.md 🟡-8）。
    /// </remarks>
    /// <param name="watchEnabled">
    /// 実際に自動監視が始まったか。<b>プロファイルの値ではなく結果を渡す。</b>
    /// FR-MOD-08 の案内待ちで見送った場合に「ON」と出しては嘘になる。
    /// </param>
    private static string AppliedSummary(Profile profile, bool watchEnabled)
    {
        var parts = new List<string>
        {
            profile.Region is { } region
                ? $"範囲 {region.X},{region.Y} {region.Width}x{region.Height}"
                : "範囲 全体",
            profile.Preprocess.ToString(),
            $"訳文 {TargetLanguages.DisplayNameOf(profile.TargetLanguage)}",
        };

        if (watchEnabled)
        {
            parts.Add("自動監視 ON");
        }

        if (profile.CursorDwell)
        {
            parts.Add("カーソル静止 ON");
        }

        return string.Join(" / ", parts);
    }

    /// <summary>
    /// コンボボックスの選択を値で合わせる。一覧に無い値なら項目を足す。
    /// </summary>
    /// <remarks>
    /// <b>手で profiles.json に書いた値を黙って捨てないため。</b>選べないからといって
    /// 既定へ戻すと、「保存したのに効いていない」ことに気づけない。
    /// </remarks>
    private static void SelectComboValue(
        System.Windows.Controls.ComboBox combo, double value, string format)
    {
        foreach (var item in combo.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (item.Tag is string tag
                && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                && parsed == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }

        var added = new System.Windows.Controls.ComboBoxItem
        {
            Content = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture),
            Tag = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        };

        combo.Items.Add(added);
        combo.SelectedItem = added;
    }

    /// <summary>
    /// 一覧の選択をプロファイルに合わせる（見つかれば）。
    /// </summary>
    /// <remarks>
    /// <b>見つからなくても何もしない。</b>ゲームを起動していないだけのことが多く、
    /// 黙って別の対象へ移ると、見当違いの画面を撮ることになる。
    /// </remarks>
    private void SelectTargetFor(Profile profile)
    {
        if (TargetList.ItemsSource is not IEnumerable<CaptureTarget> targets)
        {
            return;
        }

        foreach (var target in targets)
        {
            if (target.Kind == CaptureTargetKind.Window
                && profile.Matches(WindowIdentity.GetClassName(target.Handle), target.DisplayName))
            {
                TargetList.SelectedItem = target;
                return;
            }
        }
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not Profile profile)
        {
            StatusText.Text = "プロファイル: 削除するものを選んでください。";
            return;
        }

        // 戻せない操作なので確かめる。消すと範囲の指定からやり直しになる。
        var answer = MessageBox.Show(
            this,
            $"プロファイル「{profile.Name}」を削除します。",
            "Glosslay",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _profileStore.Remove(profile.Name);

        // いま当たっているものを消しても、設定はそのまま残す。
        // 範囲や前処理が同時に戻ると、作業中のものが消える。
        if (string.Equals(_appliedProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            _appliedProfileName = string.Empty;
        }

        RefreshProfiles();
        StatusText.Text = $"プロファイル「{profile.Name}」を削除しました（今の設定はそのままです）。";
    }

    /// <summary>
    /// <c>profiles.json</c> をファイルから読み直す（FR-CFG-03 のインポート）。
    /// </summary>
    /// <remarks>
    /// 他の人から貰った 1 件を貼り付ける経路がこれ。<b>壊れていても落ちない</b>
    /// （<see cref="JsonSettings.Load"/> が既定値に倒す）が、黙って空になると
    /// 消えたように見えるため件数を出す。
    /// </remarks>
    private void OnReloadProfilesClick(object sender, RoutedEventArgs e)
    {
        _profileStore.Reload();
        RefreshProfiles();

        // 書式が 1 か所でも壊れていると一覧ごと空になる（JsonSettings.Load が既定値に倒す）。
        // 「0 件」だけでは消えたように見えるため、理由を出す。
        var path = JsonSettings.PathOf(ProfileStore.FileName);
        var looksBroken = _profileStore.Profiles.Count == 0
            && File.Exists(path)
            && new FileInfo(path).Length > 2;

        StatusText.Text = looksBroken
            ? "プロファイルを読めませんでした。profiles.json の書式を確かめてください"
                + "（1 か所でも壊れていると全部読めません）。"
            : $"プロファイルを読み直しました: {_profileStore.Profiles.Count} 件。";
    }

    private void OnOpenProfilesClick(object sender, RoutedEventArgs e)
    {
        var path = JsonSettings.PathOf(ProfileStore.FileName);
        if (!File.Exists(path))
        {
            StatusText.Text = "プロファイルがまだありません。先に「現在の設定を保存」を押してください。";
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        StatusText.Text = "profiles.json を開きました。編集したら「読み直す」を押してください。";
    }

    private void OnProfileAutoApplyChanged(object sender, RoutedEventArgs e) =>
        SetProfileAutoApply(ProfileAutoApplyCheck.IsChecked == true);

    /// <summary>自動適用を始める / 止める（FR-CFG-02）。</summary>
    private void SetProfileAutoApply(bool enabled)
    {
        if (_profileOptions.AutoApply != enabled)
        {
            _profileOptions = _profileOptions with { AutoApply = enabled };
            _profileOptions.Save();
        }

        _profileTimer?.Stop();
        _profileTimer = null;
        _lastForegroundWindow = 0;

        if (!enabled)
        {
            return;
        }

        _profileTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = _profileOptions.PollInterval,
        };

        _profileTimer.Tick += OnProfileTick;
        _profileTimer.Start();
    }

    /// <summary>
    /// 前面のウィンドウを見て、当てはまるプロファイルがあれば当てる（FR-CFG-02）。
    /// </summary>
    /// <remarks>
    /// <para><b>行うのは前面のハンドルの問い合わせだけ。</b>変わっていなければ何もしない。
    /// ゲームプロセスへのアタッチやハンドル取得はしない（RULES.md 🔴-1）。</para>
    /// <para><b>当てはまるものが無いときは解除しない。</b>ブラウザを見に行って戻るだけで
    /// 範囲の指定が消えると、やり直しになる。</para>
    /// </remarks>
    private void OnProfileTick(object? sender, EventArgs e)
    {
        // 翻訳中は触らない。範囲や前処理が途中で変わると、出てくる訳文と設定が食い違う。
        if (_isTranslatingScreen)
        {
            return;
        }

        var foreground = WindowFocus.Current;
        if (foreground == 0 || foreground == _lastForegroundWindow)
        {
            return;
        }

        _lastForegroundWindow = foreground;

        // 自分のウィンドウは見ない。設定している最中に当て直すと、いま触った値が戻る。
        if (foreground == new WindowInteropHelper(this).Handle)
        {
            return;
        }

        if (!CaptureTargetEnumerator.TryDescribe(foreground, out var target))
        {
            return;
        }

        var profile = _profileStore.FindForWindow(
            WindowIdentity.GetClassName(foreground), target.DisplayName);

        if (profile is null)
        {
            return;
        }

        if (!string.Equals(profile.Name, _appliedProfileName, StringComparison.OrdinalIgnoreCase))
        {
            ApplyProfile(profile, automatic: true);
            RefreshProfiles(profile.Name);
        }

        // 当て直しで一覧の選択が変わるとセッションが捨てられるため、当てた後で作る。
        PrewarmCapture(target);
    }

    /// <summary>キャプチャ画像の保存先（FR-CFG-00）。</summary>
    private static string CaptureDirectory => GlosslayPaths.Captures;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ScreenCapture.IsSupported)
        {
            StatusText.Text = "この環境では Windows.Graphics.Capture が利用できません。Windows 11 か確認してください。";
            CaptureNowButton.IsEnabled = false;
            CaptureDelayedButton.IsEnabled = false;
            return;
        }

        // 自動起動から立ち上がったなら、ここで隠す（トレイで待機）。
        // 読み込みや設定の反映はこの後も同じように行う。ホットキーは隠れていても効く。
        if (_startInTray && _trayIcon is { IsShown: true })
        {
            Hide();
        }

        RefreshTraySettings();
        RefreshTargets();
        SelectTargetLanguage(_translationOptions.TargetLanguage);
        RefreshTranslatorState();
        RefreshProfiles();
        UpdateProfileTargetText();

        // チェックを入れると Checked が走って起動まで済むが、OFF のときは何も起きない。
        // どちらでも同じ道を通すよう、明示的に呼んでおく。
        ProfileAutoApplyCheck.IsChecked = _profileOptions.AutoApply;
        SetProfileAutoApply(_profileOptions.AutoApply);

        // 細かい設定（間隔・感度・静止時間・範囲・文字サイズ・隅）を一覧へ反映する。
        RefreshTuning();
        RefreshOcrLanguages();

        // RULES.md 🟡-8: 使用量は起動した時点から見えていること。
        RefreshUsage();
        UsageOverlayCheck.IsChecked = _usageOverlayOptions.Enabled;
        ProcessingTimeCheck.IsChecked = _usageOverlayOptions.ShowProcessingTime;
        SetUsageOverlay(_usageOverlayOptions.Enabled);

        // 自動呼び出しの残りは、翻訳しなくても時間とともに戻る。
        _usageTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = UsageRefreshInterval,
        };
        _usageTimer.Tick += (_, _) => RefreshUsage();
        _usageTimer.Start();

        // FR-OCR-08: 初回 OCR で待たせないよう、起動時に非同期でモデルを読み込んでおく。
        _ = WarmUpOcrAsync();

        // 翻訳モデルも同じ理由で先に読む。146MB あり、初回の翻訳で 1 秒近く待たされる。
        _ = WarmUpTranslatorAsync();
    }

    /// <summary>
    /// ローカルNMT のモデルを先に読む。
    /// </summary>
    /// <remarks>
    /// <b>失敗しても黙って進む。</b>モデルが無い環境（変換していない場合）でも
    /// クラウド翻訳は使えるため、起動を妨げない。翻訳を試みた時点で
    /// <see cref="TranslationOutcome.NotConfigured"/> として具体的に知らせる。
    /// </remarks>
    private async Task WarmUpTranslatorAsync()
    {
        if (!_localTranslator.IsConfigured)
        {
            return;
        }

        try
        {
            await _localTranslator.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException)
        {
            StatusText.Text = $"翻訳モデルを読み込めませんでした: {exception.Message}";
        }
    }

    private async Task WarmUpOcrAsync()
    {
        try
        {
            await _ocrEngine.InitializeAsync().ConfigureAwait(true);
            StatusText.Text = $"準備完了（{_ocrEngine.Name}）";
        }
        catch (FileNotFoundException ex)
        {
            // モデル未配置は想定内。キャプチャ自体は使えるので落とさない。
            StatusText.Text = $"OCR は使えません: {ex.Message}";
        }
        catch (NotSupportedException ex)
        {
            // FR-OCR-10: 高速モードの言語パックが無い。何をすればよいかはメッセージにある。
            StatusText.Text = ex.Message;
        }

        RefreshOcrEngineState();
    }

    // ===== OCR エンジンの選択（FR-OCR-03 / FR-OCR-10） =====

    /// <summary>
    /// 選択をラジオへ反映し、いま何で動くのかと代償を出す。
    /// </summary>
    /// <remarks>
    /// <b>速さだけを出さない。</b>高速モードは一致度が 30 ポイント落ち、
    /// 原文に無い数値が 8 倍になる（<see cref="OcrSpeedFacts"/>）。
    /// 選ぶ前に数字で分かるようにする。
    /// </remarks>
    private void RefreshOcrEngineState()
    {
        _suppressOcrEngineChange = true;
        OcrPaddleRadio.IsChecked = _ocrOptions.Engine == OcrEngineChoice.Paddle;
        OcrWindowsRadio.IsChecked = _ocrOptions.Engine == OcrEngineChoice.Windows;
        _suppressOcrEngineChange = false;

        OcrEngineNote.Text = _ocrOptions.Engine == OcrEngineChoice.Windows
            ? OcrSpeedFacts.FastModeTradeoff
            : OcrSpeedFacts.DefaultModeNote;

        // FR-OCR-10: 言語パックが無ければ、何が入っているかと足す場所を出す。
        var available = WindowsOcrEngine.AvailableLanguages;
        OcrLanguageNote.Text = available.Count == 0
            ? "高速モードに使える言語が 1 つも入っていません。下のボタンから追加してください。"
            : $"高速モードで読める言語: {string.Join(" / ", available)}"
                + "（原文の言語が無ければ、下のボタンから追加してください）";

        OcrEngineStateText.Text = $"{_ocrEngine.Name}"
            + (_ocrEngine.ProvidesConfidence ? string.Empty : " — 確信度を返しません");
    }

    private void OnOcrEngineChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressOcrEngineChange)
        {
            return;
        }

        var choice = ReferenceEquals(sender, OcrWindowsRadio)
            ? OcrEngineChoice.Windows
            : OcrEngineChoice.Paddle;

        _ocrOptions = _ocrOptions with { Engine = choice };
        _ocrOptions.Save();

        // 読み取り結果が変わるため、前の結果は捨てる。
        // 残したまま翻訳すると、どちらのエンジンの結果か分からなくなる。
        _lastOcrLines = [];
        TranslateButton.IsEnabled = false;

        _ocrEngine.Dispose();
        _ocrEngine = CreateOcrEngine();
        RefreshOcrEngineState();
        RefreshOcrLanguages();

        StatusText.Text = choice == OcrEngineChoice.Windows
            ? "高速モードに切り替えました。読み直すには OCR を実行してください。"
            : "既定のエンジンに戻しました。読み直すには OCR を実行してください。";

        // FR-OCR-08: 初回の OCR で待たせない。
        _ = WarmUpOcrAsync();
    }

    /// <summary>設定どおりのエンジンを作る。</summary>
    private IOcrEngine CreateOcrEngine() => _ocrOptions.Engine switch
    {
        OcrEngineChoice.Windows => new WindowsOcrEngine(_ocrOptions.WindowsLanguage),
        _ => new PaddleOcrEngine(),
    };

    /// <summary>
    /// 言語を追加する設定画面を開く（FR-OCR-10）。
    /// </summary>
    /// <remarks>
    /// <b>こちらでは入れない。</b>言語パックの追加は OS の設定の変更で、
    /// 管理者権限が要ることもある（RULES.md 🟠「管理者権限を要求しない」）。
    /// 開くところまでを行い、入れるかどうかは利用者が決める。
    /// </remarks>
    private void OnOpenLanguageSettingsClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(WindowsOcrEngine.LanguageSettingsUri)
        {
            UseShellExecute = true,
        })?.Dispose();

        StatusText.Text = "設定を開きました。言語を追加したら Glosslay を再起動してください。";
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    // ===== タスクトレイ常駐（FR-SYS-02） =====

    /// <summary>
    /// × を押したとき。設定が「しまう」ならトレイにしまって動き続ける。
    /// </summary>
    /// <remarks>
    /// <para><b>トレイにアイコンが出ていなければしまわない。</b>しまうと戻る手段が無くなる。</para>
    /// <para><b>初めてしまったときだけ知らせる。</b>閉じたつもりで動き続けていると驚かせる。</para>
    /// </remarks>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested
            || _trayOptions.CloseBehavior == CloseBehavior.Exit
            || _trayIcon is not { IsShown: true })
        {
            return;
        }

        e.Cancel = true;
        Hide();

        if (!_trayOptions.HideNoticeShown)
        {
            _trayIcon.ShowNotice(
                "Glosslay はタスクトレイで動いています",
                "ホットキーはこのまま使えます。終了するには、このアイコンを右クリックしてください。");
            _trayOptions = _trayOptions with { HideNoticeShown = true };
            _trayOptions.Save();
        }
    }

    /// <summary>本当に終了する（トレイの「終了」）。</summary>
    public void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>Windows のサインアウト・シャットダウン中。しまわずに閉じさせる。</summary>
    public void AllowExit() => _exitRequested = true;

    /// <summary>トレイから画面を出す。</summary>
    private void OpenFromTray()
    {
        ShowInTaskbar = true;
        ShowActivated = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>トレイの右クリックのメニュー。</summary>
    /// <remarks>
    /// <b>画面を開かなくても使いたいものだけを置く。</b>ホットキーを覚えていない人のために、
    /// 割り当てを灰色の説明として並べる（押せない行）。
    /// </remarks>
    private void ShowTrayMenu()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var items = new List<TrayMenuItem>
        {
            new(TrayMenuOpen, "画面を開く") { IsDefault = true },
            new(TrayMenuAutoWatch, "自動監視") { IsChecked = AutoWatchCheck.IsChecked == true },
        };

        if (_inputTrigger is RawInputTrigger { IsActive: true } trigger && trigger.Descriptions.Count > 0)
        {
            items.Add(TrayMenuItem.Separator);
            var number = 100;
            foreach (var description in trigger.Descriptions)
            {
                items.Add(new TrayMenuItem(number++, description) { IsEnabled = false });
            }
        }

        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(TrayMenuExit, "終了"));

        switch (_trayIcon.ShowMenu(items))
        {
            case TrayMenuOpen:
                OpenFromTray();
                break;

            case TrayMenuAutoWatch:
                ToggleAutoWatchFromTray();
                break;

            case TrayMenuExit:
                ExitApplication();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// トレイから自動監視を切り替える。
    /// </summary>
    /// <remarks>
    /// <b>初めて入れるときは画面を出す。</b>FR-MOD-08 の案内は確認の窓で、
    /// 画面が隠れたままでは読めない（読まずに始めてはいけない。RULES.md 🟡-8）。
    /// </remarks>
    private void ToggleAutoWatchFromTray()
    {
        var enable = AutoWatchCheck.IsChecked != true;
        if (enable && !_watchOptions.FirstEnableNoticeShown)
        {
            OpenFromTray();
        }

        AutoWatchCheck.IsChecked = enable;
    }

    /// <summary>設定画面の「× を押したとき」と「自動起動」を、いまの状態に合わせる。</summary>
    private void RefreshTraySettings()
    {
        _isRefreshingTraySettings = true;
        try
        {
            var behavior = _trayOptions.CloseBehavior.ToString();
            SelectByTag(CloseBehaviorCombo, behavior, behavior);
            AutoStartCheck.IsChecked = _autoStart.IsEnabled;
            ShowAutoStartNote();
        }
        finally
        {
            _isRefreshingTraySettings = false;
        }
    }

    private void OnCloseBehaviorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingTraySettings
            || !Enum.TryParse<CloseBehavior>(TagOf(CloseBehaviorCombo), out var behavior))
        {
            return;
        }

        _trayOptions = _trayOptions with { CloseBehavior = behavior };
        _trayOptions.Save();
        StatusText.Text = behavior == CloseBehavior.HideToTray
            ? "× を押すとタスクトレイにしまうようにしました。"
            : "× を押すとアプリを終了するようにしました。";
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingTraySettings || Environment.ProcessPath is not { Length: > 0 } path)
        {
            return;
        }

        var enabled = AutoStartCheck.IsChecked == true;
        try
        {
            _autoStart.Set(enabled, path);
            StatusText.Text = enabled
                ? "Windows の起動時に自動で起動するようにしました（タスクトレイで待機します）。"
                : "自動起動をやめました。";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            StatusText.Text = "自動起動の設定を変えられませんでした: " + ex.Message;
        }

        RefreshTraySettings();
    }

    /// <summary>
    /// 自動起動の登録が、いまの実行ファイルとは別の場所を指していれば知らせる。
    /// </summary>
    /// <remarks>
    /// ZIP 版を別のフォルダへ展開し直すと、古い場所を指したまま残る。
    /// チェックが入っているのに起動しない、という状態を見えるようにする。
    /// </remarks>
    private void ShowAutoStartNote()
    {
        var stale = _autoStart.IsEnabled
                    && Environment.ProcessPath is { Length: > 0 } path
                    && !_autoStart.PointsTo(path);

        AutoStartNote.Text = stale
            ? "登録されているのは別の場所の Glosslay です。入れ直すと、いま使っているものに切り替わります。"
            : string.Empty;
        AutoStartNote.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>キャプチャセッションと OCR エンジンを解放する。ウィンドウを閉じたときに呼ぶ。</summary>
    public void Dispose()
    {
        _hwndSource?.RemoveHook(OnWindowMessage);
        _hwndSource = null;
        _trayIcon?.Dispose();
        _trayIcon = null;

        _watchTimer?.Stop();
        _watchTimer = null;
        _dwellTimer?.Stop();
        _dwellTimer = null;
        _profileTimer?.Stop();
        _profileTimer = null;
        _usageTimer?.Stop();
        _usageTimer = null;
        _usageOverlay?.Close();
        _usageOverlay = null;
        ReleaseCapture();
        _captureStartLock.Dispose();
        _overlay?.Close();
        _inputTrigger?.Dispose();
        _ocrEngine.Dispose();
        _cloudTranslator.Dispose();
        _localTranslator.Dispose();
        GC.SuppressFinalize(this);
    }

    // ===== 対象の選択 =====

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshTargets();

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        RefreshTargets();
    }

    /// <summary>枠の有無・カーソルの有無はセッション生成時に決まるため、変更したら作り直す。</summary>
    private void OnOptionChanged(object sender, RoutedEventArgs e) => ReleaseCapture();

    private void OnTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ReleaseCapture();
        UpdateProfileTargetText();
    }

    private void RefreshTargets()
    {
        var targets = MonitorModeRadio.IsChecked == true
            ? CaptureTargetEnumerator.EnumerateMonitors()
            : CaptureTargetEnumerator.EnumerateWindows();

        TargetList.ItemsSource = targets;
        if (targets.Count > 0)
        {
            TargetList.SelectedIndex = 0;
        }

        StatusText.Text = $"対象 {targets.Count} 件を検出しました。";
    }

    // ===== キャプチャ =====

    /// <summary>
    /// 撮影から訳文の重ね合わせまでを一括で行う（ホットキーと同じ経路）。
    /// </summary>
    /// <remarks>
    /// <b>これが日常の操作。</b>下の「工程ごとに実行する」は切り分けて見るためのもので、
    /// ふだんは使わない。<b>同じことをホットキーでもできる</b>ため、
    /// この画面を出していない状態が本来の使い方。
    /// </remarks>
    private async void OnTranslateScreenClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(TranslateScreenAsync).ConfigureAwait(true);

    private async void OnCaptureNowClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(() => CaptureAsync(TimeSpan.Zero)).ConfigureAwait(true);

    private async void OnCaptureDelayedClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(() => CaptureAsync(CaptureDelay)).ConfigureAwait(true);

    private async Task CaptureAsync(TimeSpan delay)
    {
        if (TargetList.SelectedItem is not CaptureTarget target)
        {
            StatusText.Text = "対象が選択されていません。";
            return;
        }

        SetBusy(true);
        try
        {
            for (var remaining = (int)delay.TotalSeconds; remaining > 0; remaining--)
            {
                StatusText.Text = $"{remaining} 秒後にキャプチャします… 対象のゲームへ切り替えてください。";
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            }

            StatusText.Text = "キャプチャ中…";

            var startWatch = Stopwatch.StartNew();
            var capture = await EnsureCaptureAsync(target).ConfigureAwait(true);
            var startMs = startWatch.Elapsed.TotalMilliseconds;

            var frameWatch = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await capture.GetNextFrameAsync(timeout.Token).ConfigureAwait(true);
            var frameMs = frameWatch.Elapsed.TotalMilliseconds;

            ShowFrame(target, capture, frame, startMs, frameMs);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "フレームが 5 秒以内に届きませんでした。"
                + "排他フルスクリーンの場合は「モニタ全体」へ切り替えて試してください（FR-CAP-03）。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// セッションは開始したまま保持する。作り直すと初回フレームまで数百 ms かかるため。
    /// </summary>
    /// <remarks>
    /// 保持している間の 1 枚は 10〜20ms、作成からだと 293ms（2026-10-13 の実測）。
    /// 作成の費用を押した瞬間に払わないよう、<see cref="PrewarmCapture"/> で前倒しする。
    /// </remarks>
    private async Task<ScreenCapture> EnsureCaptureAsync(CaptureTarget target)
    {
        await _captureStartLock.WaitAsync().ConfigureAwait(true);
        try
        {
            var removeBorder = RemoveBorderCheck.IsChecked == true;
            var captureCursor = CaptureCursorCheck.IsChecked == true;

            if (_capture is not null
                && _activeTarget == target
                && _activeRemoveBorder == removeBorder
                && _activeCaptureCursor == captureCursor)
            {
                return _capture;
            }

            ReleaseCapture();

            var capture = await ScreenCapture.StartAsync(target, removeBorder, captureCursor).ConfigureAwait(true);
            capture.Closed += OnCaptureClosed;

            _capture = capture;
            _activeTarget = target;
            _activeRemoveBorder = removeBorder;
            _activeCaptureCursor = captureCursor;
            return capture;
        }
        finally
        {
            _captureStartLock.Release();
        }
    }

    /// <summary>
    /// 撮影のセッションを先に作っておく。最初のホットキーで 293ms 待たせないため。
    /// </summary>
    /// <remarks>
    /// <para><b>プロファイルの当たったゲームが前面に来たときだけ行う。</b>
    /// 「このゲームを翻訳する」と利用者が決めている、確かな合図だから。
    /// 一覧の選択では行わない。起動時に一覧の先頭が自動で選ばれるため、
    /// 関係の無いウィンドウを撮り始め、枠を消せない環境では黄色い枠まで出てしまう。</para>
    /// <para><b>失敗しても何も言わない。</b>前倒しは速くするためだけのもので、
    /// 撮れなければホットキーを押したときに改めて作り、そこで理由を出す。</para>
    /// </remarks>
    private async void PrewarmCapture(CaptureTarget target)
    {
        try
        {
            await EnsureCaptureAsync(target).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 前倒しの失敗は、押したときの経路が改めて扱う（上の注記）。
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void OnCaptureClosed(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "対象が閉じられました。一覧を更新してください。";
            ReleaseCapture();
        });

    private void ShowFrame(
        CaptureTarget target, ScreenCapture capture, CapturedFrame frame, double startMs, double frameMs)
    {
        UpdatePreview(target, frame);

        var nonBlackRatio = CalculateNonBlackRatio(frame);
        var blackWarning = nonBlackRatio < 0.001
            ? "  ← ほぼ黒。取得に失敗している可能性が高い（FR-CAP-03）"
            : string.Empty;

        ResultText.Text = string.Join(Environment.NewLine,
            $"対象           : {target}",
            $"サイズ         : {frame.Width} x {frame.Height} (stride={frame.Stride})",
            $"枠を消せたか   : {(capture.IsBorderRemoved ? "はい" : "いいえ（OS が許可しなかった）")}",
            $"セッション開始 : {startMs:F1} ms",
            $"フレーム取得   : {frameMs:F1} ms",
            $"非黒ピクセル   : {nonBlackRatio * 100:F1} %{blackWarning}");

        StatusText.Text = "キャプチャしました。";
    }

    /// <summary>プレビューを最新のフレームに差し替える。結果欄は書き換えない。</summary>
    private void UpdatePreview(CaptureTarget target, CapturedFrame frame)
    {
        _lastFrame = frame;
        _lastTarget = target;
        _lastBitmap = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
        _lastBitmap.Freeze();

        PreviewImage.Source = _lastBitmap;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = true;
        OcrButton.IsEnabled = true;
        OverlayButton.IsEnabled = true;

        // プロファイルで当てた範囲は、この時点で初めて描ける
        // （適用した時点ではまだプレビューが無い）。
        ShowRegionRectangle();
    }

    /// <summary>取得できたのが黒画面でないかの判定材料（FR-CAP-03）。</summary>
    private static double CalculateNonBlackRatio(CapturedFrame frame)
    {
        long nonBlack = 0;
        var pixels = frame.Pixels;
        for (var i = 0; i + 2 < pixels.Length; i += 4)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
            {
                nonBlack++;
            }
        }

        var total = (long)frame.Width * frame.Height;
        return total == 0 ? 0 : (double)nonBlack / total;
    }

    // ===== 前処理と範囲指定（P0-3） =====

    /// <summary>
    /// UI の設定から前処理のオプションを組み立てる。
    /// </summary>
    /// <remarks>
    /// <b>画面に出していない項目は <see cref="_preprocessBase"/> から来る。</b>
    /// 補間方法と背景除去の窓には操作子が無く、ここで組み直すと
    /// プロファイルに書いた値が黙って既定へ戻る。
    /// </remarks>
    private PreprocessOptions BuildPreprocessOptions() => _preprocessBase with
    {
        Scale = ComboValue(ScaleCombo, 1.0),
        Contrast = ComboValue(ContrastCombo, 1.0),
        AutoContrast = AutoContrastCheck.IsChecked == true,
        RemoveBackground = RemoveBackgroundCheck.IsChecked == true,
        Binarize = BinarizeCheck.IsChecked == true,
        Invert = InvertCheck.IsChecked == true,
    };

    private static double ComboValue(System.Windows.Controls.ComboBox combo, double fallback) =>
        combo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag }
        && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>
    /// フレームを OCR にかけられる形にする。範囲指定があれば切り出し、前処理を適用する。
    /// </summary>
    /// <param name="frame">撮った画面。</param>
    /// <param name="useRegion">
    /// プレビュー上でドラッグした範囲を使うか。ホットキーでは <c>false</c> にする。
    /// 範囲は一覧で選んだ対象の画像に対して引いたもので、前面の別ウィンドウに当てると
    /// 見当違いの場所を切り抜くため。
    /// </param>
    /// <returns>加工後の画像と、元画像へ座標を戻すための情報。</returns>
    private (Bgra32Image Image, PreprocessOptions Options, int OffsetX, int OffsetY)
        PrepareForOcr(CapturedFrame frame, bool useRegion = true)
    {
        var image = frame.AsImage();
        var offsetX = 0;
        var offsetY = 0;

        if (useRegion && _region is { } region)
        {
            // プロファイルの範囲は、保存したときと解像度が違えば画面の外に出る。
            // 入れ直してから使い、ずれたことが分かるよう指定自体も直す。
            var clipped = ClipRegion(region, image.Width, image.Height);
            if (clipped != region)
            {
                _region = clipped;
                ShowRegionRectangle();
            }

            if (clipped is { } fitted)
            {
                // 範囲を絞ると検出コストが大きく下がる。全画面の 1/3 程度になる。
                image = image.Crop(fitted.X, fitted.Y, fitted.Width, fitted.Height);
                offsetX = fitted.X;
                offsetY = fitted.Y;
            }
        }

        var options = BuildPreprocessOptions();
        return (ImagePreprocessor.Apply(image, options), options, offsetX, offsetY);
    }

    /// <summary>
    /// 指定した矩形だけを切り出して OCR にかける（カーソル位置モード）。
    /// </summary>
    /// <remarks>
    /// プレビュー上でドラッグした範囲（<c>_region</c>）とは別の経路。
    /// こちらはホットキーを押した瞬間のカーソル位置から決まる。
    /// </remarks>
    private (Bgra32Image Image, PreprocessOptions Options, int OffsetX, int OffsetY)
        PrepareForOcr(CapturedFrame frame, FrameRect region)
    {
        var image = frame.AsImage().Crop(region.X, region.Y, region.Width, region.Height);
        var options = BuildPreprocessOptions();
        return (ImagePreprocessor.Apply(image, options), options, region.X, region.Y);
    }

    /// <summary>OCR の結果を、加工後の座標から元画像の座標へ戻す。</summary>
    private static IReadOnlyList<OcrLine> MapToSource(
        IReadOnlyList<OcrLine> lines, double scale, int offsetX, int offsetY)
    {
        if (scale is 1.0 && offsetX == 0 && offsetY == 0)
        {
            return lines;
        }

        return [.. lines.Select(line => line with
        {
            Box = new OcrBox(
                (int)(line.Box.X / scale) + offsetX,
                (int)(line.Box.Y / scale) + offsetY,
                (int)(line.Box.Width / scale),
                (int)(line.Box.Height / scale)),
        })];
    }

    /// <summary>
    /// 範囲を画像の中へ収める。収まる大きさが残らなければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>プロファイルの範囲は、保存したときと解像度が違えば画面の外に出る。</b>
    /// 1920x1080 で保存した範囲を 1280x720 の画面に当てると
    /// <c>Bgra32Image.Crop</c> が例外を投げ、翻訳そのものが失敗する。
    /// </remarks>
    private static Int32Rect? ClipRegion(Int32Rect region, int width, int height)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(0, width));
        var y = Math.Clamp(region.Y, 0, Math.Max(0, height));
        var clippedWidth = Math.Min(region.Width, width - x);
        var clippedHeight = Math.Min(region.Height, height - y);

        return clippedWidth >= MinRegionSize && clippedHeight >= MinRegionSize
            ? new Int32Rect(x, y, clippedWidth, clippedHeight)
            : null;
    }

    /// <summary>
    /// いまの範囲指定をプレビューに描く。
    /// </summary>
    /// <remarks>
    /// プロファイルで範囲を当てたとき、<b>切り抜く場所が見えないと確かめようがない。</b>
    /// プレビューがまだ無いときは描かない（次のキャプチャで描ける）。
    /// </remarks>
    private void ShowRegionRectangle()
    {
        if (_region is not { } region
            || _lastBitmap is null
            || !TryGetPreviewLayout(out var layout))
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        var scaleX = layout.DisplayWidth / _lastBitmap.PixelWidth;
        var scaleY = layout.DisplayHeight / _lastBitmap.PixelHeight;

        System.Windows.Controls.Canvas.SetLeft(
            SelectionRectangle, layout.MarginX + (region.X * scaleX));
        System.Windows.Controls.Canvas.SetTop(
            SelectionRectangle, layout.MarginY + (region.Y * scaleY));

        SelectionRectangle.Width = region.Width * scaleX;
        SelectionRectangle.Height = region.Height * scaleY;
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private void OnClearRegionClick(object sender, RoutedEventArgs e)
    {
        _region = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        StatusText.Text = "範囲の指定を解除しました。画像全体を OCR します。";
    }

    private void OnSelectionStart(object sender, MouseButtonEventArgs e)
    {
        if (_lastBitmap is null)
        {
            return;
        }

        _isDragging = true;
        _dragOrigin = e.GetPosition(SelectionCanvas);
        SelectionCanvas.CaptureMouse();

        SelectionRectangle.Visibility = Visibility.Visible;
        UpdateSelectionRectangle(_dragOrigin, _dragOrigin);
    }

    private void OnSelectionMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            UpdateSelectionRectangle(_dragOrigin, e.GetPosition(SelectionCanvas));
        }
    }

    private void OnSelectionEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        SelectionCanvas.ReleaseMouseCapture();

        var end = e.GetPosition(SelectionCanvas);
        UpdateSelectionRectangle(_dragOrigin, end);

        _region = ToSourceRegion(_dragOrigin, end);
        if (_region is { } region)
        {
            StatusText.Text = $"範囲を指定しました: {region.X},{region.Y} "
                + $"{region.Width}x{region.Height}（元画像の画素）";
        }
        else
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            StatusText.Text = "範囲が小さすぎます。指定を解除しました。";
        }
    }

    private void UpdateSelectionRectangle(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);

        System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, x);
        System.Windows.Controls.Canvas.SetTop(SelectionRectangle, y);
        SelectionRectangle.Width = Math.Abs(a.X - b.X);
        SelectionRectangle.Height = Math.Abs(a.Y - b.Y);
    }

    /// <summary>プレビューに実際に描かれている画像の位置と大きさ。</summary>
    private readonly record struct PreviewLayout(
        double MarginX, double MarginY, double DisplayWidth, double DisplayHeight);

    /// <summary>
    /// プレビューに描かれている画像の位置と大きさを求める。
    /// </summary>
    /// <remarks>
    /// プレビューは Stretch="Uniform" のため上下または左右に余白ができる。
    /// <b>座標の行きと帰りで同じ計算を使う。</b>片方だけ直すとずれる。
    /// </remarks>
    private bool TryGetPreviewLayout(out PreviewLayout layout)
    {
        layout = default;
        if (_lastBitmap is null)
        {
            return false;
        }

        var controlWidth = SelectionCanvas.ActualWidth;
        var controlHeight = SelectionCanvas.ActualHeight;
        if (controlWidth <= 0 || controlHeight <= 0)
        {
            return false;
        }

        var imageAspect = (double)_lastBitmap.PixelWidth / _lastBitmap.PixelHeight;
        var controlAspect = controlWidth / controlHeight;

        double displayWidth, displayHeight;
        if (imageAspect > controlAspect)
        {
            displayWidth = controlWidth;
            displayHeight = controlWidth / imageAspect;
        }
        else
        {
            displayHeight = controlHeight;
            displayWidth = controlHeight * imageAspect;
        }

        layout = new PreviewLayout(
            (controlWidth - displayWidth) / 2,
            (controlHeight - displayHeight) / 2,
            displayWidth,
            displayHeight);

        return true;
    }

    /// <summary>
    /// プレビュー上の座標を元画像の画素座標へ変換する。
    /// </summary>
    /// <remarks>
    /// 余白を除いてから比率を掛けないと、指定した位置とずれる
    /// （<see cref="TryGetPreviewLayout"/>）。
    /// </remarks>
    private Int32Rect? ToSourceRegion(Point a, Point b)
    {
        if (_lastBitmap is null || !TryGetPreviewLayout(out var layout))
        {
            return null;
        }

        var left = ToSource(
            Math.Min(a.X, b.X), layout.MarginX, layout.DisplayWidth, _lastBitmap.PixelWidth);
        var top = ToSource(
            Math.Min(a.Y, b.Y), layout.MarginY, layout.DisplayHeight, _lastBitmap.PixelHeight);
        var right = ToSource(
            Math.Max(a.X, b.X), layout.MarginX, layout.DisplayWidth, _lastBitmap.PixelWidth);
        var bottom = ToSource(
            Math.Max(a.Y, b.Y), layout.MarginY, layout.DisplayHeight, _lastBitmap.PixelHeight);

        var width = right - left;
        var height = bottom - top;

        // 極端に小さい範囲は誤操作とみなす。
        return width >= MinRegionSize && height >= MinRegionSize
            ? new Int32Rect(left, top, width, height)
            : null;

        static int ToSource(double value, double margin, double display, int sourceSize) =>
            Math.Clamp((int)Math.Round((value - margin) / display * sourceSize), 0, sourceSize);
    }

    // ===== OCR =====

    private async void OnOcrClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(RunOcrAsync).ConfigureAwait(true);

    private async Task RunOcrAsync()
    {
        if (_lastFrame is null || _lastBitmap is null)
        {
            StatusText.Text = "先にキャプチャしてください。";
            return;
        }

        OcrButton.IsEnabled = false;
        StatusText.Text = "OCR 実行中…";
        try
        {
            var (image, options, offsetX, offsetY) = PrepareForOcr(_lastFrame);
            var result = await _ocrEngine.RecognizeAsync(image).ConfigureAwait(true);
            var lines = MapToSource(result.Lines, options.Scale, offsetX, offsetY);

            _lastOcrLines = lines;
            TranslateButton.IsEnabled = lines.Count > 0;

            PreviewImage.Source = DrawBoxes(_lastBitmap, lines);
            ResultText.Text = string.Join(Environment.NewLine,
                $"前処理     : {options}",
                $"OCR 入力   : {image.Width}x{image.Height}"
                    + (_region is { } r ? $"（範囲 {r.X},{r.Y} {r.Width}x{r.Height} を切り出し）" : "（全体）"),
                $"処理時間   : {result.Elapsed.TotalMilliseconds:F0} ms",
                string.Empty,
                FormatOcrResult(lines));

            StatusText.Text = $"OCR 完了: {lines.Count} 行 / "
                + $"{result.Elapsed.TotalMilliseconds:F0} ms（{_ocrEngine.Name}）";
        }
        finally
        {
            OcrButton.IsEnabled = true;
        }
    }

    // ===== 翻訳（P0-5） =====

    private void RefreshTranslatorState()
    {
        var configured = _cloudTranslator.IsConfigured;

        // 選択をラジオへ反映する。Checked が発火して保存し直さないよう、先に外す。
        _suppressBackendChange = true;
        BackendAutoRadio.IsChecked = _translationOptions.Backend == TranslationBackendChoice.Auto;
        BackendCloudRadio.IsChecked = _translationOptions.Backend == TranslationBackendChoice.CloudOnly;
        BackendLocalRadio.IsChecked = _translationOptions.Backend == TranslationBackendChoice.LocalOnly;
        _suppressBackendChange = false;

        // いま何で動くのかを出す。キーが無くても「使えない」ではないことを見せる。
        var active = ActiveTranslator;
        TranslatorStateText.Text = active.IsConfigured
            ? $"{active.Name} — 使用中"
            : ReferenceEquals(active, _cloudTranslator)
                ? $"{active.Name} — キー未登録のため訳せません（「自動」にすればローカルで動きます）"
                : $"{active.Name} — モデル未配置";

        DeleteApiKeyButton.IsEnabled = configured;

        // キーの有無とバックエンドの選択で、選んだ言語が訳せるかは変わる。
        // 一覧がまだできていない起動直後は何もしない（OnLoaded が後で呼ぶ）。
        if (TargetLanguageCombo.ItemsSource is not null)
        {
            UpdateTargetLanguageNote();
        }
    }

    private void OnSaveApiKeyClick(object sender, RoutedEventArgs e)
    {
        // Password は読んだ直後に使い切り、変数にも UI にも残さない。
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            StatusText.Text = "APIキーが空です。";
            return;
        }

        _apiKeyStore.Save(ApiKeyBox.Password);
        ApiKeyBox.Clear();

        RefreshTranslatorState();
        StatusText.Text = "APIキーを暗号化して保存しました。「翻訳を実行」で試せます。";
    }

    /// <summary>バックエンドの選択が変わった（FR-TRN-01 / FR-TRN-06）。</summary>
    /// <remarks>
    /// <b>押した時点で保存する。</b>「適用」を押し忘れて効いていない、という状態を作らない。
    /// </remarks>
    private void OnBackendChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressBackendChange)
        {
            return;
        }

        var choice = sender switch
        {
            _ when ReferenceEquals(sender, BackendCloudRadio) => TranslationBackendChoice.CloudOnly,
            _ when ReferenceEquals(sender, BackendLocalRadio) => TranslationBackendChoice.LocalOnly,
            _ => TranslationBackendChoice.Auto,
        };

        _translationOptions = _translationOptions with { Backend = choice };
        _translationOptions.Save();

        // 覚えている訳文はバックエンドごとに分かれているが、選択を変えた直後に
        // 古い訳が出ると「切り替わっていない」と見える。まとめて忘れる。
        _translationCache.Clear();
        RefreshTranslatorState();

        StatusText.Text = choice switch
        {
            TranslationBackendChoice.CloudOnly => "クラウドのみを使います。失敗しても切り替えません。",
            TranslationBackendChoice.LocalOnly => "ローカルのみを使います。通信しません。",
            _ => "自動で切り替えます（クラウドが失敗したらローカルへ）。",
        };
    }

    // ===== 使用量（RULES.md 🟡-8） =====

    /// <summary>
    /// 翻訳の呼び出しを使用量に数える（オーバーレイ経路）。
    /// </summary>
    /// <remarks>
    /// <para><b>切り替えが起きた場合は 2 回ぶん数える。</b>クラウドで失敗してローカルで成功したとき、
    /// <b>クラウドへの通信は実際に発生している</b>。失敗したから無かったことにはならない。</para>
    /// <para>呼んだ回数は <see cref="LineTranslationResult.CallCount"/> を見る。
    /// 全部キャッシュに当たった場合は 0 で、そのときは何も数えない。</para>
    /// </remarks>
    private void RecordUsage(LineTranslationResult result)
    {
        if (result.FellBackFrom is { } failed)
        {
            _usage.RecordCloud(failed.TotalTokens, succeeded: false);
        }

        // 切り替えが起きた場合、CallCount は 2。上で 1 回ぶん数えたので残りは 1 回。
        if (result.CallCount > (result.FellBackFrom is null ? 0 : 1))
        {
            RecordOneCall(result.Backend, result.Translation);
        }

        _usage.RecordCached(result.CachedCount);
        RefreshUsage();
    }

    /// <summary>翻訳の呼び出しを 1 回ぶん数える（検証用ボタンの経路）。</summary>
    private void RecordOneCall(string backend, TranslationResult translation)
    {
        if (IsCloudBackend(backend))
        {
            _usage.RecordCloud(translation.TotalTokens, translation.IsSuccess);
        }
        else
        {
            _usage.RecordLocal();
        }
    }

    /// <summary>
    /// 費用がかかるバックエンドか。
    /// </summary>
    /// <remarks>
    /// <b><see cref="ITranslator"/> に「費用がかかるか」は持たせていない。</b>
    /// 差し替え点を増やさずに済むうえ、名前は FR-TRN-13 のバッジで既に画面へ出しているものと同じで、
    /// <b>利用者が見ている文字列と数えている基準が一致する。</b>
    /// </remarks>
    private bool IsCloudBackend(string backend) =>
        string.Equals(backend, _cloudTranslator.Name, StringComparison.Ordinal);

    /// <summary>画面に出ている使用量を今の値にする。</summary>
    private void RefreshUsage()
    {
        var today = _usage.Today;
        var remaining = _autoCallLimiter.Remaining;
        var maxCalls = _autoCallLimiter.MaxCalls;

        UsageText.Text = UsageSummary.Detail(today, remaining, maxCalls);

        // 訳文に添える 1 行と、常駐表示は同じ文言を使う（食い違わせない）。
        var oneLine = UsageSummary.OneLine(today, remaining, maxCalls);
        if (_overlay is { } overlay)
        {
            overlay.UsageNote = oneLine;
        }

        _usageOneLine = oneLine;
        UpdateCornerOverlayText();
    }

    /// <summary>
    /// 画面の隅の常駐表示の中身を今の値にする。出す設定にしていない行は出さない。
    /// </summary>
    private void UpdateCornerOverlayText() =>
        _usageOverlay?.Update(
            _usageOverlayOptions.Enabled ? _usageOneLine : null,
            _usageOverlayOptions.ShowProcessingTime ? _processingTimes.OneLine : null);

    /// <summary>
    /// 使用量の常駐表示を出す / 消す（RULES.md 🟡-8）。
    /// </summary>
    /// <remarks>
    /// <b>既定は OFF。</b>出しっぱなしの表示は「モニタが1枚でも邪魔されずに遊べる」という
    /// ゴールと衝突するため、欲しい人だけが入れる形にしてある（2026-10-12 の人間の判断）。
    /// </remarks>
    private void SetUsageOverlay(bool enabled)
    {
        if (_usageOverlayOptions.Enabled != enabled)
        {
            _usageOverlayOptions = _usageOverlayOptions with { Enabled = enabled };
            _usageOverlayOptions.Save();
        }

        StatusText.Text = "使用量の常駐表示: " + ApplyCornerOverlay(enabled);
    }

    /// <summary>
    /// 処理時間の実測を画面の隅に出す / 消す（FR-OVL-10）。
    /// </summary>
    /// <remarks>
    /// <b>既定は OFF</b>（録画・配信に映り込まないように）。使用量と同じ隅に並べる。
    /// </remarks>
    private void SetProcessingTimeOverlay(bool enabled)
    {
        if (_usageOverlayOptions.ShowProcessingTime != enabled)
        {
            _usageOverlayOptions = _usageOverlayOptions with { ShowProcessingTime = enabled };
            _usageOverlayOptions.Save();
        }

        StatusText.Text = "処理時間の表示: " + ApplyCornerOverlay(enabled);
    }

    private void OnProcessingTimeOverlayChanged(object sender, RoutedEventArgs e) =>
        SetProcessingTimeOverlay(ProcessingTimeCheck.IsChecked == true);

    /// <summary>
    /// 隅のウィンドウを、出すものがあれば出し、無ければ閉じる。結果を一言で返す。
    /// </summary>
    /// <param name="turnedOn">いま入れた操作か（消した操作なら「消しました」と返す）。</param>
    private string ApplyCornerOverlay(bool turnedOn)
    {
        if (!_usageOverlayOptions.AnyVisible)
        {
            _usageOverlay?.Close();
            _usageOverlay = null;
            return "消しました。";
        }

        _usageOverlay ??= new UsageOverlayWindow();
        _usageOverlay.Show();
        _usageOverlay.Apply(_usageOverlayOptions);
        RefreshUsage();

        if (!turnedOn)
        {
            return "消しました。";
        }

        // クリックスルーが効いていなければ、ゲームのクリックを奪う。黙って出さない。
        return _usageOverlay.StyleState.IsComplete
            ? "画面の隅に出しました（クリックは下へ通ります）。"
            : "クリックスルーが効いていません。消すことをおすすめします。";
    }

    private void OnUsageOverlayChanged(object sender, RoutedEventArgs e) =>
        SetUsageOverlay(UsageOverlayCheck.IsChecked == true);

    private void OnOpenUsageClick(object sender, RoutedEventArgs e)
    {
        var path = JsonSettings.PathOf(UsageStore.FileName);
        if (!File.Exists(path))
        {
            StatusText.Text = "まだ記録がありません。翻訳を 1 回実行すると作られます。";
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        StatusText.Text = $"usage.json を開きました（直近 {UsageStore.KeepDays} 日ぶん。送信はしません）。";
    }

    // ===== 細かい設定（FR-MOD-03 / 11 / 12・FR-OVL-05 / 06） =====

    /// <summary>
    /// 設定の値を一覧へ反映する。
    /// </summary>
    /// <remarks>
    /// <b>一覧に無い値でも捨てない。</b>設定ファイルは手で編集できるため
    /// （RULES.md 🔵）、選択肢に無い値が入っていることがある。
    /// その場合は項目を足して選択状態にし、<b>書いた値がそのまま効いている</b>ことを見せる。
    /// </remarks>
    private static void SelectByTag(System.Windows.Controls.ComboBox combo, string tag, string display)
    {
        foreach (var item in combo.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (item.Tag is string existing
                && string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        var added = new System.Windows.Controls.ComboBoxItem { Content = display, Tag = tag };
        combo.Items.Add(added);
        combo.SelectedItem = added;
    }

    /// <summary>選ばれている項目の <c>Tag</c>。</summary>
    private static string? TagOf(System.Windows.Controls.ComboBox combo) =>
        combo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag } ? tag : null;

    /// <summary>細かい設定の一覧を、保存されている値に合わせる。</summary>
    private void RefreshTuning()
    {
        _suppressTuningChange = true;

        var interval = _watchOptions.Interval.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        SelectByTag(WatchIntervalCombo, interval, $"{_watchOptions.Interval.TotalSeconds:0.#} 秒");

        var threshold = _watchOptions.Threshold.ToString("0.###", CultureInfo.InvariantCulture);
        SelectByTag(WatchThresholdCombo, threshold, threshold);

        var dwell = _cursorOptions.DwellTime.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        SelectByTag(DwellTimeCombo, dwell, $"{_cursorOptions.DwellTime.TotalSeconds:0.#} 秒");

        var region = $"{_cursorOptions.Width}x{_cursorOptions.Height}";
        SelectByTag(CursorRegionCombo, region, region);

        SelectByTag(TextSizeCombo, _overlayOptions.TextSize.ToString(), _overlayOptions.TextSize.ToString());

        var hide = _overlayOptions.AutoHideAfter.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        SelectByTag(AutoHideCombo, hide, $"{hide} 秒");

        SelectByTag(UsageCornerCombo, _usageOverlayOptions.Corner.ToString(),
            _usageOverlayOptions.Corner.ToString());

        _suppressTuningChange = false;
    }

    private void OnWatchTuningChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTuningChange)
        {
            return;
        }

        if (double.TryParse(TagOf(WatchIntervalCombo), CultureInfo.InvariantCulture, out var ms))
        {
            _watchOptions = _watchOptions with { Interval = TimeSpan.FromMilliseconds(ms) };
        }

        if (double.TryParse(TagOf(WatchThresholdCombo), CultureInfo.InvariantCulture, out var threshold))
        {
            _watchOptions = _watchOptions with { Threshold = threshold };
        }

        _watchOptions.Save();

        // 動いている最中なら作り直す。次に ON にするまで効かないと、変えた結果が分からない。
        if (AutoWatchCheck.IsChecked == true)
        {
            SetAutoWatch(true);
        }

        StatusText.Text = $"自動監視: {_watchOptions.Interval.TotalSeconds:0.#} 秒ごと / 感度 {_watchOptions.Threshold:0.###}";
    }

    private void OnCursorTuningChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTuningChange)
        {
            return;
        }

        if (double.TryParse(TagOf(DwellTimeCombo), CultureInfo.InvariantCulture, out var ms))
        {
            _cursorOptions = _cursorOptions with { DwellTime = TimeSpan.FromMilliseconds(ms) };
        }

        if (TagOf(CursorRegionCombo)?.Split('x') is [var w, var h]
            && int.TryParse(w, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(h, CultureInfo.InvariantCulture, out var height))
        {
            _cursorOptions = _cursorOptions with { Width = width, Height = height };
        }

        _cursorOptions.Save();

        if (CursorDwellCheck.IsChecked == true)
        {
            SetCursorDwell(true);
        }

        StatusText.Text = $"カーソル静止: {_cursorOptions.DwellTime.TotalMilliseconds:F0} ms / "
            + $"{_cursorOptions.Width}x{_cursorOptions.Height}";
    }

    private void OnOverlayLookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTuningChange)
        {
            return;
        }

        if (Enum.TryParse<OverlayTextSize>(TagOf(TextSizeCombo), out var size))
        {
            _overlayOptions = _overlayOptions with { TextSize = size };
        }

        if (double.TryParse(TagOf(AutoHideCombo), CultureInfo.InvariantCulture, out var seconds))
        {
            _overlayOptions = _overlayOptions with { AutoHideAfter = TimeSpan.FromSeconds(seconds) };
        }

        _overlayOptions.Save();

        // 出している最中なら描き直す。次の翻訳まで分からないと、大きさを選び直せない。
        if (_overlay is { } overlay)
        {
            overlay.Options = _overlayOptions;
            overlay.Refresh();
        }

        StatusText.Text = $"訳文の見た目: 文字 {TextSizeLabel(_overlayOptions.TextSize)} / "
            + $"{_overlayOptions.AutoHideAfter.TotalSeconds:F0} 秒で消える";
    }

    private static string TextSizeLabel(OverlayTextSize size) => size switch
    {
        OverlayTextSize.Small => "小",
        OverlayTextSize.Large => "大",
        _ => "中",
    };

    private void OnUsageCornerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTuningChange || !Enum.TryParse<OverlayCorner>(TagOf(UsageCornerCombo), out var corner))
        {
            return;
        }

        _usageOverlayOptions = _usageOverlayOptions with { Corner = corner };
        _usageOverlayOptions.Save();
        _usageOverlay?.Apply(_usageOverlayOptions);
    }

    /// <summary>
    /// 高速モードの認識言語を一覧へ反映する（FR-OCR-02 / 10）。
    /// </summary>
    /// <remarks>
    /// <b>既定のエンジンでは出さない。</b>PaddleOCR は同梱のモデルが
    /// 英・中・日をまとめて扱うため、選ばせる意味がない。
    /// </remarks>
    private void RefreshOcrLanguages()
    {
        var available = WindowsOcrEngine.AvailableLanguages;
        var isFast = _ocrOptions.Engine == OcrEngineChoice.Windows;

        OcrLanguagePanel.Visibility = isFast && available.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isFast || available.Count == 0)
        {
            return;
        }

        _suppressTuningChange = true;
        OcrLanguageCombo.ItemsSource = available.ToArray();
        OcrLanguageCombo.SelectedItem =
            available.FirstOrDefault(t => string.Equals(t, _ocrOptions.WindowsLanguage, StringComparison.OrdinalIgnoreCase))
            ?? available[0];
        _suppressTuningChange = false;
    }

    private void OnOcrLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTuningChange || OcrLanguageCombo.SelectedItem is not string language)
        {
            return;
        }

        _ocrOptions = _ocrOptions with { WindowsLanguage = language };
        _ocrOptions.Save();

        _ocrEngine.Dispose();
        _ocrEngine = CreateOcrEngine();
        RefreshOcrEngineState();

        StatusText.Text = $"原文の言語を {language} にしました。";
        _ = WarmUpOcrAsync();
    }

    private void OnDeleteApiKeyClick(object sender, RoutedEventArgs e)
    {
        _apiKeyStore.Delete();
        ApiKeyBox.Clear();

        RefreshTranslatorState();
        StatusText.Text = "APIキーを削除しました。";
    }

    // ===== 訳文の言語（FR-CFG-01） =====

    /// <summary>
    /// 一覧を作り、いまの値を選ぶ。
    /// </summary>
    /// <remarks>
    /// <b>設定ファイルに一覧外のタグが書かれていても捨てない。</b>
    /// 手で編集できる以上そうなりうるし、黙って日本語へ戻すと
    /// 「書いたのに効いていない」ことに気づけない（RULES.md 🔵）。
    /// </remarks>
    private void SelectTargetLanguage(string tag)
    {
        var options = TargetLanguages.All.ToList();
        if (TargetLanguages.Find(tag) is null)
        {
            options.Add(new TargetLanguageOption(tag, tag, LocalSupported: false));
        }

        _suppressLanguageChange = true;
        TargetLanguageCombo.ItemsSource = options;
        TargetLanguageCombo.SelectedItem =
            options.Find(o => string.Equals(o.Tag, tag, StringComparison.OrdinalIgnoreCase));
        _suppressLanguageChange = false;

        UpdateTargetLanguageNote();
    }

    /// <summary>
    /// 選んだ言語で訳せるのかを、翻訳を試す前に出す。
    /// </summary>
    /// <remarks>
    /// <para><b>日本語以外は同梱のモデルでは訳せない</b>（FR-TRN-03。v1.0 は en-ja のみ）。
    /// 訳してみて初めて「対応していません」と出るのでは遅い。
    /// <b>選んだ時点で分かる</b>ようにする。</para>
    /// <para>「ローカルのみ」を選んでいる場合は、キーがあっても使われないため別に断る。</para>
    /// </remarks>
    private void UpdateTargetLanguageNote()
    {
        var tag = SelectedTargetLanguage;
        if (TargetLanguages.LocalSupports(tag))
        {
            TargetLanguageNote.Text = "APIキーが無くても訳せます。translation.json に保存します。";
            return;
        }

        var name = TargetLanguages.DisplayNameOf(tag);
        TargetLanguageNote.Text = _translationOptions.Backend == TranslationBackendChoice.LocalOnly
            ? $"「ローカルのみ」を選んでいるため、{name}には訳せません。"
                + "下のバックエンドを「自動」にするか、日本語を選んでください。"
            : _cloudTranslator.IsConfigured
                ? $"{name}は同梱のモデルでは訳せないため、クラウド翻訳を使います。"
                : $"{name}は同梱のモデルでは訳せません。下でAPIキーを登録してください。";
    }

    /// <summary>いま選ばれている言語のタグ。</summary>
    private string SelectedTargetLanguage =>
        TargetLanguageCombo.SelectedItem is TargetLanguageOption option
            ? option.Tag
            : _translationOptions.TargetLanguage;

    private void OnTargetLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageChange || TargetLanguageCombo.SelectedItem is not TargetLanguageOption option)
        {
            return;
        }

        _translationOptions = _translationOptions with { TargetLanguage = option.Tag };
        _translationOptions.Save();

        // 覚えている訳文は言語ごとに分かれているが、切り替えた直後に前の言語の訳が
        // 出ると「変わっていない」と見える。まとめて忘れる（FR-TRN-09 のキャッシュ）。
        _translationCache.Clear();

        UpdateTargetLanguageNote();
        StatusText.Text = $"訳文の言語を{option.DisplayName}にしました。";
    }

    /// <summary>
    /// モデル名などの設定ファイルを開く（FR-TRN-07 / RULES.md 🟡-3）。
    /// </summary>
    /// <remarks>
    /// 無ければ既定値で作ってから開く。ファイルが存在しないと「どこを直せばいいのか」が分からない。
    /// 閉じた後に読み直すのではなく、開くたびに翻訳側を作り直して反映させる。
    /// </remarks>
    private void OnOpenGeminiSettingsClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(GlosslayPaths.Root, "gemini.json");
        if (!File.Exists(path))
        {
            GeminiOptions.Load().Save();
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

        // 編集後に「更新」を押させるのは忘れるため、次の翻訳で必ず読み直す形にする。
        _cloudTranslator.Dispose();
        _cloudTranslator = CreateCloudTranslator();
        RefreshTranslatorState();

        StatusText.Text = "gemini.json を開きました。保存すると次の翻訳から反映されます。";
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(RunTranslateAsync).ConfigureAwait(true);

    private async Task RunTranslateAsync()
    {
        if (_lastOcrLines.Count == 0)
        {
            StatusText.Text = "先に OCR を実行してください。";
            return;
        }

        // 読み順に並べ、切れた文を繋ぎ（FR-TRN-10）、改行で繋いで 1 回の呼び出しにまとめる。
        // 行をまとめず 1 行ずつ投げると呼び出し回数が行数倍になり、RULES.md 🟡-8 に反する。
        var source = string.Join(
            Environment.NewLine,
            LineJoiner.Join(TextAggregator.InReadingOrder(_lastOcrLines)).Select(line => line.Text));

        TranslateButton.IsEnabled = false;
        StatusText.Text = "翻訳中…";
        try
        {
            // ここは検証用のボタン。FR-TRN-06 の切り替えは入れていない。
            // どのバックエンドが何を返したかを、隠さずそのまま見るための経路。
            var result = await ActiveTranslator
                .TranslateAsync(new TranslationRequest(source)
                {
                    TargetLanguage = _translationOptions.TargetLanguage,
                })
                .ConfigureAwait(true);

            // RULES.md 🟡-8。検証用のボタンでも呼び出しは起きている。
            RecordOneCall(ActiveTranslator.Name, result);
            RefreshUsage();

            ResultText.Text = string.Join(Environment.NewLine,
                $"バックエンド : {ActiveTranslator.Name}",
                $"処理時間     : {result.Elapsed.TotalMilliseconds:F0} ms"
                    + (result.TotalTokens is { } tokens ? $" / {tokens} トークン" : string.Empty),
                $"状態         : {DescribeOutcome(result)}",
                string.Empty,
                "--- 原文 ---",
                source,
                string.Empty,
                "--- 訳文 ---",
                result.IsSuccess ? result.Text : "（なし）");

            StatusText.Text = result.IsSuccess
                ? $"翻訳完了: {result.Elapsed.TotalMilliseconds:F0} ms"
                : $"翻訳失敗: {result.Message}";
        }
        finally
        {
            TranslateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 結果を利用者向けの文にする。
    /// </summary>
    /// <remarks>
    /// フォールバック対象かどうかを明示している。v1.0 ではここでローカルNMTへ切り替わる
    /// （FR-TRN-06）。<b>切り替え先は実装済み</b>だが、自動で切り替える配線はまだ入れていない。
    /// </remarks>
    private static string DescribeOutcome(TranslationResult result)
    {
        var state = result.Outcome switch
        {
            TranslationOutcome.Succeeded => "成功",
            TranslationOutcome.NotConfigured => "キー未登録",
            TranslationOutcome.RateLimited => "利用上限（HTTP 429）",
            TranslationOutcome.NetworkError => "接続できない",
            TranslationOutcome.Timeout => "応答なし（制限時間切れ）",
            TranslationOutcome.Busy => "混雑（HTTP 503）",
            TranslationOutcome.ServiceError => "サービスエラー",
            TranslationOutcome.Blocked => "応答が拒否された",
            TranslationOutcome.Unsupported => "原文の言語に非対応",
            _ => result.Outcome.ToString(),
        };

        if (result.Message is { Length: > 0 } message)
        {
            state += $" — {message}";
        }

        return result.ShouldFallBack
            ? state + "（FR-TRN-06 でローカルNMTへ自動で切り替える箇所。自動化は未実装）"
            : state;
    }

    private static string FormatOcrResult(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0)
        {
            return "文字が検出されませんでした。"
                + "範囲を絞る、または前処理（拡大・コントラスト）を試してください。";
        }

        return string.Join(Environment.NewLine, TextAggregator.InReadingOrder(lines)
            .Select(line => $"{line.Confidence:F3}  [{line.Box.X,5},{line.Box.Y,5}]  {line.Text}"));
    }

    /// <summary>認識できた位置を確認できるよう、プレビューに枠を重ねる。</summary>
    private static RenderTargetBitmap DrawBoxes(BitmapSource source, IReadOnlyList<OcrLine> lines)
    {
        var pen = new Pen(Brushes.OrangeRed, 2);
        pen.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            foreach (var line in lines)
            {
                context.DrawRectangle(
                    null, pen, new Rect(line.Box.X, line.Box.Y, line.Box.Width, line.Box.Height));
            }
        }

        var target = new RenderTargetBitmap(
            source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    // ===== オーバーレイ（P0-6） =====

    private async void OnOverlayClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(ShowOverlayAsync).ConfigureAwait(true);

    private async Task ShowOverlayAsync()
    {
        if (TargetList.SelectedItem is not CaptureTarget target)
        {
            StatusText.Text = "対象が選択されていません。";
            return;
        }

        if (!CaptureTargetBounds.TryGet(target, out var bounds))
        {
            StatusText.Text = "対象の画面上の位置を取得できませんでした。";
            return;
        }

        OverlayButton.IsEnabled = false;
        try
        {
            // 押した時点の画面を撮り直す。前のキャプチャを使い回すと、
            // ゲーム内で画面を切り替えたときに古い内容を表示してしまう（FR-MOD-01）。
            StatusText.Text = "キャプチャ中…";
            var capture = await EnsureCaptureAsync(target).ConfigureAwait(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await capture.GetNextFrameAsync(timeout.Token).ConfigureAwait(true);
            UpdatePreview(target, frame);

            StatusText.Text = "OCR 実行中…";
            var (image, options, offsetX, offsetY) = PrepareForOcr(frame);
            var result = await _ocrEngine.RecognizeAsync(image).ConfigureAwait(true);

            // 翻訳と同じ単位（読み順 → 行結合）で重ねる。
            // ここだけ生の OCR 行を重ねていると、札の大きさや折り返しを確かめたいときに
            // 本番と違うものを見ることになる。API を呼ばずに見た目を確認できる唯一の経路。
            var detected = MapToSource(result.Lines, options.Scale, offsetX, offsetY);
            var lines = LineJoiner.Join(TextAggregator.InReadingOrder(detected));

            var overlay = EnsureOverlay();

            // ウィンドウ指定なら、ゲームか操作ウィンドウが前面の間だけ表示する。
            // モニタ全体はどれがゲームか特定できないため追従しない（自動フェードのみ）。
            IReadOnlyList<nint>? keepVisibleFor = target.Kind == CaptureTargetKind.Window
                ? [target.Handle, new WindowInteropHelper(this).Handle]
                : null;

            overlay.ShowLines(
                lines, bounds, frame.Width, frame.Height, keepVisibleFor);
            OverlayHideButton.IsEnabled = true;

            // P0-6 の検証項目。設定したつもりで終わらせず、実際の値を出す。
            var style = overlay.StyleState;
            ResultText.Text = string.Join(Environment.NewLine,
                $"重ねた先       : {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}（物理px）",
                $"認識画像       : {frame.Width}x{frame.Height}（このボタンで撮り直したもの）",
                $"表示した       : {detected.Count} 行 → {lines.Count} 件"
                    + $"（{lines.Count(l => l.SourceLineCount > 1)} 件が結合・FR-TRN-10）",
                $"前処理         : {options}",
                $"自動で消える   : {overlay.AutoHideAfter.TotalSeconds:F0} 秒後（FR-OVL-06）",
                $"前面の追従     : {(keepVisibleFor is null ? "なし（モニタ全体のため）" : "ゲームと操作画面のみ表示")}",
                string.Empty,
                "拡張スタイルの実測値（RULES.md が必須と定める4つ）",
                $"  WS_EX_LAYERED     (透過)          : {Mark(style.IsLayered)}",
                $"  WS_EX_TRANSPARENT (クリックスルー): {Mark(style.IsClickThrough)}",
                $"  WS_EX_NOACTIVATE  (フォーカス維持): {Mark(style.DoesNotActivate)}",
                $"  WS_EX_TOOLWINDOW  (Alt+Tab非表示) : {Mark(style.IsHiddenFromAltTab)}",
                string.Empty,
                style.IsComplete
                    ? "4つとも設定済み。実際に効くかはゲーム上で操作して確認すること。"
                    : "★ 不足があります。要件を満たしていません。");

            StatusText.Text = $"オーバーレイ表示中: {detected.Count} 行 → {lines.Count} 件 / "
                + $"{result.Elapsed.TotalMilliseconds:F0} ms";
        }
        finally
        {
            OverlayButton.IsEnabled = true;
        }

        static string Mark(bool value) => value ? "OK" : "未設定";
    }

    /// <summary>
    /// オーバーレイを用意する。
    /// </summary>
    /// <remarks>
    /// <para><b><c>Owner</c> を設定してはいけない。</b>WPF では所有元を最小化すると
    /// 所有ウィンドウも一緒に最小化される。ホットキーは「操作ウィンドウを最小化してゲームをする」
    /// 使い方で押されるため、所有させると<b>訳文が黙って出なくなる</b>
    /// （最小化状態でも IsVisible は true のため、ShowLines の再表示も効かない）。</para>
    /// <para>所有させない代わりに、閉じるのは <see cref="Dispose"/> で明示的に行う。
    /// 最前面は Topmost が、Alt+Tab に出ないことは WS_EX_TOOLWINDOW が担うため、所有関係は要らない。</para>
    /// </remarks>
    private OverlayWindow EnsureOverlay()
    {
        if (_overlay is null)
        {
            // 設定（FR-OVL-05 / 06）は作った時点で渡す。
            _overlay = new OverlayWindow { Options = _overlayOptions };
        }

        return _overlay;
    }

    // ===== ホットキーで画面を翻訳（FR-MOD-01 手動モード） =====

    /// <summary>
    /// 前面の画面を撮り、まとめて翻訳して、訳文を原文の位置に重ねる。
    /// </summary>
    /// <remarks>
    /// <para>想定している場面: スキルアイコンに触れると<b>別の場所</b>に説明が出て、
    /// カーソルを外すと消える。カーソル位置モード（FR-MOD-09）では説明の場所を撮れないため、
    /// キーを押した瞬間の画面全体を翻訳する。</para>
    /// <para><b>撮影を最優先にする。</b>「翻訳中…」が出た時点で撮り終えているので、
    /// そこから先はカーソルを動かしてよい。</para>
    /// </remarks>
    /// <summary>前面の画面をまとめて翻訳する（FR-MOD-01）。</summary>
    private Task TranslateScreenAsync() => TranslateAsync(useCursorWindow: false);

    /// <summary>
    /// カーソルの周囲だけを翻訳する（FR-MOD-09〜14 の最小実装）。
    /// </summary>
    /// <remarks>
    /// <para>v0.5 の本実装では<b>カーソルの静止で発動する</b>のが既定になる（FR-MOD-11）。
    /// ここではホットキーだけを用意し、静止判定・窓サイズの設定・プロファイルは作っていない
    /// （PoC は「UI の作り込み・設定画面・プロファイルは対象外」）。</para>
    /// <para>狙いは課題4 の計測。全画面では合計 6 秒かかっており、
    /// 読む量を減らして 1.5 秒に入るかを実機で確かめる。</para>
    /// </remarks>
    private Task TranslateCursorAsync() => TranslateAsync(useCursorWindow: true);

    private async Task TranslateAsync(bool useCursorWindow)
    {
        if (_isTranslatingScreen)
        {
            return;
        }

        _isTranslatingScreen = true;
        try
        {
            var target = ResolveHotkeyTarget();
            if (target is null)
            {
                StatusText.Text = "ホットキー: 翻訳する画面を特定できませんでした。";
                return;
            }

            if (!CaptureTargetBounds.TryGet(target, out var bounds))
            {
                StatusText.Text = $"ホットキー: {target.DisplayName} の画面上の位置を取得できませんでした。";
                return;
            }

            // カーソルは撮る前に読む。撮影に時間がかかるため、後で読むと
            // 「押した時に指していた場所」からずれる。
            ScreenPoint cursor = default;
            if (useCursorWindow && !CursorPosition.TryGet(out cursor))
            {
                StatusText.Text = "ホットキー: カーソルの位置を取得できませんでした。";
                return;
            }

            // モニタ全体を撮るときに、前回の訳文を自分で読み取ってしまわないよう先に消す。
            // 前回の訳文は出し直しの対象からも外す。新しい翻訳を頼んだ時点で古くなっているため
            // （覚えたままだと、翻訳中に ON/OFF キーを 2 回押すと古い訳文が戻ってくる = 問題 F）。
            _overlay?.Clear();

            var total = Stopwatch.StartNew();
            var capture = await EnsureCaptureAsync(target).ConfigureAwait(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await capture.GetNextFrameAsync(timeout.Token).ConfigureAwait(true);
            var captureMs = total.Elapsed.TotalMilliseconds;
            UpdatePreview(target, frame);

            var overlay = EnsureOverlay();

            // ゲームか操作ウィンドウが前面の間だけ出す。モニタ全体はどれがゲームか分からないため追従しない。
            IReadOnlyList<nint>? keepVisibleFor = target.Kind == CaptureTargetKind.Window
                ? [target.Handle, new WindowInteropHelper(this).Handle]
                : null;

            // ここで撮り終えている。受け付けたことをゲーム画面の上で返す。
            overlay.ShowMessage("翻訳中…", bounds, frame.Width, frame.Height, keepVisibleFor);

            FrameRect? window = null;
            if (useCursorWindow)
            {
                // FR-MOD-12: 読み取る範囲は設定値（既定 640x480 は暫定。PLAN.md 課題3）。
                if (!CursorRegion.TryResolve(
                        cursor, bounds, frame.Width, frame.Height,
                        _cursorOptions.Width, _cursorOptions.Height, out var resolved))
                {
                    overlay.ShowMessage(
                        "カーソルが対象の画面の外にあります", bounds, frame.Width, frame.Height, keepVisibleFor);
                    StatusText.Text = $"ホットキー: カーソルが {target.DisplayName} の外にあります。";
                    return;
                }

                window = resolved;
            }

            var (image, options, offsetX, offsetY) = window is { } region
                ? PrepareForOcr(frame, region)
                : PrepareForOcr(frame, useRegion: false);

            var ocr = await _ocrEngine.RecognizeAsync(image).ConfigureAwait(true);

            // 読み順に並べてから渡す。崩れた順で渡すと文脈が壊れて訳が悪くなる。
            var lines = TextAggregator.InReadingOrder(
                MapToSource(ocr.Lines, options.Scale, offsetX, offsetY));
            _lastOcrLines = lines;
            TranslateButton.IsEnabled = lines.Count > 0;
            PreviewImage.Source = DrawBoxes(_lastBitmap!, lines);

            if (lines.Count == 0)
            {
                // 読み取りだけ数える。翻訳を 0 として数えると、翻訳の平均が実際より速く見える。
                _processingTimes.Record(ocr.Elapsed, translation: null);
                UpdateCornerOverlayText();

                overlay.ShowMessage("文字が見つかりませんでした", bounds, frame.Width, frame.Height, keepVisibleFor);
                StatusText.Text = $"ホットキー: {target.DisplayName} に文字が見つかりませんでした。";
                return;
            }

            // 切れた文を繋いでから翻訳する（FR-TRN-10）。
            // 繋がずに渡すと、`Your printed body can` の can が「缶」と訳されるような
            // 断片ごとの誤訳になる（PLAN.md 2026-09-26 の実測）。
            var blocks = LineJoiner.Join(lines);

            // FR-TRN-06: クラウドが混雑・上限・通信不良なら静かにローカルへ切り替える。
            // 切り替え先が自分自身のときは渡さない（同じものを 2 回試しても無駄）。
            var translated = await LineTranslation
                .TranslateAsync(
                    ActiveTranslator,
                    blocks,
                    FallbackTranslator,
                    _translationCache,
                    _translationOptions.TargetLanguage)
                .ConfigureAwait(true);

            // FR-OVL-10: かかった時間を覚える。隅の表示は次の RecordUsage で使用量と一緒に更新される。
            _processingTimes.Record(ocr.Elapsed, translated.Translation.Elapsed);

            // RULES.md 🟡-8: 呼んだぶんを数える。訳文を重ねる前に数えて、
            // この翻訳のぶんが訳文の隅にも反映されるようにする。
            RecordUsage(translated);

            // FR-TRN-08: 数値が合わない行には原文を併記する。
            var overlayLines = BuildOverlayLines(blocks, translated);

            if (!translated.Translation.IsSuccess)
            {
                overlay.ShowMessage(
                    $"翻訳できませんでした: {ShortOutcome(translated.Translation.Outcome)}",
                    bounds, frame.Width, frame.Height, keepVisibleFor);
            }
            else if (overlayLines.Count == 0)
            {
                // 数値だけの画面や、既に日本語の画面。何も重ねないと「効いていない」と区別できない。
                overlay.ShowMessage(
                    "訳す必要のある文字はありませんでした", bounds, frame.Width, frame.Height, keepVisibleFor);
            }
            else
            {
                // 訳して文字が変わった行だけを重ねる。数値だけの行や原文と同じ訳は重ねない。
                overlay.ShowLines(overlayLines, bounds, frame.Width, frame.Height, keepVisibleFor);
            }

            OverlayHideButton.IsEnabled = true;
            ShowScreenTranslationReport(target, frame, window, lines.Count, blocks, translated, captureMs,
                ocr.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "ホットキー: 画面が 5 秒以内に取得できませんでした。";
        }
        finally
        {
            _isTranslatingScreen = false;
        }
    }

    /// <summary>
    /// ホットキーで翻訳する画面を決める。
    /// </summary>
    /// <remarks>
    /// <para>ゲーム中に押されたなら、前面にあるのがゲーム。一覧から選ぶ必要がない。</para>
    /// <para>一覧の選択を使うのは<b>操作ウィンドウが前面のときだけ</b>（PoC の検証用）。
    /// デスクトップなど撮れないものが前面のときに一覧へ黙って切り替えると、
    /// 見ていない画面を翻訳することになり予測しにくい。その場合は何もしない。</para>
    /// </remarks>
    private CaptureTarget? ResolveHotkeyTarget()
    {
        var foreground = WindowFocus.Current;
        if (foreground == new WindowInteropHelper(this).Handle)
        {
            return TargetList.SelectedItem as CaptureTarget;
        }

        return CaptureTargetEnumerator.TryDescribe(foreground, out var target) ? target : null;
    }

    /// <summary>
    /// オーバーレイに出す短い失敗理由。詳細は操作ウィンドウに出す。
    /// </summary>
    /// <remarks>
    /// ゲームの上に重ねるため短く保つが、<b>こちらの問題なのか相手の問題なのかは伝える。</b>
    /// 2026-09-24、混雑による遅延を「通信できませんでした」と出していたため、
    /// 回線を疑わせてしまった（PLAN.md 課題7）。
    /// </remarks>
    private static string ShortOutcome(TranslationOutcome outcome) => outcome switch
    {
        TranslationOutcome.NotConfigured => "APIキーが未登録です",
        TranslationOutcome.RateLimited => "利用上限に達しました（しばらく待つと戻ります）",
        TranslationOutcome.NetworkError => "翻訳サービスに接続できませんでした",
        TranslationOutcome.Timeout => "翻訳サービスから応答がありません（混雑している可能性）",
        TranslationOutcome.Busy => "翻訳サービスが混雑しています（時間をおくと戻ります）",
        TranslationOutcome.ServiceError => "翻訳サービスがエラーを返しました",
        TranslationOutcome.Blocked => "翻訳が拒否されました",
        TranslationOutcome.Unsupported => "この言語には同梱のモデルが対応していません",
        _ => outcome.ToString(),
    };

    /// <summary>
    /// オーバーレイに重ねる行を作る（FR-TRN-08 の「原文併記」）。
    /// </summary>
    /// <remarks>
    /// <para><b>数値が合わない行だけ、訳文の下に原文を出す。</b>
    /// 「ダメージ50」が「ダメージ60」になっても訳文としては自然で、読んでも気づけない。
    /// <b>気づける形にするのが対処</b>（RULES.md 🟡-7 / FR-TRN-08）。</para>
    /// <para>全行に併記しないのは、画面を覆う面積が倍になるため（判断基準の 2 位）。
    /// 合っている行まで併記すると、警告としての意味も薄れる。</para>
    /// </remarks>
    private static List<OcrLine> BuildOverlayLines(
        List<OcrLine> sources, LineTranslationResult translated)
    {
        var result = new List<OcrLine>(translated.Lines.Count);

        for (var i = 0; i < translated.Lines.Count; i++)
        {
            if (translated.Statuses[i] != LineTranslationStatus.Translated)
            {
                continue;
            }

            var line = translated.Lines[i];
            if (i < translated.NumberChecks.Count && !translated.NumberChecks[i].IsConsistent)
            {
                // 文字の大きさは変えない（SourceLineCount はそのまま）。札が 1 行分高くなるだけ。
                line = line with { Text = line.Text + "\n" + sources[i].Text };
            }

            result.Add(line);
        }

        return result;
    }

    /// <summary>数値の照合の結果を 1 行にまとめる（FR-TRN-08）。</summary>
    private static string DescribeNumberChecks(
        List<OcrLine> sources, LineTranslationResult translated)
    {
        var mismatched = translated.NumberMismatchCount;
        if (mismatched == 0)
        {
            return "食い違いなし";
        }

        var detail = new List<string>(mismatched);
        for (var i = 0; i < translated.NumberChecks.Count; i++)
        {
            var check = translated.NumberChecks[i];
            if (check.IsConsistent)
            {
                continue;
            }

            var parts = new List<string>(2);
            if (check.Missing.Count > 0)
            {
                parts.Add($"落ちた {string.Join(",", check.Missing)}");
            }

            if (check.Added.Count > 0)
            {
                parts.Add($"増えた {string.Join(",", check.Added)}");
            }

            detail.Add($"「{sources[i].Text}」{string.Join(" / ", parts)}");
        }

        return $"★ {mismatched} 件が食い違い（原文を併記した） — {string.Join(" | ", detail)}";
    }

    /// <summary>
    /// 操作ウィンドウに内訳を出す。
    /// </summary>
    /// <remarks>
    /// <para>工程ごとの時間を分けて出すのは課題4（全画面が 1.5 秒に収まらない）の計測のため。</para>
    /// <para>対応付けの数を出すのは、LLM が番号の形式を守らなかったときに黙って欠落させないため。</para>
    /// <para>翻訳に回さなかった行も一覧に残す。除外のルールが訳すべき行まで落としていないか、
    /// 実画面で目で確かめられるようにするため。</para>
    /// <para>行結合（FR-TRN-10）の件数も出す。<b>繋ぎすぎは繋ぎ落としより害が大きい</b>ため、
    /// 何行が 1 つになったかを実画面で確かめられるようにする。</para>
    /// </remarks>
    /// <param name="detectedCount">OCR が検出した行数。結合前の数。</param>
    /// <param name="source">結合した後のブロック。翻訳に渡した単位。</param>
    private void ShowScreenTranslationReport(
        CaptureTarget target, CapturedFrame frame, FrameRect? window, int detectedCount,
        List<OcrLine> source,
        LineTranslationResult translated, double captureMs, double ocrMs, double totalMs)
    {
        var translation = translated.Translation;
        var statuses = translated.Statuses;
        var skipped = source.Where((_, i) => statuses[i] == LineTranslationStatus.Skipped).Select(l => l.Text);

        // 結合したブロックには元の行数を付ける。繋ぎすぎていれば一覧で気付ける。
        var pairs = source
            .Select((from, i) =>
            {
                var head = from.SourceLineCount > 1 ? $"  ⤵{from.SourceLineCount}行 {from.Text}" : $"  {from.Text}";
                return statuses[i] switch
                {
                    LineTranslationStatus.Translated => $"{head}  →  {translated.Lines[i].Text}",
                    LineTranslationStatus.Unchanged => $"{head}  →  （原文と同じ・重ねない）",
                    LineTranslationStatus.Unmapped => $"{head}  →  （対応なし・重ねない）",
                    _ => null,
                };
            })
            .OfType<string>();

        ResultText.Text = string.Join(Environment.NewLine,
            [
                window is null
                    ? "ホットキー     : 前面の画面を翻訳（FR-MOD-01）"
                    : "ホットキー     : カーソルの周囲を翻訳（FR-MOD-09〜14）",
                $"対象           : {target}",
                window is { } w
                    ? $"読んだ範囲     : {w.Width}x{w.Height} @ ({w.X},{w.Y})"
                      + $" — 画面 {frame.Width}x{frame.Height} の {(double)(w.Width * w.Height) / (frame.Width * frame.Height) * 100:F0}%"
                    : $"画面           : {frame.Width}x{frame.Height}（範囲指定は使わない）",
                $"バックエンド   : {(translated.Backend.Length > 0 ? translated.Backend : ActiveTranslator.Name)}"
                    + (translated.FellBackFrom is { } from
                        ? $"（{ShortOutcome(from.Outcome)}のため切替・FR-TRN-06）"
                        : string.Empty),
                $"状態           : {DescribeOutcome(translation)}",
                $"検出           : {detectedCount} 行",
                $"行結合         : {detectedCount} 行 → {source.Count} 件"
                    + $"（{source.Count(l => l.SourceLineCount > 1)} 件が結合・FR-TRN-10）",
                $"翻訳に回した   : {translated.SentCount} 件（数値・記号だけの {source.Count - translated.SentCount} 件は除外）"
                    + (translated.CachedCount > 0
                        ? $" / うち {translated.CachedCount} 件は記憶から（FR-TRN-09）"
                        : string.Empty),
                $"対応付け       : {translated.MappedCount}/{translated.SentCount} 件",
                $"重ねた         : {translated.LinesToOverlay.Count} 件（訳して文字が変わったものだけ）",
                $"数値の照合     : {DescribeNumberChecks(source, translated)}",
                string.Empty,
                $"撮影           : {captureMs,6:F0} ms",
                $"OCR            : {ocrMs,6:F0} ms",
                $"翻訳           : {translation.Elapsed.TotalMilliseconds,6:F0} ms"
                    + (translation.TotalTokens is { } tokens ? $"（{tokens} トークン）" : string.Empty),
                $"合計           : {totalMs,6:F0} ms（要件 1.5 秒）",
                string.Empty,
                "--- 原文 → 訳文 ---",
                .. pairs,
                string.Empty,
                $"--- 翻訳に回さなかった行 ---",
                $"  {string.Join("  |  ", skipped)}",
            ]);

        StatusText.Text = translation.IsSuccess
            ? $"ホットキー: {translated.LinesToOverlay.Count} 件を重ねた"
              + $"（{detectedCount} 行 → {source.Count} 件中 {translated.SentCount} 件を翻訳）/ 合計 {totalMs:F0} ms"
              + (translated.FellBackFrom is not null ? $" — {translated.Backend} へ切替" : string.Empty)
            : $"ホットキー: 翻訳失敗 — {translation.Message}";
    }

    /// <summary>
    /// オーバーレイの表示を切り替える（FR-OVL-08）。
    /// </summary>
    /// <remarks>
    /// <para>前の訳文が次の画面の上に残ったとき（PLAN.md 問題 F）、すぐ消すためのもの。
    /// 消えているときに押すと、最後の訳文を <b>API を呼ばずに</b>出し直す。</para>
    /// <para>翻訳中に押すと「翻訳中…」を消す。翻訳が終われば訳文は表示される
    /// （翻訳は利用者が明示的に頼んだものなので、結果は見せる）。</para>
    /// </remarks>
    private Task ToggleOverlayAsync()
    {
        if (_overlay is null)
        {
            StatusText.Text = "ホットキー: まだ訳文を表示していません。";
            return Task.CompletedTask;
        }

        var wasVisible = _overlay.IsVisible;
        var isVisible = _overlay.ToggleVisibility();
        OverlayHideButton.IsEnabled = isVisible;

        StatusText.Text = (wasVisible, isVisible) switch
        {
            (true, _) => "ホットキー: 訳文を消しました。もう一度押すと出し直します。",
            (false, true) => "ホットキー: 最後の訳文を出し直しました（API は呼んでいません）。",
            (false, false) => "ホットキー: 出し直せる訳文がありません。",
        };

        return Task.CompletedTask;
    }

    private void OnOverlayHideClick(object sender, RoutedEventArgs e)
    {
        _overlay?.HideOverlay();
        OverlayHideButton.IsEnabled = false;
        StatusText.Text = "オーバーレイを消しました。";
    }

    // ===== 保存 =====

    private async void OnSaveClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(SaveAsync).ConfigureAwait(true);

    private Task SaveAsync()
    {
        if (_lastBitmap is null || _lastFrame is null)
        {
            StatusText.Text = "保存できる画像がありません。";
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(CaptureDirectory);
        var name = $"{DateTime.Now:yyyyMMdd_HHmmss}_{_lastFrame.Width}x{_lastFrame.Height}.png";
        var path = Path.Combine(CaptureDirectory, name);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_lastBitmap));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        StatusText.Text = $"保存しました: {path}";
        return Task.CompletedTask;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(CaptureDirectory);
        using (Process.Start(new ProcessStartInfo(CaptureDirectory) { UseShellExecute = true }))
        {
        }
    }

    // ===== 後始末・共通処理 =====

    private void ReleaseCapture()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.Closed -= OnCaptureClosed;
        _capture.Dispose();
        _capture = null;
        _activeTarget = null;
    }

    private void SetBusy(bool busy)
    {
        CaptureNowButton.IsEnabled = !busy;
        CaptureDelayedButton.IsEnabled = !busy;
        TargetList.IsEnabled = !busy;
    }

    /// <summary>
    /// UI の最上位ハンドラ。失敗の原因を具体的に見せる（RULES.md 🔵「例外を握り潰さない」）。
    /// </summary>
    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 最上位のハンドラ。ここで捕捉しないとアプリごと落ちる。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText.Text = $"失敗: {ex.Message}";
            ResultText.Text = ex.ToString();
        }
    }
}
