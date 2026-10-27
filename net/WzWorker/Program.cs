// 冒险岛 WZ worker（.NET 半边）—— 用 WzComparerR2 的 WzLib 读数据。
//
// 协议与 旧实现 逐字兼容：stdin 一行一个请求，stdout 一行一个响应。
//   请求  {"id":<int>, "cmd":"packs", "args":{...}}
//   响应  {"id":<int>, "ok":true, "data":{...}}
//         {"id":<int>, "ok":false, "error":{"message":"...","type":"..."}}
//
// 为什么单独一个进程：宿主是 Node，WzLib 是 .NET；用 stdio 传 JSON 比做原生扩展简单，
// 也方便崩溃隔离（一个坏文件不会拖垮宿主）。
using System.Text.Json;
using System.Text.Json.Nodes;
using WzComparerR2.WzLib;

namespace WzWorker;

internal static partial class Program
{
    private static string _dataDir = "";
    private static string _region = "BMS";
    private static int _version = 228;
    private static Wz_Structure? _wzs;
    private static readonly object Gate = new();

    private static int Main()
    {
        _dataDir = Environment.GetEnvironmentVariable("WZ_DATA_DIR") ?? "";
        _region = Environment.GetEnvironmentVariable("WZ_REGION") ?? "BMS";
        _version = int.TryParse(Environment.GetEnvironmentVariable("WZ_VERSION"), out var v) ? v : 228;

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            JsonObject resp;
            int id = 0;
            try
            {
                var req = JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException("请求不是 JSON 对象");
                id = req["id"]?.GetValue<int>() ?? 0;
                var cmd = req["cmd"]?.GetValue<string>() ?? "";
                var args = req["args"] as JsonObject ?? new JsonObject();
                var data = Dispatch(cmd, args);
                resp = new JsonObject { ["id"] = id, ["ok"] = true, ["data"] = data };
            }
            catch (Exception ex)
            {
                resp = new JsonObject
                {
                    ["id"] = id,
                    ["ok"] = false,
                    ["error"] = new JsonObject { ["message"] = ex.Message, ["type"] = ex.GetType().Name },
                };
            }
            Console.Out.WriteLine(resp.ToJsonString());
            Console.Out.Flush();
        }
        return 0;
    }

    private static JsonNode? Dispatch(string cmd, JsonObject args) => cmd switch
    {
        "packs" => CmdPacks(),
        "tree" => CmdTree(args),
        "search" => CmdSearch(args),
        "get" => CmdGet(args),
        "msPacks" => CmdMsPacks(),
        "ms_mob" => CmdMsMob(args),
        "ms_stats" => CmdMsStats(),
        "image" => CmdImage(args),
        "frames" => CmdFrames(args),
        "sprite" => CmdSprite(args),
        "ping" => new JsonObject { ["engine"] = "wzlib", ["data_dir"] = _dataDir },
        // 各组命令在 Commands.*.cs 里；命中不了再抛
        _ => TryOther(cmd, args) ?? throw new NotSupportedException($"wzlib worker 暂不支持命令：{cmd}"),
    };

    /// <summary>
    /// 各组命令的汇总入口。每接一组就在这里加一行；
    /// 返回 null 表示这组不认识该命令，继续试下一组。
    /// </summary>
    private static JsonNode? TryOther(string cmd, JsonObject args) =>
        TryDispatchDetail(cmd, args) ??
        TryDispatchChar(cmd, args) ??
        TryDispatchChair(cmd, args) ??
        TryDispatchBalloon(cmd, args) ??
        TryDispatchMap(cmd, args) ??
        TryDispatchPet(cmd, args) ??
        TryDispatchCmsdl(cmd, args);

    // ── 载入 ────────────────────────────────────────────────────────────
    private static Wz_Structure Load()
    {
        if (_wzs is not null) return _wzs;
        lock (Gate)
        {
            if (_wzs is not null) return _wzs;
            if (string.IsNullOrWhiteSpace(_dataDir))
            {
                throw new InvalidOperationException("没有配置数据目录（dataDir）：请在插件设置或 profile 的 cordis.patch.yml 里指定冒险岛客户端的 Data 目录");
            }
            if (!Directory.Exists(_dataDir))
            {
                throw new DirectoryNotFoundException($"数据目录不存在：{_dataDir}");
            }

            Wz_Structure.DefaultAutoDetectExtFiles = true;
            Wz_Structure.DefaultImgCheckDisabled = true;

            // 入口取 Base 子目录：这份客户端是「分类目录 + 各自 .wz」的拆分结构，
            // 根目录没有 Data.wz，直接传根目录 WzLib 会找不到文件。
            var folder = Path.Combine(_dataDir, "Base");
            if (!File.Exists(Path.Combine(folder, "Base.wz"))) folder = _dataDir;

            var wzs = new Wz_Structure();
            wzs.LoadWzFolder(folder, ref wzs.WzNode, true);

            // Packs/*.ms、*.mn：新版客户端的结构容器（动作/origin/delay）。
            // WzLib 原生支持，必须一起载入，否则 .ms 客户端读不到动作数据。
            var packsDir = Path.Combine(_dataDir, "Packs");
            if (Directory.Exists(packsDir))
            {
                foreach (var f in Directory.GetFiles(packsDir, "*.ms").Concat(Directory.GetFiles(packsDir, "*.mn")))
                {
                    try { wzs.LoadMsFile(f); }
                    catch (Exception ex) { Console.Error.WriteLine($"[wzlib] {Path.GetFileName(f)} 载入失败: {ex.Message}"); }
                }
            }

            _wzs = wzs;
            return wzs;
        }
    }

    private static Wz_Node? FindTop(string pack) => Load().WzNode?.Nodes.FirstOrDefault(n => n.Text == pack);

    // ── 命令 ────────────────────────────────────────────────────────────

    /// <summary>分类与分卷清单（纯文件系统，不需要载入 WZ）。</summary>
    private static JsonNode CmdPacks()
    {
        var packs = new JsonArray();
        if (Directory.Exists(_dataDir))
        {
            foreach (var dir in Directory.GetDirectories(_dataDir).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;
                var vols = new JsonArray();
                foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var bn = Path.GetFileName(f);
                    if (!bn.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)) continue;
                    vols.Add(new JsonObject { ["name"] = bn, ["mb"] = Math.Round(new FileInfo(f).Length / 1e6, 1) });
                }
                if (vols.Count > 0) packs.Add(new JsonObject { ["pack"] = name, ["volumes"] = vols });
            }
        }
        return new JsonObject
        {
            ["data_dir"] = _dataDir,
            ["region"] = _region,
            ["version"] = _version,
            ["engine"] = "wzlib",
            ["packs"] = packs,
        };
    }

    /// <summary>目录树：包内路径逐级展开。</summary>
    private static JsonNode CmdTree(JsonObject args)
    {
        var pack = (args["pack"]?.GetValue<string>() ?? "").Trim();
        if (pack.Length == 0)
        {
            var rows = new JsonArray();
            if (Directory.Exists(_dataDir))
            {
                foreach (var dir in Directory.GetDirectories(_dataDir).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith('.')) continue;
                    rows.Add(new JsonObject { ["name"] = name, ["kind"] = "dir" });
                }
            }
            return new JsonObject
            {
                ["pack"] = "", ["path"] = "", ["kind"] = "root", ["children"] = rows,
                ["data_dir"] = _dataDir, ["note"] = "数据源根目录：挑一个分类进入",
            };
        }

        // 根目录列的是**文件系统目录**，而展开是按 WZ 树找同名节点 —— 两者并不等价：
        //   · Base 是加载入口（它的内容就是树的顶层），树里没有名为 Base 的节点；
        //   · Packs 放的是 .ms 结构容器，也不是 WZ 分类。
        // 直接抛「没有这个分类」会让用户从根目录一路点进去时断在第二个文件夹上，
        // 所以这里回落到文件系统列举，保持「从根目录探索」的连贯。
        var root = FindTop(pack);
        if (root is null)
        {
            return CmdTreeFs(pack, args);
        }
        var path = (args["path"]?.GetValue<string>() ?? "").Trim('/');
        var node = path.Length == 0 ? root : root.FindNodeByPath(path.Replace('/', '\\'), true);
        if (node is null) throw new FileNotFoundException($"{pack}/{path} 不存在");

        // 图片节点（.img）：WzLib 视情况给 Wz_Image 或已展开的普通节点，两种都要认。
        Wz_Node current = ExpandImage(node, $"{pack}/{path}");

        var limit = args["limit"]?.GetValue<int>() ?? 400;
        var children = new JsonArray();
        foreach (var c in current.Nodes.Take(limit))
        {
            children.Add(new JsonObject
            {
                ["name"] = c.Text,
                ["kind"] = c.Value is Wz_Image ? "image" : (c.Nodes.Count > 0 ? "dir" : "value"),
                ["value"] = c.Value is string s ? s : (c.Value is int i ? i.ToString() : null),
            });
        }
        return new JsonObject
        {
            ["pack"] = pack,
            ["path"] = path,
            ["kind"] = IsImageLike(current) ? "image" : "dir",
            ["children"] = children,
            ["total"] = current.Nodes.Count,
        };
    }

    /// <summary>
    /// 非 WZ 分类的目录：直接按文件系统列。用于 Base / Packs 这类
    /// 「在数据目录里存在、但不是 WZ 树节点」的目录。
    /// </summary>
    private static JsonNode CmdTreeFs(string pack, JsonObject args)
    {
        var path = (args["path"]?.GetValue<string>() ?? "").Trim('/');
        var dir = Path.Combine(_dataDir, pack, path.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir)) throw new FileNotFoundException($"{pack}/{path} 不存在（{dir}）");

        var limit = args["limit"]?.GetValue<int>() ?? 400;
        var rows = new JsonArray();
        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
        {
            rows.Add(new JsonObject { ["name"] = Path.GetFileName(d), ["kind"] = "dir" });
        }
        foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (rows.Count >= limit) break;
            var bn = Path.GetFileName(f);
            if (bn.StartsWith('.')) continue;
            rows.Add(new JsonObject
            {
                ["name"] = bn,
                ["kind"] = "file",
                ["mb"] = Math.Round(new FileInfo(f).Length / 1e6, 1),
            });
        }
        return new JsonObject
        {
            ["pack"] = pack,
            ["path"] = path,
            ["kind"] = "fs",
            ["children"] = rows,
            ["total"] = rows.Count,
            ["note"] = "该目录不是 WZ 分类（不在 WZ 树里），按文件系统列举",
        };
    }

    /// <summary>按名字/编号在字符串表里搜。</summary>
    private static JsonNode CmdSearch(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>() ?? throw new InvalidOperationException("search 需要 query");
        var limit = args["limit"]?.GetValue<int>() ?? 30;
        var wanted = args["kind"]?.GetValue<string>() ?? "all";
        var includeId = args["includeId"]?.GetValue<bool>() ?? false;

        var tableFor = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mob"] = "Mob.img", ["npc"] = "Npc.img", ["eqp"] = "Eqp.img", ["map"] = "Map.img",
            ["skill"] = "Skill.img", ["consume"] = "Consume.img", ["etc"] = "Etc.img",
            ["cash"] = "Cash.img", ["ins"] = "Ins.img",
        };
        var kinds = wanted == "all" ? tableFor.Keys.ToList() : new List<string> { wanted };

        var hits = new List<(JsonObject Hit, bool Exact, int Len)>();
        foreach (var kind in kinds)
        {
            if (!tableFor.TryGetValue(kind, out var table)) continue;
            var tableNode = Load().WzNode?.FindNodeByPath($@"String\{table}", true);
            if (tableNode is null) continue;

            foreach (var entry in tableNode.Nodes)
            {
                var nameNode = FindNameNode(entry);
                var name = nameNode?.Value?.ToString() ?? "";
                if (name.Length == 0 && !includeId) continue;

                var exact = entry.Text == query;
                var matched = exact ? "id" : (name.Contains(query, StringComparison.OrdinalIgnoreCase) ? "name" : null);
                if (matched is null) continue;

                hits.Add((
                    new JsonObject
                    {
                        ["id"] = entry.Text,
                        ["kind"] = kind,
                        ["table"] = table,
                        ["name"] = name,
                        ["matched"] = matched,
                    },
                    exact,
                    entry.Text.Length));
                if (hits.Count >= limit * 3) break;
            }
        }

        var ordered = hits.OrderBy(h => h.Exact ? 0 : 1).ThenBy(h => h.Len).ThenBy(h => h.Hit["id"]?.ToString(), StringComparer.Ordinal);
        var arr = new JsonArray();
        foreach (var h in ordered.Take(limit)) arr.Add(h.Hit);
        return new JsonObject { ["query"] = query, ["count"] = arr.Count, ["hits"] = arr };
    }

    /// <summary>
    /// 取节点的内容根：WzLib 对 .img 有两种呈现 —— 给了 Wz_Image 就解包，
    /// 已经展开成普通节点就直接用。只认前者会让大量图返回「不是图片节点」。
    /// </summary>
    private static Wz_Node ExpandImage(Wz_Node node, string label)
    {
        if (node.Value is Wz_Image img)
        {
            if (!img.TryExtract()) throw new InvalidOperationException($"{label} 解包失败");
            return img.Node ?? throw new InvalidOperationException($"{label} 内容为空");
        }
        return node;
    }

    /// <summary>
    /// 解出这一帧真正的画布节点。这份客户端把像素放在 `_Canvas/` 仓库里，
    /// 帧节点只有 origin/delay 和一个 `_outlink`（内部链接），不跟过去就只剩空节点
    /// （实测直接取帧节点会得到 1×1）。
    /// </summary>
    private static Wz_Node ResolveCanvas(Wz_Node node)
    {
        var link = node.FindNodeByPath("_outlink", true)?.Value as string
                ?? node.FindNodeByPath("_inlink", true)?.Value as string;
        if (string.IsNullOrWhiteSpace(link)) return node;

        var segs = link.Replace('\\', '/').Trim('/').Split('/', 2);
        var root = FindTop(segs[0]);
        if (root is null || segs.Length < 2) return node;
        var target = root.FindNodeByPath(segs[1].Replace('/', '\\'), true);
        if (target is null) return node;
        // 目标是 .img 时还要展开
        return target.Value is Wz_Image ? ExpandImage(target, link) : target;
    }

    /// <summary>像不像一张图：任一子节点名是数字（WZ 里帧就是 0/1/2…）。</summary>
    private static bool IsImageLike(Wz_Node n) =>
        n.Nodes.Any(c => int.TryParse(c.Text, out _)) ||
        n.Nodes.Any(c => c.Text is "info" or "origin" or "delay");

    /// <summary>名字字段在不同表里位置不同（Mob/Npc 在 name；Map 在 mapName）。</summary>
    private static Wz_Node? FindNameNode(Wz_Node entry)
    {
        foreach (var key in new[] { "name", "mapName", "streetName" })
        {
            var n = entry.FindNodeByPath(key, true);
            if (n is not null) return n;
        }
        // Eqp 是 Eqp/{group}/{id}/name 三层
        foreach (var group in entry.Nodes)
        {
            var n = group.FindNodeByPath("name", true);
            if (n is not null) return n;
        }
        return null;
    }

    /// <summary>一张图的信息与动作清单。</summary>
    private static JsonNode CmdGet(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new InvalidOperationException("get 需要 path");
        var trimmed = path.Trim('/');
        var segs = trimmed.Replace('\\', '/').Split('/', 2);
        var pack = segs[0];
        var rest = segs.Length > 1 ? segs[1] : "";

        var root = FindTop(pack) ?? throw new FileNotFoundException($"没有这个分类：{pack}");
        var node = rest.Length == 0 ? root : root.FindNodeByPath(rest.Replace('/', '\\'), true);
        if (node is null) throw new FileNotFoundException($"{path} 不存在");
        var body = ExpandImage(node, path);
        var anims = new JsonArray();
        foreach (var a in body.Nodes)
        {
            // 动作节点：子节点是帧（0,1,2…）或是带 delay 的结构
            var frames = a.Nodes.Count(x => int.TryParse(x.Text, out _));
            anims.Add(new JsonObject
            {
                ["name"] = a.Text,
                ["frames"] = frames > 0 ? frames : (a.Nodes.Count > 0 ? 1 : 0),
            });
        }
        return new JsonObject
        {
            ["path"] = path,
            ["pack"] = pack,
            ["kind"] = args["kind"]?.GetValue<string>() ?? "",
            ["animations"] = anims,
            ["nodes"] = body.Nodes.Count,
        };
    }

    /// <summary>
    /// 单帧原图直出 PNG（写文件）。给面板的 &lt;img&gt; 与验收脚本用。
    /// args: path（如 Mob/1210102.img）、animation（动作名，缺省取第一个）、frame、out（输出目录）
    /// </summary>
    private static JsonNode CmdImage(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new InvalidOperationException("image 需要 path");
        var anim = args["animation"]?.GetValue<string>();
        var frameIdx = args["frame"]?.GetValue<int>() ?? 0;
        var outDir = args["out"]?.GetValue<string>() ?? Path.GetTempPath();

        var trimmed = path.Trim('/');
        var segs = trimmed.Replace('\\', '/').Split('/', 2);
        var pack = segs[0];
        var rest = segs.Length > 1 ? segs[1] : "";
        var root = FindTop(pack) ?? throw new FileNotFoundException($"没有这个分类：{pack}");
        var node = rest.Length == 0 ? root : root.FindNodeByPath(rest.Replace('/', '\\'), true)
            ?? throw new FileNotFoundException($"{path} 不存在");

        var body = ExpandImage(node, path);
        var animNode = anim is null
            ? body.Nodes.FirstOrDefault(n => n.Nodes.Count > 0)
            : body.FindNodeByPath(anim, true);
        if (animNode is null) throw new FileNotFoundException($"{path} 里没有动作 {anim}");

        var all = animNode.Nodes.ToList();
        var target = all[Math.Clamp(frameIdx, 0, all.Count - 1)];
        var resolved = ResolveCanvas(target);
        var png = resolved.GetValue<Wz_Png>() ?? resolved.Value as Wz_Png
            ?? throw new InvalidOperationException($"{path}/{animNode.Text}/{target.Text} 解不到画布（_outlink 目标缺失）");

        var bmp = Png.Decode(png);
        var origin = target.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
        var delay = target.FindNodeByPath("delay")?.GetValueEx<int>(0) ?? 0;

        Directory.CreateDirectory(outDir);
        var file = Path.Combine(outDir, $"img_{pack}_{Path.GetFileNameWithoutExtension(rest)}_{animNode.Text}_{target.Text}.png");
        File.WriteAllBytes(file, Png.Encode(bmp));

        return new JsonObject
        {
            ["png"] = file,
            ["w"] = bmp.W,
            ["h"] = bmp.H,
            ["origin"] = new JsonArray(origin?.X ?? bmp.W / 2, origin?.Y ?? bmp.H / 2),
            ["delay"] = delay,
            ["animation"] = animNode.Text,
            ["frame"] = target.Text,
            ["engine"] = "wzlib",
        };
    }

    /// <summary>把一张 img 的某个动作展开成帧列表（每帧带画布、锚点、延迟）。</summary>
    private static (string Anim, List<Frame> Items) CollectFrames(JsonObject args, string path, out List<JsonObject> Meta)
    {
        var trimmed = path.Trim('/');
        var segs = trimmed.Replace('\\', '/').Split('/', 2);
        var pack = segs[0];
        var rest = segs.Length > 1 ? segs[1] : "";
        var root = FindTop(pack) ?? throw new FileNotFoundException($"没有这个分类：{pack}");
        var node = rest.Length == 0 ? root : root.FindNodeByPath(rest.Replace('/', '\\'), true)
            ?? throw new FileNotFoundException($"{path} 不存在");
        var body = ExpandImage(node, path);

        var want = args["animation"]?.GetValue<string>() ?? "stand";
        var anchor = "";
        var hash = want.IndexOf('#');
        if (hash >= 0) { anchor = want[(hash + 1)..]; want = want[..hash]; }

        var animNode = body.FindNodeByPath(want, true);
        var animName = want;
        if (animNode is null)
        {
            // 没指定动作就取第一个有子节点的
            var first = body.Nodes.FirstOrDefault(n => n.Nodes.Count > 0 || n.Value is Wz_Png || n.GetValue<Wz_Png>() is not null);
            if (first is null) throw new FileNotFoundException($"{path} 里没有动作");
            animNode = first;
            animName = first.Text;
        }

        var frames = new List<Frame>();
        Meta = new List<JsonObject>();
        var candidates = animNode.Nodes.Count > 0 ? animNode.Nodes.ToList() : new List<Wz_Node> { animNode };
        foreach (var fr in candidates)
        {
            if (anchor.Length > 0 && fr.Text != anchor && fr.Text.TrimStart('0') != anchor.TrimStart('0')) continue;
            var resolved = ResolveCanvas(fr);
            var png = resolved.GetValue<Wz_Png>() ?? resolved.Value as Wz_Png;
            if (png is null) continue;
            if (png.Width <= 1 && png.Height <= 1) continue; // 占位画布
            var bmp = Png.Decode(png);
            var origin = fr.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null)
                      ?? resolved.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
            var delay = fr.FindNodeByPath("delay")?.GetValueEx<int>(0) ?? 0;
            var ox = origin?.X ?? bmp.W / 2;
            var oy = origin?.Y ?? bmp.H / 2;
            frames.Add(new Frame(fr.Text, bmp, ox, oy, delay));
            Meta.Add(new JsonObject
            {
                ["name"] = fr.Text,
                ["w"] = bmp.W,
                ["h"] = bmp.H,
                ["origin"] = new JsonArray(ox, oy),
                ["delay"] = delay,
            });
        }
        if (frames.Count == 0) throw new FileNotFoundException($"{path} 的 {animName} 没有帧");
        return (animName, frames);
    }

    private static JsonNode CmdFrames(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new InvalidOperationException("frames 需要 path");
        var (anim, items) = CollectFrames(args, path, out var meta);
        var arr = new JsonArray();
        foreach (var m in meta) arr.Add(m);
        return new JsonObject
        {
            ["path"] = path,
            ["animation"] = anim,
            ["count"] = items.Count,
            ["frames"] = arr,
            ["engine"] = "wzlib",
        };
    }

    private static JsonNode CmdSprite(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new InvalidOperationException("sprite 需要 path");
        var scale = args["scale"]?.GetValue<int>() ?? 1;
        var outDir = Environment.GetEnvironmentVariable("WZ_OUT_DIR") ?? Path.GetTempPath();
        var (anim, items) = CollectFrames(args, path, out _);

        var (sheet, rects, cellW, cellH) = Sheet.Compose(items, scale);
        Directory.CreateDirectory(outDir);
        var stem = $"{path.Replace('/', '_').Replace(".img", "")}_{anim}_{scale}_{items.Count}";
        var png = Path.Combine(outDir, $"sprite_{stem}.png");
        File.WriteAllBytes(png, Png.Encode(sheet));

        // 字段名必须与宿主 api.sprite 读的一致（sheet/frame/padding/scale/frames[*].origin）
        var framesArr = new JsonArray();
        foreach (var r in rects)
        {
            framesArr.Add(new JsonObject
            {
                ["index"] = r.Index,
                ["x"] = r.X, ["y"] = r.Y, ["w"] = r.W, ["h"] = r.H,
                ["origin"] = new JsonArray(r.Ox / scale, r.Oy / scale),
                ["delay"] = r.Delay > 0 ? r.Delay : 100,
            });
        }
        // 旧的 frame.w/h 是**并集画布**尺寸（不是最大单帧），对齐它
        var maxW = cellW;
        var maxH = cellH;
        var outObj = new JsonObject
        {
            ["path"] = path,
            ["animation"] = anim,
            ["count"] = items.Count,
            ["sheet"] = new JsonObject { ["w"] = sheet.W, ["h"] = sheet.H },
            ["frame"] = new JsonObject { ["w"] = maxW, ["h"] = maxH },
            ["padding"] = 2,
            ["scale"] = scale,
            ["frames"] = framesArr,
            ["png"] = png,
            ["engine"] = "wzlib",
        };

        if (args["gif"]?.GetValue<bool>() == true)
        {
            var gif = Path.Combine(outDir, $"anim_{stem}.gif");
            var (gw, gh, gf) = Sheet.WriteGif(items, gif, scale,
                args["max_side"]?.GetValue<int>() ?? 480);
            outObj["gif"] = gif;
            outObj["gif_w"] = gw;
            outObj["gif_h"] = gh;
            outObj["gif_frames"] = gf;
        }
        return outObj;
    }

    /// <summary>
    /// 一个怪物在结构容器里的形状。
    ///
    /// WzLib 的 LoadMsFile 会把 .ms 的内容并进普通 WZ 节点树（它只公开
    /// WzStructure/FileStream，不单独给 .ms 的结构树），所以这里直接读合并后的树：
    /// 能读到动作就说明这个怪物的结构来自 .ms 或 Mob.wz，对调用方是同一件事。
    /// </summary>
    private static JsonNode CmdMsMob(JsonObject args)
    {
        var id = (args["id"]?.GetValue<string>() ?? "").Trim();
        if (id.Length == 0) throw new InvalidOperationException("ms_mob 需要 id");
        var (path, node) = FindImg("mob", id);
        if (node is null)
        {
            return new JsonObject
            {
                ["id"] = id, ["found"] = false,
                ["near"] = new JsonArray(),
                ["note"] = "结构来自 .ms；像素经 _outlink 取自 Mob/_Canvas",
            };
        }
        var anims = new JsonObject();
        var total = 0;
        foreach (var a in node.Nodes)
        {
            if (a.Text is "info" or "mobInfo") continue;
            var frames = new JsonArray();
            foreach (var f in a.Nodes) frames.Add(int.TryParse(f.Text, out var n) ? n : 0);
            if (frames.Count == 0)
            {
                var png = a.GetValue<Wz_Png>() ?? a.Value as Wz_Png;
                if (png is null) continue;
                frames.Add(0);
            }
            anims[a.Text] = frames;
            total += frames.Count;
        }
        return new JsonObject
        {
            ["id"] = id,
            ["found"] = true,
            ["path"] = path,
            ["animations"] = anims,
            ["frames_total"] = total,
            ["spine"] = null,
            ["note"] = "结构来自 .ms；像素经 _outlink 取自 Mob/_Canvas",
        };
    }

    /// <summary>已装结构容器的概览（.ms/.mn 文件清单 + 载入状态）。</summary>
    private static JsonNode CmdMsStats()
    {
        var dir = Path.Combine(_dataDir, "Packs");
        var opened = new JsonArray();
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".ms" or ".mn")) continue;
                opened.Add(new JsonObject
                {
                    ["file"] = Path.GetFileName(f),
                    ["category"] = Path.GetFileNameWithoutExtension(f).Split('_')[0],
                    ["mb"] = Math.Round(new FileInfo(f).Length / 1e6, 1),
                });
            }
        }
        return new JsonObject { ["opened"] = opened, ["cache_size"] = opened.Count, ["engine"] = "wzlib" };
    }

    /// <summary>Packs 目录下的 .ms / .mn 清单。</summary>
    private static JsonNode CmdMsPacks()
    {
        var dir = Path.Combine(_dataDir, "Packs");
        var files = new JsonArray();
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".ms" or ".mn")) continue;
                files.Add(new JsonObject
                {
                    ["name"] = Path.GetFileName(f),
                    ["mb"] = Math.Round(new FileInfo(f).Length / 1e6, 1),
                });
            }
        }
        return new JsonObject { ["packs_dir"] = dir, ["files"] = files, ["engine"] = "wzlib" };
    }
}
