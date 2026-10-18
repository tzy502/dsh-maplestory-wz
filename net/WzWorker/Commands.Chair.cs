// 椅子（Item/Install）与 ItemEff 装备特效的读取与合成。
//
// 为什么单独成文件：两者都不是 Character 部件，数据在别的包里、图层语义也和纸娃娃不同；
// 但「按 z 分前后 + 画布取全部帧并集」的合成规则一致，所以放一起给纸娃娃复用。
using System.Globalization;
using System.Text.Json.Nodes;
using WzComparerR2.WzLib;

namespace WzWorker;

internal static partial class Program
{
    internal static JsonNode? TryDispatchChair(string cmd, JsonObject args) => cmd switch
    {
        "chair_list" => CmdChairList(args),
        "chair_render" => CmdChairRender(args),
        _ => null,
    };

    /// <summary>
    /// dispatch 还没接线时的直调入口：验收脚本用反射调用它，走的是与正式命令完全同一份实现。
    /// 自己补环境初始化，是因为 Main 里那段只在进程入口执行，反射进来时字段还是空的。
    /// 名字带 Chair 是因为同项目其他命令组已经占用了 SelfTest，partial 类里不能重名。
    /// </summary>
    internal static JsonNode? SelfTestChair(string cmd, JsonObject args)
    {
        EnsureDataDir();
        return TryDispatchChair(cmd, args);
    }

    /// <summary>
    /// Main 只在自己进程入口里读环境变量；纸娃娃等内部调用方直接进来时这里补一次，
    /// 免得「能不能用」取决于调用路径。
    /// </summary>
    private static void EnsureDataDir()
    {
        if (!string.IsNullOrWhiteSpace(_dataDir)) return;
        _dataDir = Environment.GetEnvironmentVariable("WZ_DATA_DIR") ?? "";
        _region = Environment.GetEnvironmentVariable("WZ_REGION") ?? "BMS";
        _version = int.TryParse(Environment.GetEnvironmentVariable("WZ_VERSION"), out var v) ? v : 228;
    }

    // ── 数据结构（字段语义与 旧实现 chair_support 一致）──────────────────

    /// <summary>特效层里的一帧：画布 + origin（相对锚点的像素）+ 帧时长。</summary>
    internal sealed record PieceFrame(string Name, Bitmap32 Img, int Ox, int Oy, int Delay);

    /// <summary>一层特效：椅子 effect/effect2/effect3，或 ItemEff 的 effect/default。</summary>
    internal sealed record PieceLayer(string Name, int Z, int Pos, List<PieceFrame> Frames);

    /// <summary>椅子 info（未标注的字段保留 null，别用 0 冒充「没写」）。</summary>
    internal sealed record ChairInfo(
        Wz_Vector? BodyRelMove,
        string? SitAction,
        bool RemoveBody,
        int? TamingMob,
        int? SitEmotion,
        bool InvisibleWeapon,
        bool InvisibleCape,
        bool RemoveEffect);

    /// <summary>一把椅子的全部特效层。</summary>
    internal sealed record ChairData(Wz_Node Node, ChairInfo Info, List<PieceLayer> Layers);

    /// <summary>单帧落位（对应 旧实现 placements[i] 的 (z, img, tx, ty)）。</summary>
    internal readonly record struct ChairPlacement(int Z, Bitmap32 Img, int Tx, int Ty);

    // ── 椅子路径与偏移 ──────────────────────────────────────────────────

    // 分卷号探测顺序照 minipet 的 ChairRoot：现网 05 位，旧版 06/04 位
    private static readonly (int Div, int Width)[] ChairGroupings = { (1000, 5), (100, 6), (10000, 4) };

    /// <summary>椅子在 Item/Install 下可能的分卷路径（旧实现 chair_paths 等价）。</summary>
    internal static List<string> ChairPaths(string chairId)
    {
        var n = ParseChairNumber(chairId);
        var paths = new List<string>(ChairGroupings.Length);
        foreach (var (div, width) in ChairGroupings)
        {
            paths.Add($"Install/{(n / div).ToString("D" + width, CultureInfo.InvariantCulture)}.img");
        }
        return paths;
    }

    private static long ParseChairNumber(string chairId)
    {
        var digits = (chairId ?? "").Trim().TrimStart('0');
        if (digits.Length == 0) return 0;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new InvalidOperationException($"椅子编号不是数字：{chairId}");
    }

