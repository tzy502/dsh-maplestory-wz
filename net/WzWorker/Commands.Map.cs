// 地图命令组：名录 / 详情 / 渲染 / 小地图 / 内容边界。
//
// 移植自 旧实现 与 旧实现。字段名逐字对齐 旧实现，
// 因为宿主与前端按这些键读取；渲染顺序（背景→layer/obj+tile→life→前景背景）
// 与 HaCreator 的 draw order 一致，不能按直觉重排。
//
// 这份客户端把像素放在 `_Canvas/` 仓库、结构里只留 1×1 占位画布 + `_outlink`，
// 所以「画布可解码」不代表它是真画布，必须跟随链接后才能取到像素。
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using SkiaSharp;
using WzComparerR2.WzLib;

namespace WzWorker;

internal static partial class Program
{
    internal static JsonNode? TryDispatchMap(string cmd, JsonObject args) => cmd switch
    {
        "map_list" => CmdMapList(args),
        "map_random" => CmdMapRandom(args),
        "map_featured" => CmdMapFeatured(args),
        "map_next" => CmdMapNext(args),
        "map_detail" => CmdMapDetail(args),
        "map_image" => CmdMapImage(args),
        "map_canvas" => CmdMapCanvas(args),
        "map_minimap" => CmdMapMinimap(args),
        "map_content_bounds" => CmdMapContentBounds(args),
        "map_viewport_b64" => CmdMapViewportB64(args),
        _ => null,
    };

    // ── 缓存与常量 ──────────────────────────────────────────────────────

    private const int LegacyLayerCount = 8;
    private const int MaxRenderPixels = 64_000_000;
    private const long CanvasCacheLimit = 256L * 1024 * 1024;
    private const int MapCacheSchema = 3;

    private static readonly double MaxMapWorld = EnvDouble("WZ_MAP_MAX_PX", 4096.0);

    // 经典户外地图：只是「推荐/随机优先」的偏好清单，渲染数据与名字仍全部来自 WZ。
    private static readonly string[] FeaturedMaps =
    {
        "100000000", "101000000", "102000000", "103000000", "104000000", "120000000",
        "200000000", "211000000", "220000000", "230000000", "240000000", "250000000",
        "260000000", "261000000", "600000000", "800000000", "910000000", "1000000",
    };

    private static readonly string[] TownStreetHints =
    {
        "村", "城", "镇", "港", "都市", "部落", "密林", "码头", "市场", "广场", "营地", "村庄",
    };

    private static readonly string[] NotTownHints =
    {
        "隐藏", "航海", "水路", "地下", "内部", "深处", "洞穴", "窟", "塔", "道场",
        "竞技", "副本", "等候", "大厅", "祭坛", "飞船", "某处",
    };

    private static readonly Dictionary<string, (Wz_Node Root, string SourceId)> MapRootCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<Wz_Node, Bitmap32> DecodedCanvases = new(ReferenceEqualityComparer.Instance);
    private static readonly Queue<Wz_Node> CanvasOrder = new();
    private static readonly Dictionary<string, (string Path, JsonObject Meta)> MapPngCache = new(StringComparer.Ordinal);
    private static readonly object MapStateGate = new();

    private static List<MapNameRow>? _mapNameRows;
    private static Dictionary<string, MapName>? _namesById;
    private static long _canvasCacheBytes;

    private sealed record MapNameRow(string Id, string Name, string Street);
    private sealed record MapName(string Name, string Street);
    private sealed record Foothold(int Layer, int Platform, int Id, int X1, int Y1, int X2, int Y2, int Prev, int Next);
    private sealed record RopeEntry(int Id, int X, int Y1, int Y2, bool Ladder, bool Uf, int Page);
    private sealed record LifeEntry(int Index, string Type, string Id, int X, int Y, int Cy, int Rx0, int Rx1, bool Flip, bool Hide, JsonNode? MobTime);
    private sealed record PortalEntry(int Index, string Name, int Type, int X, int Y, int TargetMap, string TargetName, string Script);
    private sealed record Placement(int Layer, int Kind, int Z, int Order, Wz_Node Frame, int X, int Y, bool Flip, Wz_Node? Source);

    private sealed class MapBounds
    {
        public int Left { get; }
        public int Top { get; }
        public int Right { get; }
        public int Bottom { get; }
        public int Width => Math.Max(0, Right - Left);
        public int Height => Math.Max(0, Bottom - Top);

        public MapBounds(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public JsonObject ToJson() => new()
        {
            ["left"] = Left,
            ["top"] = Top,
            ["right"] = Right,
            ["bottom"] = Bottom,
            ["width"] = Width,
            ["height"] = Height,
        };
    }

    private sealed class MapInfo
    {
        public required string Id { get; init; }
        public required string SourceId { get; init; }
        public bool Linked => SourceId != Id;
        public string? Name { get; init; }
        public string? Street { get; init; }
        public required MapBounds Bounds { get; init; }
        public required JsonArray Layers { get; init; }
        public required JsonObject Counts { get; init; }
        public required List<LifeEntry> Life { get; init; }
        public required List<PortalEntry> Portals { get; init; }
        public required List<Foothold> Footholds { get; init; }
        public required List<RopeEntry> Ropes { get; init; }
        public JsonObject? Minimap { get; init; }
    }

    // ── 地图名录 ────────────────────────────────────────────────────────

    /// <summary>
    /// 自建名录：String/Map.img 的编号补零到 9 位。
    /// 不直接用搜索接口，是因为它把编号归一化成去前导零的形态，和 9 位地图编号对不上。
    /// </summary>
    private static List<MapNameRow> MapNameIndex()
    {
        lock (MapStateGate)
        {
            if (_mapNameRows is not null) return _mapNameRows;
            var rows = new List<MapNameRow>();
            var img = Sub(FindTop("String"), "Map.img");
            if (img is not null)
            {
                foreach (var cat in img.Nodes)
                {
                    foreach (var entry in cat.Nodes)
                    {
                        if (entry.Text.Length == 0 || !entry.Text.All(char.IsDigit)) continue;
                        var name = NodeStr(Sub(entry, "mapName")).Trim();
                        if (name.Length == 0) continue;
                        var street = NodeStr(Sub(entry, "streetName")).Trim();
                        var raw = entry.Text;
                        rows.Add(new MapNameRow(raw.Length <= 9 ? raw.PadLeft(9, '0') : raw, name, street));
                    }
                }
            }
            _mapNameRows = rows;
            return rows;
        }
    }

    /// <summary>按「去前导零」的编号建名字索引：describe 的 name/street 走这张表。</summary>
    private static void BuildNamesById()
    {
        lock (MapStateGate)
        {
            if (_namesById is not null) return;
            var map = new Dictionary<string, MapName>(StringComparer.Ordinal);
            var img = Sub(FindTop("String"), "Map.img");
            if (img is not null)
            {
                foreach (var cat in img.Nodes)
                {
                    foreach (var entry in cat.Nodes)
                    {
                        if (entry.Text.Length == 0 || !entry.Text.All(char.IsDigit)) continue;
                        var name = NodeStr(Sub(entry, "mapName"));
                        if (name.Length == 0) continue;
                        string id;
                        try { id = NormalizeMapId(entry.Text); }
                        catch (FormatException) { continue; }
                        map[id] = new MapName(name, NodeStr(Sub(entry, "streetName")));
                    }
                }
            }
            _namesById = map;
        }
    }

    private static List<MapNameRow> FilterNamedMaps(string query)
    {
        var index = MapNameIndex();
        var q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) return index;
        if (q.All(char.IsDigit))
        {
            var digits = q.TrimStart('0');
            return index.Where(r => r.Id.TrimStart('0').StartsWith(digits, StringComparison.Ordinal)).ToList();
        }
        return index.Where(r =>
            r.Name.ToLowerInvariant().Contains(q, StringComparison.Ordinal) ||
            r.Street.ToLowerInvariant().Contains(q, StringComparison.Ordinal)).ToList();
    }

