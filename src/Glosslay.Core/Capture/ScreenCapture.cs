using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.UI;

namespace Glosslay.Capture;

/// <summary>
/// Windows.Graphics.Capture (WGC) による画面キャプチャ。
/// </summary>
/// <remarks>
/// <para>ゲームプロセスには一切干渉しない。WGC が OS 側で合成した結果を受け取るだけで、
/// DLL インジェクション・メモリ読み取り・描画フックは行わない（RULES.md 🔴-1）。</para>
/// <para>セッションは開始したまま保持し、必要なときに <see cref="GetNextFrameAsync"/> で 1 枚取り出す。
/// 毎回セッションを作り直すと初回フレームまで数百 ms かかり、
/// 「トリガーから表示まで 1.5 秒以内」（SPEC.md §4）を満たせなくなるため。</para>
/// </remarks>
public sealed class ScreenCapture : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    /// <summary>フレームプールのバッファ数。オンデマンド取得なので最小限でよい。</summary>
    private const int BufferCount = 2;

    /// <summary>枠なしキャプチャの許可要求はプロセスで 1 回だけ行えばよい。</summary>
    private static readonly Lazy<Task<bool>> BorderlessAccess = new(RequestBorderlessAccessAsync);

    private readonly Lock _gate = new();
    private readonly D3D11CaptureDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    private TaskCompletionSource<CapturedFrame>? _pending;
    private SizeInt32 _size;
    private bool _disposed;

    private ScreenCapture(
        CaptureTarget target,
        D3D11CaptureDevice device,
        GraphicsCaptureItem item,
        Direct3D11CaptureFramePool framePool,
        GraphicsCaptureSession session,
        bool isBorderRemoved)
    {
        Target = target;
        _device = device;
        _item = item;
        _framePool = framePool;
        _session = session;
        _size = item.Size;
        IsBorderRemoved = isBorderRemoved;

        _framePool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;
    }

    /// <summary>この環境で WGC が利用できるか。</summary>
    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>キャプチャ対象。</summary>
    public CaptureTarget Target { get; }

    /// <summary>
    /// FR-CAP-07: キャプチャ枠（黄色い枠）を消せたか。
    /// OS が許可しなかった場合は false になり、枠が表示されたままになる。
    /// </summary>
    public bool IsBorderRemoved { get; }

    /// <summary>対象ウィンドウが閉じられた等でキャプチャが継続できなくなったときに発火する。</summary>
    public event EventHandler? Closed;

    /// <summary>
    /// キャプチャを開始する。
    /// </summary>
    /// <param name="target">ウィンドウまたはモニタ。</param>
    /// <param name="removeBorder">FR-CAP-07: キャプチャ枠を消すか。</param>
    /// <param name="captureCursor">カーソルを含めるか。OCR の妨げになるため既定は含めない。</param>
    public static async Task<ScreenCapture> StartAsync(
        CaptureTarget target,
        bool removeBorder = true,
        bool captureCursor = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!IsSupported)
        {
            throw new NotSupportedException(
                "この環境では Windows.Graphics.Capture が利用できません。Windows 11 であることを確認してください。");
        }

        var item = CreateItem(target);
        var device = D3D11CaptureDevice.Create();

        try
        {
            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device.WinRtDevice, PixelFormat, BufferCount, item.Size);

            var session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = captureCursor;

            var borderRemoved = false;
            if (removeBorder && await BorderlessAccess.Value.ConfigureAwait(false))
            {
                // 未対応環境でも落とさない。枠が出たままになるだけで機能は成立する。
                try
                {
                    session.IsBorderRequired = false;
                    borderRemoved = !session.IsBorderRequired;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException)
                {
                    borderRemoved = false;
                }
            }

            var capture = new ScreenCapture(target, device, item, framePool, session, borderRemoved);
            session.StartCapture();
            return capture;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 次に届くフレームを 1 枚だけ CPU へ読み出して返す。
    /// 待っている呼び出しがない間はフレームを破棄するだけで、読み出しコストは発生しない。
    /// </summary>
    public async Task<CapturedFrame> GetNextFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<CapturedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("前回の GetNextFrameAsync がまだ完了していません。");
            }

            _pending = tcs;
        }

        using var registration = cancellationToken.Register(static state =>
        {
            var (self, source) = ((ScreenCapture, TaskCompletionSource<CapturedFrame>))state!;
            self.ClearPending(source);
            source.TrySetCanceled();
        }, (this, tcs));

        return await tcs.Task.ConfigureAwait(false);
    }

    private static GraphicsCaptureItem CreateItem(CaptureTarget target)
    {
        var item = target.Kind switch
        {
            CaptureTargetKind.Window =>
                GraphicsCaptureItem.TryCreateFromWindowId(new WindowId { Value = (ulong)target.Handle }),
            CaptureTargetKind.Monitor =>
                GraphicsCaptureItem.TryCreateFromDisplayId(new DisplayId { Value = (ulong)target.Handle }),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "未知のキャプチャ方式です。"),
        };

        return item ?? throw new InvalidOperationException(
            $"キャプチャ対象を作成できませんでした: {target}。ウィンドウが既に閉じられている可能性があります。");
    }

    /// <summary>
    /// FR-CAP-07 の枠なしキャプチャは、未パッケージアプリでは明示的な許可要求が必要。
    /// </summary>
    private static async Task<bool> RequestBorderlessAccessAsync()
    {
        try
        {
            var status = await GraphicsCaptureAccess
                .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask()
                .ConfigureAwait(false);

            return status == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception ex) when (ex is NotSupportedException or TypeLoadException or COMException)
        {
            // 許可 API 自体が無い環境。枠付きでキャプチャは継続できる。
            return false;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        // ウィンドウのリサイズや解像度変更に追従する（FR-CAP-09）。
        if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
        {
            _size = frame.ContentSize;
            sender.Recreate(_device.WinRtDevice, PixelFormat, BufferCount, _size);
        }

        TaskCompletionSource<CapturedFrame>? waiter;
        lock (_gate)
        {
            waiter = _pending;
            _pending = null;
        }

        if (waiter is null)
        {
            return;
        }

        try
        {
            waiter.TrySetResult(_device.ReadBack(frame));
        }
#pragma warning disable CA1031 // ここは WGC のプールスレッド上のコールバック。
        // 例外を逃がすとプロセスごと落ちるため、種類を問わず捕捉して
        // GetNextFrameAsync の呼び出し元へ転送する。握り潰してはいない（RULES.md 🔵）。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            waiter.TrySetException(ex);
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        ClearPending(null)?.TrySetException(
            new InvalidOperationException("キャプチャ対象が閉じられました。"));

        Closed?.Invoke(this, EventArgs.Empty);
    }

    private TaskCompletionSource<CapturedFrame>? ClearPending(TaskCompletionSource<CapturedFrame>? expected)
    {
        lock (_gate)
        {
            if (_pending is null || (expected is not null && !ReferenceEquals(_pending, expected)))
            {
                return null;
            }

            var pending = _pending;
            _pending = null;
            return pending;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _framePool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnItemClosed;

        ClearPending(null)?.TrySetCanceled();

        _session.Dispose();
        _framePool.Dispose();
        _device.Dispose();
    }
}
