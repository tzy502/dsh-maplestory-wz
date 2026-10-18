// 雪碧图合成与 GIF 编码。
//
// 合成规则与旧的 Python 实现逐字对齐（锚点空间取并集、单行优先、padding=2、
// alpha 直接覆盖而不是相乘），这样才能拿两边输出做像素级对比。
using System.Text;

namespace WzWorker;

/// <summary>一帧：位图 + 锚点 + 帧延迟。</summary>
internal sealed record Frame(string Name, Bitmap32 Img, int Ox, int Oy, int Delay);

/// <summary>帧在雪碧图里的落位。</summary>
internal sealed record FrameRect(int Index, string Name, int X, int Y, int W, int H, int Ox, int Oy, int Delay);

internal static class Sheet
{
    /// <summary>按 origin 锚点把帧对齐拼成一张图。</summary>
    public static (Bitmap32 Sheet, List<FrameRect> Frames, int CellW, int CellH) Compose(IReadOnlyList<Frame> frames, int scale = 1, int padding = 2)
    {
        if (frames.Count == 0) throw new InvalidOperationException("没有可解码的帧");
        scale = Math.Clamp(scale, 1, 8);

        var imgs = new List<Bitmap32>(frames.Count);
        var boxes = new List<(int Ox, int Oy, int W, int H)>(frames.Count);
        foreach (var fr in frames)
        {
            var im = fr.Img.Scale(scale);
            var ox = fr.Ox * scale;
            var oy = fr.Oy * scale;
            imgs.Add(im);
            boxes.Add((ox, oy, im.W, im.H));
        }

        // 锚点空间：图片占 [-ox, w-ox) × [-oy, h-oy)
        var spans = boxes.Select(b => (-b.Ox, -b.Oy, b.W, b.H)).ToList();
        var x0 = spans.Min(s => s.Item1);
        var y0 = spans.Min(s => s.Item2);
        var x1 = spans.Max(s => s.Item1 + s.Item3);
        var y1 = spans.Max(s => s.Item2 + s.Item4);
        var cw = Math.Max(1, x1 - x0);
        var ch = Math.Max(1, y1 - y0);

        // 单行优先；整图过宽才折行，避免超宽 PNG
        var cols = imgs.Count;
        while (cols > 1 && cols * (cw + padding) > 4096) cols = (cols + 1) / 2;
        var rows = (imgs.Count + cols - 1) / cols;

        var sheetW = cols * (cw + padding) + padding;
        var sheetH = rows * (ch + padding) + padding;
        var sheet = new Bitmap32(sheetW, sheetH);

        var rects = new List<FrameRect>(imgs.Count);
        for (var idx = 0; idx < imgs.Count; idx++)
        {
            var im = imgs[idx];
            var span = spans[idx];
            var col = idx % cols;
            var row = idx / cols;
            var px = padding + col * (cw + padding) + span.Item1 - x0;
            var py = padding + row * (ch + padding) + span.Item2 - y0;
            Blit(sheet, im, px, py, replace: true);
            var b = boxes[idx];
            rects.Add(new FrameRect(idx, frames[idx].Name, px, py, span.Item3, span.Item4, b.Ox, b.Oy, frames[idx].Delay));
        }
        return (sheet, rects, cw, ch);
    }

    /// <summary>
    /// 把 src 贴到 dst 的 (px,py)。replace=true 时直接覆盖（等价 Pillow 的
    /// alpha_composite 对这一类全透明底图的效果），false 时做标准 over 合成。
    /// </summary>
    public static void Blit(Bitmap32 dst, Bitmap32 src, int px, int py, bool replace)
    {
        for (var y = 0; y < src.H; y++)
        {
            var dy = py + y;
            if (dy < 0 || dy >= dst.H) continue;
            for (var x = 0; x < src.W; x++)
            {
                var dx = px + x;
                if (dx < 0 || dx >= dst.W) continue;
                var si = (y * src.W + x) * 4;
                var di = (dy * dst.W + dx) * 4;
                var sa = src.Px[si + 3];
                if (sa == 0) continue; // 全透明像素不落笔（与 alpha_composite 一致）
                if (replace)
                {
                    dst.Px[di] = src.Px[si];
                    dst.Px[di + 1] = src.Px[si + 1];
                    dst.Px[di + 2] = src.Px[si + 2];
                    dst.Px[di + 3] = (byte)Math.Min(255, sa + dst.Px[di + 3] * (255 - sa) / 255);
                    continue;
                }
                var da = dst.Px[di + 3];
                var outA = sa + da * (255 - sa) / 255;
                if (outA == 0) continue;
                for (var c = 0; c < 3; c++)
                {
                    var v = (src.Px[si + c] * sa + dst.Px[di + c] * da * (255 - sa) / 255) / outA;
                    dst.Px[di + c] = (byte)Math.Clamp(v, 0, 255);
                }
                dst.Px[di + 3] = (byte)outA;
            }
        }
    }

