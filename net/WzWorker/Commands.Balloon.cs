// 聊天气泡命令：UI/ChatBalloon.img 的九宫格拼接 + 文字光栅化。
//
// 为什么逐行照搬 旧实现 的布局算法：气泡的像素形状由 origin 定位和
// 「n/s 按列宽平铺、c 双向平铺」决定，任何一处改成拉伸或整图缩放都会让瓦片
// 与边缘错位；文字换行也必须用同一套度量口径（字体真实 advance 宽度），
// 否则长文本会在不同的字处断行、居中也会偏移。
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SkiaSharp;
using WzComparerR2.WzLib;

namespace WzWorker;

internal static partial class Program
{
    internal static JsonNode? TryDispatchBalloon(string cmd, JsonObject args) => cmd switch
    {
        "balloon_styles" => CmdBalloonStyles(args),
        "balloon" => CmdBalloon(args),
        _ => null,
    };

    /// <summary>
    /// 直调入口（TryDispatchBalloon 已由 Program.Dispatch 接管，这里留给验收脚本
    /// 用反射调用 WzWorker.Program.SelfTestBalloon("balloon", args)；
    /// 环境变量与 worker 进程一致：WZ_DATA_DIR / WZ_REGION / WZ_VERSION / WZ_OUT_DIR）。
    /// </summary>
    internal static JsonNode SelfTestBalloon(string cmd, JsonObject args)
    {
        if (string.IsNullOrEmpty(_dataDir))
        {
            _dataDir = Environment.GetEnvironmentVariable("WZ_DATA_DIR") ?? "";
            _region = Environment.GetEnvironmentVariable("WZ_REGION") ?? "BMS";
            _version = int.TryParse(Environment.GetEnvironmentVariable("WZ_VERSION"), out var v) ? v : 228;
        }
        return TryDispatchBalloon(cmd, args)
            ?? throw new NotSupportedException($"SelfTestBalloon 只支持 balloon / balloon_styles：{cmd}");
    }

    // 与 旧实现 BALLOON_PARTS 一致：九宫格 + 顶行头像 + 底部箭头。
    private static readonly string[] BalloonParts =
    {
        "nw", "n", "head", "ne", "w", "c", "e", "sw", "s", "arrow", "se",
    };

    // 「可拼」只要求九宫格齐全：head/arrow 是可选层，缺了也能出气泡。
    private static readonly string[] BalloonNeed = { "nw", "n", "ne", "w", "c", "e", "sw", "s", "se" };

    // 字体候选与 旧实现:_FONT_CANDIDATES 逐字一致。
    // 同一字号下不同字体的 advance 不同（实测「你好，冒险岛！」在 12px 下
    // Songti/STHeiti 为 84px、PingFang 约 69px），所以顺序会改变换行与居中，
    // 不能为了「更好看」调整这个列表。
    private static readonly string[] BalloonFontCandidates =
    {
        "/System/Library/Fonts/PingFang.ttc",
        "/System/Library/Fonts/PingFang SC.ttc",
        "/System/Library/Fonts/Supplemental/Songti.ttc",
        "/System/Library/Fonts/STHeiti Medium.ttc",
        "/System/Library/Fonts/Hiragino Sans GB.ttc",
        "/Library/Fonts/Arial Unicode.ttf",
        // Windows
        "C:/Windows/Fonts/msyh.ttc",
        "C:/Windows/Fonts/msyhbd.ttc",
        "C:/Windows/Fonts/simhei.ttf",
        "C:/Windows/Fonts/simsun.ttc",
        // Linux（Noto / 文泉驿 / 文鼎）
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc",
        "/usr/share/fonts/truetype/arphic/uming.ttc",
    };

    private static readonly Bitmap32 BalloonBlank = new(1, 1);
    private static readonly Dictionary<string, BalloonStyle> BalloonStyleCache = new(StringComparer.Ordinal);
    private static SKTypeface? _balloonTypeface;

    private sealed record BalloonTile(Bitmap32 Img, int Ox, int Oy);

