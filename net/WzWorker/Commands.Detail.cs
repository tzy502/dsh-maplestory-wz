// 详情类命令：stats / detail / detail_page / sources。
//
// 字段与分支逐一对齐 旧实现 的 cmd_stats / cmd_detail / cmd_detail_page /
// cmd_sources —— 前端（client.js 的 Detail 组件）直接读这些键，少一个就会显示空白，
// 所以找不到条目时也返回完整骨架，而不是抛错换个形状。
//
// 为什么没有直接复用 Helpers.Flatten / Helpers.AnimationsOf：
//   * Flatten 把「带子节点的画布」当成标量整块跳过，装备 info 里的 icon/origin、
//     icon/_outlink 会凭空消失（旧实现把这些当子属性递归）；
//   * Flatten 把 Wz_Vector 摊成 {x,y} 对象，旧实现是 [x,y] 数组，前端按数组渲染；
//   * AnimationsOf 只下钻一层，装备那种「帧 → weapon → 画布」的分层帧取不到尺寸，
//     会显示 0×0，而且缺少 旧实现 动作项里的 kind 字段。
// 因此这三处按 旧的语义重写；路径解析、画布解码、雪碧图合成仍复用现成实现。
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WzComparerR2.WzLib;

namespace WzWorker;

internal static partial class Program
{
    // 与 旧的 PACKS 一致：_outlink 的第一段是不是分类名，决定按绝对路径还是
    // img 内相对路径解析（两套兜底顺序不同，混用会解析失败）。
    private static readonly string[] PackNames =
    {
        "Base", "Character", "Effect", "Etc", "Item", "Map", "Mob", "Morph",
        "Npc", "Quest", "Reactor", "Skill", "Sound", "String", "TamingMob", "UI",
    };

    // String/Eqp.img 比 Mob.img 多一层分组壳：Eqp → Weapon/Cap/... → 编号。
    private static readonly string[] StringTableTops = { "Eqp", "Consume", "Ins", "Etc", "Cash", "Pet", "Map", "Skill" };

    // 装备编号在字符串表里查不到分组时的顺序兜底（越靠前越常见，命中即止）。
    private static readonly string[] EqpGroupFallback =
    {
        "Weapon", "Cap", "Coat", "Longcoat", "Pants", "Shoes", "Glove", "Shield",
        "Cape", "Accessory", "Ring", "Face", "Hair", "PetEquip", "Android", "Bits",
        "ArcaneForce", "AuthenticForce", "Totem", "Jewel", "Mechanic", "Taming",
    };

    // 取代表图的动作优先级（怪物/NPC 没有 info/icon，只能从动作的首帧借）。
    private static readonly string[] IconAnimations = { "stand", "move", "stand1", "walk1", "alert", "hit1", "die1" };

    private static int _detailImages;
    private static int _detailDecodes;

    // 只用于 stats 的「读了多少张 img」：WzLib 自己缓存解包结果，这里只需要路径去重。
    private static readonly HashSet<string> _detailImageCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, DetailStringRec>> _detailStringCache = new(StringComparer.Ordinal);

    private sealed record DetailStringRec(string Group, Dictionary<string, string> Fields);

    private readonly record struct DetailAnim(string Name, int Frames, string Kind, int W, int H);

    private readonly record struct DetailCanvas(Wz_Node Node, int W, int H, Wz_Vector? Origin, int Delay);

    private readonly record struct DetailFrame(string Name, Wz_Node Source, int W, int H, Wz_Vector? Origin, int Delay);

    private readonly record struct DetailIcon(Wz_Node Canvas, string Source);

    /// <summary>
    /// 主控接线用：返回 null 表示不是本文件负责的命令。
    /// Program.Dispatch 里加一条 TryDispatchDetail(cmd, args) ?? ... 即可生效。
    /// </summary>
    internal static JsonNode? TryDispatchDetail(string cmd, JsonObject args) => cmd switch
    {
        "stats" => CmdStats(args),
        "detail" => CmdDetail(args),
        "detail_page" => CmdDetailPage(args),
        "sources" => CmdSources(args),
        _ => null,
    };

    // ── stats ─────────────────────────────────────────────────────────

