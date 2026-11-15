using System.Numerics;
using KosDraw;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Xunit;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace KosDraw.Tests;

/// <summary>
/// The real GPU path on WARP (Windows' software Direct3D 11): a fake scene depth buffer and back buffer stand in for
/// the game's, then the composited texture is read back pixel by pixel.
/// </summary>
public sealed unsafe class GpuTests : IDisposable
{
    private const int Size = 64;
    private const float SceneAt = 10; // metres: the fake scene's depth everywhere
    private static readonly Vector4 HalfRed = new(1, 0, 0, 0.5f);

    private readonly ID3D11Device* device;
    private readonly ID3D11DeviceContext* immediate;
    private readonly ID3D11Texture2D* depth;
    private readonly KosGpu gpu;

    public GpuTests()
    {
        ID3D11Device* d;
        ID3D11DeviceContext* c;
        Check(DirectX.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, HMODULE.NULL, 0, null, 0, D3D11.D3D11_SDK_VERSION, &d, null, &c));
        device = d;
        immediate = c;
        depth = DepthBuffer(Size, Size, ShapeTests.Camera().M43 / SceneAt);
        gpu = new KosGpu((nint)device);
    }

    public void Dispose()
    {
        gpu.Dispose();
        depth->Release();
        immediate->Release();
        device->Release();
    }

    private static void Check(HRESULT hr) => Assert.True(hr.SUCCEEDED, $"0x{hr.Value:X8}");

    private KosGpuFrame Frame(bool withDepth = true, float occludedAlpha = 0, nint backBuffer = 0, int size = Size) =>
        new(ShapeTests.Camera(), new Vector2(size), withDepth ? (nint)depth : 0, Vector2.One, backBuffer, occludedAlpha, 0.05f);

    /// <summary>A screen-filling triangle at <paramref name="z"/> metres.</summary>
    private static KosShapes Wall(float z)
    {
        var s = new KosShapes();
        s.Polygon([new Vector3(-3 * z, -z, z), new Vector3(3 * z, -z, z), new Vector3(0, 3 * z, z)], HalfRed);
        return s;
    }

    private ID3D11Texture2D* DepthBuffer(int w, int h, float clearTo)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_R24G8_TYPELESS,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_DEPTH_STENCIL | D3D11_BIND_SHADER_RESOURCE),
        };
        ID3D11Texture2D* tex;
        Check(device->CreateTexture2D(&desc, null, &tex));
        var dsvDesc = new D3D11_DEPTH_STENCIL_VIEW_DESC { Format = DXGI_FORMAT_D24_UNORM_S8_UINT, ViewDimension = D3D11_DSV_DIMENSION.D3D11_DSV_DIMENSION_TEXTURE2D };
        ID3D11DepthStencilView* dsv;
        Check(device->CreateDepthStencilView((ID3D11Resource*)tex, &dsvDesc, &dsv));
        immediate->ClearDepthStencilView(dsv, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, clearTo, 0);
        dsv->Release();
        return tex;
    }

    /// <summary>A stand-in back buffer: game UI (alpha 1) over the left half.</summary>
    private ID3D11Texture2D* BackBuffer()
    {
        var pixels = new uint[Size * Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            pixels[y * Size + x] = x < Size / 2 ? 0xFF202020u : 0x00202020u;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = Size, Height = Size, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE_DEFAULT, BindFlags = (uint)D3D11_BIND_RENDER_TARGET,
        };
        ID3D11Texture2D* tex;
        fixed (uint* p = pixels)
        {
            var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p, SysMemPitch = Size * 4 };
            Check(device->CreateTexture2D(&desc, &init, &tex));
        }
        return tex;
    }

    /// <summary>Reads the composited texture back as RGBA bytes, row-major.</summary>
    private (byte[] Pixels, int Width) Read(nint srv)
    {
        Assert.NotEqual(0, srv);
        ID3D11Resource* res;
        ((ID3D11ShaderResourceView*)srv)->GetResource(&res);
        D3D11_TEXTURE2D_DESC desc;
        ((ID3D11Texture2D*)res)->GetDesc(&desc);
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        Check(device->CreateTexture2D(&desc, null, &staging));
        immediate->CopyResource((ID3D11Resource*)staging, res);
        D3D11_MAPPED_SUBRESOURCE map;
        Check(immediate->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &map));
        var w = (int)desc.Width;
        var bytes = new byte[w * (int)desc.Height * 4];
        for (var y = 0; y < desc.Height; y++)
            new ReadOnlySpan<byte>((byte*)map.pData + y * map.RowPitch, w * 4).CopyTo(bytes.AsSpan(y * w * 4));
        immediate->Unmap((ID3D11Resource*)staging, 0);
        staging->Release();
        res->Release();
        return (bytes, w);
    }

    private static (byte R, byte G, byte B, byte A) At((byte[] Pixels, int Width) img, int x, int y)
    {
        var i = (y * img.Width + x) * 4;
        return (img.Pixels[i], img.Pixels[i + 1], img.Pixels[i + 2], img.Pixels[i + 3]);
    }

    [Fact]
    public void EveryShaderCompiles()
    {
        foreach (var (entry, target) in new[] { ("vsFill", "vs_5_0"), ("vsStroke", "vs_5_0"), ("vsFull", "vs_5_0"), ("psShape", "ps_5_0"), ("psComposite", "ps_5_0") })
            using (KosGpu.Compile(entry, target)) { }
    }

    [Fact]
    public void FillInFrontOfTheSceneShowsInStraightAlpha()
    {
        var p = At(Read(gpu.Render(Wall(5), Frame())), 32, 32);
        Assert.Equal((255, 0, 0), (p.R, p.G, p.B));
        Assert.InRange(p.A, 126, 129);
        Assert.Equal(KosGpuFeature.All, gpu.Enabled);
    }

    [Fact]
    public void FillBehindTheSceneIsHiddenOrFaded()
    {
        Assert.Equal(0, At(Read(gpu.Render(Wall(20), Frame())), 32, 32).A);
        Assert.InRange(At(Read(gpu.Render(Wall(20), Frame(occludedAlpha: 0.25f))), 32, 32).A, 30, 34);
        Assert.InRange(At(Read(gpu.Render(Wall(20), Frame(withDepth: false))), 32, 32).A, 126, 129);
    }

    [Fact]
    public void StrokeIsAsWideAsAsked()
    {
        var s = new KosShapes();
        s.Line(new Vector3(-3, 0, 5), new Vector3(3, 0, 5), new Vector4(0, 1, 0, 1), 4);
        var img = Read(gpu.Render(s, Frame()));
        Assert.Equal(255, At(img, 32, 31).A);
        Assert.Equal(255, At(img, 32, 33).A);
        Assert.Equal(0, At(img, 32, 36).A);
        Assert.Equal(0, At(img, 32, 27).A);
    }

    [Fact]
    public void UiMaskKeepsShapesOffTheGameUi()
    {
        var back = BackBuffer();
        try
        {
            var img = Read(gpu.Render(Wall(5), Frame(backBuffer: (nint)back)));
            Assert.Equal(0, At(img, 10, 40).A);
            Assert.InRange(At(img, 54, 40).A, 126, 129);
        }
        finally
        {
            back->Release();
        }
    }

    [Fact]
    public void AFaultSwitchesOffOnlyThatFeature()
    {
        gpu.InjectFault = KosGpuFeature.Depth;
        Assert.Equal(0, gpu.Render(Wall(20), Frame()));
        Assert.Equal(KosGpuFeature.All & ~KosGpuFeature.Depth, gpu.Enabled);
        Assert.Contains("Depth", gpu.LastFault);
        Assert.InRange(At(Read(gpu.Render(Wall(20), Frame())), 32, 32).A, 126, 129); // no depth: shows through

        gpu.InjectFault = KosGpuFeature.Fills;
        Assert.Equal(0, gpu.Render(Wall(5), Frame()));
        Assert.False(gpu.Enabled.HasFlag(KosGpuFeature.Fills));
        Assert.Equal(0, gpu.Render(Wall(5), Frame())); // fills now go to the ImGui path
        var line = new KosShapes();
        line.Line(new Vector3(-3, 0, 5), new Vector3(3, 0, 5), HalfRed, 4);
        Assert.NotEqual(0, gpu.Render(line, Frame()));

        gpu.InjectFault = KosGpuFeature.Composite;
        Assert.Equal(0, gpu.Render(line, Frame()));
        Assert.Equal(0, gpu.Render(line, Frame())); // composite gone: the whole GPU path is off
    }

    [Fact]
    public void TargetsFollowTheViewportSize()
    {
        Assert.Equal(Size, Read(gpu.Render(Wall(5), Frame(withDepth: false))).Width);
        Assert.Equal(32, Read(gpu.Render(Wall(5), Frame(withDepth: false, size: 32))).Width);
    }

    [Fact]
    public void DisposeReleasesEveryReference()
    {
        static uint Refs(ID3D11Device* d)
        {
            d->AddRef();
            return d->Release();
        }
        var before = Refs(device);
        var back = BackBuffer();
        var other = new KosGpu((nint)device);
        Read(other.Render(Wall(5), Frame(backBuffer: (nint)back)));
        Assert.True(Refs(device) > before);
        other.Dispose();
        other.Dispose();
        back->Release();
        Assert.Equal(before, Refs(device));
    }
}