    private sealed record BalloonStyle(Dictionary<string, BalloonTile> Tiles, int? ClrRaw);

    // ── 样式清单 ────────────────────────────────────────────────────────

    /// <summary>列出 ChatBalloon.img 里哪些样式真的能拼（九宫格必须齐全）。</summary>
    private static JsonNode CmdBalloonStyles(JsonObject args)
    {
        var img = BalloonImage();
        var rows = new List<(string Id, List<string> Has, bool Usable)>();
        foreach (var child in img.Nodes)
        {
            var names = new HashSet<string>(child.Nodes.Select(n => n.Text), StringComparer.Ordinal);
            var has = BalloonParts.Where(names.Contains).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var usable = BalloonNeed.All(names.Contains);
            rows.Add((child.Text, has, usable));
        }

        var styles = new JsonArray();
        foreach (var row in rows.Where(r => r.Usable).Take(40)) styles.Add(BalloonStyleRow(row));
        var broken = new JsonArray();
        foreach (var row in rows.Where(r => !r.Usable).Take(20)) broken.Add(BalloonStyleRow(row));

        return new JsonObject
        {
            ["total"] = rows.Count,
            ["usable"] = rows.Count(r => r.Usable),
            ["styles"] = styles,
            ["broken"] = broken,
        };
    }