    /// <summary>
    /// 进程自检信息。字段名与 旧实现 一致；值按 C# 侧能拿到的等价物填：
    /// roots 是已载入的 WZ 分类（WzLib 一次性全载，不像 旧实现 那样按需打开），
    /// fingerprint 取入口程序集（旧实现是源文件 sha1），用来识别宿主复用了旧进程。
    /// </summary>
    internal static JsonNode CmdStats(JsonObject args)
    {
        var roots = new JsonArray();
        if (_wzs?.WzNode is not null)
        {
            // 旧的 roots 只装「已打开的分类」；WzLib 一次载入全部，所以列出所有分类。
            // 根节点上还挂着 String 里的零散 img（*_KR.img、smap.img…），不算分类，滤掉。
            foreach (var node in _wzs.WzNode.Nodes)
            {
                if (Array.IndexOf(PackNames, node.Text) >= 0) roots.Add(node.Text);
            }
        }

        var fingerprint = new JsonObject();
        var location = typeof(Program).Assembly.Location;
        if (!string.IsNullOrEmpty(location) && File.Exists(location))
        {
            var hash = SHA1.HashData(File.ReadAllBytes(location));
            fingerprint[Path.GetFileName(location)] = Convert.ToHexString(hash).ToLowerInvariant()[..12];
        }

        return new JsonObject
        {
            ["engine"] = "",
            ["data_dir"] = _dataDir,
            ["region"] = _region,
            ["version"] = _version,
            ["roots"] = roots,
            ["cached_images"] = _detailImageCache.Count,
            ["fingerprint"] = fingerprint,
            ["pid"] = Environment.ProcessId,
            ["opens"] = _wzs is null ? 0 : 1,
            ["images"] = _detailImages,
            ["decodes"] = _detailDecodes,
        };
    }

    // ── detail ────────────────────────────────────────────────────────

    internal static JsonNode CmdDetail(JsonObject args)
    {
        var id = DetailArgText(args, "id").Trim();
        if (id.Length == 0) throw new InvalidOperationException("detail 需要 id");

        // 没给类别时按编号猜：7 位以上且不以 1/9 开头的是装备（怪物编号都以 1/9 打头）。
        // 这个规则直接照抄 旧实现，改了指名道姓的调用方会拿到另一个类别。
        var kind = DetailArgText(args, "kind").Trim();
        if (kind.Length == 0)
        {
            kind = id.Length >= 7 && !id.StartsWith('1') && !id.StartsWith('9') ? "eqp" : "mob";
        }
        if (kind is not ("mob" or "npc" or "eqp"))
        {
            throw new InvalidOperationException($"不支持的类别 '{kind}'（只支持 mob / npc / eqp）");
        }

        var (path, img, err) = DetailFindImg(kind, id);
        var table = kind switch { "mob" => "Mob.img", "npc" => "Npc.img", _ => "Eqp.img" };
        var entry = DetailLookupString(table, id);
        var name = entry is not null && entry.Fields.TryGetValue("name", out var nm) ? nm : null;
        var sources = ProbeSources(kind, id);

        if (img is null)
        {
            // 失败也要给齐键：页面靠 string/sources 解释「为什么没有图」。
            return new JsonObject
            {
                ["kind"] = kind,
                ["id"] = id,
                ["name"] = name,
                ["path"] = null,
                ["error"] = err,
                ["string"] = DetailStringEntryJson(entry),
                ["info"] = new JsonObject(),
                ["info_source"] = null,
                ["animations"] = new JsonArray(),
                ["total_frames"] = 0,
                ["icon"] = null,
                ["link"] = null,
                ["link_path"] = null,
                ["link_info"] = new JsonObject(),
                ["sources"] = sources,
                ["pack"] = null,
            };
        }

        var pack = path!.Split('/')[0];
        var info = new JsonObject();
        DetailFlattenImage(img, info);

        // 同型号共用数据：info/link 指向另一个编号，把对方的 info 并进来但单独标注来源。
        var link = DetailLinkTarget(img);
        string? linkPath = null;
        var linkInfo = new JsonObject();
        if (link is not null && link != id)
        {
            var (linkImgPath, linkImg, _) = DetailFindImg(kind, link);
            if (linkImg is not null)
            {
                linkPath = linkImgPath;
                DetailFlattenImage(linkImg, linkInfo);
            }
        }

        var anims = DetailAnimations(img, pack);
        var icon = DetailIconOf(kind, path!, img, pack);
        JsonNode? iconInfo = null;
        if (icon is not null)
        {
            var png = icon.Value.Canvas.GetValue<Wz_Png>()
                ?? throw new InvalidOperationException($"{icon.Value.Source} 取不到画布");
            var bmp = Png.Decode(png);
            _detailDecodes++;
            // 旧实现里 alpha 通道的 getbbox() 判空：没有一像素不透明才算「全透明」。
            var transparent = true;
            for (var i = 3; i < bmp.Px.Length; i += 4)
            {
                if (bmp.Px[i] == 0) continue;
                transparent = false;
                break;
            }
            // 旧的 detail.icon 只有这四个键（图片本体由 image/sprite 命令出），
            // 这里保持同样形状，不额外写文件。
            iconInfo = new JsonObject
            {
                ["w"] = bmp.W,
                ["h"] = bmp.H,
                ["source"] = icon.Value.Source,
                ["transparent"] = transparent,
            };
        }

        var animations = new JsonArray();
        foreach (var a in anims)
        {
            animations.Add(new JsonObject
            {
                ["name"] = a.Name,
                ["frames"] = a.Frames,
                ["kind"] = a.Kind,
                ["w"] = a.W,
                ["h"] = a.H,
            });
        }

        // 帧总数让页面先判断「有没有得看」，不必等逐个动作取帧。
        var totalFrames = 0;
        foreach (var a in anims) totalFrames += a.Frames;

        return new JsonObject
        {
            ["kind"] = kind,
            ["id"] = id,
            ["name"] = name,
            ["path"] = path,
            ["string"] = DetailStringEntryJson(entry),
            ["info"] = info,
            ["info_source"] = info.Count > 0 ? $"{path}/info" : null,
            ["link"] = link,
            ["link_path"] = linkPath,
            ["link_info"] = linkInfo,
            ["sources"] = sources,
            ["pack"] = pack,
            ["animations"] = animations,
            ["total_frames"] = totalFrames,
            ["icon"] = iconInfo,
            ["error"] = null,
        };
    }

