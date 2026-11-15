#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_BLEND;
using static TerraFX.Interop.DirectX.D3D11_BLEND_OP;
using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_CULL_MODE;
using static TerraFX.Interop.DirectX.D3D11_FILL_MODE;
using static TerraFX.Interop.DirectX.D3D11_INPUT_CLASSIFICATION;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.D3D_PRIMITIVE_TOPOLOGY;
using static TerraFX.Interop.DirectX.D3D_SRV_DIMENSION;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace KosDraw;

/// <summary>The parts of the GPU path. Each one that fails is switched off on its own (a fault breaker).</summary>
[Flags]
internal enum KosGpuFeature
{
    None = 0,
    Fills = 1,
    Strokes = 2,
    /// <summary>Hides shapes behind scenery by reading the game's depth buffer.</summary>
    Depth = 4,
    /// <summary>Keeps shapes off the game's own UI by reading the back buffer's alpha.</summary>
    UiMask = 8,
    /// <summary>The final pass and the hand-over to ImGui: without it nothing on the GPU path shows.</summary>
    Composite = 16,
    All = Fills | Strokes | Depth | UiMask | Composite,
}

/// <summary>
/// One frame's inputs. <see cref="Depth"/> and <see cref="BackBuffer"/> are the game's <c>ID3D11Texture2D*</c>
/// (0 = not available): only read, never written, never released. <see cref="DepthScale"/> = depth texels per screen
/// pixel (dynamic resolution renders the scene smaller than the screen).
/// </summary>
internal readonly record struct KosGpuFrame(
    Matrix4x4 ViewProj, Vector2 Size, nint Depth, Vector2 DepthScale, nint BackBuffer, float OccludedAlpha, float Tolerance);