    /// <summary>
    /// 写 GIF89a 动画。调色板用固定的 6×6×6 色立方 + 透明索引，
    /// 不追求与 PIL 的量化结果一致（GIF 只用于预览，尺寸/帧数/延迟才是语义）。
    /// </summary>
    public static (int W, int H, int Frames) WriteGif(IReadOnlyList<Frame> frames, string path, int scale = 1, int maxSide = 480)
    {
        scale = Math.Clamp(scale, 1, 8);
        var raw = frames.Select(f =>
        {
            var im = f.Img.Scale(scale);
            return (Img: im, Ox: f.Ox * scale, Oy: f.Oy * scale, Delay: f.Delay > 0 ? f.Delay : 100);
        }).ToList();

        var spans = raw.Select(r => (-r.Ox, -r.Oy, r.Img.W, r.Img.H)).ToList();
        var gx0 = spans.Min(s => s.Item1);
        var gy0 = spans.Min(s => s.Item2);
        var gx1 = spans.Max(s => s.Item1 + s.Item3);
        var gy1 = spans.Max(s => s.Item2 + s.Item4);
        int w = Math.Max(1, gx1 - gx0), h = Math.Max(1, gy1 - gy0);
        if (Math.Max(w, h) > maxSide)
        {
            var k = maxSide / (double)Math.Max(w, h);
            w = Math.Max(1, (int)(w * k));
            h = Math.Max(1, (int)(h * k));
        }
        var zoom = gx1 > gx0 ? w / (double)(gx1 - gx0) : 1.0;

        var quant = new Quantizer();
        var built = new List<(Bitmap32 Img, int Delay)>(raw.Count);
        foreach (var (im, ox, oy, delay) in raw)
        {
            var canvas = new Bitmap32(w, h);
            var px = (int)((-ox - gx0) * zoom);
            var py = (int)((-oy - gy0) * zoom);
            var tw = Math.Max(1, (int)(im.W * zoom));
            var th = Math.Max(1, (int)(im.H * zoom));
            var resized = Resize(im, tw, th);
            Blit(canvas, resized, px, py, replace: true);
            built.Add((canvas, delay));
        }

        using var fs = File.Create(path);
        WriteGifBytes(fs, w, h, built, quant);
        return (w, h, built.Count);
    }

    /// <summary>最近邻缩放（与 Image.NEAREST 同义）。</summary>
    private static Bitmap32 Resize(Bitmap32 src, int tw, int th)
    {
        var dst = new Bitmap32(tw, th);
        for (var y = 0; y < th; y++)
        {
            var sy = src.H == th ? y : (int)((long)y * src.H / th);
            for (var x = 0; x < tw; x++)
            {
                var sx = src.W == tw ? x : (int)((long)x * src.W / tw);
                var si = (sy * src.W + sx) * 4;
                var di = (y * tw + x) * 4;
                dst.Px[di] = src.Px[si];
                dst.Px[di + 1] = src.Px[si + 1];
                dst.Px[di + 2] = src.Px[si + 2];
                dst.Px[di + 3] = src.Px[si + 3];
            }
        }
        return dst;
    }