    private static JsonObject BalloonStyleRow((string Id, List<string> Has, bool Usable) row) => new()
    {
        ["id"] = row.Id,
        ["has"] = new JsonArray(row.Has.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
        ["usable"] = row.Usable,
    };

    // ── 气泡渲染 ────────────────────────────────────────────────────────

    /// <summary>
    /// 把一句话渲染成冒险岛聊天气泡 PNG。
    /// 参数与 旧实现 的 cmd_balloon 对齐：text / style / fontSize。
    /// </summary>
    private static JsonNode CmdBalloon(JsonObject args)
    {
        var text = BalloonTextArg(args);
        var wantStyle = BalloonStyleArg(args);

        // 未指定样式时取第一个可拼的；指定的样式如果解不出 nw 也回退。
        var candidates = new List<string>();
        if (wantStyle.Length > 0)
        {
            candidates.Add(wantStyle);
        }
        else
        {
            if (CmdBalloonStyles(new JsonObject())["styles"] is JsonArray available)
            {
                foreach (var row in available)
                {
                    if (row?["id"]?.GetValue<string>() is { } sid) candidates.Add(sid);
                }
            }
            if (candidates.Count == 0) candidates.Add("5");
        }

        BalloonStyle? style = null;
        var styleId = "";
        foreach (var sid in candidates)
        {
            var loaded = LoadBalloonStyle(sid);
            if (loaded is not null && loaded.Tiles.ContainsKey("nw"))
            {
                style = loaded;
                styleId = sid;
                break;
            }
        }
        if (style is null)
        {
            throw new InvalidOperationException($"没有可用的气泡样式（试过 {string.Join(", ", candidates.Take(5))}）");
        }

        var tiles = style.Tiles;
        Bitmap32? Tile(string name) => tiles.TryGetValue(name, out var t) ? t.Img : null;
        (int X, int Y) Org(string name) => tiles.TryGetValue(name, out var t) ? (t.Ox, t.Oy) : (0, 0);

        // 缺片用 1×1 透明占位：布局仍按同类尺寸兜底，画出来不可见，
        // 这样 nw/ne/sw/se 不全的样式（如 id=1）也能出图而不是直接失败。
        var nw = BalloonUsable(Tile("nw")) ? Tile("nw")! : BalloonBlank;
        var n = BalloonUsable(Tile("n")) ? Tile("n")! : BalloonBlank;
        var ne = BalloonUsable(Tile("ne")) ? Tile("ne")! : BalloonBlank;
        var wTile = BalloonUsable(Tile("w")) ? Tile("w")! : BalloonBlank;
        var eTile = BalloonUsable(Tile("e")) ? Tile("e")! : BalloonBlank;
        var sw = BalloonUsable(Tile("sw")) ? Tile("sw")! : BalloonBlank;
        var sTile = BalloonUsable(Tile("s")) ? Tile("s")! : BalloonBlank;
        var se = BalloonUsable(Tile("se")) ? Tile("se")! : BalloonBlank;
        var arrow = Tile("arrow");
        var head = Tile("head");

        var cOrig = Tile("c");
        var colW = cOrig is not null && cOrig.W > 1
            ? cOrig.W
            : Math.Max(12, wTile.W > 1 ? wTile.W : (n.H > 1 ? n.H : 16));
        var colH = cOrig is not null && cOrig.H > 1
            ? cOrig.H
            : Math.Max(12, n.H > 1 ? n.H : 16);
        var tiledCenter = cOrig is not null && cOrig.W > 1;
        var c = tiledCenter ? cOrig! : BalloonBlank;

        // clr 是 int32 ARGB；0/-1 视为「未指定」，用深灰保证浅色气泡上可见。
        var textRgb = (R: 0x22, G: 0x22, B: 0x22);
        var clr = style.ClrRaw;
        if (clr is not null && clr != 0 && clr != -1)
        {
            var rgb = (0x1000000 + clr.Value) & 0xFFFFFF;
            textRgb = ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        var fontSize = BalloonFontSize(args);
        const double wrapW = 90.0;
        const double lineH = 16.0;
        using var font = BalloonFont(fontSize);
        var lines = BalloonWrap(text, (float)wrapW, font);

        var minW = Math.Max(80, (int)wrapW);
        var minH = Math.Max(40, (int)(lines.Count * lineH));
        if (!tiledCenter)
        {
            // 没有 c 时中央区只能靠文字撑开，否则文字会溢出气泡。
            colW = Math.Max(80, (int)wrapW);
            colH = Math.Max(20, (int)(lines.Count * lineH + 10));
        }
        var colCount = Math.Max(1, (int)Math.Ceiling(minW / (double)colW));
        var rowCount = Math.Max(1, (int)Math.Ceiling(minH / (double)colH));

        var (nwOx, nwOy) = Org("nw");
        var (nOx, nOy) = Org("n");
        var (neOx, neOy) = Org("ne");
        var (wOx, wOy) = Org("w");
        var (eOx, eOy) = Org("e");
        var (swOx, swOy) = Org("sw");
        var (sOx, sOy) = Org("s");
        var (seOx, seOy) = Org("se");
        var (aOx, aOy) = Org("arrow");
        var (hOx, hOy) = Org("head");

        var topOffset = Math.Max(nwOy, nOy);
        var xOffArrow = arrow is not null && sTile.W > arrow.W ? sTile.W - arrow.W : 0;

        // origin.x == -1 是 MapleSalon2 的「未定义」哨兵；有 arrow 偏移时不能用 0，
        // 否则顶行宽度会和底行的箭头缩进对不上。
        double PivotX(int ox) => ox == -1 && xOffArrow == 0 ? 0.0 : ox;

        var rects = new List<(Bitmap32 Img, double L, double T)>();

        // 顶行：nw → n×colCount（正中换 head）→ ne
        rects.Add((nw, -PivotX(nwOx), -nwOy));
        double x = nw.W - PivotX(nwOx);
        var half = colCount / 2;
        for (var i = 0; i < colCount; i++)
        {
            if (i == half && BalloonUsable(head)) rects.Add((head!, x - PivotX(hOx), -hOy));
            else rects.Add((n, x - PivotX(nOx), -nOy));
            x += n.W;
        }
        x -= xOffArrow;
        rects.Add((ne, x - PivotX(neOx), -neOy));

        // 中部：w×rowCount + 中央 c 网格 + e×rowCount
        double y = nw.H - topOffset;
        var leftOff = wTile.W - PivotX(wOx);
        var rightX = leftOff + colW * colCount - xOffArrow;
        var centerTop = y;
        for (var i = 0; i < rowCount; i++)
        {
            rects.Add((wTile, -PivotX(wOx), y - wOy));
            rects.Add((eTile, rightX - PivotX(eOx), y - eOy));
            y += colH;
        }
        if (tiledCenter)
        {
            for (var ty = 0; ty < rowCount; ty++)
            {
                for (var tx = 0; tx < colCount; tx++)
                {
                    rects.Add((c, leftOff + tx * colW, centerTop + ty * colH));
                }
            }
        }
        else
        {
            // 透明占位撑出中央区尺寸，和 旧实现「无 c 用 placeholder」一致。
            var placeholder = new Bitmap32(Math.Max(1, colW * colCount), Math.Max(1, colH * rowCount));
            rects.Add((placeholder, leftOff, centerTop));
        }

        // 底行：sw → s×colCount（第一格换 arrow）→ se
        rects.Add((sw, -PivotX(swOx), y - swOy));
        x = sw.W - PivotX(swOx);
        for (var i = 0; i < colCount; i++)
        {
            if (i == 0 && BalloonUsable(arrow))
            {
                rects.Add((arrow!, x - PivotX(aOx), y - aOy));
                if (sTile.W > arrow!.W) x -= sTile.W - arrow.W;
            }
            else
            {
                rects.Add((sTile, x - PivotX(sOx), y - sOy));
            }
            // 每轮都前进 colWidth（不是瓦片宽度）：arrow 换掉 s 时下一块仍要
            // 落在网格上，否则会与箭头重叠。
            x += colW;
        }
        rects.Add((se, x - PivotX(seOx), y - seOy));

        // origin 会把顶行推到负坐标，先求并集再整体平移。
        var minL = rects.Min(r => r.L);
        var minT = rects.Min(r => r.T);
        var maxR = rects.Max(r => r.L + r.Img.W);
        var maxB = rects.Max(r => r.T + r.Img.H);
        var totalW = (int)Math.Ceiling(maxR - minL);
        var totalH = (int)Math.Ceiling(maxB - minT);
        if (totalW <= 0 || totalH <= 0) throw new InvalidOperationException("气泡尺寸算出来是空的");

        var outBmp = new Bitmap32(totalW, totalH);
        foreach (var rect in rects)
        {
            Sheet.Blit(outBmp, rect.Img,
                (int)Math.Round(rect.L - minL), (int)Math.Round(rect.T - minT), replace: false);
        }

        // 文字块起点沿用 minipet 的两段式：先按气泡局部坐标取 textBlock，
        // 再加回整张画布被 origin 平移掉的那段（-minT），漏掉第二段文字会偏上。
        var textBlockMinT = nwOy - nw.H;
        var textX = leftOff + colW * colCount / 2.0;
        var textY0 = nw.H - topOffset + -textBlockMinT;
        BalloonDrawText(outBmp, lines, textX, textY0, lineH, font,
            new SKColor((byte)textRgb.R, (byte)textRgb.G, (byte)textRgb.B, 255));

        // 文件名确定性：旧的 hash(str) 每进程随机，会让验收读到旧图。
        var digest = Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes($"{styleId}|{text}|{fontSize}")))[..10].ToLowerInvariant();
        var fileName = $"balloon_{styleId}_{digest}.png";
        var outDir = Environment.GetEnvironmentVariable("WZ_OUT_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
        {
            outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "wz-assets");
        }
        Directory.CreateDirectory(outDir);
        var pngPath = Path.Combine(outDir, fileName);
        File.WriteAllBytes(pngPath, Png.Encode(outBmp));

        return new JsonObject
        {
            ["png"] = pngPath,
            ["url"] = $"/file/{fileName}",
            ["style"] = styleId,
            ["w"] = outBmp.W,
            ["h"] = outBmp.H,
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
            ["colCount"] = colCount,
            ["rowCount"] = rowCount,
            ["colW"] = colW,
            ["colH"] = colH,
            ["tiledCenter"] = tiledCenter,
            ["textColor"] = new JsonArray(
                JsonValue.Create(textRgb.R),
                JsonValue.Create(textRgb.G),
                JsonValue.Create(textRgb.B),
                JsonValue.Create(255)),
        };
    }