/// <summary>
/// The GPU path: records the shapes on a private deferred context into its own targets, testing each pixel against
/// the game's depth, then composites one straight-alpha texture for ImGui to draw. The recorded list runs on the
/// immediate context with the context state restored, so the game's and ImGui's state is untouched.
/// Not thread-safe: call only from the draw thread.
/// </summary>
internal sealed unsafe class KosGpu : IDisposable
{
    private const string Hlsl = """
        cbuffer Frame : register(b0)
        {
            row_major float4x4 viewProj;
            float2 size;          // viewport, pixels
            float2 depthScale;    // depth texels per pixel
            float occludedAlpha;  // alpha kept by a pixel behind scenery
            float tolerance;      // metres a pixel may sit behind the scene and still count as in front
            float useDepth;
            float useMask;
        };

        Texture2D<float> sceneDepth : register(t0);
        Texture2D<float4> shapes : register(t0);
        Texture2D<float4> uiMask : register(t1);

        struct PSIn { float4 pos : SV_Position; float4 color : COLOR; };

        PSIn vsFill(float3 pos : POSITION, float4 color : COLOR)
        {
            PSIn o;
            o.pos = mul(float4(pos, 1), viewProj);
            o.color = color;
            return o;
        }

        // Each line segment is a quad widened in screen space; corner.x picks the end (0 = a, 1 = b), corner.y the side.
        PSIn vsStroke(float3 a : POSITION0, float3 b : POSITION1, float2 corner : TEXCOORD0, float4 color : COLOR, float thickness : TEXCOORD1)
        {
            float4 ca = mul(float4(a, 1), viewProj);
            float4 cb = mul(float4(b, 1), viewProj);
            float2 half_size = size * 0.5;
            float2 d = cb.xy / cb.w * half_size - ca.xy / ca.w * half_size;
            float len = length(d);
            d = len > 1e-4 ? d / len : float2(1, 0);
            float2 n = float2(-d.y, d.x);
            float4 c = corner.x < 0.5 ? ca : cb;
            float2 px = (n * corner.y + d * (corner.x < 0.5 ? -1 : 1)) * thickness * 0.5; // square caps overlap at joins
            c.xy += px / half_size * c.w;
            PSIn o;
            o.pos = c;
            o.color = color;
            return o;
        }

        // Reverse-Z with an infinite far plane: depth = near / distance, so both depths turn back into metres.
        float4 psShape(PSIn i) : SV_Target
        {
            float4 c = i.color;
            if (useDepth > 0.5)
            {
                float near = viewProj._m32;
                float scene = sceneDepth.Load(int3(i.pos.xy * depthScale, 0));
                float behind = near / max(i.pos.z, 1e-6) - near / max(scene, 1e-6);
                if (behind > tolerance) c.a *= occludedAlpha;
            }
            return float4(c.rgb * c.a, c.a);
        }

        float4 vsFull(uint id : SV_VertexID) : SV_Position
        {
            float2 t = float2((id << 1) & 2, id & 2);
            return float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        // Premultiplied in, straight alpha out (what ImGui's image blend expects). Game UI marks the back buffer's alpha.
        float4 psComposite(float4 pos : SV_Position) : SV_Target
        {
            int3 p = int3(pos.xy, 0);
            float4 c = shapes.Load(p);
            if (useMask > 0.5)
            {
                float k = 1 - uiMask.Load(p).a;
                c *= k * k;
            }
            return c.a > 0 ? float4(c.rgb / c.a, c.a) : 0;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj;
        public Vector2 Size, DepthScale;
        public float OccludedAlpha, Tolerance, UseDepth, UseMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FillVertex
    {
        public Vector3 Pos;
        public uint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StrokeVertex
    {
        public Vector3 A, B;
        public Vector2 Corner;
        public uint Color;
        public float Thickness;
    }

    private static readonly Vector2[] QuadCorners = [new(0, -1), new(0, 1), new(1, 1), new(0, -1), new(1, 1), new(1, -1)];

    private ID3D11Device* device;
    private ID3D11DeviceContext* deferred;
    private ID3D11VertexShader* vsFill, vsStroke, vsFull;
    private ID3D11PixelShader* psShape, psComposite;
    private ID3D11InputLayout* fillLayout, strokeLayout;
    private ID3D11BlendState* premultiplied;
    private ID3D11RasterizerState* noCull;
    private ID3D11Buffer* constants;
    private ID3D11Buffer* fillBuffer, strokeBuffer;
    private uint fillCapacity, strokeCapacity; // bytes

    private int width, height;
    private ID3D11Texture2D* shapeTex, outTex;
    private ID3D11RenderTargetView* shapeRtv, outRtv;
    private ID3D11ShaderResourceView* shapeSrv, outSrv;

    private ID3D11ShaderResourceView* depthSrv;
    private nint maskSource;
    private ID3D11Texture2D* maskCopy;
    private ID3D11ShaderResourceView* maskSrv;

    private readonly List<FillVertex> fills = [];
    private readonly List<StrokeVertex> strokes = [];

    /// <summary>The features still on. A fault clears its flag for the life of this object.</summary>
    public KosGpuFeature Enabled { get; private set; } = KosGpuFeature.All;

    /// <summary>The last fault, for the host's status line.</summary>
    public string? LastFault { get; private set; }

    /// <summary>For testing the breakers: the next <see cref="Render"/> throws inside each feature set here, once.</summary>
    public KosGpuFeature InjectFault { get; set; }

    /// <param name="deviceUnknown">The game's device as an <c>IUnknown*</c> (Dalamud's <c>IUiBuilder.DeviceHandle</c>).</param>
    public KosGpu(nint deviceUnknown)
    {
        if (deviceUnknown == 0) throw new ArgumentException("No Direct3D device yet.", nameof(deviceUnknown));
        try
        {
            ID3D11Device* d;
            Guid* iid = TerraFX.Interop.Windows.Windows.__uuidof<ID3D11Device>();
            Check(((IUnknown*)deviceUnknown)->QueryInterface(iid, (void**)&d), "QueryInterface(ID3D11Device)");
            device = d;
            ID3D11DeviceContext* ctx;
            Check(device->CreateDeferredContext(0, &ctx), "CreateDeferredContext");
            deferred = ctx;
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Draws <paramref name="shapes"/>' fills and strokes (those whose features are on) and returns the composited
    /// texture as an <c>ID3D11ShaderResourceView*</c> for ImGui, owned by this object and valid until the next call.
    /// Returns 0 when nothing was drawn, including the frame a feature faults (it is off from then on).
    /// </summary>
    public nint Render(KosShapes shapes, in KosGpuFrame frame)
    {
        if (!Enabled.HasFlag(KosGpuFeature.Composite)) return 0;
        var doFills = Enabled.HasFlag(KosGpuFeature.Fills) && shapes.Fills.Count > 0;
        var doStrokes = Enabled.HasFlag(KosGpuFeature.Strokes) && shapes.Strokes.Count > 0;
        if (!doFills && !doStrokes) return 0;

        var stage = KosGpuFeature.Composite;
        try
        {
            EnsureTargets((int)MathF.Max(1, MathF.Round(frame.Size.X)), (int)MathF.Max(1, MathF.Round(frame.Size.Y)));

            ID3D11ShaderResourceView* depth = null, mask = null;
            if (Enabled.HasFlag(KosGpuFeature.Depth) && frame.Depth != 0)
            {
                stage = KosGpuFeature.Depth;
                Inject(stage);
                depth = DepthView(frame.Depth);
            }
            if (Enabled.HasFlag(KosGpuFeature.UiMask) && frame.BackBuffer != 0)
            {
                stage = KosGpuFeature.UiMask;
                Inject(stage);
                mask = MaskView(frame.BackBuffer); // null when the back buffer's size differs from the overlay's
            }

            stage = KosGpuFeature.Composite;
            var c = new Constants
            {
                ViewProj = frame.ViewProj, Size = new Vector2(width, height), DepthScale = frame.DepthScale,
                OccludedAlpha = frame.OccludedAlpha, Tolerance = frame.Tolerance,
                UseDepth = depth != null ? 1 : 0, UseMask = mask != null ? 1 : 0,
            };
            Upload(constants, &c, (uint)sizeof(Constants));
            BeginShapes(depth);

            if (doFills)
            {
                stage = KosGpuFeature.Fills;
                Inject(stage);
                DrawFills(shapes);
            }
            if (doStrokes)
            {
                stage = KosGpuFeature.Strokes;
                Inject(stage);
                DrawStrokes(shapes, frame.ViewProj);
            }

            stage = KosGpuFeature.Composite;
            Inject(stage);
            Composite(mask);
            Execute();
            return (nint)outSrv;
        }
        catch (Exception e)
        {
            Enabled &= ~stage;
            LastFault = $"{stage} switched off: {e.Message}";
            Discard();
            return 0;
        }
        finally
        {
            Release(ref depthSrv); // the executed command list keeps what it needs; nothing of the game's is held past the frame
        }
    }

    private void Inject(KosGpuFeature stage)
    {
        if (!InjectFault.HasFlag(stage)) return;
        InjectFault &= ~stage;
        throw new InvalidOperationException("injected fault");
    }

    private void CreatePipeline()
    {
        using var vsFillCode = Compile("vsFill", "vs_5_0");
        using var vsStrokeCode = Compile("vsStroke", "vs_5_0");
        using var vsFullCode = Compile("vsFull", "vs_5_0");
        using var psShapeCode = Compile("psShape", "ps_5_0");
        using var psCompositeCode = Compile("psComposite", "ps_5_0");

        fixed (ID3D11VertexShader** p = &vsFill) Check(device->CreateVertexShader(vsFillCode.Ptr, vsFillCode.Size, null, p), "CreateVertexShader(fill)");
        fixed (ID3D11VertexShader** p = &vsStroke) Check(device->CreateVertexShader(vsStrokeCode.Ptr, vsStrokeCode.Size, null, p), "CreateVertexShader(stroke)");
        fixed (ID3D11VertexShader** p = &vsFull) Check(device->CreateVertexShader(vsFullCode.Ptr, vsFullCode.Size, null, p), "CreateVertexShader(full)");
        fixed (ID3D11PixelShader** p = &psShape) Check(device->CreatePixelShader(psShapeCode.Ptr, psShapeCode.Size, null, p), "CreatePixelShader(shape)");
        fixed (ID3D11PixelShader** p = &psComposite) Check(device->CreatePixelShader(psCompositeCode.Ptr, psCompositeCode.Size, null, p), "CreatePixelShader(composite)");

        fixed (byte* position = "POSITION"u8, color = "COLOR"u8, texcoord = "TEXCOORD"u8) // u8 literals end in a NUL
        {
            var fill = stackalloc D3D11_INPUT_ELEMENT_DESC[]
            {
                Element(position, 0, DXGI_FORMAT_R32G32B32_FLOAT, 0),
                Element(color, 0, DXGI_FORMAT_R8G8B8A8_UNORM, 12),
            };
            fixed (ID3D11InputLayout** p = &fillLayout) Check(device->CreateInputLayout(fill, 2, vsFillCode.Ptr, vsFillCode.Size, p), "CreateInputLayout(fill)");
            var stroke = stackalloc D3D11_INPUT_ELEMENT_DESC[]
            {
                Element(position, 0, DXGI_FORMAT_R32G32B32_FLOAT, 0),
                Element(position, 1, DXGI_FORMAT_R32G32B32_FLOAT, 12),
                Element(texcoord, 0, DXGI_FORMAT_R32G32_FLOAT, 24),
                Element(color, 0, DXGI_FORMAT_R8G8B8A8_UNORM, 32),
                Element(texcoord, 1, DXGI_FORMAT_R32_FLOAT, 36),
            };
            fixed (ID3D11InputLayout** p = &strokeLayout) Check(device->CreateInputLayout(stroke, 5, vsStrokeCode.Ptr, vsStrokeCode.Size, p), "CreateInputLayout(stroke)");
        }

        var blend = new D3D11_BLEND_DESC();
        ref var rt = ref blend.RenderTarget[0];
        rt.BlendEnable = true;
        (rt.SrcBlend, rt.DestBlend, rt.BlendOp) = (D3D11_BLEND_ONE, D3D11_BLEND_INV_SRC_ALPHA, D3D11_BLEND_OP_ADD);
        (rt.SrcBlendAlpha, rt.DestBlendAlpha, rt.BlendOpAlpha) = (D3D11_BLEND_ONE, D3D11_BLEND_INV_SRC_ALPHA, D3D11_BLEND_OP_ADD);
        rt.RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL;
        fixed (ID3D11BlendState** p = &premultiplied) Check(device->CreateBlendState(&blend, p), "CreateBlendState");

        // DepthClipEnable keeps the hardware clipping at the near plane (reverse-Z: z <= w).
        var raster = new D3D11_RASTERIZER_DESC { FillMode = D3D11_FILL_SOLID, CullMode = D3D11_CULL_NONE, DepthClipEnable = true };
        fixed (ID3D11RasterizerState** p = &noCull) Check(device->CreateRasterizerState(&raster, p), "CreateRasterizerState");

        constants = DynamicBuffer((uint)sizeof(Constants), D3D11_BIND_CONSTANT_BUFFER);
    }

    private static D3D11_INPUT_ELEMENT_DESC Element(byte* name, uint index, DXGI_FORMAT format, uint offset) => new()
    {
        SemanticName = (sbyte*)name, SemanticIndex = index, Format = format, InputSlot = 0,
        AlignedByteOffset = offset, InputSlotClass = D3D11_INPUT_PER_VERTEX_DATA, InstanceDataStepRate = 0,
    };

    private ID3D11Buffer* DynamicBuffer(uint bytes, D3D11_BIND_FLAG bind)
    {
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = bytes, Usage = D3D11_USAGE_DYNAMIC, BindFlags = (uint)bind, CPUAccessFlags = (uint)D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* b;
        Check(device->CreateBuffer(&desc, null, &b), "CreateBuffer");
        return b;
    }

    private void Upload(ID3D11Buffer* buffer, void* data, uint bytes)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(deferred->Map((ID3D11Resource*)buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        Buffer.MemoryCopy(data, mapped.pData, bytes, bytes);
        deferred->Unmap((ID3D11Resource*)buffer, 0);
    }

    private void EnsureTargets(int w, int h)
    {
        if (w == width && h == height && outSrv != null) return;
        ReleaseTargets();
        Target(w, h, out shapeTex, out shapeRtv, out shapeSrv);
        Target(w, h, out outTex, out outRtv, out outSrv);
        (width, height) = (w, h);
    }

    private void Target(int w, int h, out ID3D11Texture2D* texture, out ID3D11RenderTargetView* view, out ID3D11ShaderResourceView* shaderView)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE),
        };
        ID3D11Texture2D* tex = null;
        ID3D11RenderTargetView* rtv = null;
        ID3D11ShaderResourceView* srv = null;
        try
        {
            Check(device->CreateTexture2D(&desc, null, &tex), "CreateTexture2D(target)");
            Check(device->CreateRenderTargetView((ID3D11Resource*)tex, null, &rtv), "CreateRenderTargetView");
            Check(device->CreateShaderResourceView((ID3D11Resource*)tex, null, &srv), "CreateShaderResourceView(target)");
        }
        catch
        {
            Release(ref srv);
            Release(ref rtv);
            Release(ref tex);
            throw;
        }
        texture = tex;
        view = rtv;
        shaderView = srv;
    }

    /// <summary>
    /// A typed view of the game's typeless depth texture, made afresh each frame and released when the frame ends:
    /// a view holds its texture alive, and the game replaces that texture whenever the resolution changes.
    /// </summary>
    private ID3D11ShaderResourceView* DepthView(nint texture)
    {
        Release(ref depthSrv);
        D3D11_TEXTURE2D_DESC desc;
        ((ID3D11Texture2D*)texture)->GetDesc(&desc);
        if (desc.SampleDesc.Count != 1) throw new NotSupportedException("multisampled depth");
        var format = desc.Format switch
        {
            DXGI_FORMAT_R24G8_TYPELESS or DXGI_FORMAT_D24_UNORM_S8_UINT => DXGI_FORMAT_R24_UNORM_X8_TYPELESS,
            DXGI_FORMAT_R32_TYPELESS or DXGI_FORMAT_D32_FLOAT => DXGI_FORMAT_R32_FLOAT,
            DXGI_FORMAT_R32G8X24_TYPELESS or DXGI_FORMAT_D32_FLOAT_S8X24_UINT => DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS,
            DXGI_FORMAT_R16_TYPELESS or DXGI_FORMAT_D16_UNORM => DXGI_FORMAT_R16_UNORM,
            _ => throw new NotSupportedException($"depth format {desc.Format}"),
        };
        var view = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = format, ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D };
        view.Texture2D.MipLevels = 1;
        ID3D11ShaderResourceView* srv;
        Check(device->CreateShaderResourceView((ID3D11Resource*)texture, &view, &srv), "CreateShaderResourceView(depth)"); // holds its own reference
        depthSrv = srv;
        return srv;
    }

    /// <summary>A copy of the back buffer (swap-chain buffers can't be read by a shader), re-copied every frame.</summary>
    private ID3D11ShaderResourceView* MaskView(nint backBuffer)
    {
        D3D11_TEXTURE2D_DESC desc;
        ((ID3D11Texture2D*)backBuffer)->GetDesc(&desc);
        if (desc.Width != width || desc.Height != height) return null;
        var stale = backBuffer != maskSource || maskSrv == null;
        if (!stale) // the same address can come back as a new buffer after a resize; CopyResource drops a mismatched copy silently
        {
            D3D11_TEXTURE2D_DESC have;
            maskCopy->GetDesc(&have);
            stale = have.Width != desc.Width || have.Height != desc.Height || have.Format != desc.Format;
        }
        if (stale)
        {
            Release(ref maskSrv);
            Release(ref maskCopy);
            maskSource = 0;
            if (desc.SampleDesc.Count != 1) throw new NotSupportedException("multisampled back buffer");
            var copy = new D3D11_TEXTURE2D_DESC
            {
                Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = desc.Format,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE_DEFAULT, BindFlags = (uint)D3D11_BIND_SHADER_RESOURCE,
            };
            ID3D11Texture2D* tex;
            Check(device->CreateTexture2D(&copy, null, &tex), "CreateTexture2D(mask)");
            maskCopy = tex;
            ID3D11ShaderResourceView* srv;
            Check(device->CreateShaderResourceView((ID3D11Resource*)tex, null, &srv), "CreateShaderResourceView(mask)");
            maskSrv = srv;
            maskSource = backBuffer;
        }
        deferred->CopyResource((ID3D11Resource*)maskCopy, (ID3D11Resource*)backBuffer);
        return maskSrv;
    }

    private void BeginShapes(ID3D11ShaderResourceView* depth)
    {
        var clear = stackalloc float[4];
        deferred->ClearRenderTargetView(shapeRtv, clear);
        var viewport = new D3D11_VIEWPORT { Width = width, Height = height, MinDepth = 0, MaxDepth = 1 };
        deferred->RSSetViewports(1, &viewport);
        deferred->RSSetState(noCull);
        var rtv = shapeRtv;
        deferred->OMSetRenderTargets(1, &rtv, null);
        deferred->OMSetBlendState(premultiplied, null, 0xffffffff);
        deferred->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        var cb = constants;
        deferred->VSSetConstantBuffers(0, 1, &cb);
        deferred->PSSetConstantBuffers(0, 1, &cb);
        deferred->PSSetShader(psShape, null, 0);
        deferred->PSSetShaderResources(0, 1, &depth);
    }

    private void DrawFills(KosShapes shapes)
    {
        fills.Clear();
        foreach (var f in shapes.Fills)
        {
            fills.Add(new FillVertex { Pos = f.A, Color = f.Color });
            fills.Add(new FillVertex { Pos = f.B, Color = f.Color });
            fills.Add(new FillVertex { Pos = f.C, Color = f.Color });
        }
        DrawVertices(CollectionsMarshal.AsSpan(fills), ref fillBuffer, ref fillCapacity, fillLayout, vsFill);
    }

    private void DrawStrokes(KosShapes shapes, in Matrix4x4 viewProj)
    {
        strokes.Clear();
        foreach (var s in shapes.Strokes)
        foreach (var run in KosProjection.ClipPolyline(viewProj, s.Points, s.Closed)) // the quad widening needs w > 0
            for (var k = 0; k < run.Count - 1; k++)
                foreach (var corner in QuadCorners)
                    strokes.Add(new StrokeVertex { A = run[k], B = run[k + 1], Corner = corner, Color = s.Color, Thickness = s.Thickness });
        if (strokes.Count > 0) DrawVertices(CollectionsMarshal.AsSpan(strokes), ref strokeBuffer, ref strokeCapacity, strokeLayout, vsStroke);
    }

    private void DrawVertices<T>(Span<T> vertices, ref ID3D11Buffer* buffer, ref uint capacity, ID3D11InputLayout* layout, ID3D11VertexShader* vs)
        where T : unmanaged
    {
        var bytes = (uint)(vertices.Length * sizeof(T));
        if (bytes > capacity)
        {
            var grown = Math.Max(bytes, Math.Max(capacity * 2, 64 * 1024u));
            Release(ref buffer);
            capacity = 0;
            buffer = DynamicBuffer(grown, D3D11_BIND_VERTEX_BUFFER);
            capacity = grown;
        }
        fixed (T* data = vertices) Upload(buffer, data, bytes);
        var b = buffer;
        var stride = (uint)sizeof(T);
        var offset = 0u;
        deferred->IASetInputLayout(layout);
        deferred->IASetVertexBuffers(0, 1, &b, &stride, &offset);
        deferred->VSSetShader(vs, null, 0);
        deferred->Draw((uint)vertices.Length, 0);
    }

    private void Composite(ID3D11ShaderResourceView* mask)
    {
        var rtv = outRtv;
        deferred->OMSetRenderTargets(1, &rtv, null); // unbinds the shape target before it is read
        deferred->OMSetBlendState(null, null, 0xffffffff);
        deferred->IASetInputLayout(null);
        deferred->VSSetShader(vsFull, null, 0);
        deferred->PSSetShader(psComposite, null, 0);
        var views = stackalloc ID3D11ShaderResourceView*[2] { shapeSrv, mask };
        deferred->PSSetShaderResources(0, 2, views);
        deferred->Draw(3, 0);
    }

    private void Execute()
    {
        ID3D11CommandList* list;
        Check(deferred->FinishCommandList(false, &list), "FinishCommandList");
        ID3D11DeviceContext* immediate;
        device->GetImmediateContext(&immediate);
        immediate->ExecuteCommandList(list, true); // true: restore the immediate context's state afterwards
        immediate->Release();
        list->Release();
    }

    /// <summary>Drops whatever a failed frame half-recorded.</summary>
    private void Discard()
    {
        if (deferred == null) return;
        ID3D11CommandList* list = null;
        if (deferred->FinishCommandList(false, &list).SUCCEEDED && list != null) list->Release();
    }

    private void ReleaseTargets()
    {
        Release(ref outSrv);
        Release(ref outRtv);
        Release(ref outTex);
        Release(ref shapeSrv);
        Release(ref shapeRtv);
        Release(ref shapeTex);
        (width, height) = (0, 0);
    }

    /// <summary>Releases every COM reference, newest first. Safe to call twice.</summary>
    public void Dispose()
    {
        Release(ref maskSrv);
        Release(ref maskCopy);
        Release(ref depthSrv);
        ReleaseTargets();
        Release(ref strokeBuffer);
        Release(ref fillBuffer);
        Release(ref constants);
        Release(ref noCull);
        Release(ref premultiplied);
        Release(ref strokeLayout);
        Release(ref fillLayout);
        Release(ref psComposite);
        Release(ref psShape);
        Release(ref vsFull);
        Release(ref vsStroke);
        Release(ref vsFill);
        Release(ref deferred);
        Release(ref device);
        (maskSource, fillCapacity, strokeCapacity) = (0, 0, 0);
    }

    private static void Release<T>(ref T* p) where T : unmanaged
    {
        if (p == null) return;
        ((IUnknown*)p)->Release();
        p = null;
    }

    private static void Check(HRESULT hr, string what)
    {
        if (hr.FAILED) throw new InvalidOperationException($"{what} failed (0x{hr.Value:X8})");
    }

    /// <summary>Compiled shader bytecode.</summary>
    internal readonly struct Blob(ID3DBlob* blob) : IDisposable
    {
        public void* Ptr => blob->GetBufferPointer();
        public nuint Size => blob->GetBufferSize();
        public void Dispose() => blob->Release();
    }

    /// <summary>Compiles one entry point of <see cref="Hlsl"/> with the system's d3dcompiler_47.</summary>
    internal static Blob Compile(string entry, string target)
    {
        var src = Encoding.UTF8.GetBytes(Hlsl);
        var e = Encoding.ASCII.GetBytes(entry + "\0");
        var t = Encoding.ASCII.GetBytes(target + "\0");
        ID3DBlob* code = null, errors = null;
        HRESULT hr;
        fixed (byte* s = src, pe = e, pt = t)
            hr = DirectX.D3DCompile(s, (nuint)src.Length, null, null, null, (sbyte*)pe, (sbyte*)pt, 1u << 11 /* D3DCOMPILE_ENABLE_STRICTNESS */, 0, &code, &errors);
        var message = errors != null ? Marshal.PtrToStringAnsi((nint)errors->GetBufferPointer(), (int)errors->GetBufferSize()) : null;
        if (errors != null) errors->Release();
        if (hr.FAILED || code == null) throw new InvalidOperationException($"shader {entry} did not compile (0x{hr.Value:X8}): {message}");
        return new Blob(code);
    }
}
