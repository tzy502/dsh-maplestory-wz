// WZ 画布 → RGBA 像素，以及 RGBA → PNG 编码。
//
// 为什么自己写 PNG 编码：插件要能被别人一条命令装上，多一个 NuGet 依赖就多一层
// 还原失败的可能。PNG 的最小写入集（IHDR/IDAT/IEND + zlib）用 BCL 就够了。
using System.IO.Compression;
using WzComparerR2.WzLib;
using WzComparerR2.WzLib.Utilities;

namespace WzWorker;

/// <summary>一张已解码的位图（RGBA8888，直通 alpha，行优先）。</summary>
internal sealed class Bitmap32
{
    public int W { get; }
    public int H { get; }
    public byte[] Px { get; }

    public Bitmap32(int w, int h)
    {
        W = Math.Max(1, w);
        H = Math.Max(1, h);
        Px = new byte[W * H * 4];
    }

    public Bitmap32(int w, int h, byte[] px)
    {
        W = w;
        H = h;
        Px = px;
    }

    /// <summary>最近邻放大（与 Python 侧 Image.NEAREST 一致，逐像素整数倍）。</summary>
    public Bitmap32 Scale(int k)
    {
        if (k <= 1) return this;
        var dst = new Bitmap32(W * k, H * k);
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var si = (y * W + x) * 4;
                for (var dy = 0; dy < k; dy++)
                {
                    for (var dx = 0; dx < k; dx++)
                    {
                        var di = ((y * k + dy) * dst.W + (x * k + dx)) * 4;
                        dst.Px[di] = Px[si];
                        dst.Px[di + 1] = Px[si + 1];
                        dst.Px[di + 2] = Px[si + 2];
                        dst.Px[di + 3] = Px[si + 3];
                    }
                }
            }
        }
        return dst;
    }
}

internal static class Png
{
    /// <summary>WZ 画布解码为 RGBA。格式分支照 WzComparerR2 的 ExtractPng + ImageCodec。</summary>
    public static Bitmap32 Decode(Wz_Png png)
    {
        var raw = png.GetRawData();
        int w = png.Width, h = png.Height;
        var rgba = new byte[w * h * 4];

        switch (png.Format)
        {
            case Wz_TextureFormat.ARGB8888:
                // WzLib 给的是 BGRA，转成 RGBA
                for (var i = 0; i < w * h; i++)
                {
                    rgba[i * 4] = raw[i * 4 + 2];
                    rgba[i * 4 + 1] = raw[i * 4 + 1];
                    rgba[i * 4 + 2] = raw[i * 4];
                    rgba[i * 4 + 3] = raw[i * 4 + 3];
                }
                break;

            case Wz_TextureFormat.ARGB4444:
                for (var i = 0; i < w * h; i++)
                {
                    int b0 = raw[i * 2], b1 = raw[i * 2 + 1];
                    rgba[i * 4] = N4(b1 & 0xF);
                    rgba[i * 4 + 1] = N4(b0 >> 4);
                    rgba[i * 4 + 2] = N4(b0 & 0xF);
                    rgba[i * 4 + 3] = N4(b1 >> 4);
                }
                break;

            case Wz_TextureFormat.ARGB1555:
                for (var i = 0; i < w * h; i++)
                {
                    var v = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                    rgba[i * 4] = N5(v & 0x1F);
                    rgba[i * 4 + 1] = N5((v >> 5) & 0x1F);
                    rgba[i * 4 + 2] = N5((v >> 10) & 0x1F);
                    rgba[i * 4 + 3] = (byte)((v >> 15) * 255);
                }
                break;

            case Wz_TextureFormat.RGB565:
                for (var i = 0; i < w * h; i++)
                {
                    var v = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                    rgba[i * 4] = N5(v & 0x1F);
                    rgba[i * 4 + 1] = N6((v >> 5) & 0x3F);
                    rgba[i * 4 + 2] = N5((v >> 11) & 0x1F);
                    rgba[i * 4 + 3] = 255;
                }
                break;

            default:
                {
                    // 压缩格式（DXT3/DXT5/BC7/RGBA1010102）：交给 WzLib 自己的 ImageCodec 解成 BGRA
                    var bgra = new byte[w * h * 4];
                    switch (png.Format)
                    {
                        case Wz_TextureFormat.DXT3:
                            ImageCodec.DXT3ToBGRA32(raw, bgra, w, w * 4, h);
                            break;
                        case Wz_TextureFormat.DXT5:
                            ImageCodec.DXT5ToBGRA32(raw, bgra, w, w * 4, h);
                            break;
                        case Wz_TextureFormat.RGBA1010102:
                            ImageCodec.R10G10B10A2ToBGRA32(raw, bgra);
                            break;
                        case Wz_TextureFormat.BC7:
                            {
                                // BC7 的输出是 RGBA，不是 BGRA（签名：blockData, srcStride, out, width, stride, height）
                                var rc = new byte[w * h * 4];
                                ImageCodec.BC7ToRGBA32(raw, w * 4, rc, w, w * 4, h);
                                Buffer.BlockCopy(rc, 0, rgba, 0, Math.Min(rc.Length, rgba.Length));
                                return new Bitmap32(w, h, rgba);
                            }
                        default:
                            throw new NotSupportedException($"不支持的纹理格式：{png.Format}");
                    }
                    for (var i = 0; i < w * h; i++)
                    {
                        rgba[i * 4] = bgra[i * 4 + 2];
                        rgba[i * 4 + 1] = bgra[i * 4 + 1];
                        rgba[i * 4 + 2] = bgra[i * 4];
                        rgba[i * 4 + 3] = bgra[i * 4 + 3];
                    }
                    break;
                }
        }
        return new Bitmap32(w, h, rgba);
    }

    /// <summary>RGBA → PNG 字节。</summary>
    public static byte[] Encode(Bitmap32 bmp)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)bmp.W);
        WriteBE(ihdr, 4, (uint)bmp.H);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: RGBA
        ihdr[10] = 0;  // deflate
        ihdr[11] = 0;  // adaptive filtering
        ihdr[12] = 0;  // no interlace
        WriteChunk(ms, "IHDR", ihdr);

        // 每行前置 filter byte 0（None）
        var stride = bmp.W * 4;
        var rawLen = (stride + 1) * bmp.H;
        var raw = new byte[rawLen];
        for (var y = 0; y < bmp.H; y++)
        {
            raw[y * (stride + 1)] = 0;
            Buffer.BlockCopy(bmp.Px, y * stride, raw, y * (stride + 1) + 1, stride);
        }
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, true))
        {
            z.Write(raw);
        }
        WriteChunk(ms, "IDAT", deflated.ToArray());
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        var crcInput = new byte[4 + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
        Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
        var crc = new byte[4];
        WriteBE(crc, 0, Crc32(crcInput));
        s.Write(crc);
    }

    private static void WriteBE(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24);
        buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8);
        buf[off + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static byte N4(int v) => (byte)(v * 17);
    private static byte N5(int v) => (byte)((v << 3) | (v >> 2));
    private static byte N6(int v) => (byte)((v << 2) | (v >> 4));
}
