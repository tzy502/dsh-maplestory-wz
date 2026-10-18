// 冒险岛 WZ worker（.NET 半边）—— 用 WzComparerR2 的 WzLib 读数据。
//
// 协议与 py/wz_worker.py 逐字兼容：stdin 一行一个请求，stdout 一行一个响应。
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

internal static class Program
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
        "ping" => new JsonObject { ["engine"] = "wzlib", ["data_dir"] = _dataDir },
        _ => throw new NotSupportedException($"wzlib worker 暂不支持命令：{cmd}"),
    };

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
            ["wzpy"] = "",
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

        var root = FindTop(pack) ?? throw new FileNotFoundException($"没有这个分类：{pack}（{_dataDir}）");
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