    // ── 瓦片解析 ────────────────────────────────────────────────────────

    private static Wz_Node BalloonImage() =>
        ResolveNode("UI/ChatBalloon.img") ?? throw new FileNotFoundException("UI/ChatBalloon.img 读不到");

    /// <summary>取一个样式的九片（解 _outlink 后的真图 + origin）。</summary>
    private static BalloonStyle? LoadBalloonStyle(string styleId)
    {
        if (BalloonStyleCache.TryGetValue(styleId, out var cached)) return cached;
        var img = BalloonImage();
        var node = img.Nodes.FirstOrDefault(n => n.Text == styleId);
        if (node is null) return null;

        var tiles = new Dictionary<string, BalloonTile>(StringComparer.Ordinal);
        foreach (var name in BalloonParts)
        {
            var child = node.Nodes.FirstOrDefault(n => n.Text == name);
            if (child is null) continue;
            var tile = BalloonTileMeta(child, img);
            if (tile is not null) tiles[name] = tile;
        }

        int? clr = null;
        if (node.Nodes.FirstOrDefault(n => n.Text == "clr")?.Value is int clrValue) clr = clrValue;
        var style = new BalloonStyle(tiles, clr);
        BalloonStyleCache[styleId] = style;
        return style;
    }

    /// <summary>
    /// 解析一个瓦片节点：占位画布（1×1）要跟着 _outlink/_inlink 找到真图，
    /// origin 优先取真图自己的，其次退回占位节点上的（本客户端两者互补）。
    /// </summary>
    private static BalloonTile? BalloonTileMeta(Wz_Node node, Wz_Node imgRoot)
    {
        var cur = node.ResolveUol();
        for (var step = 0; step < 8; step++)
        {
            if (cur is null) return null;
            var png = cur.GetValue<Wz_Png>() ?? cur.Value as Wz_Png;
            if (png is not null && !(png.Width <= 1 && png.Height <= 1))
            {
                Bitmap32 bmp;
                try
                {
                    bmp = Png.Decode(png);
                }
                catch
                {
                    return null;
                }
                var origin = BalloonOrigin(cur) ?? BalloonOrigin(node);
                return new BalloonTile(bmp, origin?.X ?? 0, origin?.Y ?? 0);
            }

            var link = cur.FindNodeByPath("_outlink", true)?.Value as string
                    ?? cur.FindNodeByPath("_inlink", true)?.Value as string;
            if (string.IsNullOrWhiteSpace(link)) return null;
            var next = BalloonFollowLink(link!, imgRoot);
            if (next is null) return null;
            cur = next.ResolveUol();
        }
        return null;
    }