    private static void WriteGifBytes(Stream s, int w, int h, List<(Bitmap32 Img, int Delay)> frames, Quantizer q)
    {
        // Header + Logical Screen Descriptor
        s.Write(Encoding.ASCII.GetBytes("GIF89a"));
        s.WriteByte((byte)(w & 0xFF)); s.WriteByte((byte)(w >> 8));
        s.WriteByte((byte)(h & 0xFF)); s.WriteByte((byte)(h >> 8));
        s.WriteByte(0xF7); // 全局色表 256 色
        s.WriteByte(0);    // 背景色索引
        s.WriteByte(0);    // 像素宽高比
        s.Write(q.Palette);

        // NETSCAPE2.0 循环扩展（0 = 无限循环）
        s.Write(new byte[] { 0x21, 0xFF, 0x0B });
        s.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        s.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });

        foreach (var (img, delay) in frames)
        {
            var indices = q.Index(img);
            // Graphic Control Extension：延迟单位 1/100 秒，透明色 = 255
            var cs = Math.Max(2, delay / 10);
            s.Write(new byte[] { 0x21, 0xF9, 0x04, 0x01, (byte)(cs & 0xFF), (byte)(cs >> 8), 0xFF, 0x00 });
            // Image Descriptor
            s.WriteByte(0x2C);
            s.Write(new byte[] { 0, 0, 0, 0 });
            s.WriteByte((byte)(w & 0xFF)); s.WriteByte((byte)(w >> 8));
            s.WriteByte((byte)(h & 0xFF)); s.WriteByte((byte)(h >> 8));
            s.WriteByte(0x00); // 无局部色表、非交错
            LzwEncode(s, indices, w * h);
        }
        s.WriteByte(0x3B); // Trailer
    }

    /// <summary>GIF 变长 LZW 编码。</summary>
    private static void LzwEncode(Stream s, byte[] indices, int count)
    {
        const int minCode = 8;
        var clear = 1 << minCode;
        var eoi = clear + 1;
        var next = eoi + 1;
        var codeSize = minCode + 1;

        var dict = new Dictionary<int, int>();
        var block = new List<byte>(256);
        var bitBuf = 0;
        var bitCount = 0;
        var cur = -1;

        void Flush()
        {
            if (block.Count == 0) return;
            s.WriteByte((byte)block.Count);
            s.Write(block.ToArray());
            block.Clear();
        }

        void Emit(int code)
        {
            bitBuf |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                block.Add((byte)(bitBuf & 0xFF));
                bitBuf >>= 8;
                bitCount -= 8;
                if (block.Count == 255) Flush();
            }
        }

        Emit(clear);
        for (var i = 0; i < count; i++)
        {
            var k = indices[i];
            if (cur < 0)
            {
                cur = k;
                continue;
            }
            var key = (cur << 8) | k;
            if (dict.TryGetValue(key, out var code))
            {
                cur = code;
                continue;
            }
            Emit(cur);
            dict[key] = next++;
            if (next > (1 << codeSize) && codeSize < 12) codeSize++;
            if (next >= 4096)
            {
                Emit(clear);
                dict.Clear();
                next = eoi + 1;
                codeSize = minCode + 1;
            }
            cur = k;
        }
        if (cur >= 0) Emit(cur);
        Emit(eoi);
        if (bitCount > 0)
        {
            block.Add((byte)(bitBuf & 0xFF));
        }
        Flush();
        s.WriteByte(0x00); // Block terminator
    }

    /// <summary>固定 6×6×6 色立方 + 透明索引的量化器。</summary>
    private sealed class Quantizer
    {
        public byte[] Palette { get; } = new byte[256 * 3];
        private const int Levels = 6;

        public Quantizer()
        {
            for (var r = 0; r < Levels; r++)
            {
                for (var g = 0; g < Levels; g++)
                {
                    for (var b = 0; b < Levels; b++)
                    {
                        var i = (r * Levels + g) * Levels + b;
                        Palette[i * 3] = (byte)(r * 255 / (Levels - 1));
                        Palette[i * 3 + 1] = (byte)(g * 255 / (Levels - 1));
                        Palette[i * 3 + 2] = (byte)(b * 255 / (Levels - 1));
                    }
                }
            }
            // 255 号留给透明
            Palette[255 * 3] = 0;
            Palette[255 * 3 + 1] = 0;
            Palette[255 * 3 + 2] = 0;
        }

        public byte[] Index(Bitmap32 img)
        {
            var idx = new byte[img.W * img.H];
            for (var i = 0; i < idx.Length; i++)
            {
                var a = img.Px[i * 4 + 3];
                if (a < 128)
                {
                    idx[i] = 255;
                    continue;
                }
                var r = img.Px[i * 4] * (Levels - 1) / 255;
                var g = img.Px[i * 4 + 1] * (Levels - 1) / 255;
                var b = img.Px[i * 4 + 2] * (Levels - 1) / 255;
                idx[i] = (byte)((r * Levels + g) * Levels + b);
            }
            return idx;
        }
    }
}
