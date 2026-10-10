namespace Glosslay.Configuration;

/// <summary>
/// 遠隔から取らず、既定値だけを返す実装（v1.0）。
/// </summary>
/// <remarks>
/// <para><b>配信先がまだ無い。</b>CDN の URL も署名鍵も、広告ネットワークとの契約と
/// 配布サイトの整備が済むまで決まらない。推測で実装すると、<b>鍵の持ち方を間違えたまま
/// 配線だけが出来上がる</b>ほうが危ない。</para>
/// <para><b>この実装は「取得に失敗し続けている状態」と同じ。</b>
/// FR-ADS-06 が求めるフォールバック先そのものなので、
/// 署名付き取得を足すときも<b>この挙動を失敗時の既定として残す</b>こと。</para>
/// <para>署名付き取得を実装するときの要件は <see cref="IRemoteConfig"/> に書いてある。</para>
/// </remarks>
public sealed class BuiltInRemoteConfig : IRemoteConfig
{
    /// <summary>製品が使う実体。</summary>
    public static IRemoteConfig Default { get; } = new BuiltInRemoteConfig();

    /// <summary>
    /// 値を指定して作る。<b>検査用。</b>既定は安全側。
    /// </summary>
    public BuiltInRemoteConfig(RemoteSettings? settings = null) =>
        Current = settings ?? RemoteSettings.Safe;

    /// <inheritdoc />
    public RemoteSettings Current { get; }

    /// <inheritdoc />
    /// <remarks>取りに行く先が無いので何もしない。<b>失敗ではないので例外も投げない。</b></remarks>
    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