    /// <summary>链接有两种口径：带分类前缀的绝对路径，和 img 内相对路径。</summary>
    private static Wz_Node? BalloonFollowLink(string link, Wz_Node imgRoot)
    {
        var normalized = link.Replace('\\', '/').Trim('/');
        var parts = normalized.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (FindTop(parts[0]) is not null) return ResolveNode(normalized);
        if (parts.Length < 2) return null;
        return imgRoot.FindNodeByPath(parts[1].Replace('/', '\\'), true);
    }

    private static Wz_Vector? BalloonOrigin(Wz_Node node) =>
        node.FindNodeByPath("origin", true)?.Value as Wz_Vector;

    private static bool BalloonUsable(Bitmap32? tile) => tile is not null && tile.W > 1 && tile.H > 1;

    // ── 文字 ────────────────────────────────────────────────────────────

    /// <summary>按候选顺序取第一个能加载的字体；全失败才退回系统默认。</summary>
    private static SKTypeface BalloonTypeface()
    {
        if (_balloonTypeface is not null) return _balloonTypeface;
        foreach (var path in BalloonFontCandidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var typeface = SKTypeface.FromFile(path);
                if (typeface is not null)
                {
                    _balloonTypeface = typeface;
                    return typeface;
                }
            }
            catch
            {
                // 字体文件损坏或格式不支持：继续下一个候选，别让整个命令失败。
            }
        }
        _balloonTypeface = SKTypeface.FromFamilyName("sans-serif") ?? SKTypeface.Default;
        return _balloonTypeface;
    }

    private static SKFont BalloonFont(int fontSize) => new(BalloonTypeface(), fontSize)
    {
        Subpixel = true,
        Hinting = SKFontHinting.Normal,
        Edging = SKFontEdging.Antialias,
    };

    /// <summary>逐字符断行：中文没有空格可断，超过 wrapW 就在当前字前换行。</summary>
    private static List<string> BalloonWrap(string text, float wrapW, SKFont font)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                lines.Add(cur);
                cur = "";
                continue;
            }
            var ch = rune.ToString();
            var trial = cur + ch;
            if (cur.Length > 0 && font.MeasureText(trial) > wrapW)
            {
                lines.Add(cur);
                cur = ch;
            }
            else
            {
                cur = trial;
            }
        }
        lines.Add(cur);
        return lines.Count > 0 ? lines : new List<string> { "" };
    }

    /// <summary>
    /// 在透明图层上画字再按 straight-alpha 覆盖到气泡上：
    /// 只改文字像素，避免把整张气泡送进 Skia 的预乘/反预乘往返而劣化边缘。
    /// </summary>
    private static void BalloonDrawText(Bitmap32 canvas, IReadOnlyList<string> lines,
        double textX, double textY0, double lineH, SKFont font, SKColor color)
    {
        var info = new SKImageInfo(canvas.W, canvas.H, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var layer = new SKBitmap(info);
        using var skCanvas = new SKCanvas(layer);
        skCanvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = color, IsAntialias = true };

        // PIL 的 text() 默认锚点是左上角（ascender），Skia 的 DrawText 收基线：
        // 基线 = 左上角 + ascent，这样两边落在同一行像素上。
        var ascent = -font.Metrics.Ascent;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var tw = font.MeasureText(line);
            skCanvas.DrawText(line,
                (float)(textX - tw / 2.0),
                (float)(textY0 + i * lineH + ascent),
                SKTextAlign.Left, font, paint);
        }
        skCanvas.Flush();

        var px = new byte[canvas.W * canvas.H * 4];
        var stride = layer.RowBytes;
        var src = layer.GetPixels();
        for (var y = 0; y < canvas.H; y++)
        {
            Marshal.Copy(IntPtr.Add(src, y * stride), px, y * canvas.W * 4, canvas.W * 4);
        }
        Sheet.Blit(canvas, new Bitmap32(canvas.W, canvas.H, px), 0, 0, replace: false);
    }

    // ── 参数 ────────────────────────────────────────────────────────────

    private static string BalloonTextArg(JsonObject args)
    {
        var node = args["text"];
        if (node is null) return "你好，冒险岛！";
        var text = node.ToString();
        return text.Length == 0 ? "你好，冒险岛！" : text;
    }

    /// <summary>style 允许字符串或数字；数字 0 与 旧的 `0 or ""` 一样按未指定处理。</summary>
    private static string BalloonStyleArg(JsonObject args)
    {
        var node = args["style"];
        if (node is null) return "";
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s ?? "";
            if (value.TryGetValue<int>(out var i)) return i == 0 ? "" : i.ToString();
            if (value.TryGetValue<double>(out var d)) return d == 0 ? "" : ((long)d).ToString();
        }
        return node.ToJsonString();
    }

    private static int BalloonFontSize(JsonObject args)
    {
        var node = args["fontSize"];
        if (node is null) return 12;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i)) return i == 0 ? 12 : i;
            if (value.TryGetValue<double>(out var d))
            {
                var n = (int)d;
                return n == 0 ? 12 : n;
            }
            if (value.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p == 0 ? 12 : p;
        }
        return 12;
    }
}