    /// <summary>
    /// 椅子特效相对角色的平移（照抄 minipet PaperdollService.ComputeChairEffectOffset）。
    /// 带 bodyRelMove 且是坐姿时，锚点 = navel − bodyRelMove；非坐姿的 pos==1 才用固定上移。
    /// </summary>
    internal static (int X, int Y) ComputeChairEffectOffset(
        bool hasBodyRelMove, int bmX, int bmY, bool hasSitAction, int pos,
        bool removeBody = false, bool hasTamingMob = false, long idNum = 0)
    {
        var offX = 0;
        var offY = 0;
        if (hasBodyRelMove)
        {
            if (!hasSitAction && pos < 2)
            {
                if (pos == 1) offY = -50;
            }
            else
            {
                offX = -bmX;
                offY = -bmY;
            }
        }
        else if (pos == 1)
        {
            offY = hasTamingMob && idNum / 1000 < 3011 ? -30 : -50;
        }
        if (removeBody) offY += 30;
        return (offX, offY);
    }

    // ── chair_list ──────────────────────────────────────────────────────

    /// <summary>椅子清单：扫 Item/Install 的 03000..03049 分卷，名字取 String/Ins.img。</summary>
    private static JsonNode CmdChairList(JsonObject args)
    {
        var names = ChairNameMap();
        var root = FindTop("Item") ?? throw new FileNotFoundException("没有 Item 分类（数据目录不完整）");
        var rows = new List<(string Id, string Name)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 按 旧实现枚举 gi 而不是直接列目录：路径集合不同会让数量/顺序对不上验收基准
        for (var gi = 3000; gi < 3050; gi++)
        {
            foreach (var rel in new[] { $"Install/{gi:D5}.img", $"Install/{gi / 10:D4}.img", $"Install/{gi * 10:D6}.img" })
            {
                var body = TryImageBody(root, rel);
                if (body is null) continue;
                foreach (var child in body.Nodes)
                {
                    var key = child.Text;
                    var bare = key.TrimStart('0');
                    if (!bare.StartsWith("301", StringComparison.Ordinal) || !seen.Add(key)) continue;
                    rows.Add((key, LookupChairName(names, key)));
                }
            }
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var query = (args["query"]?.GetValue<string>() ?? "").Trim();
        if (query.Length > 0)
        {
            rows = rows.Where(r => r.Name.Contains(query, StringComparison.Ordinal) || r.Id.Contains(query, StringComparison.Ordinal)).ToList();
        }
        var limit = args["limit"]?.GetValue<int>() ?? 200;
        if (limit <= 0) limit = 200;

        var arr = new JsonArray();
        foreach (var r in rows.Take(limit)) arr.Add(new JsonObject { ["id"] = r.Id, ["name"] = r.Name });
        return new JsonObject { ["chairs"] = arr, ["total"] = rows.Count };
    }

    /// <summary>String/Ins.img 的名字表；条目是子属性，名字在 name 子节点里（不是字符串值）。</summary>
    private static Dictionary<string, string> ChairNameMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, entry) in StringTable("Ins.img"))
        {
            var name = entry.Value as string;
            if (string.IsNullOrEmpty(name)) name = NodeName(entry);
            if (!string.IsNullOrEmpty(name)) map[key] = name;
        }
        return map;
    }

    private static string LookupChairName(Dictionary<string, string> names, string id)
    {
        if (names.TryGetValue(id, out var name) && name.Length > 0) return name;
        if (names.TryGetValue(id.TrimStart('0'), out var bare) && bare.Length > 0) return bare;
        return "（无名字）";
    }

    private static Wz_Node? TryImageBody(Wz_Node root, string rel)
    {
        try
        {
            var node = root.FindNodeByPath(rel.Replace('/', '\\'), true);
            if (node is null) return null;
            return node.Value is Wz_Image ? ExpandImage(node, rel) : node;
        }
        catch
        {
            return null;
        }
    }

    // ── chair_render ────────────────────────────────────────────────────

    private static JsonNode CmdChairRender(JsonObject args)
    {
        var chairId = (args["chairId"]?.GetValue<string>()
                    ?? args["chair"]?.GetValue<string>()
                    ?? args["id"]?.GetValue<string>() ?? "").Trim();
        if (chairId.Length == 0) throw new InvalidOperationException("chair_render 需要 chairId（如 03019632）");

        var chair = RenderChairPieces(chairId)
            ?? throw new FileNotFoundException($"椅子 {chairId} 不存在：Item/Install 下没找到该编号");
        var hasSitAction = args["sitAction"]?.GetValue<bool>() ?? true;
        var charW = Math.Max(1, args["charW"]?.GetValue<int>() ?? 1);
        var charH = Math.Max(1, args["charH"]?.GetValue<int>() ?? 1);
        var navel = (X: 0, Y: 0);
        if (args["navel"] is JsonArray nav && nav.Count >= 2)
        {
            navel = (nav[0]!.GetValue<int>(), nav[1]!.GetValue<int>());
        }

        var charImg = new Bitmap32(charW, charH);
        var (frames, delays) = ComposeChairAnimation(charImg, navel, chair, chairId, hasSitAction);
        if (frames.Count == 0) throw new InvalidOperationException($"椅子 {chairId} 没有可渲染的帧");
        var w = frames[0].W;
        var h = frames[0].H;

        // 帧与帧之间画布恒定（并集），所以雪碧图直接等宽平铺，不需要再算锚点
        const int pad = 2;
        var sheet = new Bitmap32(frames.Count * (w + pad) + pad, h + pad * 2);
        var rects = new JsonArray();
        for (var i = 0; i < frames.Count; i++)
        {
            var x = pad + i * (w + pad);
            Sheet.Blit(sheet, frames[i], x, pad, replace: true);
            rects.Add(new JsonObject
            {
                ["index"] = i,
                ["x"] = x,
                ["y"] = pad,
                ["w"] = w,
                ["h"] = h,
                ["delay"] = delays[i],
            });
        }

        var outDir = args["out"]?.GetValue<string>()
                  ?? Environment.GetEnvironmentVariable("WZ_OUT_DIR")
                  ?? Path.GetTempPath();
        Directory.CreateDirectory(outDir);
        var stem = $"chair_{chairId}_{w}x{h}_{frames.Count}";
        var png = Path.Combine(outDir, $"sheet_{stem}.png");
        File.WriteAllBytes(png, Png.Encode(sheet));

        var data = new JsonObject
        {
            ["id"] = chairId,
            ["name"] = LookupChairName(ChairNameMap(), chairId),
            ["w"] = w,
            ["h"] = h,
            ["count"] = frames.Count,
            ["frames"] = rects,
            ["delays"] = IntArray(delays),
            ["sheet"] = new JsonObject { ["w"] = sheet.W, ["h"] = sheet.H },
            ["png"] = png,
            ["info"] = ChairInfoJson(chair.Info),
            ["layers"] = LayersJson(chair.Layers),
            ["paths"] = StringArray(ChairPaths(chairId).Select(p => "Item/" + p)),
            ["navel"] = new JsonArray(navel.X, navel.Y),
            ["engine"] = "wzlib",
        };

        var onlyFrame = args["frame"]?.GetValue<int>();
        if (onlyFrame.HasValue)
        {
            var idx = Math.Clamp(onlyFrame.Value, 0, frames.Count - 1);
            var single = Path.Combine(outDir, $"frame_{stem}_{idx}.png");
            File.WriteAllBytes(single, Png.Encode(frames[idx]));
            data["frame"] = idx;
            data["framePng"] = single;
        }

        if (args["gif"]?.GetValue<bool>() == true)
        {
            var gifFrames = frames.Select((img, i) => new Frame($"chair_{i}", img, 0, 0, delays[i])).ToList();
            var gif = Path.Combine(outDir, $"anim_{stem}.gif");
            var (gw, gh, gf) = Sheet.WriteGif(gifFrames, gif, 1, args["max_side"]?.GetValue<int>() ?? 480);
            data["gif"] = gif;
            data["gif_w"] = gw;
            data["gif_h"] = gh;
            data["gif_frames"] = gf;
        }
        return data;
    }

    // ── 读取椅子 ────────────────────────────────────────────────────────

    /// <summary>取一把椅子的全部特效层；不存在返回 null（旧实现 render_chair_pieces 等价）。</summary>
    internal static ChairData? RenderChairPieces(string chairId)
    {
        EnsureDataDir();
        var node = FindChairNode(chairId);
        if (node is null) return null;

        var info = ReadChairInfo(node.FindNodeByPath("info", true));
        var layers = new List<PieceLayer>();
        foreach (var name in new[] { "effect", "effect2", "effect3" })
        {
            var effectNode = node.FindNodeByPath(name, true);
            if (effectNode is null) continue;
            var z = effectNode.FindNodeByPath("z", true)?.GetValueEx<int>(0) ?? 0;
            var pos = effectNode.FindNodeByPath("pos", true)?.GetValueEx<int>(0) ?? 0;
            var frames = new List<PieceFrame>();
            foreach (var child in effectNode.Nodes)
            {
                if (!int.TryParse(child.Text, out _)) continue;
                var frame = ReadPieceFrame(child, 210);
                if (frame is not null) frames.Add(frame);
            }
            // 空层不参与摆放，否则 max(count) 会被一个解不出的层抬高
            if (frames.Count > 0) layers.Add(new PieceLayer(name, z, pos, frames));
        }
        return new ChairData(node, info, layers);
    }

    private static Wz_Node? FindChairNode(string chairId)
    {
        // 键名补零/不补零两种都有（现网 03019.img 键是补零的 03019632），只试一种会漏
        var keys = ChairNodeKeys(chairId);
        var root = FindTop("Item");
        if (root is null) return null;
        foreach (var rel in ChairPaths(chairId))
        {
            var body = TryImageBody(root, rel);
            if (body is null) continue;
            foreach (var key in keys)
            {
                var node = body.Nodes.FirstOrDefault(n => n.Text == key);
                if (node is not null) return node;
            }
        }
        return null;
    }

    private static List<string> ChairNodeKeys(string chairId)
    {
        var key = (chairId ?? "").Trim();
        var keys = new List<string> { key, key.PadLeft(8, '0') };
        var bare = key.TrimStart('0');
        if (bare.Length > 0)
        {
            keys.Add(bare);
            keys.Add(ParseChairNumber(key).ToString(CultureInfo.InvariantCulture));
        }
        return keys.Distinct().ToList();
    }

    private static ChairInfo ReadChairInfo(Wz_Node? info)
    {
        var bodyRelMove = info?.FindNodeByPath("bodyRelMove", true)?.GetValueEx<Wz_Vector>(null);
        int Ivi(string key) => InfoInt(info, key) ?? 0;
        return new ChairInfo(
            bodyRelMove,
            info?.FindNodeByPath("sitAction", true)?.Value as string,
            Ivi("removeBody") != 0,
            InfoInt(info, "tamingMob"),
            InfoInt(info, "sitEmotion"),
            Ivi("invisibleWeapon") != 0,
            Ivi("invisibleCape") != 0,
            Ivi("removeEffect") != 0);
    }

    private static int? InfoInt(Wz_Node? info, string key)
    {
        var node = info?.FindNodeByPath(key, true);
        if (node?.Value is null) return null;
        return node.GetValueEx<int>(0);
    }

    private static PieceFrame? ReadPieceFrame(Wz_Node frame, int defaultDelay)
    {
        var canvas = ResolveCanvasChain(frame);
        if (canvas is null) return null;
        var png = canvas.GetValue<Wz_Png>() ?? canvas.Value as Wz_Png;
        if (png is null) return null;

        var bmp = Png.Decode(png);
        // origin 优先取真实画布（占位帧自己不写），缺省 0 与 旧实现一致
        var origin = canvas.FindNodeByPath("origin", true)?.GetValueEx<Wz_Vector>(null)
                  ?? frame.FindNodeByPath("origin", true)?.GetValueEx<Wz_Vector>(null);
        var delay = frame.FindNodeByPath("delay", true)?.GetValueEx<int>(0) ?? 0;
        if (delay == 0) delay = defaultDelay;
        return new PieceFrame(frame.Text, bmp, origin?.X ?? 0, origin?.Y ?? 0, delay);
    }

    /// <summary>
    /// 帧节点常常是 1×1 占位 + `_outlink`，真像素在同包的 `_Canvas/` 仓库里；
    /// 最多跟 8 跳（与 旧实现 _canvas_meta 一致），跟不到就当这个部件缺失。
    /// </summary>
    private static Wz_Node? ResolveCanvasChain(Wz_Node node)
    {
        var cur = node;
        for (var i = 0; i < 8; i++)
        {
            var png = cur.GetValue<Wz_Png>() ?? cur.Value as Wz_Png;
            if (png is not null && (png.Width > 1 || png.Height > 1)) return cur;
            var next = ResolveCanvas(cur);
            if (ReferenceEquals(next, cur)) return null;
            cur = next;
        }
        return null;
    }

    // ── 摆放与合成 ──────────────────────────────────────────────────────

    /// <summary>算每一帧的落点：navel + bodyRelMove 修正 − 该帧 origin（旧实现 chair_placements 等价）。</summary>
    internal static (List<List<ChairPlacement>> Placements, int Count) ChairPlacements(
        ChairData chair, string chairId, (int X, int Y) navel, bool hasSitAction = true)
    {
        var bm = chair.Info.BodyRelMove;
        var hasBodyRelMove = bm is not null;
        var bmX = bm?.X ?? 0;
        var bmY = bm?.Y ?? 0;
        var idNum = ParseChairNumber(chairId);
        var hasTamingMob = (chair.Info.TamingMob ?? 0) != 0;
        var count = chair.Layers.Count == 0 ? 1 : chair.Layers.Max(l => l.Frames.Count);

        var placements = new List<List<ChairPlacement>>(count);
        for (var i = 0; i < count; i++)
        {
            var row = new List<ChairPlacement>();
            foreach (var layer in chair.Layers)
            {
                var (offX, offY) = ComputeChairEffectOffset(
                    hasBodyRelMove, bmX, bmY, hasSitAction, layer.Pos,
                    chair.Info.RemoveBody, hasTamingMob, idNum);
                // 层帧数少于总帧数时重复最后一帧（effect2 只有 1 帧）
                var frame = layer.Frames[Math.Min(i, layer.Frames.Count - 1)];
                row.Add(new ChairPlacement(
                    layer.Z, frame.Img,
                    navel.X + offX - frame.Ox,
                    navel.Y + offY - frame.Oy));
            }
            placements.Add(row);
        }
        return (placements, count);
    }

    /// <summary>角色图 + 这些层的并集（坐标相对角色图左上）。</summary>
    private static (int X0, int Y0, int X1, int Y1) ChairBBox(Bitmap32 charImg, List<ChairPlacement> layers)
    {
        var minX = 0;
        var minY = 0;
        var maxX = charImg.W;
        var maxY = charImg.H;
        foreach (var p in layers)
        {
            minX = Math.Min(minX, p.Tx);
            minY = Math.Min(minY, p.Ty);
            maxX = Math.Max(maxX, p.Tx + p.Img.W);
            maxY = Math.Max(maxY, p.Ty + p.Img.H);
        }
        return (minX, minY, maxX, maxY);
    }

    /// <summary>按给定 box 渲染一帧（层级照 minipet：z 大的先画）。</summary>
    internal static Bitmap32 RenderChairFrame(
        Bitmap32 charImg, IReadOnlyList<ChairPlacement> layers, (int X0, int Y0, int X1, int Y1) box)
    {
        var canvas = new Bitmap32(box.X1 - box.X0, box.Y1 - box.Y0);
        Sheet.Blit(canvas, charImg, -box.X0, -box.Y0, replace: false);
        foreach (var p in layers.OrderByDescending(l => l.Z))
        {
            CompositeLayer(canvas, p.Img, p.Tx - box.X0, p.Ty - box.Y0, p.Z);
        }
        return canvas;
    }

    /// <summary>
    /// 叠一层：z&gt;0 盖在现有内容前面，z&lt;=0 垫到后面。
    /// 椅子与 ItemEff 共用这套语义，纸娃娃直接复用即可。
    /// </summary>
    internal static void CompositeLayer(Bitmap32 canvas, Bitmap32 layer, int x, int y, int z)
    {
        if (z > 0)
        {
            Sheet.Blit(canvas, layer, x, y, replace: false);
            return;
        }
        // 「垫到后面」需要一层全透明底再叠，最后整块写回，避免改动调用方持有的尺寸
        var behind = new Bitmap32(canvas.W, canvas.H);
        Sheet.Blit(behind, layer, x, y, replace: false);
        Sheet.Blit(behind, canvas, 0, 0, replace: false);
        Buffer.BlockCopy(behind.Px, 0, canvas.Px, 0, canvas.Px.Length);
    }

    /// <summary>
    /// 逐帧合成成尺寸一致的帧（旧实现 compose_chair_animation 等价）。
    /// 画布必须取**全部帧**的并集：逐帧各算各的并集会让格子忽大忽小（3019632 实测抖动），
    /// 每帧内容再按自己的并集对齐后垫进统一画布，角色与椅子才不会逐帧跳。
    /// </summary>
    internal static (List<Bitmap32> Frames, List<int> Delays) ComposeChairAnimation(
        Bitmap32 charImg, (int X, int Y) navel, ChairData chair, string chairId, bool hasSitAction = true)
    {
        var (placements, count) = ChairPlacements(chair, chairId, navel, hasSitAction);
        if (count <= 0) return (new List<Bitmap32> { charImg }, new List<int>());

        var boxes = placements.Select(ls => ChairBBox(charImg, ls)).ToList();
        var ux0 = boxes.Min(b => b.X0);
        var uy0 = boxes.Min(b => b.Y0);
        var ux1 = boxes.Max(b => b.X1);
        var uy1 = boxes.Max(b => b.Y1);

        var frames = new List<Bitmap32>(count);
        for (var i = 0; i < count; i++)
        {
            var tile = RenderChairFrame(charImg, placements[i], boxes[i]);
            var canvas = new Bitmap32(ux1 - ux0, uy1 - uy0);
            Sheet.Blit(canvas, tile, boxes[i].X0 - ux0, boxes[i].Y0 - uy0, replace: false);
            frames.Add(canvas);
        }

        // 时长跟椅子的第一层走；层帧数不够的帧沿用 210（与 旧实现同一兜底）
        var baseFrames = chair.Layers.Count > 0 ? chair.Layers[0].Frames : new List<PieceFrame>();
        var delays = new List<int>(count);
        for (var i = 0; i < count; i++) delays.Add(i < baseFrames.Count ? baseFrames[i].Delay : 210);
        return (frames, delays);
    }

    /// <summary>单帧版本（面板静态预览用；多帧走 ComposeChairAnimation）。</summary>
    internal static Bitmap32 ComposeWithChair(
        Bitmap32 charImg, (int X, int Y) navel, ChairData chair, string chairId,
        int frameIndex = 0, bool hasSitAction = true)
    {
        var (frames, _) = ComposeChairAnimation(charImg, navel, chair, chairId, hasSitAction);
        if (frames.Count == 0) return charImg;
        return frames[Math.Clamp(frameIndex, 0, frames.Count - 1)];
    }

    // ── ItemEff 装备特效（effect_support 等价，供纸娃娃复用）────────────

    /// <summary>
    /// ItemEff 特效查找：Effect/ItemEff.img/{去前导零的装备编号}/effect。
    /// 去前导零是官方约定（Character 的 01004759 在 ItemEff.img 里写作 1004759），
    /// 与 effect_support.LocalEffectLookup.find 等价；补零写法做兜底（minipet 两种都试）。
    /// </summary>
    internal static Wz_Node? ItemEffEffectNode(string equipId)
    {
        EnsureDataDir();
        var raw = (equipId ?? "").Trim();
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var idNum)) return null;
        var root = FindTop("Effect");
        if (root is null) return null;
        var img = root.FindNodeByPath("ItemEff.img", true);
        if (img is null) return null;
        var body = img.Value is Wz_Image ? ExpandImage(img, "Effect/ItemEff.img") : img;

        var key = idNum.ToString(CultureInfo.InvariantCulture);
        var node = body.Nodes.FirstOrDefault(n => n.Text == key)
                ?? body.Nodes.FirstOrDefault(n => n.Text == raw.PadLeft(8, '0'));
        return node?.FindNodeByPath("effect", true);
    }

    /// <summary>
    /// ItemEff 的逐帧特效层：帧结构与椅子层一致，可直接走同一合成路径。
    /// 一次取全帧给 timeline 用；只画某一帧请用 ItemEffEffectFrame，避免每帧都解全部画布。
    /// 锚点对齐 MapleSalon2：部件锚到 brow，再按 map {brow:{-10,-50}} 反向偏移（见 ItemEffAnchorOffset）。
    /// </summary>
    internal static PieceLayer? ItemEffEffectLayer(string equipId)
    {
        var effect = ItemEffEffectNode(equipId);
        if (effect is null) return null;
        var def = effect.FindNodeByPath("default", true);
        if (def is null) return null;

        // 编号就是帧号（minipet 用 default/{frame} 按名字取），排序后再用下标才不会错位
        var ordered = def.Nodes.Where(n => int.TryParse(n.Text, out _))
            .OrderBy(n => int.Parse(n.Text, CultureInfo.InvariantCulture)).ToList();
        var frames = new List<PieceFrame>();
        foreach (var child in ordered)
        {
            var frame = ReadPieceFrame(child, 0);
            if (frame is not null) frames.Add(frame);
        }
        if (frames.Count == 0) return null;
        return new PieceLayer("effect", ItemEffZ(effect, def), 0, frames);
    }

    /// <summary>minipet AddItemEffEffectPieces 等价：取指定帧（帧号按特效帧数取模循环）。</summary>
    internal static PieceFrame? ItemEffEffectFrame(string equipId, int frame)
    {
        var effect = ItemEffEffectNode(equipId);
        if (effect is null) return null;
        var def = effect.FindNodeByPath("default", true);
        if (def is null) return null;

        var numeric = def.Nodes.Where(n => int.TryParse(n.Text, out _)).ToList();
        if (numeric.Count == 0) return null;
        var idx = ((frame % numeric.Count) + numeric.Count) % numeric.Count;
        // 先按帧号名字取（minipet 的路径语义），名字不连续时再退回下标
        var target = def.Nodes.FirstOrDefault(n => n.Text == idx.ToString(CultureInfo.InvariantCulture)) ?? numeric[idx];
        return ReadPieceFrame(target, 0);
    }

    private static int ItemEffZ(Wz_Node effect, Wz_Node def)
    {
        // minipet 用 effect/default 的 z（1102149 冰凌披风实测 -2），没有才看 effect 自己
        return def.FindNodeByPath("z", true)?.GetValueEx<int>(0)
            ?? effect.FindNodeByPath("z", true)?.GetValueEx<int>(0)
            ?? 0;
    }

    /// <summary>
    /// ItemEff 部件的锚点偏移：MapleSalon2 resolveEffectFrames 的 {brow:{-10,-50}} 取负，
    /// 即最终锚点 = brow + (10, 50)；brow 不在锚表时纸娃娃回退 navel。
    /// </summary>
    internal static (int X, int Y) ItemEffAnchorOffset => (10, 50);

    // ── JSON ────────────────────────────────────────────────────────────

    private static JsonObject ChairInfoJson(ChairInfo info) => new()
    {
        ["bodyRelMove"] = info.BodyRelMove is null
            ? null
            : new JsonArray(info.BodyRelMove.X, info.BodyRelMove.Y),
        ["sitAction"] = info.SitAction,
        ["removeBody"] = info.RemoveBody,
        ["tamingMob"] = NullableInt(info.TamingMob),
        ["sitEmotion"] = NullableInt(info.SitEmotion),
        ["invisibleWeapon"] = info.InvisibleWeapon,
        ["invisibleCape"] = info.InvisibleCape,
        ["removeEffect"] = info.RemoveEffect,
    };

    private static JsonArray LayersJson(List<PieceLayer> layers)
    {
        var arr = new JsonArray();
        foreach (var layer in layers)
        {
            var frames = new JsonArray();
            foreach (var f in layer.Frames)
            {
                frames.Add(new JsonObject
                {
                    ["name"] = f.Name,
                    ["w"] = f.Img.W,
                    ["h"] = f.Img.H,
                    ["origin"] = new JsonArray(f.Ox, f.Oy),
                    ["delay"] = f.Delay,
                });
            }
            arr.Add(new JsonObject
            {
                ["name"] = layer.Name,
                ["z"] = layer.Z,
                ["pos"] = layer.Pos,
                ["frames"] = frames,
            });
        }
        return arr;
    }

    private static JsonNode? NullableInt(int? value) => value is null ? null : JsonValue.Create(value.Value);

    private static JsonArray IntArray(IEnumerable<int> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }
}