    private static JsonNode CmdMapList(JsonObject args)
    {
        var query = ScalarText(args["query"]).Trim();
        var limit = IntOr(args, "limit", 60);
        var hits = FilterNamedMaps(query);

        // 无查询时优先 2~6 字的名字：那是城镇/野外图，适合当背景；
        // 否则「光」「冬」这类极短名会排在最前，选出来几乎没法用。
        var ordered = query.Length == 0
            ? hits.OrderBy(r => r.Name.Length is >= 2 and <= 6 ? 0 : 1)
                  .ThenBy(r => r.Name.Length)
                  .ThenBy(r => r.Id, StringComparer.Ordinal)
            : hits.OrderBy(r => r.Name == query ? 0 : 1)
                  .ThenBy(r => r.Name.Length)
                  .ThenBy(r => r.Id, StringComparer.Ordinal);

        var maps = new JsonArray();
        var dropped = 0;
        foreach (var row in ordered.Take(limit * 4))
        {
            if (maps.Count >= limit) break;
            try
            {
                if (!HasMap(row.Id))
                {
                    dropped++;
                    continue;
                }
            }
            catch
            {
                dropped++;
                continue;
            }
            maps.Add(MapRowJson(row));
        }

        return new JsonObject
        {
            ["query"] = query,
            ["total"] = hits.Count,
            ["count"] = maps.Count,
            ["maps"] = maps,
            ["dropped"] = dropped,
            ["note"] = "编号左补零到 9 位；名字取自 String/Map.img；已剔除渲染不出来的",
        };
    }

    private static List<MapNameRow> FeaturedRowsValidated()
    {
        var byId = new Dictionary<string, MapNameRow>(StringComparer.Ordinal);
        foreach (var row in MapNameIndex()) byId[row.Id.TrimStart('0')] = row;

        var rows = new List<MapNameRow>();
        foreach (var mid in FeaturedMaps)
        {
            if (!byId.TryGetValue(mid.TrimStart('0'), out var row)) continue;
            try
            {
                if (!HasMap(row.Id)) continue;
            }
            catch
            {
                continue;
            }
            rows.Add(row);
        }
        return rows;
    }

    private static JsonNode CmdMapFeatured(JsonObject args)
    {
        var rows = FeaturedRowsValidated();
        var maps = new JsonArray();
        foreach (var row in rows) maps.Add(MapRowJson(row));
        return new JsonObject { ["maps"] = maps, ["count"] = maps.Count };
    }

    private static JsonNode CmdMapRandom(JsonObject args)
    {
        var exclude = ExcludeSet(args);
        var prefer = ScalarText(args["prefer"]).Trim().ToLowerInvariant();
        var featured = BoolArg(args, "featured");

        List<MapNameRow> Build(bool featuredOnly)
        {
            if (prefer.Length > 0) return FilterNamedMaps(prefer);
            if (featuredOnly && featured) return FeaturedRowsValidated();
            return MapNameIndex();
        }

        MapNameRow? Draw(List<MapNameRow> pool, out int tried)
        {
            var candidates = pool.Where(r => !exclude.Contains(r.Id)).ToList();
            Shuffle(candidates);
            tried = 0;
            foreach (var row in candidates.Take(200))
            {
                tried++;
                try
                {
                    if (!HasMap(row.Id)) continue;
                }
                catch
                {
                    continue;
                }
                return row;
            }
            return null;
        }

        var hit = Draw(Build(true), out var tried);
        var relaxed = false;
        // 精选池只有十几张，排除最近 20 张后常常抽空；不自动放宽就永远重复同几张。
        if (hit is null && prefer.Length == 0 && featured)
        {
            hit = Draw(Build(false), out tried);
            if (hit is not null) relaxed = true;
        }
        if (hit is null) throw new InvalidOperationException($"没抽到可用地图（已排除最近 {exclude.Count} 张）");

        var obj = MapRowJson(hit);
        obj["tried"] = tried;
        if (relaxed) obj["relaxed"] = true;
        return obj;
    }

    private static JsonNode CmdMapNext(JsonObject args)
    {
        var exclude = ExcludeSet(args);
        var shapeCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        // 超过 7:1 的细长条（实测天空之城上空 51149×3650）铺到屏幕上只剩一条，直接排除。
        bool ShapeOk(string mid)
        {
            if (shapeCache.TryGetValue(mid, out var cached)) return cached;
            var ok = false;
            try
            {
                var info = Describe(mid);
                var w = Math.Max(1, info.Bounds.Width);
                var h = Math.Max(1, info.Bounds.Height);
                ok = Math.Max(w, h) / (double)Math.Min(w, h) <= 7.0;
            }
            catch
            {
                ok = false;
            }
            shapeCache[mid] = ok;
            return ok;
        }

        bool Usable(MapNameRow row)
        {
            try { return HasMap(row.Id) && ShapeOk(row.Id); }
            catch { return false; }
        }

        var ranked = new[] { new List<MapNameRow>(), new List<MapNameRow>(), new List<MapNameRow>() };
        var seenName = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in MapNameIndex())
        {
            var tier = TownRank(row);
            if (tier < 0) continue;
            // 同一城镇常有多个编号（童话村有 4 个），按名字去重，避免连着抽到同名图。
            if (!seenName.Add(row.Name)) continue;
            ranked[tier].Add(row);
        }
        var townsTotal = ranked.Sum(bucket => bucket.Count);

        var tries = 0;
        for (var tier = 0; tier < 3; tier++)
        {
            var bucket = ranked[tier].Where(r => !exclude.Contains(r.Id)).ToList();
            Shuffle(bucket);
            foreach (var row in bucket.Take(300))
            {
                tries++;
                if (!Usable(row)) continue;
                var obj = MapRowJson(row);
                obj["town"] = true;
                obj["tier"] = tier;
                obj["candidates"] = townsTotal;
                obj["tried"] = tries;
                return obj;
            }
        }

