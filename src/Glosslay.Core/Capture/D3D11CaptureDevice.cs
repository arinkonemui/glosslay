using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.WinRT.Direct3D11;
using WinRT;

namespace Glosslay.Capture;

/// <summary>
/// WGC のフレームを受け取り、CPU から読めるピクセル列へ変換するための D3D11 デバイス。
/// </summary>
/// <remarks>
/// WGC はフレームを GPU テクスチャ（<c>IDirect3DSurface</c>）で返すため、D3D11 の経由は避けられない。
/// これは「GPU 推論は既定で無効」（RULES.md 🟠）とは別の話で、推論ではなくキャプチャ経路の要件。
/// </remarks>
internal sealed class D3D11CaptureDevice : IDisposable
{
    /// <summary>D3D11_SDK_VERSION。d3d11.h の定数だが CsWin32 は定数を生成しないため直接持つ。</summary>
    private const uint D3D11SdkVersion = 7;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    /// <summary>読み出し用のステージングテクスチャ。サイズが変わるまで使い回す。</summary>
    private ID3D11Texture2D? _staging;
    private uint _stagingWidth;
    private uint _stagingHeight;

    private bool _disposed;

    private D3D11CaptureDevice(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winRtDevice)
    {
        _device = device;
        _context = context;
        WinRtDevice = winRtDevice;
    }

    /// <summary><see cref="Direct3D11CaptureFramePool"/> に渡す WinRT 側のデバイス。</summary>
    public IDirect3DDevice WinRtDevice { get; }

    /// <summary>
    /// D3D11 デバイスを作成する。ハードウェアが使えない環境では WARP（ソフトウェア）へ退避する。
    /// </summary>
    public static D3D11CaptureDevice Create()
    {
        // BGRA_SUPPORT は Direct3D11CaptureFramePool に必須。
        // SINGLETHREADED は付けない（フレーム到着がプールスレッドで起きるため）。
        const D3D11_CREATE_DEVICE_FLAG Flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;

        foreach (var driverType in (ReadOnlySpan<D3D_DRIVER_TYPE>)
                 [D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP])
        {
            var hr = PInvoke.D3D11CreateDevice(
                pAdapter: null,
                DriverType: driverType,
                Software: default,
                Flags: Flags,
                pFeatureLevels: default,
                SDKVersion: D3D11SdkVersion,
                ppDevice: out var device,
                ppImmediateContext: out var context);

            if (hr.Failed)
            {
                continue;
            }

            return new D3D11CaptureDevice(device, context, CreateWinRtDevice(device));
        }

        throw new NotSupportedException(
            "D3D11 デバイスを作成できませんでした。グラフィックドライバが古いか、GPU が利用できない可能性があります。");
    }

    /// <summary>D3D11 デバイスを WinRT の <see cref="IDirect3DDevice"/> へ変換する。</summary>
    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        var dxgiDevice = (IDXGIDevice)device;
        PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable).ThrowOnFailure();

        var abi = Marshal.GetIUnknownForObject(inspectable);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>
    /// GPU 上のフレームをステージングテクスチャ経由で CPU へ読み出す。
    /// </summary>
    public CapturedFrame ReadBack(Direct3D11CaptureFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // IDirect3DSurface -> ID3D11Texture2D
        var source = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        source.GetInterface<ID3D11Texture2D>(out var sourceTexture);

        sourceTexture.GetDesc(out var desc);
        var staging = GetOrCreateStaging(desc);

        _context.CopyResource(staging, sourceTexture);
        _context.Map(staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out var mapped);
        try
        {
            var width = (int)desc.Width;
            var height = (int)desc.Height;
            var stride = width * 4;
            var pixels = new byte[stride * height];

            // GPU 側の RowPitch は 4 の倍数などにパディングされているため行単位でコピーする。
            unsafe
            {
                var src = (byte*)mapped.pData;
                fixed (byte* dst = pixels)
                {
                    for (var y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(src + ((long)y * mapped.RowPitch), dst + ((long)y * stride), stride, stride);
                    }
                }
            }

            return new CapturedFrame(width, height, stride, pixels, frame.SystemRelativeTime);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    /// <summary>サイズが同じならステージングテクスチャを再利用する（毎フレームの確保を避ける）。</summary>
    private ID3D11Texture2D GetOrCreateStaging(D3D11_TEXTURE2D_DESC sourceDesc)
    {
        if (_staging is not null && _stagingWidth == sourceDesc.Width && _stagingHeight == sourceDesc.Height)
        {
            return _staging;
        }

        ReleaseStaging();

        var desc = sourceDesc;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;

        _device.CreateTexture2D(desc, null, out var staging);
        _staging = staging;
        _stagingWidth = desc.Width;
        _stagingHeight = desc.Height;
        return staging;
    }

    private void ReleaseStaging()
    {
        if (_staging is null)
        {
            return;
        }

        // GPU メモリを溜めないよう明示的に解放する。
        Marshal.FinalReleaseComObject(_staging);
        _staging = null;
        _stagingWidth = 0;
        _stagingHeight = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseStaging();
        WinRtDevice.Dispose();
        Marshal.FinalReleaseComObject(_context);
        Marshal.FinalReleaseComObject(_device);
    }
}
