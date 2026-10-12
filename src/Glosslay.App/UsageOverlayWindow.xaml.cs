using System.Windows;
using System.Windows.Interop;
using Glosslay.Overlay;
using Glosslay.Usage;

namespace Glosslay;

/// <summary>
/// 使用量を画面の隅に出しっぱなしにする小さなウィンドウ（RULES.md 🟡-8）。
/// </summary>
/// <remarks>
/// <para><b>既定では使わない。</b>出しっぱなしの表示は「モニタが1枚でも邪魔されずに遊べる」
/// というゴールと衝突する。文字どおりの「常時表示」が欲しい人のための選択肢
/// （<see cref="UsageOverlayOptions"/>）。</para>
/// <para><b>訳文のオーバーレイとは別のウィンドウにしてある。</b>
/// 訳文は 10 秒で消え、前面のウィンドウを見張って引っ込む。
/// 出しっぱなしのこちらが同じ仕組みを共有すると、互いの都合で消し合うことになる。</para>
/// </remarks>
public partial class UsageOverlayWindow : Window
{
    private UsageOverlayOptions _options = new();

    public UsageOverlayWindow()
    {
        InitializeComponent();

        // SizeToContent の実寸が決まるのはレイアウトの後。
        // 文字数が変わると幅も変わるため、決まった時点で寄せ直す。
        // これが無いと、右寄せのときに縁からはみ出す瞬間ができる。
        SizeChanged += (_, _) => MoveToCorner();
    }

    /// <summary>必須の拡張スタイルが実際に効いているか。</summary>
    public OverlayStyleState StyleState { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // クリックスルー・非アクティブ化・Alt+Tab 非表示（FR-OVL-02 / 03 / 09）。
        // 訳文オーバーレイと同じ処理を使う。ゲームの操作を奪わないことが最優先。
        StyleState = OverlayWindowStyles.Apply(new WindowInteropHelper(this).Handle);
    }

    /// <summary>設定を反映する。</summary>
    public void Apply(UsageOverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        UsageText.FontSize = options.FontSize;
        Opacity = options.Opacity;

        // 文字の大きさで寸法が変わるため、位置は測り直した後に決める。
        UpdateLayout();
        MoveToCorner();
    }

    /// <summary>出す文言を差し替える。</summary>
    public void Update(string text)
    {
        UsageText.Text = text;

        // 文字数で幅が変わる。隅に寄せ直さないと、右寄せのときに縁からはみ出す。
        UpdateLayout();
        MoveToCorner();
    }

    /// <summary>
    /// 設定された隅へ寄せる。
    /// </summary>
    /// <remarks>
    /// <para><b>作業領域ではなく画面全体を使う。</b>ゲームはボーダーレスで画面全体を覆うため、
    /// タスクバーを除いた作業領域に合わせると、ゲームの上では縁から離れて見える。</para>
    /// <para>WPF の <c>Left</c> / <c>Top</c> は論理ピクセル。
    /// <c>SystemParameters</c> も論理ピクセルを返すため、DPI の変換は要らない。</para>
    /// </remarks>
    private void MoveToCorner()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var margin = _options.Margin;

        Left = _options.Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? margin
            : Math.Max(margin, screenWidth - ActualWidth - margin);

        Top = _options.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? margin
            : Math.Max(margin, screenHeight - ActualHeight - margin);
    }
}