        var rest = MapNameIndex().Where(r => !exclude.Contains(r.Id)).ToList();
        Shuffle(rest);
        foreach (var row in rest.Take(300))
        {
            tries++;
            if (!Usable(row)) continue;
            var obj = MapRowJson(row);
            obj["town"] = false;
            obj["tier"] = 9;
            obj["candidates"] = townsTotal;
            obj["tried"] = tries;
            return obj;
        }
        throw new InvalidOperationException($"没抽到可用地图（试了 {tries} 张）");
    }

    private static int TownRank(MapNameRow row)
    {
        var name = (row.Name ?? "").Trim();
        var street = (row.Street ?? "").Trim();
        if (street.Length == 0 || name.Length == 0) return -1;
        if (NotTownHints.Any(h => street.Contains(h, StringComparison.Ordinal)) ||
            NotTownHints.Any(h => name.Contains(h, StringComparison.Ordinal))) return -1;
        if (name == street && TownStreetHints.Any(h => street.Contains(h, StringComparison.Ordinal))) return 0;
        if (name.Length <= 6 && new[] { "村", "城", "镇", "港", "都市", "部落" }
                .Any(h => name.Contains(h, StringComparison.Ordinal))) return 1;
        if (TownStreetHints.Any(h => street.Contains(h, StringComparison.Ordinal))) return 2;
        return -1;
    }

    // ── 地图详情 ────────────────────────────────────────────────────────

    private static JsonNode CmdMapDetail(JsonObject args)
    {
        if (args["id"] is null) throw new InvalidOperationException("map_detail 需要 id");
        var mid = ScalarText(args["id"]);
        var info = Describe(mid);
        var view = args["view"] as JsonObject ?? new JsonObject();
        var zoom = DoubleOr(view, "zoom", 1.0);
        var viewW = IntOr(view, "w", 1920);
        var viewH = IntOr(view, "h", 1080);
        var b = info.Bounds;
        var camera = ClampCamera(b, b.Left + b.Width / 2.0, b.Top + b.Height / 2.0, zoom, viewW, viewH);

        var life = new JsonArray();
        foreach (var item in info.Life.Take(200)) life.Add(LifeJson(item));
        var portals = new JsonArray();
        foreach (var item in info.Portals) portals.Add(PortalJson(item));
        var footholds = new JsonArray();
        foreach (var item in info.Footholds) footholds.Add(FootholdJson(item));
        var ropes = new JsonArray();
        foreach (var item in info.Ropes) ropes.Add(RopeJson(item));

        return new JsonObject
        {
            ["id"] = info.Id,
            ["sourceId"] = info.SourceId,
            ["linked"] = info.Linked,
            ["name"] = info.Name,
            ["street"] = info.Street,
            ["bounds"] = b.ToJson(),
            ["counts"] = info.Counts,
            ["layers"] = info.Layers,
            ["life"] = life,
            ["portals"] = portals,
            ["footholds"] = footholds,
            ["ropes"] = ropes,
            ["footholdCount"] = info.Footholds.Count,
            ["camera"] = camera,
            ["note"] = "相机 clamp 规则与 MiniPet MapService.ClampCamera 一致",
        };
    }

    private static MapInfo Describe(string mapId)
    {
        var requested = NormalizeMapId(mapId);
        var (root, sourceId) = MapRoot(requested);
        BuildNamesById();
        string? name = null;
        string? street = null;
        if (_namesById is not null && _namesById.TryGetValue(requested, out var named))
        {
            name = named.Name;
            street = named.Street;
        }

        var layers = new JsonArray();
        var tileTotal = 0;
        var objectTotal = 0;
        for (var number = 0; number < LegacyLayerCount; number++)
        {
            var layer = Sub(root, number.ToString(CultureInfo.InvariantCulture));
            var tiles = Sub(layer, "tile")?.Nodes.Count ?? 0;
            var objects = Sub(layer, "obj")?.Nodes.Count ?? 0;
            tileTotal += tiles;
            objectTotal += objects;
            layers.Add(new JsonObject
            {
                ["number"] = number,
                ["tileSet"] = NodeStr(Sub(layer, "info/tS")),
                ["tiles"] = tiles,
                ["objects"] = objects,
            });
        }

        var footholds = Footholds(root);
        var ropes = Ropes(root);
        var life = LifeItems(root);
        var portals = PortalItems(root);
        var minimapNode = Sub(root, "miniMap");
        var minimap = minimapNode is null ? null : new JsonObject
        {
            ["width"] = NodeInt(minimapNode, "width"),
            ["height"] = NodeInt(minimapNode, "height"),
            ["centerX"] = NodeInt(minimapNode, "centerX"),
            ["centerY"] = NodeInt(minimapNode, "centerY"),
            ["mag"] = NodeInt(minimapNode, "mag"),
        };

        var counts = new JsonObject
        {
            ["tiles"] = tileTotal,
            ["objects"] = objectTotal,
            ["backgrounds"] = Sub(root, "back")?.Nodes.Count ?? 0,
            ["mobs"] = life.Count(item => item.Type == "mob"),
            ["npcs"] = life.Count(item => item.Type == "npc"),
            ["reactors"] = ReactorCount(root),
            ["portals"] = portals.Count,
            ["footholds"] = footholds.Count,
            ["ropes"] = ropes.Count,
        };

        return new MapInfo
        {
            Id = requested,
            SourceId = sourceId,
            Name = name,
            Street = street,
            Bounds = Bounds(root),
            Layers = layers,
            Counts = counts,
            Life = life,
            Portals = portals,
            Footholds = footholds,
            Ropes = ropes,
            Minimap = minimap,
        };
    }

    private static List<Foothold> Footholds(Wz_Node root)
    {
        var result = new List<Foothold>();
        var parent = Sub(root, "foothold");
        if (parent is null) return result;
        foreach (var layer in parent.Nodes)
        {
            foreach (var platform in layer.Nodes)
            {
                foreach (var item in platform.Nodes)
                {
                    if (Sub(item, "x1") is null) continue;
                    result.Add(new Foothold(
                        IntName(layer.Text), IntName(platform.Text), IntName(item.Text),
                        NodeInt(item, "x1"), NodeInt(item, "y1"), NodeInt(item, "x2"), NodeInt(item, "y2"),
                        NodeInt(item, "prev"), NodeInt(item, "next")));
                }
            }
        }
        return result;
    }

    private static List<RopeEntry> Ropes(Wz_Node root)
    {
        var result = new List<RopeEntry>();
        var parent = Sub(root, "ladderRope");
        if (parent is null) return result;
        foreach (var item in parent.Nodes)
        {
            if (Sub(item, "x") is null) continue;
            result.Add(new RopeEntry(
                IntName(item.Text), NodeInt(item, "x"), NodeInt(item, "y1"), NodeInt(item, "y2"),
                NodeInt(item, "l") != 0, NodeInt(item, "uf") != 0, NodeInt(item, "page")));
        }
        return result;
    }

    private static List<LifeEntry> LifeItems(Wz_Node root)
    {
        var result = new List<LifeEntry>();
        var parent = Sub(root, "life");
        if (parent is null) return result;
        foreach (var item in parent.Nodes)
        {
            var type = NodeStr(Sub(item, "type"));
            if (type is not ("m" or "n")) continue;
            var cyNode = Sub(item, "cy");
            var cy = cyNode is null ? NodeInt(item, "y") : NodeInt(item, "cy");
            result.Add(new LifeEntry(
                IntName(item.Text),
                type == "m" ? "mob" : "npc",
                NodeStr(Sub(item, "id")),
                NodeInt(item, "x"), NodeInt(item, "y"), cy,
                NodeInt(item, "rx0"), NodeInt(item, "rx1"),
                NodeInt(item, "f") != 0, NodeInt(item, "hide") != 0,
                RawJson(Sub(item, "mobTime")?.Value)));
        }
        return result;
    }

    private static List<PortalEntry> PortalItems(Wz_Node root)
    {
        var result = new List<PortalEntry>();
        var parent = Sub(root, "portal");
        if (parent is null) return result;
        foreach (var item in parent.Nodes)
        {
            if (Sub(item, "pn") is null) continue;
            result.Add(new PortalEntry(
                IntName(item.Text), NodeStr(Sub(item, "pn")), NodeInt(item, "pt"),
                NodeInt(item, "x"), NodeInt(item, "y"),
                NodeIntOr(item, "tm", 999999999),
                NodeStr(Sub(item, "tn")), NodeStr(Sub(item, "script"))));
        }
        return result;
    }

    private static int ReactorCount(Wz_Node root)
    {
        var parent = Sub(root, "reactor");
        if (parent is null) return 0;
        var count = 0;
        foreach (var item in parent.Nodes)
            if (Sub(item, "id") is not null) count++;
        return count;
    }

    private static MapBounds Bounds(Wz_Node root)
    {
        var info = Sub(root, "info");
        if (info is not null &&
            Sub(info, "VRLeft") is not null && Sub(info, "VRTop") is not null &&
            Sub(info, "VRRight") is not null && Sub(info, "VRBottom") is not null)
        {
            var left = NodeInt(info, "VRLeft");
            var top = NodeInt(info, "VRTop");
            var right = NodeInt(info, "VRRight");
            var bottom = NodeInt(info, "VRBottom");
            if (right > left && bottom > top) return new MapBounds(left, top, right, bottom);
        }

        var minimap = Sub(root, "miniMap");
        if (minimap is not null)
        {
            var width = NodeInt(minimap, "width");
            var height = NodeInt(minimap, "height");
            var centerX = NodeInt(minimap, "centerX");
            var centerY = NodeInt(minimap, "centerY");
            if (width > 0 && height > 0)
                return new MapBounds(-centerX, -centerY, width - centerX, height - centerY);
        }

        var footholds = Footholds(root);
        if (footholds.Count > 0)
        {
            var xs = footholds.SelectMany(f => new[] { f.X1, f.X2 }).ToList();
            var ys = footholds.SelectMany(f => new[] { f.Y1, f.Y2 }).ToList();
            return new MapBounds(xs.Min() - 10, ys.Min() - 360, xs.Max() + 10, ys.Max() + 110);
        }

        throw new InvalidOperationException("map has no VR, miniMap, or footholds from which to derive bounds");
    }

    private static JsonObject ClampCamera(MapBounds bounds, double camX, double camY, double zoom, int viewW, int viewH)
    {
        if (zoom <= 0 || viewW <= 0 || viewH <= 0)
        {
            return new JsonObject { ["x"] = camX, ["y"] = camY };
        }
        var halfW = viewW / (2.0 * zoom);
        var halfH = viewH / (2.0 * zoom);
        var minX = bounds.Left + halfW;
        var maxX = bounds.Right - halfW;
        var cx = minX > maxX ? (bounds.Left + bounds.Right) / 2.0 : Math.Min(Math.Max(camX, minX), maxX);
        var minY = bounds.Top + halfH;
        var maxY = bounds.Bottom - halfH;
        var cy = minY > maxY ? (bounds.Top + bounds.Bottom) / 2.0 : Math.Min(Math.Max(camY, minY), maxY);
        return new JsonObject
        {
            ["x"] = cx,
            ["y"] = cy,
            ["halfW"] = halfW,
            ["halfH"] = halfH,
        };
    }

    // ── 地图渲染 ────────────────────────────────────────────────────────

    private static (Bitmap32 Img, MapInfo Info, MapBounds Bounds, double Scale) RenderFullMap(string mid, double maxScale)
    {
        var info = Describe(mid);
        var b = info.Bounds;
        var scale = Math.Min(maxScale, MaxMapWorld / Math.Max(1, Math.Max(b.Width, b.Height)));
        scale = Math.Max(0.02, Math.Min(scale, 4.0));
        var img = Compose(mid, scale);
        return (img, info, b, scale);
    }

    private static Bitmap32 Compose(string mapId, double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale > 4)
            throw new InvalidOperationException("scale must be greater than 0 and at most 4");

        var (root, _) = MapRoot(mapId);
        var bounds = Bounds(root);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("render bounds are empty");

        var outW = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var outH = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        if ((long)outW * outH > MaxRenderPixels)
            throw new InvalidOperationException($"render would create {outW}x{outH} pixels; lower scale or request a viewport");

        var output = new Bitmap32(outW, outH);
        var mapTop = FindTop("Map");
        var back = Sub(root, "back");
        var backItems = back?.Nodes.ToList() ?? new List<Wz_Node>();

        foreach (var item in backItems)
            if (NodeInt(item, "front") == 0)
                DrawBackground(output, bounds, scale, item, 0, mapTop);

        var placements = new List<Placement>();
        for (var layerNumber = 0; layerNumber < LegacyLayerCount; layerNumber++)
        {
            var layer = Sub(root, layerNumber.ToString(CultureInfo.InvariantCulture));
            if (layer is null) continue;
            var tileSet = NodeStr(Sub(layer, "info/tS"));

            var order = 0;
            var objects = Sub(layer, "obj");
            if (objects is not null)
            {
                foreach (var item in objects.Nodes)
                {
                    var objectSet = NodeStr(Sub(item, "oS"));
                    var asset = mapTop is null ? null : Sub(mapTop, $"Obj/{objectSet}.img");
                    var source = asset is null
                        ? null
                        : Sub(asset, $"{NodeStr(Sub(item, "l0"))}/{NodeStr(Sub(item, "l1"))}/{NodeStr(Sub(item, "l2"))}");
                    var frame = FrameAt(source, 0);
                    if (frame is not null)
                        placements.Add(new Placement(layerNumber, 0, NodeInt(item, "z"), order,
                            frame, NodeInt(item, "x"), NodeInt(item, "y"), NodeInt(item, "f") != 0, mapTop));
                    order++;
                }
            }

            order = 0;
            var tiles = Sub(layer, "tile");
            var tileAsset = tileSet.Length == 0 || mapTop is null ? null : Sub(mapTop, $"Tile/{tileSet}.img");
            if (tiles is not null)
            {
                foreach (var item in tiles.Nodes)
                {
                    var source = tileAsset is null
                        ? null
                        : Sub(tileAsset, $"{NodeStr(Sub(item, "u"))}/{NodeInt(item, "no")}");
                    var frame = FrameAt(source, 0);
                    if (frame is not null)
                        placements.Add(new Placement(layerNumber, 1, NodeInt(frame, "z"),
                            IntName(item.Text, order), frame, NodeInt(item, "x"), NodeInt(item, "y"), false, mapTop));
                    order++;
                }
            }
        }

        placements.Sort((a, b) =>
        {
            var cmp = a.Layer.CompareTo(b.Layer);
            if (cmp != 0) return cmp;
            cmp = a.Kind.CompareTo(b.Kind);
            if (cmp != 0) return cmp;
            cmp = a.Z.CompareTo(b.Z);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });
        foreach (var p in placements)
            DrawSprite(output, bounds, scale, p.Frame, p.Source, p.X, p.Y, p.Flip);

        var rawLife = Sub(root, "life");
        if (rawLife is not null)
        {
            // HaCreator 的调度是「先全部 mob 再全部 npc」，与源顺序无关。
            foreach (var (wanted, component, pack) in new[]
                     {
                         ("m", "Mob", FindTop("Mob")),
                         ("n", "Npc", FindTop("Npc")),
                     })
            {
                foreach (var item in rawLife.Nodes)
                {
                    if (NodeStr(Sub(item, "type")) != wanted) continue;
                    var asset = LinkedLifeAsset(pack, component, NodeStr(Sub(item, "id")));
                    if (asset is null) continue;
                    var action = Sub(asset, "stand") ?? Sub(asset, "fly") ?? Sub(asset, "move");
                    var frame = FrameAt(action, 0);
                    if (frame is null) continue;
                    var cyNode = Sub(item, "cy");
                    var y = cyNode is null ? NodeInt(item, "y") : NodeInt(item, "cy");
                    DrawSprite(output, bounds, scale, frame, pack,
                        NodeInt(item, "x"), y, NodeInt(item, "f") != 0);
                }
            }
        }

        // reactor 精灵在 旧实现里没有提供数据源（self.reactor 为 None），这里保持同样的空缺。
        foreach (var item in backItems)
            if (NodeInt(item, "front") != 0)
                DrawBackground(output, bounds, scale, item, 0, mapTop);

        return output;
    }

    private static void DrawBackground(Bitmap32 output, MapBounds bounds, double scale, Wz_Node item, int timeMs, Wz_Node? mapTop)
    {
        var backgroundSet = NodeStr(Sub(item, "bS"));
        var asset = mapTop is null ? null : Sub(mapTop, $"Back/{backgroundSet}.img");
        if (asset is null) return;
        var number = NodeInt(item, "no").ToString(CultureInfo.InvariantCulture);
        var source = NodeInt(item, "ani") != 0 ? Sub(asset, $"ani/{number}") : Sub(asset, $"back/{number}");
        var frame = FrameAt(source, timeMs);
        if (frame is null) return;
        var pixels = Pixels(frame, mapTop);
        if (pixels is null || pixels.W <= 1 || pixels.H <= 1) return;

        var (originX, originY) = Origin(frame);
        var anchorX = NodeInt(item, "x");
        var anchorY = NodeInt(item, "y");
        var flip = NodeInt(item, "f") != 0;
        var alpha = Math.Clamp(NodeInt(item, "a", 255), 0, 255);
        var bgType = NodeInt(item, "type");
        var stepX = NodeInt(item, "cx");
        if (stepX == 0) stepX = pixels.W;
        var stepY = NodeInt(item, "cy");
        if (stepY == 0) stepY = pixels.H;

        if (bgType is 4 or 6 && stepX > 0)
            anchorX += (int)((NodeInt(item, "rx") * (double)Math.Max(0, timeMs) / 200.0) % stepX);
        if (bgType is 5 or 7 && stepY > 0)
            anchorY += (int)((NodeInt(item, "ry") * (double)Math.Max(0, timeMs) / 200.0) % stepY);

        var horizontal = bgType is 1 or 3 or 4 or 6 or 7;
        var vertical = bgType is 2 or 3 or 5 or 6 or 7;
        var xOffsets = horizontal
            ? CopyOffsets(anchorX - (flip ? pixels.W - originX : originX), pixels.W, stepX, bounds.Left, bounds.Right)
            : new List<int> { 0 };
        var yOffsets = vertical
            ? CopyOffsets(anchorY - originY, pixels.H, stepY, bounds.Top, bounds.Bottom)
            : new List<int> { 0 };

        foreach (var dx in xOffsets)
            foreach (var dy in yOffsets)
                DrawSprite(output, bounds, scale, frame, mapTop, anchorX + dx, anchorY + dy, flip, alpha);
    }

    private static List<int> CopyOffsets(int firstLeft, int size, int step, int viewStart, int viewEnd)
    {
        if (step <= 0) return new List<int> { 0 };
        var first = (long)Math.Floor((viewStart - firstLeft - size) / (double)step) + 1;
        var last = (long)Math.Ceiling((viewEnd - firstLeft) / (double)step) - 1;
        if (last < first) return new List<int> { 0 };
        // 畸形地图会给巨大位图配 cx/cy=1，硬性封顶避免几百万次粘贴请求。
        if (last - first > 10_000) last = first + 10_000;
        var offsets = new List<int>();
        for (var i = first; i <= last; i++) offsets.Add((int)(i * step));
        return offsets;
    }

    private static void DrawSprite(Bitmap32 output, MapBounds bounds, double scale, Wz_Node canvas,
        Wz_Node? source, int anchorX, int anchorY, bool flip = false, int alpha = 255)
    {
        var pixels = Pixels(canvas, source);
        if (pixels is null) return;
        var (originX, originY) = Origin(canvas);
        var left = anchorX - originX;
        if (flip)
        {
            left = anchorX - pixels.W + originX;
            pixels = FlipH(pixels);
        }
        var top = anchorY - originY;
        PasteScaled(output, bounds, scale, pixels, left, top, alpha);
    }

    private static void PasteScaled(Bitmap32 output, MapBounds bounds, double scale, Bitmap32 pixels,
        int left, int top, int alpha)
    {
        var x = (int)Math.Round((left - bounds.Left) * scale, MidpointRounding.ToEven);
        var y = (int)Math.Round((top - bounds.Top) * scale, MidpointRounding.ToEven);
        if (scale != 1.0)
        {
            // 两条边都按世界坐标换算，相邻图块在非整数缩放下才共用同一条输出边界。
            var right = (int)Math.Round((left + pixels.W - bounds.Left) * scale, MidpointRounding.ToEven);
            var bottom = (int)Math.Round((top + pixels.H - bounds.Top) * scale, MidpointRounding.ToEven);
            var width = Math.Max(1, right - x);
            var height = Math.Max(1, bottom - y);
            pixels = ResizeNearest(pixels, width, height);
        }
        if (alpha < 255) pixels = ApplyAlpha(pixels, alpha);
        Sheet.Blit(output, pixels, x, y, replace: false);
    }

    private static (int X, int Y) Origin(Wz_Node canvas)
    {
        var node = Sub(canvas, "origin");
        return node?.Value is Wz_Vector vector ? (vector.X, vector.Y) : (0, 0);
    }

    // ── 画布与帧解析 ────────────────────────────────────────────────────

    private static List<Wz_Node> Frames(Wz_Node? source)
    {
        source = ResolveUol(source);
        if (source is null) return new List<Wz_Node>();
        if (source.Value is Wz_Png) return new List<Wz_Node> { source };

        var result = new List<Wz_Node>();
        foreach (var child in NumericChildren(source))
        {
            var real = ResolveUol(child);
            if (real is null) continue;
            if (real.Value is Wz_Png) result.Add(real);
            else if (real.Nodes.Count > 0) result.AddRange(Frames(real));
        }
        if (result.Count == 0)
        {
            // 少数资源把唯一一张画布放在非数字包装节点下。
            foreach (var child in source.Nodes)
            {
                var real = ResolveUol(child);
                if (real?.Value is Wz_Png) result.Add(real);
            }
        }
        return result;
    }

    private static Wz_Node? FrameAt(Wz_Node? source, int timeMs)
    {
        var frames = Frames(source);
        if (frames.Count == 0) return null;
        if (frames.Count == 1) return frames[0];
        var delays = frames.Select(f => Math.Max(1, NodeInt(f, "delay", 100))).ToList();
        var cursor = Math.Max(0, timeMs) % delays.Sum();
        for (var i = 0; i < frames.Count; i++)
        {
            if (cursor < delays[i]) return frames[i];
            cursor -= delays[i];
        }
        return frames[^1];
    }

    /// <summary>
    /// 跟随画布的 UOL 与 _outlink/_inlink 链，返回真正带像素的画布。
    /// 这份客户端里占位画布也是 1×1 Wz_Png，所以「有 value」不能当作「有像素」。
    /// </summary>
    private static Wz_Node? ResolveCanvasLink(Wz_Node prop, Wz_Node? sourceRoot)
    {
        var seen = new HashSet<Wz_Node>(ReferenceEqualityComparer.Instance);
        var current = prop;
        for (var depth = 0; depth < 8; depth++)
        {
            if (current is null || !seen.Add(current)) return null;
            if (current.Value is Wz_Uol)
            {
                current = ResolveUol(current);
                if (current is null) return null;
                continue;
            }
            if (current.Value is not Wz_Png) return null;

            var outlink = NodeStr(Sub(current, "_outlink"));
            if (outlink.Length > 0)
            {
                var target = NavigateLink(sourceRoot, outlink);
                if (target is not null)
                {
                    current = target;
                    continue;
                }
            }

            var inlink = NodeStr(Sub(current, "_inlink"));
            if (inlink.Length > 0)
            {
                var imageRoot = ImageRoot(current);
                var target = imageRoot is null ? null : Sub(imageRoot, inlink);
                if (target is not null)
                {
                    current = target;
                    continue;
                }
            }

            return HasPixels(current) ? current : null;
        }
        return null;
    }

    private static Bitmap32? Pixels(Wz_Node canvas, Wz_Node? sourceRoot)
    {
        var logical = ResolveUol(canvas);
        if (logical is null || logical.Value is not Wz_Png) return null;

        var pixelCanvas = logical;
        if (Sub(logical, "_outlink") is not null || Sub(logical, "_inlink") is not null || !HasPixels(logical))
        {
            var linked = ResolveCanvasLink(logical, sourceRoot);
            if (linked is null) return null;
            pixelCanvas = linked;
        }
        if (pixelCanvas.Value is not Wz_Png png) return null;
        if (DecodedCanvases.TryGetValue(pixelCanvas, out var cached)) return cached;

        try
        {
            var bitmap = Png.Decode(png);
            DecodedCanvases[pixelCanvas] = bitmap;
            CanvasOrder.Enqueue(pixelCanvas);
            _canvasCacheBytes += (long)bitmap.W * bitmap.H * 4;
            while (_canvasCacheBytes > CanvasCacheLimit && DecodedCanvases.Count > 1)
            {
                var oldest = CanvasOrder.Dequeue();
                if (!DecodedCanvases.Remove(oldest, out var evicted)) continue;
                _canvasCacheBytes -= (long)evicted.W * evicted.H * 4;
            }
            return bitmap;
        }
        catch
        {
            // 个别私有服/旧版归档的画布元数据指向已损坏的字节，跳过它比整张地图失败好。
            return null;
        }
    }

    private static bool HasPixels(Wz_Node node)
    {
        if (node.Value is not Wz_Png png) return false;
        return png.Width > 0 && png.Height > 0;
    }

    // ── 资源查找 ────────────────────────────────────────────────────────

    private static Wz_Node? LinkedLifeAsset(Wz_Node? packRoot, string component, string assetId)
    {
        var asset = IdAsset(packRoot, component, assetId);
        var seen = new HashSet<Wz_Node>(ReferenceEqualityComparer.Instance);
        for (var hop = 0; hop < 8; hop++)
        {
            if (asset is null || !seen.Add(asset)) return asset;
            var link = Sub(asset, "info/link");
            if (link is null) return asset;
            asset = IdAsset(packRoot, component, NodeStr(link));
        }
        return asset;
    }

    private static Wz_Node? IdAsset(Wz_Node? packRoot, string component, string assetId)
    {
        if (packRoot is null) return null;
        var padded = assetId.PadLeft(7, '0');
        var direct = Sub(packRoot, $"{padded}.img");
        if (direct is not null) return direct;
        return Sub(packRoot, $"{component}/{padded}.img");
    }

    /// <summary>_outlink 是包内绝对路径；首段可能是分类名前缀，原路径走不通就去掉首段再试。</summary>
    private static Wz_Node? NavigateLink(Wz_Node? root, string path)
    {
        if (root is null) return null;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var hit = WalkPath(root, parts);
        if (hit is not null) return hit;
        return parts.Length > 1 ? WalkPath(root, parts.Skip(1).ToArray()) : null;
    }

    private static Wz_Node? WalkPath(Wz_Node root, string[] parts)
    {
        var current = Extract(root);
        foreach (var part in parts)
        {
            if (current is null) return null;
            var next = current.Nodes[part];
            if (next is null) return null;
            current = Extract(next);
        }
        return current;
    }

    private static Wz_Node? ImageRoot(Wz_Node node)
    {
        var current = node;
        while (current.ParentNode is not null) current = current.ParentNode;
        return current;
    }

    private static Wz_Node? ResolveUol(Wz_Node? node)
    {
        var seen = new HashSet<Wz_Node>(ReferenceEqualityComparer.Instance);
        var current = node;
        for (var hop = 0; hop < 16; hop++)
        {
            if (current is null) return null;
            if (current.Value is not Wz_Uol uol) return current;
            if (!seen.Add(current)) return null;
            current = uol.HandleUol(current);
        }
        return null;
    }

    /// <summary>按相对路径取节点；中间遇到 .img 会解包，缺节点返回 null。</summary>
    private static Wz_Node? Sub(Wz_Node? node, string path)
    {
        if (node is null) return null;
        var current = Extract(node);
        if (current is null || path.Length == 0) return current;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null) return null;
            var next = current.Nodes[part];
            if (next is null) return null;
            current = Extract(next);
        }
        return current;
    }

    private static Wz_Node? Extract(Wz_Node node)
    {
        if (node.Value is not Wz_Image image) return node;
        if (!image.TryExtract()) return null;
        return image.Node;
    }

    private static List<Wz_Node> NumericChildren(Wz_Node node)
    {
        return node.Nodes
            .Where(child => int.TryParse(child.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .OrderBy(child => int.Parse(child.Text, CultureInfo.InvariantCulture))
            .ToList();
    }

    // ── 地图节点定位 ────────────────────────────────────────────────────

    private static (Wz_Node Root, string SourceId) MapRoot(string mapId)
    {
        var requested = NormalizeMapId(mapId);
        lock (MapStateGate)
        {
            if (MapRootCache.TryGetValue(requested, out var cached)) return cached;
        }

        var current = requested;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var hop = 0; hop < 16; hop++)
        {
            if (!seen.Add(current)) throw new InvalidOperationException($"linked-map cycle while loading {requested}");
            var image = MapImageNode(current) ?? throw new FileNotFoundException($"'map {requested} not found'");
            var link = Sub(image, "info/link");
            if (link is null)
            {
                var result = (image, current);
                lock (MapStateGate) MapRootCache[requested] = result;
                return result;
            }
            current = NormalizeMapId(NodeStr(link));
        }
        throw new InvalidOperationException($"too many linked-map hops while loading {requested}");
    }

    private static Wz_Node? MapImageNode(string mapId)
    {
        var padded = mapId.PadLeft(9, '0');
        var mapDir = Sub(FindTop("Map"), "Map");
        if (mapDir is null) return null;
        var direct = Sub(mapDir, $"Map{padded[0]}/{padded}.img");
        if (direct is not null) return direct;

        // 少数包把地图放到别的 Map<N> 分片；与 HaCreator 一样扫一遍分片目录。
        foreach (var shard in mapDir.Nodes)
        {
            var candidate = Sub(shard, $"{padded}.img");
            if (candidate is not null) return candidate;
        }
        return null;
    }

    private static bool HasMap(string mapId)
    {
        try
        {
            MapRoot(mapId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeMapId(string mapId)
    {
        var text = (mapId ?? "").Trim();
        if (text.Length == 0 || !text.All(char.IsDigit))
            throw new FormatException($"invalid map id '{mapId}'");
        return long.Parse(text, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    }

    // ── map_image / map_canvas / map_content_bounds / map_minimap ──────

    private static JsonNode CmdMapImage(JsonObject args)
    {
        var (path, meta) = RenderMapPng(args);
        var result = new JsonObject { ["png"] = path };
        foreach (var kv in meta) result[kv.Key] = kv.Value?.DeepClone();
        return result;
    }

    private static (string Path, JsonObject Meta) RenderMapPng(JsonObject args)
    {
        if (args["id"] is null) throw new InvalidOperationException("map_image 需要 id");
        var mid = ScalarText(args["id"]);
        var (img, info, bounds, scale) = RenderFullMap(mid, DoubleOr(args, "scale", 1.0));

        MapBounds? crop = null;
        if (TryBoundsFromArgs(args, out var cropBounds))
        {
            // 用实际渲染尺寸反算缩放：请求值会被 MAX_MAP_WORLD 压掉，拿它换算会裁错区域。
            var sx = img.W / Math.Max(1.0, bounds.Width);
            var sy = img.H / Math.Max(1.0, bounds.Height);
            var left = Math.Max(0, (int)((cropBounds.Left - bounds.Left) * sx));
            var top = Math.Max(0, (int)((cropBounds.Top - bounds.Top) * sy));
            var right = Math.Min(img.W, (int)((cropBounds.Right - bounds.Left) * sx));
            var bottom = Math.Min(img.H, (int)((cropBounds.Bottom - bounds.Top) * sy));
            if (right > left && bottom > top)
            {
                img = Crop(img, left, top, right - left, bottom - top);
                crop = cropBounds;
            }
        }

        var targetW = IntOr(args, "target_w", 0);
        var targetH = IntOr(args, "target_h", 0);
        string? padded = null;
        if (targetW > 0 && targetH > 0) (img, padded) = FitToViewport(img, targetW, targetH);

        var key = StableHash(string.Join("|",
            mid, scale.ToString("R", CultureInfo.InvariantCulture),
            crop?.Left, crop?.Top, crop?.Right, crop?.Bottom,
            targetW, targetH, IntOr(args, "time_ms", 0)));
        var outDir = OutDir;
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"map_{key}.png");
        File.WriteAllBytes(path, Png.Encode(img));

        var meta = new JsonObject
        {
            ["w"] = img.W,
            ["h"] = img.H,
            ["viewport"] = crop?.ToJson(),
            ["scale"] = Math.Round(scale, 6),
            ["mapId"] = mid,
            ["worldScale"] = scale,
            ["bounds"] = bounds.ToJson(),
            ["padded"] = padded,
            ["name"] = info.Name,
        };
        return (path, meta);
    }

    private static JsonNode CmdMapCanvas(JsonObject args)
    {
        if (args["id"] is null) throw new InvalidOperationException("map_canvas 需要 id");
        var mid = ScalarText(args["id"]);
        var maxScale = DoubleOr(args, "scale", 0.0);
        if (maxScale == 0) maxScale = 1.0;

        // 前端要 1:1 像素，所以按最长边给足（上限 4096）；先缩后放会糊。
        var probeBounds = Describe(mid).Bounds;
        var span = Math.Max(1, Math.Max(probeBounds.Width, probeBounds.Height));
        if (span > 4096) maxScale = Math.Min(maxScale, 4096.0 / span);

        var (img, info, bounds, scale) = RenderFullMap(mid, maxScale);
        if (img.W > img.H * 8 && img.H > 0)
        {
            var keep = img.H * 8;
            var start = Math.Max(0, (img.W - keep) / 2);
            img = Crop(img, start, 0, keep, img.H);
        }

        var bbox = AlphaBBox(img);
        MapBounds content;
        if (bbox is not null)
        {
            img = Crop(img, bbox.Value.X0, bbox.Value.Y0, bbox.Value.X1 - bbox.Value.X0, bbox.Value.Y1 - bbox.Value.Y0);
            content = new MapBounds(
                bounds.Left + (int)Math.Round(bbox.Value.X0 / scale, MidpointRounding.ToEven),
                bounds.Top + (int)Math.Round(bbox.Value.Y0 / scale, MidpointRounding.ToEven),
                bounds.Left + (int)Math.Round(bbox.Value.X1 / scale, MidpointRounding.ToEven),
                bounds.Top + (int)Math.Round(bbox.Value.Y1 / scale, MidpointRounding.ToEven));
        }
        else
        {
            content = bounds;
        }

        // 裁剪后按内容宽重算真实比例，避免与请求 scale 有舍入差。
        var realScale = img.W / (double)Math.Max(1, content.Width);
        var digest = StableHash(string.Join("|",
            mid, realScale.ToString("R", CultureInfo.InvariantCulture), content.Left, content.Top));
        var outDir = OutDir;
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"mapcanvas_{digest}.png");
        File.WriteAllBytes(path, Png.Encode(img));

        return new JsonObject
        {
            ["png"] = path,
            ["w"] = img.W,
            ["h"] = img.H,
            ["scale"] = Math.Round(realScale, 6),
            ["contentBounds"] = content.ToJson(),
            ["vrBounds"] = bounds.ToJson(),
            ["mapId"] = mid,
            ["name"] = info.Name,
        };
    }

    private static JsonNode CmdMapContentBounds(JsonObject args)
    {
        if (args["id"] is null) throw new InvalidOperationException("map_content_bounds 需要 id");
        var mid = ScalarText(args["id"]);
        var (img, info, bounds, scale) = RenderFullMap(mid, DoubleOr(args, "probe_scale", 0.25));
        var bbox = AlphaBBox(img) ?? throw new InvalidOperationException($"{mid} 渲染结果全透明");

        var inv = 1.0 / scale;
        var left = bounds.Left + (int)(bbox.X0 * inv);
        var top = bounds.Top + (int)(bbox.Y0 * inv);
        var right = bounds.Left + (int)(bbox.X1 * inv);
        var bottom = bounds.Top + (int)(bbox.Y1 * inv);
        var content = new MapBounds(left, top, right, bottom);

        return new JsonObject
        {
            ["id"] = mid,
            ["name"] = info.Name,
            ["street"] = info.Street,
            ["contentBounds"] = content.ToJson(),
            ["vrBounds"] = bounds.ToJson(),
            ["probeScale"] = scale,
            ["probePixels"] = new JsonArray(img.W, img.H),
            ["note"] = "VR 边界是相机用的，可能远大于画面；背景请用 contentBounds",
        };
    }

    private static JsonNode CmdMapMinimap(JsonObject args)
    {
        if (args["id"] is null) throw new InvalidOperationException("map_minimap 需要 id");
        var mid = ScalarText(args["id"]);
        var (root, _) = MapRoot(NormalizeMapId(mid));
        var canvas = ResolveUol(Sub(root, "miniMap/canvas"));
        if (canvas is null || canvas.Value is not Wz_Png)
            throw new FileNotFoundException($"{mid} 没有小地图");
        var img = Pixels(canvas, FindTop("Map")) ?? throw new FileNotFoundException($"{mid} 没有小地图");
        var bytes = Png.Encode(img);
        var outDir = OutDir;
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"minimap_{mid}.png");
        File.WriteAllBytes(path, bytes);
        return new JsonObject { ["png"] = path, ["bytes"] = bytes.Length, ["mapId"] = mid };
    }

    private static JsonNode CmdMapViewportB64(JsonObject args)
    {
        var key = ViewportCacheKey(args);
        var targetW = IntOr(args, "target_w", 0);
        var targetH = IntOr(args, "target_h", 0);
        lock (MapStateGate)
        {
            if (MapPngCache.TryGetValue(key, out var cached) && File.Exists(cached.Path))
            {
                // 缓存命中也必须校验目标尺寸：裁错区域的那版会按同一个 key 一直命中。
                var cachedW = cached.Meta["w"]?.GetValue<int>() ?? 0;
                var cachedH = cached.Meta["h"]?.GetValue<int>() ?? 0;
                var matches = (targetW == 0 || cachedW == targetW) && (targetH == 0 || cachedH == targetH);
                if (matches)
                {
                    var data = File.ReadAllBytes(cached.Path);
                    var hit = new JsonObject
                    {
                        ["png_base64"] = Convert.ToBase64String(data),
                        ["cached"] = true,
                        ["bytes"] = data.Length,
                        ["key"] = key,
                    };
                    foreach (var kv in cached.Meta) hit[kv.Key] = kv.Value?.DeepClone();
                    return hit;
                }
                MapPngCache.Remove(key);
            }
        }

        var (path, meta) = RenderMapPng(args);
        var bytes = File.ReadAllBytes(path);
        lock (MapStateGate)
        {
            if (MapPngCache.Count >= 60) MapPngCache.Remove(MapPngCache.Keys.First());
            MapPngCache[key] = (path, meta);
        }
        var result = new JsonObject
        {
            ["png_base64"] = Convert.ToBase64String(bytes),
            ["cached"] = false,
            ["bytes"] = bytes.Length,
            ["key"] = key,
        };
        foreach (var kv in meta) result[kv.Key] = kv.Value?.DeepClone();
        return result;
    }

    private static string ViewportCacheKey(JsonObject args)
    {
        var keys = new[] { "bottom", "id", "left", "right", "scale", "top", "v" };
        var parts = new List<string>();
        foreach (var key in keys)
        {
            var value = key == "v" ? JsonValue.Create(MapCacheSchema) : args[key]?.DeepClone();
            parts.Add($"\"{key}\": {value?.ToJsonString() ?? "null"}");
        }
        return "{" + string.Join(", ", parts) + "}";
    }

    private static bool TryBoundsFromArgs(JsonObject args, out MapBounds bounds)
    {
        bounds = new MapBounds(0, 0, 0, 0);
        if (args["left"] is null || args["top"] is null || args["right"] is null || args["bottom"] is null)
            return false;
        var left = (int)ScalarLong(args["left"]);
        var top = (int)ScalarLong(args["top"]);
        var right = (int)ScalarLong(args["right"]);
        var bottom = (int)ScalarLong(args["bottom"]);
        bounds = new MapBounds(left, top, right, bottom);
        return true;
    }

    // ── 位图工具 ────────────────────────────────────────────────────────

    private static (Bitmap32 Img, string Padded) FitToViewport(Bitmap32 img, int targetW, int targetH)
    {
        var bbox = AlphaBBox(img);
        if (bbox is not null) img = Crop(img, 0, bbox.Value.Y0, img.W, bbox.Value.Y1 - bbox.Value.Y0);

        var scaledH = Math.Max(1, (int)Math.Round(img.H * (double)targetW / Math.Max(1, img.W), MidpointRounding.ToEven));
        var scaled = ResizeHigh(img, targetW, scaledH);

        // 地图片很扁时纯按宽度缩放，天空会占掉大半个屏幕；把空档压到 25% 以内。
        var maxPad = (int)(targetH * 0.25);
        if (targetH - scaled.H > maxPad)
        {
            var need = targetH - maxPad;
            var k = need / (double)Math.Max(1, scaled.H);
            scaled = ResizeHigh(scaled, Math.Max(1, (int)Math.Round(scaled.W * k, MidpointRounding.ToEven)), need);
        }

        if (scaled.H >= targetH)
        {
            var top = Math.Max(0, scaled.H - targetH);
            return (Crop(scaled, 0, top, targetW, top + targetH), "crop");
        }

        var rows = Math.Min(24, scaled.H);
        var sky = EdgeColor(scaled, rows, fromTop: true);
        var filled = new Bitmap32(targetW, targetH);
        FillTop(filled, targetH - scaled.H, sky);
        Sheet.Blit(filled, scaled, 0, targetH - scaled.H, replace: false);
        return (filled, "edges");
    }

    private static (byte R, byte G, byte B) EdgeColor(Bitmap32 source, int rows, bool fromTop)
    {
        rows = Math.Clamp(rows, 1, source.H);
        var y0 = fromTop ? 0 : source.H - rows;
        double r = 0, g = 0, b = 0;
        for (var y = y0; y < y0 + rows; y++)
        {
            for (var x = 0; x < source.W; x++)
            {
                var i = (y * source.W + x) * 4;
                r += source.Px[i];
                g += source.Px[i + 1];
                b += source.Px[i + 2];
            }
        }
        var count = (double)rows * source.W;
        return ((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    private static void FillTop(Bitmap32 img, int rows, (byte R, byte G, byte B) color)
    {
        rows = Math.Clamp(rows, 0, img.H);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < img.W; x++)
            {
                var i = (y * img.W + x) * 4;
                img.Px[i] = color.R;
                img.Px[i + 1] = color.G;
                img.Px[i + 2] = color.B;
                img.Px[i + 3] = 255;
            }
        }
    }

    private static (int X0, int Y0, int X1, int Y1)? AlphaBBox(Bitmap32 img)
    {
        var minX = img.W;
        var minY = img.H;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < img.H; y++)
        {
            var row = y * img.W * 4;
            for (var x = 0; x < img.W; x++)
            {
                if (img.Px[row + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                maxY = y;
            }
        }
        if (maxX < 0) return null;
        return (minX, minY, maxX + 1, maxY + 1);
    }

    private static Bitmap32 Crop(Bitmap32 src, int x, int y, int width, int height)
    {
        width = Math.Max(1, Math.Min(width, src.W - x));
        height = Math.Max(1, Math.Min(height, src.H - y));
        x = Math.Clamp(x, 0, src.W - 1);
        y = Math.Clamp(y, 0, src.H - 1);
        width = Math.Min(width, src.W - x);
        height = Math.Min(height, src.H - y);
        var dst = new Bitmap32(width, height);
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(src.Px, ((y + row) * src.W + x) * 4, dst.Px, row * width * 4, width * 4);
        return dst;
    }

    private static Bitmap32 FlipH(Bitmap32 src)
    {
        var dst = new Bitmap32(src.W, src.H);
        for (var y = 0; y < src.H; y++)
        {
            for (var x = 0; x < src.W; x++)
            {
                var si = (y * src.W + x) * 4;
                var di = (y * src.W + (src.W - 1 - x)) * 4;
                dst.Px[di] = src.Px[si];
                dst.Px[di + 1] = src.Px[si + 1];
                dst.Px[di + 2] = src.Px[si + 2];
                dst.Px[di + 3] = src.Px[si + 3];
            }
        }
        return dst;
    }

    private static Bitmap32 ApplyAlpha(Bitmap32 src, int alpha)
    {
        var dst = new Bitmap32(src.W, src.H);
        Buffer.BlockCopy(src.Px, 0, dst.Px, 0, src.Px.Length);
        for (var i = 3; i < dst.Px.Length; i += 4)
            dst.Px[i] = (byte)(dst.Px[i] * alpha / 255);
        return dst;
    }

    private static Bitmap32 ResizeNearest(Bitmap32 src, int width, int height)
    {
        if (src.W == width && src.H == height) return src;
        var dst = new Bitmap32(width, height);
        for (var y = 0; y < height; y++)
        {
            // Pillow 的 NEAREST 取源坐标 floor((dst + 0.5) * src / dst)，不是简单的比例截断。
            var sy = (int)Math.Floor((y + 0.5) * src.H / height);
            sy = Math.Clamp(sy, 0, src.H - 1);
            for (var x = 0; x < width; x++)
            {
                var sx = (int)Math.Floor((x + 0.5) * src.W / width);
                sx = Math.Clamp(sx, 0, src.W - 1);
                var si = (sy * src.W + sx) * 4;
                var di = (y * width + x) * 4;
                dst.Px[di] = src.Px[si];
                dst.Px[di + 1] = src.Px[si + 1];
                dst.Px[di + 2] = src.Px[si + 2];
                dst.Px[di + 3] = src.Px[si + 3];
            }
        }
        return dst;
    }

    /// <summary>高质量缩放（Pillow LANCZOS 的近似）。失败时退回最近邻，不因滤镜问题丢图。</summary>
    private static Bitmap32 ResizeHigh(Bitmap32 src, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (src.W == width && src.H == height) return src;
        try
        {
            using var source = ToSkia(src);
            using var resized = source.Resize(
                new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
            return resized is null ? ResizeNearest(src, width, height) : FromSkia(resized);
        }
        catch
        {
            return ResizeNearest(src, width, height);
        }
    }

    private static SKBitmap ToSkia(Bitmap32 src)
    {
        var bitmap = new SKBitmap(new SKImageInfo(src.W, src.H, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(src.Px, 0, bitmap.GetPixels(), src.Px.Length);
        return bitmap;
    }

    private static Bitmap32 FromSkia(SKBitmap bitmap)
    {
        var dst = new Bitmap32(bitmap.Width, bitmap.Height);
        Marshal.Copy(bitmap.GetPixels(), dst.Px, 0, dst.Px.Length);
        return dst;
    }

    // ── JSON 与参数 ─────────────────────────────────────────────────────

    private static JsonObject MapRowJson(MapNameRow row) => new()
    {
        ["id"] = row.Id,
        ["name"] = row.Name,
        ["street"] = row.Street,
    };

    private static JsonObject LifeJson(LifeEntry life) => new()
    {
        ["index"] = life.Index,
        ["type"] = life.Type,
        ["id"] = life.Id,
        ["x"] = life.X,
        ["y"] = life.Y,
        ["cy"] = life.Cy,
        ["rx0"] = life.Rx0,
        ["rx1"] = life.Rx1,
        ["flip"] = life.Flip,
        ["hide"] = life.Hide,
        ["mobTime"] = life.MobTime?.DeepClone(),
    };

    private static JsonObject PortalJson(PortalEntry portal) => new()
    {
        ["index"] = portal.Index,
        ["name"] = portal.Name,
        ["type"] = portal.Type,
        ["x"] = portal.X,
        ["y"] = portal.Y,
        ["targetMap"] = portal.TargetMap,
        ["targetName"] = portal.TargetName,
        ["script"] = portal.Script,
    };

    private static JsonObject FootholdJson(Foothold fh) => new()
    {
        ["layer"] = fh.Layer,
        ["platform"] = fh.Platform,
        ["id"] = fh.Id,
        ["x1"] = fh.X1,
        ["y1"] = fh.Y1,
        ["x2"] = fh.X2,
        ["y2"] = fh.Y2,
        ["prev"] = fh.Prev,
        ["next"] = fh.Next,
    };

    private static JsonObject RopeJson(RopeEntry rope) => new()
    {
        ["id"] = rope.Id,
        ["x"] = rope.X,
        ["y1"] = rope.Y1,
        ["y2"] = rope.Y2,
        ["ladder"] = rope.Ladder,
        ["uf"] = rope.Uf,
        ["page"] = rope.Page,
    };

    private static HashSet<string> ExcludeSet(JsonObject args)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (args["exclude"] is JsonArray array)
            foreach (var item in array) set.Add(ScalarText(item));
        return set;
    }

    private static int NodeInt(Wz_Node node, string key, int fallback = 0)
    {
        var child = Sub(node, key);
        if (child is null) return fallback;
        var value = child.Value;
        try
        {
            return value switch
            {
                int i => i,
                short s => s,
                long l => (int)l,
                byte b => b,
                float f => (int)f,
                double d => (int)d,
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (int)parsed,
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static int NodeIntOr(Wz_Node node, string key, int fallback)
    {
        var child = Sub(node, key);
        return child is null ? fallback : NodeInt(node, key, fallback);
    }

    private static string NodeStr(Wz_Node? node)
    {
        if (node?.Value is null) return "";
        return node.Value.ToString() ?? "";
    }

    private static int IntName(string name, int fallback = 0)
    {
        return int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static JsonNode? RawJson(object? value) => value switch
    {
        null => null,
        int i => JsonValue.Create(i),
        short s => JsonValue.Create((int)s),
        long l => JsonValue.Create(l),
        float f => JsonValue.Create(f),
        double d => JsonValue.Create(d),
        bool b => JsonValue.Create(b),
        _ => JsonValue.Create(value.ToString()),
    };

    private static string ScalarText(JsonNode? node)
    {
        if (node is not JsonValue value) return "";
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<bool>(out var flag)) return flag ? "true" : "false";
        if (value.TryGetValue<long>(out var integer)) return integer.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var number)) return number.ToString("R", CultureInfo.InvariantCulture);
        return node.ToJsonString();
    }

    private static long ScalarLong(JsonNode? node)
    {
        var text = ScalarText(node).Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return (long)number;
        throw new FormatException($"不是整数：{text}");
    }

    private static int IntOr(JsonObject args, string key, int fallback)
    {
        var node = args[key];
        if (node is null) return fallback;
        var text = ScalarText(node).Trim();
        if (text.Length == 0) return fallback;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new FormatException($"参数 {key} 不是整数：{text}");
            value = (long)number;
        }
        return value == 0 ? fallback : (int)value;
    }

    private static double DoubleOr(JsonObject args, string key, double fallback)
    {
        var node = args[key];
        if (node is null) return fallback;
        var text = ScalarText(node).Trim();
        if (text.Length == 0) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"参数 {key} 不是数字：{text}");
        return value == 0 ? fallback : value;
    }

    private static bool BoolArg(JsonObject args, string key)
    {
        var node = args[key];
        return node switch
        {
            null => false,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<string>(out var text) => text.Length > 0,
            JsonValue value when value.TryGetValue<long>(out var integer) => integer != 0,
            JsonArray array => array.Count > 0,
            JsonObject obj => obj.Count > 0,
            _ => false,
        };
    }

    private static void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static string StableHash(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return (hash % 1_000_000_000_000UL).ToString(CultureInfo.InvariantCulture);
    }

    private static double EnvDouble(string name, double fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }

    private static string OutDir
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("WZ_OUT_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".dsh", "wz-assets");
        }
    }
}