    // ── detail_page ───────────────────────────────────────────────────

    /// <summary>
    /// 详情页一站式数据：详情 + 首个可渲染动作的缩略图矩形。
    /// 旧实现也只做首个动作（完整帧网格由 /frame 路由按需取），这里照做。
    /// </summary>
    internal static JsonNode CmdDetailPage(JsonObject args)
    {
        var detail = CmdDetail(args) as JsonObject ?? throw new InvalidOperationException("detail 返回的不是对象");
        detail["thumb"] = DetailBuildThumb(detail, args);
        return detail;
    }

    private static JsonObject? DetailBuildThumb(JsonObject detail, JsonObject args)
    {
        var path = detail["path"]?.ToString();
        if (string.IsNullOrEmpty(path)) return null;
        var body = DetailResolveImage(path!);
        if (body is null) return null;

        var pack = path!.Split('/')[0];
        // 旧实现里 `int(x or 默认值)`：0 与缺省同义；负数交给 Compose 夹到 1。
        var thumbScale = DetailArgInt(args, "thumb_scale", 2);
        var scale = thumbScale == 0 ? 2 : thumbScale;
        var maxAnimsArg = DetailArgInt(args, "max_anims", 24);
        var maxAnims = maxAnimsArg == 0 ? 24 : maxAnimsArg;
        var perAnimArg = DetailArgInt(args, "frames_per_anim", 8);
        var perAnim = perAnimArg == 0 ? 8 : perAnimArg;
        if (detail["animations"] is not JsonArray animations) return null;

        foreach (var item in DetailSlice(animations.ToList(), maxAnims))
        {
            var animName = item?["name"]?.ToString() ?? "";
            if (animName.Length == 0) continue;
            var node = body.FindNodeByPath(animName.Replace('/', '\\'));
            // 旧实现只处理子属性动作；画布型动作在它那里也走不到，直接跳过。
            if (node is null || node.Nodes.Count == 0) continue;

            var framesMeta = DetailFrames(node, pack, body);
            if (framesMeta.Count == 0) continue;

            var frames = new List<Frame>();
            foreach (var meta in DetailSlice(framesMeta, perAnim))
            {
                var png = meta.Source.GetValue<Wz_Png>();
                if (png is null) continue;
                var bmp = Png.Decode(png);
                _detailDecodes++;
                var ox = meta.Origin?.X ?? bmp.W / 2;
                var oy = meta.Origin?.Y ?? bmp.H / 2;
                frames.Add(new Frame(meta.Name, bmp, ox, oy, meta.Delay > 0 ? meta.Delay : 100));
            }
            // 这里不判空：旧的 compose 对空列表直接抛「没有可解码的帧」，
            // 帧数被参数裁成 0 时应当同样报错，而不是悄悄换下一个动作。
            var (sheet, rects, cellW, cellH) = Sheet.Compose(frames, scale);
            var framesJson = new JsonArray();
            foreach (var rect in rects)
            {
                framesJson.Add(new JsonObject
                {
                    ["index"] = rect.Index,
                    ["x"] = rect.X,
                    ["y"] = rect.Y,
                    ["w"] = rect.W,
                    ["h"] = rect.H,
                    // 旧实现是 boxes[idx][0] // scale（向下取整），负数时和 C# 的截断不同。
                    ["origin"] = new JsonArray(
                        (int)Math.Floor((double)rect.Ox / scale),
                        (int)Math.Floor((double)rect.Oy / scale)),
                    ["delay"] = rect.Delay,
                });
            }

            return new JsonObject
            {
                ["animation"] = animName,
                ["frames"] = framesMeta.Count,
                ["sheet"] = new JsonObject { ["w"] = sheet.W, ["h"] = sheet.H },
                ["frame"] = new JsonObject { ["w"] = cellW, ["h"] = cellH },
                ["frames_meta"] = framesJson,
            };
        }
        return null;
    }

