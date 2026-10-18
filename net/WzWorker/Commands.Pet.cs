// 桌宠命令：怪物雪碧图 + 纸娃娃。
//
// 为什么要重写一遍而不是复用 Program.cs 的 sprite：桌宠的几何口径与面板不同 ——
// 每帧按 origin 锚进「等宽格」，同一次调用的所有动作共用格尺寸，客户端只做
// left = snap.x - anchor.x 的整数摆放，不能有 padding、不能折行。
//
// 字段名与 py/pet_worker.py 逐字对齐：前端 client.js 的 pet 渲染逻辑直接读它们，
// 改名会白图。这里只移植渲染所需的子集，ItemEff 特效层与武器姿态探测没有移植。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WzComparerR2.WzLib;

namespace WzWorker;

internal static partial class Program
{
    private const int PetDefaultFrameDelay = 120;
    private const int PetMinFrameDelay = 20;
    private const int PetMaxFrameDelay = 5000;
    private const int PetCharaLayoutDelay = 180;
    private const int PetComposeVersion = 3;

    // 实现标记进文件名：与 旧实现共用同一个输出目录时不能互相命中缓存，
    // 两边像素语义（paste 的 alpha 平方 vs alpha_composite）并不完全相同。
    private const string PetImplTag = "cs1";

    private static readonly string[] PetKnownPacks = { "Mob", "Npc", "Character", "Item", "Skill", "Effect" };

    private static readonly string[] PetDefaultEquips = { "00002000", "00012000", "00030000", "00020000" };
    private static readonly string[] PetDefaultActions = { "stand", "move", "hit1" };

    private static readonly Dictionary<string, string[]> PetActionAliases = new(StringComparer.Ordinal)
    {
        ["stand"] = new[] { "stand", "stand1", "stand0", "stand2" },
        ["move"] = new[] { "move", "walk1", "walk2", "walk" },
        ["fly"] = new[] { "fly", "jump" },
        ["hit"] = new[] { "hit1", "hit", "hit2" },
        ["die"] = new[] { "die1", "die", "die2" },
    };

    private static readonly Dictionary<string, string> PetActionFallbacks = new(StringComparer.Ordinal)
    {
        ["move"] = "stand",
        ["fly"] = "stand",
        ["hit"] = "stand",
        ["hit1"] = "stand",
        ["say"] = "stand",
        ["mouse"] = "stand",
        ["laugh"] = "stand",
        ["eye"] = "stand",
    };

    private static Dictionary<string, Wz_Node>? _petMobTable;
    private static int _petOpens;
    private static int _petDecodes;
    private static bool _petEnvReady;
    private static bool _petLoadCounted;

    /// <summary>桌宠命令分发。Program.Dispatch 还没接这里，测试入口见 SelfTestPet。</summary>
    internal static JsonNode? TryDispatchPet(string cmd, JsonObject args) => cmd switch
    {
        "pet" => CmdPet(args),
        "chara" => CmdChara(args),
        "pet_info" => CmdPetInfo(args),
        "pet_search" => CmdPetSearch(args),
        "pet_presets" => CmdPetPresets(args),
        "pet_stats" => CmdPetStats(args),
        _ => null,
    };

    /// <summary>
    /// 绕过 stdio 直接调用桌宠命令的入口（Program.Dispatch 未接入期间给验收用）。
    /// 同时接受 旧实现命令名 info/search/presets/stats，避免两套叫法各记一遍。
    /// </summary>
    internal static JsonNode? SelfTestPet(string cmd, JsonObject args)
    {
        PetEnsureEnv();
        var mapped = cmd switch
        {
            "info" => "pet_info",
            "search" => "pet_search",
            "presets" => "pet_presets",
            "stats" => "pet_stats",
            _ => cmd,
        };
        return TryDispatchPet(mapped, args)
            ?? throw new NotSupportedException($"pet worker 不支持命令：{cmd}");
    }

    // ── 环境 ─────────────────────────────────────────────────────────────
    private static void PetEnsureEnv()
    {
        if (_petEnvReady) return;
        _petEnvReady = true;
        // SelfTestPet 不走 Main，环境变量得自己补一次；Main 已赋值时不覆盖。
        if (string.IsNullOrEmpty(_dataDir))
        {
            _dataDir = Environment.GetEnvironmentVariable("WZ_DATA_DIR")
                ?? Environment.GetEnvironmentVariable("PET_DATA_DIR") ?? "";
        }
        if (!int.TryParse(Environment.GetEnvironmentVariable("WZ_VERSION"), out var version))
        {
            int.TryParse(Environment.GetEnvironmentVariable("PET_VERSION"), out version);
        }
        if (version > 0) _version = version;
        _region = Environment.GetEnvironmentVariable("WZ_REGION")
            ?? Environment.GetEnvironmentVariable("PET_REGION") ?? _region;
    }

    private static string PetOutDir()
    {
        var dir = Environment.GetEnvironmentVariable("PET_OUT_DIR");
        if (!string.IsNullOrWhiteSpace(dir)) return dir;
        dir = Environment.GetEnvironmentVariable("WZ_OUT_DIR");
        if (!string.IsNullOrWhiteSpace(dir)) return dir;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "desktop-pet");
    }

    private static void PetEnsureLoaded()
    {
        _ = Load();
        // 对齐 旧的 opens 语义：同一进程里只记一次装载，不是每次取数据都加。
        if (_petLoadCounted) return;
        _petLoadCounted = true;
        _petOpens++;
    }

    private static Bitmap32 PetDecode(Wz_Png png)
    {
        _petDecodes++;
        return Png.Decode(png);
    }

    // ── JSON 取值 ────────────────────────────────────────────────────────
    private static string PetStr(JsonNode? node) => node?.ToString() ?? "";

    private static int PetIntArg(JsonObject args, string key, int def)
    {
        var node = args[key];
        if (node is null) return def;
        if (node is JsonValue value && value.TryGetValue<int>(out var i)) return i;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : def;
    }

    private static bool PetBoolArg(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null) return false;
        if (node is JsonValue value && value.TryGetValue<bool>(out var b)) return b;
        if (node is JsonValue number && number.TryGetValue<int>(out var i)) return i != 0;
        var text = node.ToString();
        return text is "true" or "1" || text.Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> PetStrList(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var text = item?.ToString();
                if (!string.IsNullOrEmpty(text)) list.Add(text);
            }
        }
        return list;
    }

    private static JsonObject PetCloneObject(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var kv in source) copy[kv.Key] = kv.Value?.DeepClone();
        return copy;
    }

    // ── WZ 取值 ──────────────────────────────────────────────────────────
    private static (int X, int Y)? PetVec(Wz_Node? node) =>
        node?.Value is Wz_Vector vec ? (vec.X, vec.Y) : null;

    private static int? PetIntNode(Wz_Node? node) => node?.Value switch
    {
        int i => i,
        short s => s,
        long l => (int)l,
        string text when int.TryParse(text, out var parsed) => parsed,
        _ => null,
    };

    private static string? PetLink(Wz_Node node)
    {
        foreach (var key in new[] { "_outlink", "_inlink" })
        {
            var value = node.FindNodeByPath(key, true)?.Value as string;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static bool PetIsContainer(Wz_Node node) =>
        node.Value is not Wz_Png && node.Value is not Wz_Uol && node.Nodes.Count > 0;

    /// <summary>按相对路径取图片节点，大小写/前导零兜底（同 旧的 _parsed_image）。</summary>
    private static Wz_Node? PetFindImage(Wz_Node? root, string rel)
    {
        if (root is null) return null;
        var path = rel.Replace('\\', '/').Trim('/').Replace('/', '\\');
        if (path.Length == 0) return null;
        var node = root.FindNodeByPath(path, true);
        if (node is not null) return node;

        var cut = path.LastIndexOf('\\');
        var parentPath = cut < 0 ? "" : path[..cut];
        var leaf = cut < 0 ? path : path[(cut + 1)..];
        var parent = parentPath.Length == 0 ? root : root.FindNodeByPath(parentPath, true);
        if (parent is null) return null;
        var norm = leaf.ToLowerInvariant().TrimStart('0');
        foreach (var child in parent.Nodes)
        {
            if (child.Text.Equals(leaf, StringComparison.OrdinalIgnoreCase)
                || child.Text.ToLowerInvariant().TrimStart('0') == norm)
            {
                return child.Value is Wz_Image ? ExpandImage(child, rel) : child;
            }
        }
        return null;
    }

    private static Wz_Node? PetParsedImage(string pack, string rel)
    {
        var root = FindTop(pack);
        return root is null ? null : PetFindImage(root, rel);
    }

    /// <summary>解析 _outlink / _inlink。本客户端把 _Canvas/ 做进了图片路径前缀。</summary>
    private static Wz_Node? PetResolveRef(string reference, string pack, Wz_Node? img)
    {
        var refStr = reference.Replace('\\', '/').Trim('/');
        var parts = refStr.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        if (!PetKnownPacks.Contains(parts[0]))
        {
            return img?.FindNodeByPath(refStr.Replace('/', '\\'), true);
        }

        var candidates = new List<(string Pack, string Rel)>
        {
            (parts[0], string.Join('/', parts.Skip(1))),
        };
        if (parts[0] != pack) candidates.Add((pack, refStr));

        foreach (var (packName, rel) in candidates)
        {
            if (rel.Length == 0) continue;
            var root = FindTop(packName);
            if (root is null) continue;
            var mark = rel.IndexOf(".img", StringComparison.Ordinal);
            if (mark < 0) continue;
            var imagePath = rel[..(mark + 4)];
            var sub = rel[(mark + 4)..].Trim('/');
            var image = PetFindImage(root, imagePath);
            if (image is null) continue;
            var node = sub.Length == 0
                ? image
                : image.FindNodeByPath(sub.Replace('/', '\\'), true);
            if (node is not null) return node;
        }
        return null;
    }

    // ── 画布元数据（origin / delay / 像素）────────────────────────────────
    private sealed record PetCanvas(Wz_Png Png, int W, int H, (int X, int Y)? Origin, int? Delay);

    /// <summary>
    /// 解一个画布节点：跟 UOL、跟 _outlink/_inlink，返回像素与 origin/delay。
    /// origin 先看真实像素节点、再看外层占位节点 —— 本客户端只有外层带 origin。
    /// </summary>
    private static PetCanvas? PetCanvasMeta(Wz_Node canvas, string pack, Wz_Node? img)
    {
        var current = canvas;
        var seen = new HashSet<Wz_Node>();
        for (var hop = 0; hop < 8; hop++)
        {
            if (current is null || !seen.Add(current)) return null;

            if (current.Value is Wz_Uol uol)
            {
                var target = uol.HandleUol(current);
                if (target is null) return null;
                current = target;
                continue;
            }

            if (current.Value is Wz_Png png && (png.Width > 1 || png.Height > 1))
            {
                var origin = PetVec(current.FindNodeByPath("origin", true))
                    ?? PetVec(canvas.FindNodeByPath("origin", true));
                var delay = PetIntNode(current.FindNodeByPath("delay", true))
                    ?? PetIntNode(canvas.FindNodeByPath("delay", true));
                return new PetCanvas(png, png.Width, png.Height, origin, delay);
            }

            var link = PetLink(current);
            if (link is null) return null;
            var next = PetResolveRef(link, pack, img);
            if (next is null) return null;
            current = next;
        }
        return null;
    }

    /// <summary>按 旧实现 _leaves 的口径枚举候选画布（最多下钻 3 层）。</summary>
    private static IEnumerable<Wz_Node> PetLeaves(Wz_Node node, int depth)
    {
        yield return node;
        if (depth <= 0) yield break;
        if (!PetIsContainer(node)) yield break;
        foreach (var child in node.Nodes)
        {
            if (child.Value is Wz_Png || child.Value is Wz_Uol || PetIsContainer(child))
            {
                foreach (var leaf in PetLeaves(child, depth - 1)) yield return leaf;
            }
        }
    }

    /// <summary>一帧里挑面积最大的可解码画布（同 旧实现 _frame_meta）。</summary>
    private static PetCanvas? PetFrameMeta(Wz_Node frame, string pack, Wz_Node? img)
    {
        PetCanvas? best = null;
        foreach (var leaf in PetLeaves(frame, 3))
        {
            var candidate = leaf;
            if (candidate.Value is Wz_Uol uol)
            {
                var target = uol.HandleUol(candidate);
                if (target is null) continue;
                candidate = target;
            }
            if (candidate.Value is not Wz_Png && PetLink(candidate) is null) continue;
            var meta = PetCanvasMeta(candidate, pack, img);
            if (meta is null) continue;
            if (best is null || meta.W * meta.H > best.W * best.H) best = meta;
        }
        return best;
    }

    private static List<PetCanvas> PetFramesOf(Wz_Node node, string pack, Wz_Node? img)
    {
        var frames = new List<PetCanvas>();
        foreach (var child in node.Nodes)
        {
            var meta = PetFrameMeta(child, pack, img);
            if (meta is not null) frames.Add(meta);
        }
        return frames;
    }

    private static List<(string Name, int Frames)> PetAnimationsOf(Wz_Node img)
    {
        var animations = new List<(string Name, int Frames)>();
        foreach (var child in img.Nodes)
        {
            if (child.Value is Wz_Png)
            {
                if (PetCanvasMeta(child, "Mob", img) is not null)
                {
                    animations.Add((child.Text, 1));
                }
                continue;
            }
            var frames = PetFramesOf(child, "Mob", img);
            if (frames.Count > 0) animations.Add((child.Text, frames.Count));
        }
        return animations;
    }

    // ── 怪物定位 ─────────────────────────────────────────────────────────
    private static (string Rel, Wz_Node Img, List<(string Name, int Frames)> Anims) PetFindMob(string id)
    {
        PetEnsureLoaded();
        var tried = new List<string>();
        var padded = id.PadLeft(7, '0');
        foreach (var rel in new[] { $"{padded}.img", $"_Canvas/{padded}.img", $"{id}.img" })
        {
            Wz_Node? img;
            try
            {
                img = PetParsedImage("Mob", rel);
            }
            catch (Exception ex)
            {
                tried.Add($"{rel}: {ex.GetType().Name}");
                continue;
            }
            if (img is null)
            {
                tried.Add($"{rel}: 不存在");
                continue;
            }
            var animations = PetAnimationsOf(img);
            if (animations.Count > 0) return (rel, img, animations);
            tried.Add($"{rel}: 无动作");
        }
        throw new FileNotFoundException(
            $"怪物 {id} 在本地 WZ 里没有可用动作（试过 {string.Join("; ", tried)}）");
    }

    private static Dictionary<string, Wz_Node> PetMobTable()
    {
        if (_petMobTable is not null) return _petMobTable;
        PetEnsureLoaded();
        var table = new Dictionary<string, Wz_Node>(StringComparer.Ordinal);
        var img = PetParsedImage("String", "Mob.img");
        if (img is not null)
        {
            foreach (var child in img.Nodes) table[child.Text] = child;
        }
        _petMobTable = table;
        return table;
    }

    private static string? PetMobName(string id)
    {
        try
        {
            var table = PetMobTable();
            Wz_Node? entry = null;
            if (!table.TryGetValue(id.TrimStart('0'), out entry)
                && !table.TryGetValue(id, out entry))
            {
                return null;
            }
            return entry?.FindNodeByPath("name", true)?.Value?.ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pet] 名字查询失败: {ex.Message}");
            return null;
        }
    }

    // ── 动作名兜底（照搬 AnimService 语义）────────────────────────────────
    private static string? PetPickAction(string want, List<(string Name, int Frames)> available)
    {
        var names = available.Select(a => a.Name).ToList();
        var lower = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names) lower[name.ToLowerInvariant()] = name;

        var aliases = PetActionAliases.TryGetValue(want, out var alias) ? alias : new[] { want };
        foreach (var candidate in aliases)
        {
            if (lower.TryGetValue(candidate.ToLowerInvariant(), out var hit)) return hit;
        }
        foreach (var name in names)
        {
            if (name.ToLowerInvariant().StartsWith(want.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return name;
            }
        }
        if (PetActionFallbacks.TryGetValue(want, out var fallback) && fallback != want)
        {
            var got = PetPickAction(fallback, available);
            if (got is not null) return got;
        }
        if (want == "stand")
        {
            foreach (var alt in new[] { "move", "fly", "walk", "walk1" })
            {
                if (lower.TryGetValue(alt.ToLowerInvariant(), out var hit)) return hit;
            }
        }
        return null;
    }

    // ── 雪碧图合成 ───────────────────────────────────────────────────────
    private static int PetMulDiv255(int a, int b)
    {
        var tmp = a * b + 0x80;
        return (tmp + (tmp >> 8)) >> 8;
    }

    /// <summary>
    /// Pillow 的 paste(im, pos, im) 用自身 alpha 当蒙版，会把 alpha 平方、RGB 预乘。
    /// 前端读的是非预乘 RGBA，不照抄这一步就会有半透明边缘色差。
    /// </summary>
    private static void PetPasteMask(Bitmap32 dst, Bitmap32 src, int px, int py)
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
                var mask = src.Px[si + 3];
                if (mask == 0) continue;
                for (var c = 0; c < 3; c++)
                {
                    dst.Px[di + c] = (byte)PetMulDiv255(src.Px[si + c], mask);
                }
                dst.Px[di + 3] = (byte)PetMulDiv255(mask, mask);
            }
        }
    }

    /// <summary>
    /// Pillow alpha_composite 的逐像素等价实现。Blit 的 over 分支先乘后除，
    /// 与 Pillow「先截断 coef2 再相乘」差 1 个色阶，纸娃娃边缘会看出细线。
    /// </summary>
    private static void PetBlitOver(Bitmap32 dst, Bitmap32 src, int px, int py)
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
                if (sa == 0) continue;
                var da = dst.Px[di + 3];
                if (sa == 255 || da == 0)
                {
                    dst.Px[di] = src.Px[si];
                    dst.Px[di + 1] = src.Px[si + 1];
                    dst.Px[di + 2] = src.Px[si + 2];
                    dst.Px[di + 3] = (byte)(sa == 255 ? 255 : sa);
                    continue;
                }
                var coef2 = da * (255 - sa);
                var denominator = sa * 255 + coef2;
                if (denominator == 0) continue;
                for (var c = 0; c < 3; c++)
                {
                    var numerator = src.Px[si + c] * sa * 255 + dst.Px[di + c] * coef2;
                    dst.Px[di + c] = (byte)((numerator + denominator / 2) / denominator);
                }
                dst.Px[di + 3] = (byte)((denominator + 127) / 255);
            }
        }
    }

    private static (Bitmap32 Sheet, JsonObject Meta) PetCompose(List<PetCanvas> frames, string originFallback)
    {
        var imgs = new List<Bitmap32>();
        var origins = new List<(int X, int Y)>();
        var delays = new List<int>();
        foreach (var frame in frames)
        {
            var img = PetDecode(frame.Png);
            // C# 的实体路径会跳过 null / 1×1 退化帧，桌宠沿用同一口径。
            if (img.W <= 1 || img.H <= 1) continue;

            int ox, oy;
            if (frame.Origin is { } origin)
            {
                ox = origin.X;
                oy = origin.Y;
            }
            else if (originFallback == "topleft")
            {
                ox = 0;
                oy = 0;
            }
            else
            {
                ox = img.W / 2;
                oy = img.H / 2;
            }

            var delay = frame.Delay ?? PetDefaultFrameDelay;
            delay = Math.Clamp(delay, PetMinFrameDelay, PetMaxFrameDelay);

            imgs.Add(img);
            origins.Add((ox, oy));
            delays.Add(delay);
        }
        if (imgs.Count == 0) throw new InvalidOperationException("没有可解码的帧");

        var maxL = origins.Max(o => o.X);
        var maxT = origins.Max(o => o.Y);
        var maxR = imgs.Select((im, i) => im.W - origins[i].X).Max();
        var maxB = imgs.Select((im, i) => im.H - origins[i].Y).Max();
        var cellW = Math.Max(1, maxL + maxR);
        var cellH = Math.Max(1, maxT + maxB);

        var sheet = new Bitmap32(cellW * imgs.Count, cellH);
        for (var i = 0; i < imgs.Count; i++)
        {
            var (ox, oy) = origins[i];
            PetPasteMask(sheet, imgs[i], i * cellW + (maxL - ox), maxT - oy);
        }

        var sizes = new JsonArray();
        for (var i = 0; i < imgs.Count; i++)
        {
            var (ox, oy) = origins[i];
            sizes.Add(new JsonObject
            {
                ["w"] = imgs[i].W,
                ["h"] = imgs[i].H,
                ["origin"] = new JsonArray(ox, oy),
            });
        }
        var framesJson = new JsonArray();
        foreach (var delay in delays) framesJson.Add(new JsonObject { ["delay"] = delay });

        var meta = new JsonObject
        {
            ["cell"] = new JsonObject { ["w"] = cellW, ["h"] = cellH },
            ["anchor"] = new JsonObject { ["x"] = maxL, ["y"] = maxT },
            ["cols"] = imgs.Count,
            ["rows"] = 1,
            ["count"] = imgs.Count,
            ["frames"] = framesJson,
            ["totalMs"] = delays.Sum(),
            ["sizes"] = sizes,
        };
        return (sheet, meta);
    }

    private static string PetCacheKey(string id, string action, string originFallback)
    {
        var raw = $"{id}|{action}|{originFallback}|{_dataDir}|{_version}|{PetImplTag}";
        var digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..10];
        return $"pet_{id.PadLeft(7, '0')}_{action}_{originFallback}_{digest}";
    }

    private static JsonObject PetBuildSheet(
        string id, string rel, Wz_Node img, string action, string originFallback,
        Dictionary<string, JsonObject> cache)
    {
        if (cache.TryGetValue(action, out var hit)) return hit;

        var outDir = PetOutDir();
        var stem = PetCacheKey(id, action, originFallback);
        var pngPath = Path.Combine(outDir, stem + ".png");
        var metaPath = Path.Combine(outDir, stem + ".json");

        if (File.Exists(pngPath) && File.Exists(metaPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(metaPath)) is JsonObject cached)
                {
                    cached["cached"] = true;
                    cache[action] = cached;
                    return cached;
                }
            }
            catch
            {
                // 缓存 JSON 坏了就当没命中，重新合成总比把坏数据回给前端强。
            }
        }

        var node = img.FindNodeByPath(action.Replace('/', '\\'), true)
            ?? throw new FileNotFoundException($"{rel} 里没有动作 {action}");
        List<PetCanvas> frames;
        if (PetIsContainer(node))
        {
            frames = PetFramesOf(node, "Mob", img);
        }
        else
        {
            var single = PetCanvasMeta(node, "Mob", img);
            frames = single is null ? new List<PetCanvas>() : new List<PetCanvas> { single };
        }
        if (frames.Count == 0) throw new FileNotFoundException($"{rel} 的 {action} 没有帧");

        var (sheet, meta) = PetCompose(frames, originFallback);
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(pngPath, Png.Encode(sheet));

        var result = new JsonObject
        {
            ["file"] = stem + ".png",
            ["action"] = action,
            ["originFallback"] = originFallback,
        };
        foreach (var kv in meta) result[kv.Key] = kv.Value?.DeepClone();
        File.WriteAllText(metaPath, result.ToJsonString());
        cache[action] = result;
        return result;
    }

    // ── 命令：pet ────────────────────────────────────────────────────────
    private static JsonNode CmdPet(JsonObject args)
    {
        var id = PetStr(args["id"]);
        if (id.Length == 0) id = "1210102";

        var want = PetStrList(args["actions"]);
        if (want.Count == 0) want = PetDefaultActions.ToList();

        var originFallback = PetStr(args["originFallback"]);
        if (originFallback is not ("center" or "topleft")) originFallback = "center";

        var (rel, img, animations) = PetFindMob(id);
        var cache = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var resolved = new JsonObject();
        var missing = new JsonArray();

        foreach (var requested in want)
        {
            var real = PetPickAction(requested, animations);
            if (real is null)
            {
                missing.Add(requested);
                continue;
            }
            JsonObject sheet;
            try
            {
                sheet = PetBuildSheet(id, rel, img, real, originFallback, cache);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pet] 合成 {id}/{requested} 失败：{ex.Message}");
                missing.Add(requested);
                continue;
            }
            var entry = PetCloneObject(sheet);
            entry["requested"] = requested;
            entry["resolved"] = real;
            entry["exact"] = string.Equals(real, requested, StringComparison.OrdinalIgnoreCase);
            resolved[requested] = entry;
        }

        var actions = new JsonArray();
        foreach (var animation in animations)
        {
            actions.Add(new JsonObject { ["name"] = animation.Name, ["frames"] = animation.Frames });
        }
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = PetMobName(id),
            ["path"] = $"Mob/{rel}",
            ["actions"] = actions,
            ["originFallback"] = originFallback,
            ["defaultDelay"] = PetDefaultFrameDelay,
            ["anims"] = resolved,
            ["missing"] = missing,
            ["outDir"] = PetOutDir(),
        };
    }

    private static JsonNode CmdPetInfo(JsonObject args)
    {
        var id = PetStr(args["id"]);
        if (id.Length == 0) id = "1210102";
        var (rel, _img, animations) = PetFindMob(id);
        var actions = new JsonArray();
        foreach (var animation in animations)
        {
            actions.Add(new JsonObject { ["name"] = animation.Name, ["frames"] = animation.Frames });
        }
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = PetMobName(id),
            ["path"] = $"Mob/{rel}",
            ["actions"] = actions,
            ["source"] = rel.Contains('/') ? "canvas" : "packs",
        };
    }

    private static JsonNode CmdPetSearch(JsonObject args)
    {
        var query = PetStr(args["query"]).Trim();
        var limit = PetIntArg(args, "limit", 40);
        if (limit <= 0) limit = 40;
        var table = PetMobTable();
        var q = query.ToLowerInvariant();

        var hits = new List<(string Id, string Name)>();
        foreach (var (mid, entry) in table)
        {
            var name = entry.FindNodeByPath("name", true)?.Value?.ToString() ?? "";
            if (q.Length == 0 || name.ToLowerInvariant().Contains(q, StringComparison.Ordinal)
                || q == mid || q == mid.TrimStart('0'))
            {
                hits.Add((mid, name));
                if (hits.Count >= limit * 4) break;
            }
        }
        var ordered = hits
            .OrderBy(h => h.Id == query || h.Name == query ? 0 : 1)
            .ThenBy(h => h.Id.Length)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var arr = new JsonArray();
        foreach (var hit in ordered)
        {
            arr.Add(new JsonObject { ["id"] = hit.Id, ["name"] = hit.Name });
        }
        return new JsonObject
        {
            ["query"] = query,
            ["total"] = table.Count,
            ["count"] = hits.Count,
            ["hits"] = arr,
        };
    }

    private static JsonNode CmdPetPresets(JsonObject args)
    {
        var ids = new[]
        {
            "1210102", "1210100", "1210101", "100100", "130100", "210100",
            "300100", "4230101", "5100000", "8140700", "9300018", "9400203",
            "9500390", "9602538", "8880140", "1110101", "2230101", "3210100",
        };
        var table = PetMobTable();
        var presets = new JsonArray();
        foreach (var mid in ids)
        {
            var key = mid.TrimStart('0');
            if (!table.TryGetValue(key, out var entry) && !table.TryGetValue(mid, out entry)) continue;
            var name = entry.FindNodeByPath("name", true)?.Value?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            presets.Add(new JsonObject { ["id"] = key, ["name"] = name });
        }
        return new JsonObject
        {
            ["presets"] = presets,
            ["note"] = "编号取自经典怪物；查不到名字的会被过滤掉",
        };
    }

    private static JsonNode CmdPetStats(JsonObject args)
    {
        var fingerprint = new JsonObject();
        try
        {
            var location = typeof(Program).Assembly.Location;
            if (File.Exists(location))
            {
                var hash = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(location)))
                    .ToLowerInvariant()[..12];
                fingerprint[Path.GetFileName(location)] = hash;
            }
        }
        catch
        {
            // 指纹只用于判断常驻进程是否跑旧代码，取不到不影响命令结果。
        }
        return new JsonObject
        {
            ["data_dir"] = _dataDir,
            ["region"] = _region,
            ["version"] = _version,
            ["out_dir"] = PetOutDir(),
            ["engine"] = "wzlib",
            ["opens"] = _petOpens,
            ["decodes"] = _petDecodes,
            ["fingerprint"] = fingerprint,
            ["pid"] = Environment.ProcessId,
        };
    }

    // ── 纸娃娃 ───────────────────────────────────────────────────────────
    private static readonly string[] PetZmap =
    {
        "Bd", "Hd", "Hr", "Fc", "At", "Af", "Am", "Ae", "As", "Ay",
        "Cp", "Ri", "Gv", "Wp", "Si", "So", "Pn", "Ws", "Ma", "Wg",
        "Sr", "Tm", "Sd",
        "backTamingMobMid",
        "backMobEquipUnderSaddle",
        "backSaddle",
        "backMobEquipMid",
        "backTamingMobFront",
        "backMobEquipFront",
        "mobEquipRear",
        "tamingMobRear",
        "saddleRear",
        "characterEnd",
        "backWeaponEffectUnder",
        "backWeapon",
        "backWeaponEffectOver",
        "backHairBelowHead",
        "backShieldBelowBody",
        "backMailChestAccessory",
        "backCapAccessory",
        "backAccessoryFace",
        "backAccessoryEar",
        "backBody",
        "backGlove",
        "backGloveWrist",
        "backWeaponOverGloveEffectUnder",
        "backWeaponOverGlove",
        "backWeaponOverGloveEffectOver",
        "backMailChestBelowPants",
        "backPantsBelowShoes",
        "backShoesBelowPants",
        "backPants",
        "backShoes",
        "backPantsOverShoesBelowMailChest",
        "backMailChest",
        "backPantsOverMailChest",
        "backMailChestOverPants",
        "backHead",
        "backAccessoryFaceOverHead",
        "backAccessoryOverHead",
        "backCape",
        "backHairBelowCap",
        "backHairBelowCapNarrow",
        "backHairBelowCapWide",
        "backWeaponOverHeadEffectUnder",
        "backWeaponOverHead",
        "backWeaponOverHeadEffectOver",
        "backCap",
        "backHair",
        "backCapOverHair",
        "backShield",
        "backWeaponOverShieldEffectUnder",
        "backWeaponOverShield",
        "backWeaponOverShieldEffectOver",
        "backWing",
        "backHairOverCape",
        "weaponBelowBodyEffectUnder",
        "weaponBelowBody",
        "weaponBelowBodyEffectOver",
        "hairBelowBody",
        "capeBelowBody",
        "shieldBelowBody",
        "capAccessoryBelowBody",
        "gloveBelowBody",
        "gloveWristBelowBody",
        "body",
        "gloveOverBody",
        "mailChestBelowPants",
        "pantsBelowShoes",
        "shoes",
        "pants",
        "mailChestOverPants",
        "shoesOverPants",
        "pantsOverShoesBelowMailChest",
        "shoesTop",
        "mailChest",
        "pantsOverMailChest",
        "mailChestOverHighest",
        "gloveWristOverBody",
        "mailChestTop",
        "capeBelowWeapon",
        "weaponOverBodyEffectUnder",
        "weaponOverBody",
        "weaponOverBodyEffectOver",
        "armBelowHead",
        "mailArmBelowHead",
        "armBelowHeadOverMailChest",
        "gloveBelowHead",
        "mailArmBelowHeadOverMailChest",
        "gloveWristBelowHead",
        "weaponOverArmBelowHeadEffectUnder",
        "weaponOverArmBelowHead",
        "weaponOverArmBelowHeadEffectOver",
        "shield",
        "weaponEffectUnder",
        "weapon",
        "weaponEffectOver",
        "arm",
        "hand",
        "glove",
        "mailArm",
        "gloveWrist",
        "cape",
        "head",
        "hairShade",
        "accessoryFaceBelowFace",
        "accessoryEyeBelowFace",
        "face",
        "accessoryFaceOverFaceBelowCap",
        "capBelowAccessory",
        "accessoryEar",
        "capAccessoryBelowAccFace",
        "accessoryFace",
        "accessoryEyeShadow",
        "accessoryEye",
        "capeOverFace",
        "hair",
        "cap",
        "capAccessory",
        "accessoryEyeOverCap",
        "hairOverHead",
        "accessoryOverHair",
        "accessoryEarOverHair",
        "capOverHair",
        "weaponBelowArmEffectUnder",
        "weaponBelowArm",
        "weaponBelowArmEffectOver",
        "armOverHairBelowWeapon",
        "mailArmOverHairBelowWeapon",
        "armOverHair",
        "gloveBelowMailArm",
        "mailArmOverHair",
        "gloveWristBelowMailArm",
        "weaponOverArmEffectUnder",
        "weaponOverArm",
        "weaponOverArmEffectOver",
        "handBelowWeapon",
        "gloveBelowWeapon",
        "gloveWristBelowWeapon",
        "shieldOverHair",
        "weaponOverHandEffectUnder",
        "weaponOverHand",
        "weaponOverHandEffectOver",
        "handOverHair",
        "gloveOverHair",
        "gloveWristOverHair",
        "weaponOverGloveEffectUnder",
        "weaponOverGlove",
        "weaponOverGloveEffectOver",
        "capeOverHead",
        "weaponWristOverGloveEffectUnder",
        "weaponWristOverGlove",
        "weaponWristOverGloveEffectOver",
        "emotionOverBody",
        "characterStart",
        "backSaddleFront",
        "saddleMid",
        "tamingMobMid",
        "mobEquipUnderSaddle",
        "saddleFront",
        "mobEquipMid",
        "tamingMobFront",
        "mobEquipFront",
    };

    private static readonly HashSet<string> PetHeadAnchors = new(StringComparer.Ordinal)
    {
        "brow", "neck", "handMove",
    };

    private static readonly string[] PetSupportedPoses =
    {
        "stand1", "stand2", "walk1", "walk2", "alert", "jump", "prone", "proneStab",
        "sit", "fly", "ladder", "rope", "heal",
        "swingO1", "swingO2", "swingO3", "swingT1", "swingT2", "swingT3",
        "stabO1", "stabO2", "stabT1", "stabT2", "shoot1", "shoot2", "shootF", "dead",
    };

    private static readonly Dictionary<string, string[]> PetPoseAliases = new(StringComparer.Ordinal)
    {
        ["stand"] = new[] { "stand1", "stand2" },
        ["move"] = new[] { "walk1", "walk2", "run1" },
        ["hit1"] = new[] { "hit1", "hit0" },
        ["fly"] = new[] { "fly", "jump" },
        ["die"] = new[] { "die1" },
        ["jump"] = new[] { "jump" },
        ["sit"] = new[] { "sit" },
        ["ladder"] = new[] { "ladder" },
    };

    private sealed class PetPlacement
    {
        public PetPlacement(string equipId, string category, string name, Wz_Node meta, Wz_Png png,
            (int X, int Y) origin, Dictionary<string, (int X, int Y)> anchors, string? zSlot)
        {
            EquipId = equipId;
            Category = category;
            Name = name;
            Meta = meta;
            Png = png;
            Origin = origin;
            Anchors = anchors;
            ZSlot = zSlot;
        }

        public string EquipId { get; }
        public string Category { get; }
        public string Name { get; }
        public Wz_Node Meta { get; }
        public Wz_Png Png { get; }
        public (int X, int Y) Origin { get; }
        public Dictionary<string, (int X, int Y)> Anchors { get; }
        public string? ZSlot { get; }
        public (int X, int Y)? TopLeft { get; set; }
        public int ZIndex { get; set; }
        public int W => Png.Width;
        public int H => Png.Height;
    }

    private static readonly Dictionary<int, string> PetCategoryById = BuildPetCategories();

    private static Dictionary<int, string> BuildPetCategories()
    {
        var map = new Dictionary<int, string>
        {
            [0] = "Body", [1] = "Head",
            [2] = "Face", [5] = "Face",
            [3] = "Hair", [4] = "Hair", [6] = "Hair",
            [100] = "Cap", [101] = "FaceAcc", [102] = "Glass", [103] = "Earring",
            [104] = "Coat", [105] = "Longcoat", [106] = "Pants", [107] = "Shoes",
            [108] = "Glove", [109] = "Shield", [110] = "Cape", [170] = "Weapon",
        };
        for (var n = 121; n <= 160; n++) map[n] = "Weapon";
        return map;
    }

    private static string? PetCategory(string equipId)
    {
        if (equipId.Length == 0 || !equipId.All(char.IsDigit)) return null;
        return int.TryParse(equipId, out var value)
            ? PetCategoryById.GetValueOrDefault(value / 10000)
            : null;
    }

    private static string PetCategoryDir(string category) => category switch
    {
        "Body" or "Head" => "",
        "Accessory" => "Accessory",
        "FaceAcc" or "Glass" or "Earring" => "Accessory",
        _ => category,
    };

    private static Wz_Node? PetOpenPart(string equipId)
    {
        var category = PetCategory(equipId);
        if (category is null) return null;
        var root = FindTop("Character");
        if (root is null) return null;
        var dir = PetCategoryDir(category);
        foreach (var id in new[] { equipId, equipId.PadLeft(8, '0') })
        {
            var rel = dir.Length == 0 ? $"{id}.img" : $"{dir}/{id}.img";
            var node = PetFindImage(root, rel);
            if (node is not null) return node;
        }
        return null;
    }

    private static string PetDetermineAnchor(PetPlacement placement)
    {
        foreach (var name in new[] { "navel", "neck", "hand", "brow", "handMove" })
        {
            if (placement.Anchors.ContainsKey(name)) return name;
        }
        return placement.Category switch
        {
            "Body" => "navel",
            "Head" => "neck",
            "Hair" or "Face" => "brow",
            "Glove" => "hand",
            "Weapon" => "hand",
            "FaceAcc" or "Glass" or "Earring" => "brow",
            _ => "navel",
        };
    }

    private static List<string> PetFramePaths(string category, string pose, int frame)
    {
        var path = $"{pose}/{frame}";
        return category switch
        {
            "Body" => new List<string> { path },
            "Head" => new List<string> { path, "front" },
            "Hair" => new List<string> { path, "default" },
            "Face" => new List<string> { "default" },
            "Cap" => new List<string> { path, "default" },
            "FaceAcc" or "Glass" or "Earring" => new List<string> { "default" },
            _ => new List<string> { path },
        };
    }

    private static List<PetPlacement> PetCollectPart(
        Wz_Node img, string category, string equipId, string pose, int frame,
        string earType, HashSet<string> hideHair)
    {
        var result = new List<PetPlacement>();
        var seenCanvases = new HashSet<Wz_Node>();
        var seenSlots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in PetFramePaths(category, pose, frame))
        {
            var node = img.FindNodeByPath(path.Replace('/', '\\'), true);
            if (node?.Value is Wz_Uol uol) node = uol.HandleUol(node);
            if (node is null || node.Nodes.Count == 0) continue;

            var before = result.Count;
            foreach (var child in node.Nodes)
            {
                if (category == "Head" && child.Text != "head" && child.Text != earType) continue;
                if (category == "Hair" && hideHair.Contains(child.Text)) continue;

                var target = child;
                if (target.Value is Wz_Uol targetUol)
                {
                    var resolved = targetUol.HandleUol(target);
                    if (resolved is null) continue;
                    target = resolved;
                }
                if (target.Value is not Wz_Png && PetLink(target) is null) continue;
                if (seenCanvases.Contains(target)) continue;

                var zSlot = target.FindNodeByPath("z", true)?.Value as string;
                if (zSlot is not null && seenSlots.Contains(zSlot)) continue;

                var pixels = target;
                var link = PetLink(target);
                if (link is not null)
                {
                    var resolved = PetResolveRef(link, "Character", img);
                    if (resolved is null) continue;
                    pixels = resolved;
                }
                if (pixels.Value is not Wz_Png png || png.Width <= 1 && png.Height <= 1) continue;

                var origin = PetVec(target.FindNodeByPath("origin", true)) ?? (0, 0);
                var anchors = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
                var mapNode = target.FindNodeByPath("map", true);
                if (mapNode is not null)
                {
                    foreach (var mapChild in mapNode.Nodes)
                    {
                        if (PetVec(mapChild) is { } vec) anchors[mapChild.Text] = vec;
                    }
                }
                seenCanvases.Add(target);
                if (zSlot is not null) seenSlots.Add(zSlot);
                result.Add(new PetPlacement(equipId, category, child.Text, target, png, origin, anchors, zSlot));
            }
            if (result.Count > before) break;
        }
        return result;
    }

    private static bool PetIsCashWeapon(string equipId) =>
        equipId.StartsWith("0170", StringComparison.Ordinal);

    private static Wz_Node PetRenderRoot(Wz_Node img, string category, string equipId, string pose)
    {
        // 现金武器的动作挂在数字子节点里；没做逐 pose 找家，直接退回图片根。
        if (category != "Weapon" || !PetIsCashWeapon(equipId)) return img;
        var numeric = img.Nodes
            .Where(n => n.Text.All(char.IsDigit) && n.Nodes.Count > 0)
            .OrderBy(n => int.TryParse(n.Text, out var value) ? value : int.MaxValue)
            .ToList();
        foreach (var child in numeric)
        {
            var home = child.FindNodeByPath(pose, true);
            if (home is not null) return child;
        }
        return numeric.Count > 0 ? numeric[0] : img;
    }

    private static (bool HideFull, HashSet<string> HideSet, HashSet<string> CapTokens) PetCapHairFilter(
        List<string> equips)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var capId = equips.FirstOrDefault(e => PetCategory(e) == "Cap");
        if (capId is null) return (false, new HashSet<string>(), tokens);
        var img = PetOpenPart(capId);
        var vslot = img?.FindNodeByPath("info", true)?.FindNodeByPath("vslot", true)?.Value as string;
        for (var i = 0; i + 2 <= (vslot?.Length ?? 0); i += 2)
        {
            tokens.Add(vslot!.Substring(i, 2));
        }
        if (tokens.Contains("H2")) return (true, new HashSet<string>(), tokens);
        if (tokens.Contains("H1") && tokens.Contains("H3"))
        {
            return (false, new HashSet<string>(StringComparer.Ordinal)
            {
                "hairOverHead", "backHair", "hairBelowBody", "backHairBelowCap",
            }, tokens);
        }
        if (tokens.Contains("H1"))
        {
            return (false, new HashSet<string>(StringComparer.Ordinal)
            {
                "hairOverHead", "backHair",
            }, tokens);
        }
        return (false, new HashSet<string>(), tokens);
    }

    private static void PetRegisterAnchors(
        PetPlacement placement, Dictionary<string, (int X, int Y)> world, bool overwrite)
    {
        if (placement.TopLeft is not { } topLeft) return;
        foreach (var (name, vec) in placement.Anchors)
        {
            if (!overwrite && world.ContainsKey(name)) continue;
            world[name] = (
                topLeft.X + placement.Origin.X + vec.X,
                topLeft.Y + placement.Origin.Y + vec.Y);
        }
    }

    private static void PetRefreshDynamic(
        PetPlacement placement, Dictionary<string, (int X, int Y)> world, HashSet<string> seen)
    {
        if (placement.TopLeft is not { } topLeft) return;
        foreach (var (name, vec) in placement.Anchors)
        {
            if (name is not ("hand" or "lHand") || seen.Contains(name)) continue;
            world[name] = (
                topLeft.X + placement.Origin.X + vec.X,
                topLeft.Y + placement.Origin.Y + vec.Y);
            seen.Add(name);
        }
    }

    private static int PetZIndex(string? slot)
    {
        if (string.IsNullOrEmpty(slot)) return PetZmap.Length;
        var index = Array.IndexOf(PetZmap, slot);
        if (index >= 0) return index;

        var lower = slot.ToLowerInvariant();
        if (lower.StartsWith("back", StringComparison.Ordinal))
        {
            var baseSlot = slot.Length > 4 ? char.ToLowerInvariant(slot[4]) + slot[5..] : slot;
            var baseIndex = Array.IndexOf(PetZmap, baseSlot);
            if (baseIndex >= 0) return Math.Max(1, baseIndex - 5);
            foreach (var (sep, delta) in new[] { ("Below", -1), ("Over", 1) })
            {
                if (!slot.Contains(sep, StringComparison.Ordinal)) continue;
                var tail = slot[(slot.LastIndexOf(sep, StringComparison.Ordinal) + sep.Length)..];
                var sibling = tail.Length > 0 ? "back" + char.ToUpperInvariant(tail[0]) + tail[1..] : "";
                var siblingIndex = Array.IndexOf(PetZmap, sibling);
                if (siblingIndex >= 0) return Math.Max(1, siblingIndex + delta);
            }
            var bodyIndex = Array.IndexOf(PetZmap, "body");
            return bodyIndex >= 0 ? Math.Max(1, bodyIndex - 1) : 1;
        }
        if (lower.StartsWith("cap", StringComparison.Ordinal) && lower.Contains("below", StringComparison.Ordinal))
        {
            var capIndex = Array.IndexOf(PetZmap, "cap");
            if (capIndex >= 0) return Math.Max(1, capIndex - 1);
        }
        foreach (var separator in new[] { "Below", "below" })
        {
            if (!slot.Contains(separator, StringComparison.Ordinal)) continue;
            var tail = slot[(slot.LastIndexOf(separator, StringComparison.Ordinal) + separator.Length)..];
            if (tail.Length == 0) continue;
            var target = char.ToLowerInvariant(tail[0]) + tail[1..];
            var targetIndex = Array.IndexOf(PetZmap, target);
            if (targetIndex >= 0) return Math.Max(1, targetIndex - 1);
        }
        foreach (var separator in new[] { "Over", "over" })
        {
            if (!slot.Contains(separator, StringComparison.Ordinal)) continue;
            var tail = slot[(slot.LastIndexOf(separator, StringComparison.Ordinal) + separator.Length)..];
            if (tail.Length == 0) continue;
            var target = char.ToLowerInvariant(tail[0]) + tail[1..];
            var targetIndex = Array.IndexOf(PetZmap, target);
            if (targetIndex >= 0) return Math.Min(PetZmap.Length - 1, targetIndex + 1);
        }
        return PetZmap.Length - 1;
    }

    private static List<PetPlacement> PetBuildPlacements(
        List<string> equips, string pose, string earType, int frame,
        Dictionary<string, (int X, int Y)>? frozen,
        out Dictionary<string, (int X, int Y)> anchors)
    {
        anchors = frozen is null
            ? new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal)
            : new Dictionary<string, (int X, int Y)>(frozen, StringComparer.Ordinal);

        var (hideFull, hideSet, capTokens) = PetCapHairFilter(equips);
        var hasCape = equips.Any(e => PetCategory(e) == "Cape");
        var placements = new List<PetPlacement>();
        foreach (var equipId in equips)
        {
            var category = PetCategory(equipId);
            if (category is null) continue;
            if (category == "Hair" && hideFull) continue;
            var img = PetOpenPart(equipId);
            if (img is null) continue;
            var root = PetRenderRoot(img, category, equipId, pose);
            placements.AddRange(PetCollectPart(root, category, equipId, pose, frame, earType, hideSet));
        }
        if (placements.Count == 0) return placements;

        placements = placements
            .OrderBy(p => p.Category == "Body" ? 0 : p.Category == "Head" ? 1 : 2)
            .ThenBy(p => p.Category == "Body" ? (p.Name == "body" ? 0 : 1)
                : p.Category == "Head" ? (p.Name == "head" ? 0 : 1) : 0)
            .ToList();

        var bodyAnchored = false;
        var bodyNavel = (X: 0, Y: 0);
        var seenDynamic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in placements)
        {
            if (placement.Category == "Body" && placement.Name == "body" && !bodyAnchored)
            {
                var navel = placement.Anchors.GetValueOrDefault("navel");
                placement.TopLeft = (-placement.Origin.X - navel.X, -placement.Origin.Y - navel.Y);
                bodyNavel = navel;
                if (frozen is null) PetRegisterAnchors(placement, anchors, true);
                else PetRefreshDynamic(placement, anchors, seenDynamic);
                bodyAnchored = true;
                continue;
            }

            var anchorName = PetDetermineAnchor(placement);
            var mapPoint = placement.Anchors.GetValueOrDefault(anchorName);
            if (anchors.TryGetValue(anchorName, out var world))
            {
                placement.TopLeft = (
                    world.X - placement.Origin.X - mapPoint.X,
                    world.Y - placement.Origin.Y - mapPoint.Y);
            }
            else if (anchorName == "handMove")
            {
                // 没有上游 handMove 时以身体为枢轴：这是客户端对副手的定义方式。
                placement.TopLeft = (-placement.Origin.X - bodyNavel.X, -placement.Origin.Y - bodyNavel.Y);
            }
            else
            {
                placement.TopLeft = (-placement.Origin.X, -placement.Origin.Y);
            }

            if (frozen is null) PetRegisterAnchors(placement, anchors, false);
            else if (placement.Category == "Body") PetRefreshDynamic(placement, anchors, seenDynamic);
        }

        var capCoversHair = hideFull || hideSet.Count > 0;
        var demote = PetCapDemoteSet(placements);
        foreach (var placement in placements)
        {
            placement.ZIndex = demote.Contains(placement)
                ? PetZIndex("body") - 1
                : PetEffectiveZ(placement, pose, hasCape, capCoversHair, capTokens);
        }
        return placements.OrderBy(p => p.ZIndex).ToList();
    }

    private static HashSet<PetPlacement> PetCapDemoteSet(List<PetPlacement> placements)
    {
        var demote = new HashSet<PetPlacement>();
        foreach (var group in placements.Where(p => p.Category == "Cap").GroupBy(p => p.EquipId))
        {
            var front = group
                .Where(p => p.ZSlot is ("cap" or "capOverHair" or "capAccessory") && p.TopLeft is not null)
                .OrderByDescending(p => p.W * p.H)
                .FirstOrDefault();
            if (front is null) continue;
            var frontArea = front.W * front.H;
            foreach (var placement in group)
            {
                if (ReferenceEquals(placement, front) || placement.TopLeft is null) continue;
                if (placement.ZSlot is null || !placement.ZSlot.StartsWith("cap", StringComparison.Ordinal)) continue;
                if (!placement.ZSlot.Contains("Below", StringComparison.Ordinal)) continue;
                if (placement.ZSlot == "capAccessoryBelowBody")
                {
                    demote.Add(placement);
                    continue;
                }
                if (frontArea >= placement.W * placement.H
                    && placement.TopLeft.Value.Y >= front.TopLeft!.Value.Y)
                {
                    demote.Add(placement);
                }
            }
        }
        return demote;
    }

    private static readonly Dictionary<string, (string Target, string? Token)> PetOverCapRemap =
        new(StringComparer.Ordinal)
        {
            ["accessoryEyeOverCap"] = ("accessoryEye", "Ay"),
            ["accessoryFaceOverCap"] = ("accessoryFace", "Af"),
            ["accessoryFaceUpperOverCap"] = ("accessoryFace", "Af"),
            ["cap"] = ("capBelowHairOverHead", null),
        };

    private static int PetEffectiveZ(
        PetPlacement placement, string pose, bool hasCape, bool capCoversHair, HashSet<string> capTokens)
    {
        var slot = placement.ZSlot;
        if (slot is null) return PetZIndex(slot);

        if (pose == "stand1" && placement.Category == "Weapon" && slot == "weaponOverArm")
        {
            slot = "weapon";
        }
        else if (pose is "prone" or "proneStab" or "shoot1" or "shoot2" or "shootF" or "swingT1" or "stabO1" or "stabO2")
        {
            if (placement.Category == "Weapon" && slot is ("weapon" or "weaponBelowArm")) slot = "weaponOverHand";
            else if (placement.Category == "Body" && slot is ("arm" or "armOverHair")) slot = "handOverHair";
        }
        else if (pose is "swingT2" or "stabT1" or "stabT2")
        {
            if (placement.Category == "Body"
                && slot is ("arm" or "armBelowHead" or "armBelowHeadOverMailChest"))
            {
                slot = "handOverHair";
            }
        }
        else if (pose == "swingT3")
        {
            if (placement.Category == "Body" && slot is ("arm" or "armBelowHeadOverMailChest"))
            {
                slot = "handOverHair";
            }
        }
        else if (pose == "stand2" && placement.Category == "Weapon" && slot == "weaponBelowArm")
        {
            slot = "weaponOverArm";
        }

        if (hasCape && placement.Category == "Hair" && slot == "hairBelowBody") slot = "backHair";

        if (PetOverCapRemap.TryGetValue(slot, out var rule)
            && (!capCoversHair || rule.Token is not null && capTokens.Contains(rule.Token)))
        {
            slot = rule.Target;
        }
        return PetZIndex(slot);
    }

    private static (int X, int Y)? PetBodyAnchor(List<PetPlacement> placements)
    {
        var body = placements.FirstOrDefault(p => p.Category == "Body" && p.Name == "body" && p.TopLeft is not null);
        return body is null ? null : (body.TopLeft!.Value.X + body.W, body.TopLeft!.Value.Y);
    }

    private static Bitmap32 PetFlip(Bitmap32 src)
    {
        var dst = new Bitmap32(src.W, src.H);
        for (var y = 0; y < src.H; y++)
        {
            for (var x = 0; x < src.W; x++)
            {
                var si = (y * src.W + (src.W - 1 - x)) * 4;
                var di = (y * src.W + x) * 4;
                dst.Px[di] = src.Px[si];
                dst.Px[di + 1] = src.Px[si + 1];
                dst.Px[di + 2] = src.Px[si + 2];
                dst.Px[di + 3] = src.Px[si + 3];
            }
        }
        return dst;
    }

    private static List<int> PetPoseFrameDelays(string pose, string bodyId)
    {
        var delays = new List<int>();
        var img = PetOpenPart(bodyId);
        if (img is null) return delays;
        var node = img.FindNodeByPath(pose.Replace('/', '\\'), true);
        if (node?.Value is Wz_Uol uol) node = uol.HandleUol(node);
        if (node is null || node.Nodes.Count == 0) return delays;

        var frames = node.Nodes
            .Where(n => n.Text.Length > 0 && n.Text.All(char.IsDigit))
            .OrderBy(n => int.TryParse(n.Text, out var value) ? value : int.MaxValue);
        foreach (var frame in frames)
        {
            var ms = PetIntNode(frame.FindNodeByPath("delay", true)) ?? 0;
            delays.Add(ms > 0 ? ms : 100);
        }
        return delays;
    }

    private static List<Bitmap32>? PetComposePose(
        List<string> equips, string pose, string earType, bool flip,
        out List<int> delays, out List<PetPlacement>? firstFrame)
    {
        delays = new List<int>();
        firstFrame = null;
        if (!PetSupportedPoses.Contains(pose)) return null;

        var bodyId = equips.FirstOrDefault(e => PetCategory(e) == "Body") ?? "00002000";
        delays = PetPoseFrameDelays(pose, bodyId);
        var frameCount = delays.Count > 0 ? delays.Count : 1;
        var stabilize = pose is "stand1" or "stand2";

        var perFrame = new List<List<PetPlacement>>();
        Dictionary<string, (int X, int Y)>? frozen = null;
        (int X, int Y)? bodyAnchor0 = null;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var placements = PetBuildPlacements(equips, pose, earType, frame, frozen, out var anchors);
            if (!stabilize)
            {
                perFrame.Add(placements);
                continue;
            }
            if (frozen is null)
            {
                frozen = anchors;
                bodyAnchor0 = PetBodyAnchor(placements);
            }
            else
            {
                var bodyAnchorNow = PetBodyAnchor(placements);
                var dx = 0;
                var dy = 0;
                if (bodyAnchor0 is { } a0 && bodyAnchorNow is { } a1)
                {
                    dx = a0.X - a1.X;
                    dy = a0.Y - a1.Y;
                }
                foreach (var placement in placements)
                {
                    if (placement.TopLeft is not { } topLeft) continue;
                    var anchorName = PetDetermineAnchor(placement);
                    if (PetHeadAnchors.Contains(anchorName)) continue;
                    if (dx != 0 || dy != 0) placement.TopLeft = (topLeft.X + dx, topLeft.Y + dy);
                }
            }
            perFrame.Add(placements);
        }
        firstFrame = perFrame.FirstOrDefault();

        var all = perFrame.SelectMany(p => p).Where(p => p.TopLeft is not null).ToList();
        if (all.Count == 0) return Enumerable.Range(0, frameCount).Select(_ => new Bitmap32(1, 1)).ToList();

        var minX = all.Min(p => p.TopLeft!.Value.X);
        var minY = all.Min(p => p.TopLeft!.Value.Y);
        var maxX = all.Max(p => p.TopLeft!.Value.X + p.W);
        var maxY = all.Max(p => p.TopLeft!.Value.Y + p.H);
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);

        var images = new List<Bitmap32>(perFrame.Count);
        foreach (var placements in perFrame)
        {
            var canvas = new Bitmap32(width, height);
            foreach (var placement in placements)
            {
                if (placement.TopLeft is not { } topLeft) continue;
                var layer = PetDecode(placement.Png);
                PetBlitOver(canvas, layer, topLeft.X - minX, topLeft.Y - minY);
            }
            images.Add(flip ? PetFlip(canvas) : canvas);
        }
        return images;
    }

    private static List<int> PetTimelineDelays(string pose, List<int> delays, int frameCount)
    {
        var source = delays.Count > 0 ? delays : new List<int> { 100 };
        List<int> sequence;
        if (pose is "stand1" or "stand2" && source.Count == 3)
        {
            // 呼吸循环走 0→1→2→1，硬切回第 0 帧会看到一次顿挫。
            sequence = new List<int> { 0, 1, 2, 1 };
        }
        else
        {
            sequence = Enumerable.Range(0, source.Count).ToList();
        }
        return sequence
            .Select(i => i < frameCount ? (i < source.Count ? source[i] : 100) : 100)
            .ToList();
    }

    private static List<Bitmap32> PetApplyTimeline(string pose, List<Bitmap32> images, List<int> delays)
    {
        if (images.Count == 0) return images;
        var sequence = pose is "stand1" or "stand2" && delays.Count == 3
            ? new List<int> { 0, 1, 2, 1 }
            : Enumerable.Range(0, Math.Max(delays.Count, 1)).ToList();
        return sequence
            .Select(i => images[Math.Min(i, images.Count - 1)])
            .ToList();
    }

    private static List<string> PetResolveFaceEquips(List<string> equips)
    {
        var faceAcc = equips.Where(e => PetCategory(e) == "FaceAcc").ToList();
        if (faceAcc.Count == 0) return equips.ToList();
        var drop = new HashSet<string>(faceAcc, StringComparer.Ordinal);
        foreach (var equip in equips)
        {
            if (PetCategory(equip) == "Face") drop.Add(equip);
        }
        var result = equips.Where(e => !drop.Contains(e)).Concat(faceAcc).ToList();
        if (!result.Any(e => PetCategory(e) == "Body"))
        {
            result = equips.Where(e => PetCategory(e) != "Face").ToList();
        }
        return result;
    }

    private static List<string> PetEarTypes(string headId)
    {
        var img = PetOpenPart(headId);
        var front = img?.FindNodeByPath("front", true);
        if (front is null) return new List<string>();
        return front.Nodes.Where(n => n.Text != "head").Select(n => n.Text).ToList();
    }

    // ── 椅子 ─────────────────────────────────────────────────────────────
    private sealed class PetChairLayer
    {
        public string Name = "";
        public int Z;
        public int Pos;
        public List<(Bitmap32 Img, (int X, int Y) Origin, int Delay)> Frames = new();
    }

    private sealed class PetChair
    {
        public (int X, int Y)? BodyRelMove;
        public bool RemoveBody;
        public bool TamingMob;
        public List<PetChairLayer> Layers = new();
    }

    private static List<string> PetChairPaths(string chairId)
    {
        var number = int.TryParse(chairId.TrimStart('0'), out var parsed) ? parsed : 0;
        return new List<string>
        {
            $"Install/{number / 1000:D5}.img",
            $"Install/{number / 100:D6}.img",
            $"Install/{number / 10000:D4}.img",
        };
    }

    private static PetChair? PetLoadChair(string chairId)
    {
        var itemRoot = FindTop("Item");
        if (itemRoot is null) return null;
        Wz_Node? chairNode = null;
        foreach (var path in PetChairPaths(chairId))
        {
            var img = PetFindImage(itemRoot, path);
            if (img is null) continue;
            foreach (var key in new[]
                     {
                         chairId, chairId.PadLeft(8, '0'), chairId.TrimStart('0'),
                         (int.TryParse(chairId, out var number) ? number : 0).ToString(),
                     })
            {
                if (key.Length == 0) continue;
                chairNode = img.FindNodeByPath(key, true);
                if (chairNode is not null) break;
            }
            if (chairNode is not null) break;
        }
        if (chairNode is null) return null;

        var chair = new PetChair();
        var info = chairNode.FindNodeByPath("info", true);
        if (info is not null)
        {
            chair.BodyRelMove = PetVec(info.FindNodeByPath("bodyRelMove", true));
            chair.RemoveBody = PetIntNode(info.FindNodeByPath("removeBody", true)) is > 0;
            chair.TamingMob = info.FindNodeByPath("tamingMob", true) is not null;
        }

        foreach (var layerName in new[] { "effect", "effect2", "effect3" })
        {
            var layerNode = chairNode.FindNodeByPath(layerName, true);
            if (layerNode is null) continue;
            var layer = new PetChairLayer
            {
                Name = layerName,
                Z = PetIntNode(layerNode.FindNodeByPath("z", true)) ?? 0,
                Pos = PetIntNode(layerNode.FindNodeByPath("pos", true)) ?? 0,
            };
            foreach (var child in layerNode.Nodes
                         .Where(n => n.Text.All(char.IsDigit))
                         .OrderBy(n => int.TryParse(n.Text, out var index) ? index : int.MaxValue))
            {
                var meta = PetCanvasMeta(child, "Item", null);
                if (meta is null) continue;
                try
                {
                    var image = PetDecode(meta.Png);
                    layer.Frames.Add((image, meta.Origin ?? (0, 0),
                        meta.Delay ?? 210));
                }
                catch
                {
                    // 单帧解不开只丢这一帧，整把椅子还能出图。
                }
            }
            if (layer.Frames.Count > 0) chair.Layers.Add(layer);
        }
        return chair;
    }

    private static (int X, int Y) PetChairOffset(
        PetChair chair, string chairId, PetChairLayer layer, bool hasSitAction)
    {
        var hasBodyRelMove = chair.BodyRelMove is not null;
        var bm = chair.BodyRelMove ?? (X: 0, Y: 0);
        var idNum = int.TryParse(chairId.TrimStart('0'), out var parsed) ? parsed : 0;
        var offX = 0;
        var offY = 0;
        if (hasBodyRelMove)
        {
            if (!hasSitAction && layer.Pos < 2)
            {
                if (layer.Pos == 1) offY = -50;
            }
            else
            {
                offX = -bm.X;
                offY = -bm.Y;
            }
        }
        else if (layer.Pos == 1)
        {
            offY = chair.TamingMob && idNum / 1000 < 3011 ? -30 : -50;
        }
        if (chair.RemoveBody) offY += 30;
        return (offX, offY);
    }

    private static (List<Bitmap32>? Imgs, List<int> Delays) PetApplyChair(
        List<Bitmap32> imgs, string chairId, List<string> equips, string pose, bool flip)
    {
        if (imgs.Count == 0) return (null, new List<int>());
        var chair = PetLoadChair(chairId);
        if (chair is null || chair.Layers.Count == 0)
        {
            Console.Error.WriteLine($"[pet] 椅子 {chairId} 不存在");
            return (null, new List<int>());
        }

        var count = chair.Layers.Max(layer => layer.Frames.Count);
        if (count <= 1) count = Math.Max(1, imgs.Count);
        var baseFrames = chair.Layers[0].Frames;
        var delays = Enumerable.Range(0, count)
            .Select(i => i < baseFrames.Count ? baseFrames[i].Delay : 210)
            .ToList();

        var unionW = imgs.Max(im => im.W);
        var unionH = imgs.Max(im => im.H);
        var baseImage = imgs[0];
        if (baseImage.W != unionW || baseImage.H != unionH)
        {
            var padded = new Bitmap32(unionW, unionH);
            PetBlitOver(padded, baseImage, 0, 0);
            baseImage = padded;
        }

        var placements = PetBuildPlacements(PetResolveFaceEquips(equips), pose, "", 0, null, out _);
        var tops = placements.Where(p => p.TopLeft is not null).Select(p => p.TopLeft!.Value).ToList();
        var navel = tops.Count > 0
            ? (-tops.Min(t => t.X), -tops.Min(t => t.Y))
            : (baseImage.W / 2, baseImage.H);

        var boxes = new List<(int X0, int Y0, int X1, int Y1)>();
        for (var i = 0; i < count; i++)
        {
            var minX = 0;
            var minY = 0;
            var maxX = baseImage.W;
            var maxY = baseImage.H;
            foreach (var layer in chair.Layers)
            {
                var frame = layer.Frames[Math.Min(i, layer.Frames.Count - 1)];
                var (offX, offY) = PetChairOffset(chair, chairId, layer, true);
                var tx = navel.Item1 + offX - frame.Origin.X;
                var ty = navel.Item2 + offY - frame.Origin.Y;
                minX = Math.Min(minX, tx);
                minY = Math.Min(minY, ty);
                maxX = Math.Max(maxX, tx + frame.Img.W);
                maxY = Math.Max(maxY, ty + frame.Img.H);
            }
            boxes.Add((minX, minY, maxX, maxY));
        }

        var ux0 = boxes.Min(b => b.X0);
        var uy0 = boxes.Min(b => b.Y0);
        var ux1 = boxes.Max(b => b.X1);
        var uy1 = boxes.Max(b => b.Y1);
        var frames = new List<Bitmap32>();
        for (var i = 0; i < count; i++)
        {
            var box = boxes[i];
            var tile = new Bitmap32(box.X1 - box.X0, box.Y1 - box.Y0);
            PetBlitOver(tile, baseImage, -box.X0, -box.Y0);
            var layers = new List<(int Z, Bitmap32 Img, int Tx, int Ty)>();
            foreach (var layer in chair.Layers)
            {
                var frame = layer.Frames[Math.Min(i, layer.Frames.Count - 1)];
                var (offX, offY) = PetChairOffset(chair, chairId, layer, true);
                layers.Add((layer.Z, frame.Img,
                    navel.Item1 + offX - frame.Origin.X,
                    navel.Item2 + offY - frame.Origin.Y));
            }
            foreach (var (z, img, tx, ty) in layers.OrderByDescending(l => l.Z))
            {
                var posX = tx - box.X0;
                var posY = ty - box.Y0;
                if (z <= 0)
                {
                    var behind = new Bitmap32(tile.W, tile.H);
                    PetBlitOver(behind, img, posX, posY);
                    PetBlitOver(behind, tile, 0, 0);
                    tile = behind;
                }
                else
                {
                    PetBlitOver(tile, img, posX, posY);
                }
            }
            var canvas = new Bitmap32(ux1 - ux0, uy1 - uy0);
            PetBlitOver(canvas, tile, box.X0 - ux0, box.Y0 - uy0);
            frames.Add(canvas);
        }
        return (frames, delays);
    }

    // ── 命令：chara ──────────────────────────────────────────────────────
    private static string PetCharaStem(List<string> equips, string action, string chairId, bool flip)
    {
        static string Trim(string value) => value.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";
        var joined = string.Join("_", equips.Select(Trim));
        var stem = $"pet_chara_{joined}_{action}";
        if (chairId.Length > 0) stem += $"_chair{Trim(chairId)}";
        if (flip) stem += "_flip";
        return stem + $"_v{PetComposeVersion}_{PetImplTag}";
    }

    private static JsonNode CmdChara(JsonObject args)
    {
        PetEnsureLoaded();
        var equips = PetStrList(args["equips"]);
        if (equips.Count == 0) equips = PetDefaultEquips.ToList();
        var want = PetStrList(args["actions"]);
        if (want.Count == 0) want = PetDefaultActions.ToList();
        var flip = PetBoolArg(args, "flip");
        var earType = PetStr(args["earType"]);
        if (earType.Length == 0) earType = PetStr(args["ear_type"]).Trim();
        if (earType.Length == 0) earType = "humanEar";
        var chairId = PetStr(args["chairId"]).Trim();
        if (chairId.Length == 0) chairId = PetStr(args["chair"]).Trim();

        var headId = equips.FirstOrDefault(e => PetCategory(e) == "Head");
        if (headId is not null)
        {
            try
            {
                var ears = PetEarTypes(headId);
                if (ears.Count > 0 && !ears.Contains(earType))
                {
                    Console.Error.WriteLine($"[pet] 耳型 {earType} 不在 [{string.Join(", ", ears)}]，退回 humanEar");
                    earType = "humanEar";
                }
            }
            catch
            {
                earType = "humanEar";
            }
        }

        var renderEquips = PetResolveFaceEquips(equips);
        var staged = new Dictionary<string, (string Real, List<Bitmap32> Imgs, List<int> Delays)>(StringComparer.Ordinal);
        var missing = new JsonArray();

        foreach (var requested in want)
        {
            string? real = null;
            List<Bitmap32>? images = null;
            var delays = new List<int>();
            foreach (var candidate in PetPoseAliases.TryGetValue(requested, out var alias)
                         ? alias
                         : new[] { requested })
            {
                try
                {
                    var composed = PetComposePose(renderEquips, candidate, earType, flip,
                        out var poseDelays, out _);
                    if (composed is not null && composed.Count > 0
                        && !(composed.Count == 1 && composed[0].W <= 1))
                    {
                        real = candidate;
                        images = composed;
                        delays = poseDelays;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[pet] chara 合成 {requested}/{candidate} 失败：{ex.Message}");
                }
            }
            if (real is null || images is null)
            {
                missing.Add(requested);
                continue;
            }

            images = PetApplyTimeline(real, images, delays);
            delays = PetTimelineDelays(real, delays, images.Count);

            if (chairId.Length > 0 && real == "sit")
            {
                var (chairImages, chairDelays) = PetApplyChair(images, chairId, equips, "sit", flip);
                if (chairImages is not null && chairImages.Count > 0)
                {
                    images = chairImages;
                    if (chairDelays.Count > 0) delays = chairDelays;
                }
            }
            staged[requested] = (real, images, delays);
        }

        var outDir = PetOutDir();
        var outAnims = new JsonObject();
        var resolved = new JsonObject();

        if (chairId.Length > 0)
        {
            foreach (var (requested, value) in staged)
            {
                var (real, images, delays) = value;
                var step = images.Count > 0 ? images[0].W : 1;
                var chairCellH = images.Count > 0 ? images[0].H : 1;
                var sheet = new Bitmap32(images.Count * step, chairCellH);
                var framesMeta = new JsonArray();
                var delayArr = new JsonArray();
                long total = 0;
                for (var i = 0; i < images.Count; i++)
                {
                    var image = images[i];
                    var ox = i * step;
                    PetBlitOver(sheet, image, ox, 0);
                    var delay = i < delays.Count ? delays[i] : PetCharaLayoutDelay;
                    framesMeta.Add(new JsonObject
                    {
                        ["index"] = i, ["x"] = ox, ["y"] = 0,
                        ["w"] = image.W, ["h"] = image.H, ["delay"] = delay,
                    });
                    delayArr.Add(delay);
                    total += delay;
                }

                var stem = PetCharaStem(equips, real, chairId, flip);
                Directory.CreateDirectory(outDir);
                File.WriteAllBytes(Path.Combine(outDir, stem + ".png"), Png.Encode(sheet));
                outAnims[requested] = new JsonObject
                {
                    ["file"] = stem + ".png",
                    ["action"] = real,
                    ["count"] = images.Count,
                    ["cell"] = new JsonObject { ["w"] = step, ["h"] = chairCellH },
                    ["anchor"] = new JsonObject { ["x"] = step / 2, ["y"] = chairCellH },
                    ["cols"] = images.Count,
                    ["rows"] = 1,
                    ["frames"] = framesMeta,
                    ["delays"] = delayArr,
                    ["totalMs"] = total,
                };
            }

            return new JsonObject
            {
                ["kind"] = "chara",
                ["id"] = "chara:" + string.Join(",", equips),
                ["name"] = "纸娃娃",
                ["earType"] = earType,
                ["path"] = "Character/<equips>",
                ["actions"] = PetCharaActions(outAnims),
                ["originFallback"] = "center",
                ["defaultDelay"] = PetCharaLayoutDelay,
                ["anims"] = PetCharaAnimsWithUrl(outAnims),
                ["resolved"] = resolved,
                ["missing"] = missing,
                ["outDir"] = outDir,
                ["equips"] = new JsonArray(equips.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
                ["chairId"] = chairId,
            };
        }

        var sizes = staged.Values
            .Where(v => v.Imgs.Count > 0)
            .Select(v => (W: v.Imgs[0].W, H: v.Imgs[0].H))
            .ToList();
        if (sizes.Count == 0) sizes.Add((1, 1));
        var cellW = sizes.Max(s => s.W);
        var cellH = sizes.Max(s => s.H);
        var anchorX = cellW / 2;
        var anchorY = cellH;

        foreach (var (requested, value) in staged)
        {
            var (real, images, delays) = value;
            var step = cellW;
            var sheet = new Bitmap32(images.Count * step, cellH);
            var framesMeta = new JsonArray();
            var delayArr = new JsonArray();
            long total = 0;
            for (var i = 0; i < images.Count; i++)
            {
                var image = images[i];
                var ox = i * step + Math.Max(0, (cellW - image.W) / 2);
                var oy = Math.Max(0, cellH - image.H);
                PetBlitOver(sheet, image, ox, oy);
                framesMeta.Add(new JsonObject
                {
                    ["index"] = i, ["x"] = i * step, ["y"] = 0,
                    ["w"] = step, ["h"] = cellH, ["ox"] = ox, ["oy"] = oy,
                });
                var delay = i < delays.Count ? delays[i] : PetCharaLayoutDelay;
                delayArr.Add(delay);
                total += delay;
            }

            var stem = PetCharaStem(equips, real, chairId, flip);
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, stem + ".png"), Png.Encode(sheet));
            var entry = new JsonObject
            {
                ["file"] = stem + ".png",
                ["action"] = real,
                ["count"] = images.Count,
                ["cell"] = new JsonObject { ["w"] = cellW, ["h"] = cellH },
                ["frames"] = framesMeta,
                ["anchor"] = new JsonObject { ["x"] = anchorX, ["y"] = anchorY },
                ["cols"] = images.Count,
                ["rows"] = 1,
                ["delays"] = delayArr,
                ["totalMs"] = total,
            };
            outAnims[requested] = entry;

            var resolvedEntry = PetCloneObject(entry);
            resolvedEntry["requested"] = requested;
            resolvedEntry["resolved"] = real;
            resolvedEntry["exact"] = real == requested;
            resolved[requested] = resolvedEntry;
        }

        return new JsonObject
        {
            ["kind"] = "chara",
            ["id"] = "chara:" + string.Join(",", equips),
            ["name"] = "纸娃娃",
            ["earType"] = earType,
            ["path"] = "Character/<equips>",
            ["actions"] = PetCharaActions(outAnims),
            ["originFallback"] = "center",
            ["defaultDelay"] = PetCharaLayoutDelay,
            ["anims"] = PetCharaAnimsWithUrl(outAnims),
            ["resolved"] = resolved,
            ["missing"] = missing,
            ["outDir"] = outDir,
            ["equips"] = new JsonArray(equips.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
        };
    }

    private static JsonArray PetCharaActions(JsonObject anims)
    {
        var actions = new JsonArray();
        foreach (var (name, value) in anims)
        {
            var count = value is JsonObject obj ? obj["count"]?.GetValue<int>() ?? 0 : 0;
            actions.Add(new JsonObject { ["name"] = name, ["frames"] = count });
        }
        return actions;
    }

    private static JsonObject PetCharaAnimsWithUrl(JsonObject anims)
    {
        var result = new JsonObject();
        foreach (var (name, value) in anims)
        {
            if (value is not JsonObject entry) continue;
            var copy = PetCloneObject(entry);
            copy["url"] = "/dsh-maplestory-wz/file/" + (entry["file"]?.ToString() ?? "");
            result[name] = copy;
        }
        return result;
    }
}