    // ── sources ───────────────────────────────────────────────────────

    internal static JsonNode CmdSources(JsonObject args)
    {
        var kind = DetailArgText(args, "kind");
        if (kind.Length == 0) kind = "mob";
        var id = DetailArgText(args, "id").Trim();
        if (id.Length == 0) throw new InvalidOperationException("sources 需要 id");
        return new JsonObject { ["kind"] = kind, ["id"] = id, ["sources"] = ProbeSources(kind, id) };
    }

    /// <summary>
    /// 枚举这个编号的全部候选来源，并说清每条到底有什么。
    /// 本体 img 才有 info/动作结构；_Canvas 多数条目只是像素仓库 —— 两者分开报，
    /// 页面据此解释「为什么属性是空的」。
    /// </summary>
    internal static JsonArray ProbeSources(string kind, string id)
    {
        var pad7 = id.PadLeft(7, '0');
        var pad8 = id.PadLeft(8, '0');
        var candidates = new List<(string Label, string Pack, string Rel)>();
        switch (kind)
        {
            case "mob":
                candidates.Add(("Packs/*.ms 元数据主源", "__ms__", $"{pad7}.img"));
                candidates.Add(("Mob 本体 img", "Mob", $"{pad7}.img"));
                candidates.Add(("Mob PNG 仓库", "Mob", $"_Canvas/{pad7}.img"));
                break;
            case "npc":
                candidates.Add(("Npc 信息主源", "Npc", $"{pad7}.img"));
                candidates.Add(("Npc PNG 仓库", "Npc", $"_Canvas/{pad7}.img"));
                break;
            default:
                foreach (var group in DetailEqpGroups(id))
                {
                    candidates.Add(($"装备本体（{group}）", "Character", $"{group}/{pad8}.img"));
                    candidates.Add(($"装备 PNG 仓库（{group}）", "Character", $"{group}/_Canvas/{pad8}.img"));
                }
                break;
        }

        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (label, pack, rel) in candidates)
        {
            var key = $"{pack}/{rel}";
            if (!seen.Add(key)) continue;

            var entry = new JsonObject
            {
                ["label"] = label,
                ["path"] = key.Replace("//", "/"),
                ["exists"] = false,
                ["has_info"] = false,
                ["info_keys"] = 0,
                ["animations"] = 0,
                ["frames"] = 0,
                ["note"] = "",
            };

            Wz_Node? img;
            if (pack == "__ms__")
            {
                img = DetailFindMsImage($"Mob/{rel}");
            }
            else
            {
                if (FindTop(pack) is null)
                {
                    entry["note"] = $"打不开分类 {pack}：没有这个分类（{Path.Combine(_dataDir, pack)}）";
                    result.Add(entry);
                    continue;
                }
                try
                {
                    img = DetailResolveImage($"{pack}/{rel}");
                }
                catch (Exception ex)
                {
                    // 坏图只影响这一条来源，不该让整个 sources 命令失败。
                    entry["note"] = $"解析失败：{ex.GetType().Name}";
                    result.Add(entry);
                    continue;
                }
            }

            if (img is null)
            {
                entry["note"] = "不存在（本客户端没有这个条目）";
                result.Add(entry);
                continue;
            }

            entry["exists"] = true;
            var top = new List<string>();
            foreach (var child in img.Nodes) top.Add(child.Text);

            var infoNode = img.FindNodeByPath("info");
            if (infoNode is not null)
            {
                entry["has_info"] = true;
                entry["info_keys"] = infoNode.Nodes.Count;
            }

            var anims = DetailAnimations(img, pack == "__ms__" ? "Mob" : pack);
            var frames = 0;
            foreach (var a in anims) frames += a.Frames;
            entry["animations"] = anims.Count;
            entry["frames"] = frames;

            if (top.Contains("info"))
            {
                entry["note"] = $"含 info（{infoNode?.Nodes.Count ?? 0} 项）+ {anims.Count} 个动作";
            }
            else if (anims.Count > 0)
            {
                entry["note"] = $"只有图像数据：{anims.Count} 个动作 / {frames} 帧，无 info";
            }
            else
            {
                var topText = top.Count > 0 ? string.Join(", ", top.Take(5)) : "无";
                entry["note"] = $"空壳或非图像节点（顶层：{topText}）";
            }
            result.Add(entry);
        }
        return result;
    }

    // ── 候选路径与查找 ─────────────────────────────────────────────────

    private static List<string> DetailCandidatePaths(string kind, string id)
    {
        var pad7 = id.PadLeft(7, '0');
        var pad8 = id.PadLeft(8, '0');
        switch (kind)
        {
            case "eqp":
                var list = new List<string>();
                foreach (var group in DetailEqpGroups(id))
                {
                    list.Add($"Character/{group}/{pad8}.img");
                    list.Add($"Character/{group}/_Canvas/{pad8}.img");
                }
                return list;
            case "npc":
                return new List<string> { $"Npc/{pad7}.img", $"Npc/_Canvas/{pad7}.img", $"Npc/{id}.img" };
            case "mob":
                // 傲天把怪物图并进 _Canvas，但旧结构是 Mob/{id}.img —— 两种都试。
                return new List<string> { $"Mob/{pad7}.img", $"Mob/_Canvas/{pad7}.img", $"Mob/{id}.img" };
            default:
                return new List<string>();
        }
    }

    /// <summary>返回 (可用路径, 展开后的 img, 错误原因)。</summary>
    private static (string? Path, Wz_Node? Img, string? Error) DetailFindImg(string kind, string id)
    {
        var candidates = DetailCandidatePaths(kind, id);
        foreach (var candidate in candidates)
        {
            var img = DetailResolveImage(candidate);
            if (img is not null) return (candidate, img, null);
        }
        var joined = string.Join(", ", candidates);
        if (joined.Length > 160) joined = joined[..160];
        return (null, null, $"{kind} {id}：候选路径都没命中（{joined}）");
    }

    /// <summary>按路径取展开后的 img；路径大小写/补零不一致时在父目录里兜底找。</summary>
    private static Wz_Node? DetailResolveImage(string path)
    {
        // 旧的 _parsed_image 对「裸数字文件名」一律先查 Packs/*.ms 里的 Mob 镜像，
        // 不管当前在哪个分类下（老规则，Npc/1210102.img 会拿到怪物数据）。
        // 这里照做：kind 与编号不一致时，页面看到的仍是 旧实现那套结果。
        var (_, bareRel) = SplitWzPath(path);
        if (bareRel.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
            && bareRel.IndexOf('/') < 0
            && DetailIsDigits(bareRel[..^4]))
        {
            var msNode = DetailFindMsImage($"Mob/{bareRel}");
            if (msNode is not null)
            {
                if (_detailImageCache.Add(path)) _detailImages++;
                return msNode;
            }
        }

        var node = ResolveNode(path);
        if (node is null) node = DetailResolveLoose(path);
        if (node is not null && _detailImageCache.Add(path)) _detailImages++;
        return node;
    }

    private static Wz_Node? DetailResolveLoose(string path)
    {
        var (pack, rel) = SplitWzPath(path);
        if (rel.Length == 0) return null;
        var root = FindTop(pack);
        if (root is null) return null;

        var slash = rel.LastIndexOf('/');
        var parentRel = slash < 0 ? "" : rel[..slash];
        var leaf = slash < 0 ? rel : rel[(slash + 1)..];
        var parent = parentRel.Length == 0
            ? root
            : root.FindNodeByPath(parentRel.Replace('/', '\\'), true);
        if (parent is null) return null;

        var leafLower = leaf.ToLowerInvariant();
        var leafNorm = leafLower.TrimStart('0');
        foreach (var child in parent.Nodes)
        {
            var nameLower = child.Text.ToLowerInvariant();
            if (nameLower != leafLower && nameLower.TrimStart('0') != leafNorm) continue;
            var candidatePath = parentRel.Length == 0 ? $"{pack}/{child.Text}" : $"{pack}/{parentRel}/{child.Text}";
            return ResolveNode(candidatePath);
        }
        return null;
    }

    /// <summary>
    /// 在 Packs/*.ms 里找镜像。.ms 被 WzLib 并进同一棵树的 Mob 节点并覆盖同名 img，
    /// 所以这里按节点类型名确认「确实是 .ms 提供的」，不能只看路径存在。
    /// </summary>
    private static Wz_Node? DetailFindMsImage(string path)
    {
        var (pack, rel) = SplitWzPath(path);
        var root = FindTop(pack);
        if (root is null || rel.Length == 0) return null;
        var node = root.FindNodeByPath(rel.Replace('/', '\\'), false);
        if (node?.Value is null) return null;
        if (!node.Value.GetType().Name.StartsWith("Ms_Image", StringComparison.Ordinal)) return null;
        try
        {
            return ExpandImage(node, path);
        }
        catch
        {
            // .ms 里个别条目解包失败只丢这一条来源，不影响 WZ 本体。
            return null;
        }
    }

    // ── 字符串表 ──────────────────────────────────────────────────────

    private static Dictionary<string, DetailStringRec> DetailStringMap(string table)
    {
        if (_detailStringCache.TryGetValue(table, out var cached)) return cached;

        var map = new Dictionary<string, DetailStringRec>(StringComparer.Ordinal);
        var root = Load().WzNode?.FindNodeByPath($@"String\{table}", true);
        if (root is not null)
        {
            var kids = root.Nodes;
            // Eqp/Map 这些表多一层分组壳；Mob/Npc 的编号直接挂在表下。
            Wz_Node? top = null;
            if (kids.Count == 1 && Array.IndexOf(StringTableTops, kids[0].Text) >= 0) top = kids[0];

            if (top is not null)
            {
                foreach (var group in top.Nodes)
                {
                    foreach (var entry in group.Nodes) map[entry.Text] = DetailStringFields(group.Text, entry);
                }
            }
            else
            {
                var groupName = table.Split('.')[0];
                foreach (var entry in kids) map[entry.Text] = DetailStringFields(groupName, entry);
            }
        }
        _detailStringCache[table] = map;
        return map;
    }

    private static DetailStringRec DetailStringFields(string group, Wz_Node entry)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in entry.Nodes)
        {
            if (child.Value is null) continue;
            fields[child.Text] = DetailPyStr(child.Value);
        }
        return new DetailStringRec(group, fields);
    }

    private static DetailStringRec? DetailLookupString(string table, string id)
    {
        var map = DetailStringMap(table);
        if (map.TryGetValue(id, out var rec)) return rec;
        var stripped = id.TrimStart('0');
        if (stripped.Length > 0 && stripped != id && map.TryGetValue(stripped, out rec)) return rec;
        return null;
    }

    private static List<string> DetailEqpGroups(string id)
    {
        var map = DetailStringMap("Eqp.img");
        DetailStringRec? rec = null;
        if (!map.TryGetValue(id, out rec))
        {
            var stripped = id.TrimStart('0');
            if (stripped.Length > 0) map.TryGetValue(stripped, out rec);
        }
        if (rec is not null && rec.Group.Length > 0) return new List<string> { rec.Group };
        return EqpGroupFallback.ToList();
    }

    private static JsonNode? DetailStringEntryJson(DetailStringRec? rec)
    {
        if (rec is null) return null;
        var obj = new JsonObject { ["group"] = rec.Group };
        foreach (var pair in rec.Fields) obj[pair.Key] = pair.Value;
        return obj;
    }

    // ── info 摊平 ─────────────────────────────────────────────────────

    private static void DetailFlattenImage(Wz_Node img, JsonObject into)
    {
        foreach (var key in new[] { "info", "mobInfo", "npcInfo" })
        {
            var node = img.FindNodeByPath(key);
            if (node is null) continue;
            DetailFlatten(node, into);
        }
    }

    /// <summary>
    /// 把属性树压成 "a/b" → 标量。层数上限与 旧实现 一致（2 层），
    /// 更深的子树折成 "&lt;N 项&gt;" 占位，避免 info 里个别嵌套把面板刷爆。
    /// </summary>
    private static void DetailFlatten(Wz_Node node, JsonObject into, string prefix = "", int depth = 0, int maxDepth = 2)
    {
        foreach (var child in node.Nodes)
        {
            var key = prefix.Length == 0 ? child.Text : $"{prefix}/{child.Text}";
            // 画布在 旧实现里也是子属性，带子节点时按容器继续摊（icon/origin 就在这一层）。
            if (child.Nodes.Count > 0)
            {
                if (depth < maxDepth) DetailFlatten(child, into, key, depth + 1, maxDepth);
                else into[key] = $"<{child.Nodes.Count} 项>";
                continue;
            }
            var value = child.Value;
            if (value is null || value is Wz_Png) continue;
            into[key] = DetailScalarJson(value);
        }
    }

    private static JsonNode DetailScalarJson(object value)
    {
        switch (value)
        {
            case string s: return JsonValue.Create(s)!;
            case bool b: return JsonValue.Create(b)!;
            case int i: return JsonValue.Create(i)!;
            case long l: return JsonValue.Create(l)!;
            case short sh: return JsonValue.Create((int)sh)!;
            case ushort ush: return JsonValue.Create((int)ush)!;
            case byte by: return JsonValue.Create((int)by)!;
            case float f: return JsonValue.Create(f)!;
            case double d: return JsonValue.Create(d)!;
            case Wz_Vector vec: return new JsonArray(vec.X, vec.Y);
            case Wz_Uol uol: return JsonValue.Create(uol.Uol)!;
            case Wz_Sound sound: return JsonValue.Create(sound.DataLength)!;
            case Wz_Convex convex:
                var points = new JsonArray();
                foreach (var point in convex.Points) points.Add(new JsonArray(point.X, point.Y));
                return points;
            default: return JsonValue.Create(value.ToString())!;
        }
    }

    /// <summary>与 旧实现的内置 str() 对齐：bool 用 True/False，浮点保留 .0。</summary>
    private static string DetailPyStr(object value)
    {
        switch (value)
        {
            case string s: return s;
            case bool b: return b ? "True" : "False";
            case float f:
                var text = f.ToString("R", CultureInfo.InvariantCulture);
                return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
            case double d:
                var text2 = d.ToString("R", CultureInfo.InvariantCulture);
                return text2.Contains('.') || text2.Contains('E') || text2.Contains('e') ? text2 : text2 + ".0";
            default: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    // ── link ──────────────────────────────────────────────────────────

    /// <summary>同型号共用数据：link 或 info/link 指向另一个编号（"0"/"None" 视为无链接）。</summary>
    private static string? DetailLinkTarget(Wz_Node img)
    {
        foreach (var key in new[] { "link", @"info\link" })
        {
            var value = img.FindNodeByPath(key)?.Value;
            var text = value switch
            {
                string s => s.Trim(),
                int i => i.ToString(CultureInfo.InvariantCulture),
                _ => "",
            };
            if (text.Length > 0 && text is not ("0" or "None")) return text;
        }
        return null;
    }

    // ── 动作与帧 ──────────────────────────────────────────────────────

    private static List<DetailAnim> DetailAnimations(Wz_Node body, string pack)
    {
        var list = new List<DetailAnim>();
        foreach (var child in body.Nodes)
        {
            var png = child.GetValue<Wz_Png>();
            if (png is not null)
            {
                // 单帧动作直接就是一块画布（NPC 的 stand 常见这种结构）。
                var meta = DetailCanvasMeta(child, pack, body);
                if (meta is not null) list.Add(new DetailAnim(child.Text, 1, "canvas", meta.Value.W, meta.Value.H));
                continue;
            }
            if (child.Nodes.Count == 0) continue;
            var frames = DetailFrames(child, pack, body);
            if (frames.Count == 0) continue;
            var maxW = 0;
            var maxH = 0;
            foreach (var frame in frames)
            {
                maxW = Math.Max(maxW, frame.W);
                maxH = Math.Max(maxH, frame.H);
            }
            list.Add(new DetailAnim(child.Text, frames.Count, "anim", maxW, maxH));
        }
        return list;
    }

    private static List<DetailFrame> DetailFrames(Wz_Node animNode, string pack, Wz_Node body)
    {
        var list = new List<DetailFrame>();
        foreach (var frameNode in animNode.Nodes)
        {
            var meta = DetailFrameMeta(frameNode, pack, body);
            if (meta is not null) list.Add(meta.Value);
        }
        return list;
    }

    /// <summary>
    /// 一帧的绘制内容：在最多 3 层里挑面积最大的可解码画布。
    /// 装备的帧是「帧 → weapon → 画布」这种分层结构，只看第一层会得到 0×0。
    /// </summary>
    private static DetailFrame? DetailFrameMeta(Wz_Node frameNode, string pack, Wz_Node body)
    {
        DetailCanvas? best = null;
        foreach (var candidate in DetailLeaves(frameNode, 3))
        {
            var current = candidate;
            if (current.Value is Wz_Uol)
            {
                current = current.ResolveUol();
                if (current is null) continue;
            }
            if (current.GetValue<Wz_Png>() is null) continue;
            var meta = DetailCanvasMeta(current, pack, body);
            if (meta is null) continue;
            if (best is null || meta.Value.W * meta.Value.H > best.Value.W * best.Value.H) best = meta;
        }
        if (best is null) return null;
        var hit = best.Value;
        // 帧名取帧节点本身的名字（不是最终画布的），旧的 source 路径也用它。
        return new DetailFrame(frameNode.Text, hit.Node, hit.W, hit.H, hit.Origin, hit.Delay);
    }

    private static IEnumerable<Wz_Node> DetailLeaves(Wz_Node node, int depth)
    {
        yield return node;
        if (depth <= 0) yield break;
        if (node.Nodes.Count == 0 && node.GetValue<Wz_Png>() is null) yield break;
        foreach (var child in node.Nodes)
        {
            if (child.Nodes.Count > 0 || child.Value is Wz_Uol || child.GetValue<Wz_Png>() is not null)
            {
                foreach (var leaf in DetailLeaves(child, depth - 1)) yield return leaf;
            }
        }
    }

    /// <summary>
    /// 解析画布节点（跟 UOL / _outlink / _inlink 最多 8 跳）拿尺寸与锚点。
    /// 占位画布（1×1 或无像素）必须跟链接，否则 .ms 里全是 1×1 的空图。
    /// </summary>
    private static DetailCanvas? DetailCanvasMeta(Wz_Node canvas, string pack, Wz_Node body)
    {
        var current = canvas;
        var seen = new HashSet<Wz_Node>(ReferenceEqualityComparer.Instance);
        for (var hop = 0; hop < 8; hop++)
        {
            if (current is null || !seen.Add(current)) return null;
            if (current.Value is Wz_Uol)
            {
                current = current.ResolveUol();
                if (current is null) return null;
                continue;
            }
            var png = current.GetValue<Wz_Png>();
            if (png is null) return null;
            if (png.DataLength <= 0 || (png.Width <= 1 && png.Height <= 1))
            {
                var link = current.FindNodeByPath("_outlink")?.Value as string
                        ?? current.FindNodeByPath("_inlink")?.Value as string;
                if (string.IsNullOrWhiteSpace(link)) return null;
                var next = DetailResolveRef(link!, pack, body);
                if (next is null) return null;
                current = next;
                continue;
            }
            var origin = DetailVector(current.FindNodeByPath("origin"))
                      ?? DetailVector(canvas.FindNodeByPath("origin"));
            var delayNode = current.FindNodeByPath("delay") ?? canvas.FindNodeByPath("delay");
            var delay = delayNode?.GetValueEx<int>(0) ?? 0;
            return new DetailCanvas(current, png.Width, png.Height, origin, delay);
        }
        return null;
    }

    private static Wz_Vector? DetailVector(Wz_Node? node) => node?.Value as Wz_Vector;

    /// <summary>
    /// 解析 _outlink/_inlink。本客户端把 Weapon/、_Canvas/ 当成图片名前缀而不是子目录，
    /// 所以优先按「分类/图片路径/内部路径」直接取，失败再按当前 img 相对路径兜底。
    /// </summary>
    private static Wz_Node? DetailResolveRef(string reference, string pack, Wz_Node body)
    {
        var normalized = reference.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) return null;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        if (Array.IndexOf(PackNames, parts[0]) < 0)
        {
            // _inlink 是 img 内相对路径。
            return body.FindNodeByPath(normalized.Replace('/', '\\'), true);
        }

        var candidates = new List<(string Pack, string Rel)>();
        candidates.Add((parts[0], string.Join('/', parts.Skip(1))));
        if (parts[0] != pack) candidates.Add((pack, normalized));

        foreach (var (candidatePack, rel) in candidates)
        {
            if (rel.Length == 0) continue;
            var imgAt = rel.IndexOf(".img", StringComparison.OrdinalIgnoreCase);
            if (imgAt < 0) continue;
            var imgRel = rel[..(imgAt + 4)];
            var sub = rel[(imgAt + 4)..].TrimStart('/');
            var target = DetailResolveImage($"{candidatePack}/{imgRel}");
            if (target is null) continue;
            var node = sub.Length == 0
                ? target
                : target.FindNodeByPath(sub.Replace('/', '\\'), true);
            if (node is not null) return node;
        }
        return null;
    }

    // ── 代表图 ────────────────────────────────────────────────────────

    private static DetailIcon? DetailIconOf(string kind, string imgPath, Wz_Node body, string pack)
    {
        if (kind == "eqp")
        {
            // 旧实现是 `info/icon or info/iconRaw`：只有前者不存在才看 iconRaw。
            var iconNode = body.FindNodeByPath(@"info\icon") ?? body.FindNodeByPath(@"info\iconRaw");
            if (iconNode is not null)
            {
                var meta = DetailCanvasMeta(iconNode, pack, body);
                if (meta is not null) return new DetailIcon(meta.Value.Node, $"{imgPath}/info/icon");
            }
        }

        foreach (var anim in IconAnimations)
        {
            var node = body.FindNodeByPath(anim);
            if (node is null || node.Nodes.Count == 0) continue;
            foreach (var frameNode in node.Nodes)
            {
                var meta = DetailFrameMeta(frameNode, pack, body);
                if (meta is null) continue;
                return new DetailIcon(meta.Value.Source, $"{imgPath}/{anim}/{meta.Value.Name}");
            }
        }
        return null;
    }

    // ── 参数读取 ──────────────────────────────────────────────────────

    private static string DetailArgText(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null) return "";
        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            // 数字编号（JSON number）也接受，和 旧的 str(args["id"]) 一致。
            return node.ToJsonString().Trim('"');
        }
    }

    private static int DetailArgInt(JsonObject args, string key, int fallback)
    {
        var node = args[key];
        if (node is null) return fallback;
        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return int.TryParse(node.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }
    }

    /// <summary>旧的切片语义：负数表示从尾部去掉，正数就是 C# 的 Take。</summary>
    private static IEnumerable<T> DetailSlice<T>(IReadOnlyList<T> items, int count)
    {
        var take = count >= 0 ? count : Math.Max(0, items.Count + count);
        return items.Take(take);
    }

    private static bool DetailIsDigits(string text)
    {
        if (text.Length == 0) return false;
        foreach (var ch in text)
        {
            if (ch < '0' || ch > '9') return false;
        }
        return true;
    }
}
