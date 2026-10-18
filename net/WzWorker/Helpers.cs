// 各命令组共用的 WZ 访问工具。
//
// 拆到 partial 类外面（同一个 static class 的另一个文件）是刻意的：
// 每个命令组一个文件，互不改动，便于并行开发与单独 review。
using System.Text.Json.Nodes;
using WzComparerR2.WzLib;

namespace WzWorker;

internal static partial class Program
{
    /// <summary>把 "Mob/1210102.img" 拆成 (pack, rest)。</summary>
    internal static (string Pack, string Rel) SplitWzPath(string path)
    {
        var trimmed = (path ?? "").Replace('\\', '/').Trim('/');
        var i = trimmed.IndexOf('/');
        return i < 0 ? (trimmed, "") : (trimmed[..i], trimmed[(i + 1)..]);
    }

    /// <summary>按 WZ 路径取节点（自动展开 .img）。找不到返回 null。</summary>
    internal static Wz_Node? ResolveNode(string path)
    {
        var (pack, rest) = SplitWzPath(path);
        var root = FindTop(pack);
        if (root is null) return null;
        var node = rest.Length == 0 ? root : root.FindNodeByPath(rest.Replace('/', '\\'), true);
        if (node is null) return null;
        return node.Value is Wz_Image ? ExpandImage(node, path) : node;
    }

    /// <summary>String/Mob.img 之类的字符串表 → { id: 节点 }。</summary>
    internal static Dictionary<string, Wz_Node> StringTable(string table)
    {
        var outMap = new Dictionary<string, Wz_Node>(StringComparer.Ordinal);
        var node = Load().WzNode?.FindNodeByPath($@"String\{table}", true);
        if (node is null) return outMap;
        foreach (var c in node.Nodes) outMap[c.Text] = c;
        return outMap;
    }

    /// <summary>取节点里的名字（name / mapName / streetName；Eqp 多一层 group）。</summary>
    internal static string NodeName(Wz_Node? entry)
    {
        if (entry is null) return "";
        foreach (var key in new[] { "name", "mapName", "streetName" })
        {
            var n = entry.FindNodeByPath(key, true);
            if (n?.Value is not null) return n.Value.ToString() ?? "";
        }
        foreach (var g in entry.Nodes)
        {
            var n = g.FindNodeByPath("name", true);
            if (n?.Value is not null) return n.Value.ToString() ?? "";
        }
        return "";
    }

    /// <summary>把 info / mobInfo / npcInfo 这类属性节点摊平成键值（深一层足够覆盖实际数据）。</summary>
    internal static void Flatten(Wz_Node? node, JsonObject into, string prefix = "")
    {
        if (node is null) return;
        foreach (var c in node.Nodes)
        {
            var key = prefix.Length == 0 ? c.Text : $"{prefix}/{c.Text}";
            if (c.Value is Wz_Png) continue;
            if (c.Nodes.Count > 0 && c.Value is null)
            {
                Flatten(c, into, key);
                continue;
            }
            var v = c.Value;
            if (v is null) continue;
            into[key] = v switch
            {
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                short sh => JsonValue.Create((int)sh),
                float f => JsonValue.Create(f),
                double d => JsonValue.Create(d),
                bool b => JsonValue.Create(b),
                Wz_Vector vec => new JsonObject { ["x"] = vec.X, ["y"] = vec.Y },
                _ => JsonValue.Create(v.ToString()),
            };
        }
    }

    /// <summary>列出 img 的动作与帧数（面板的动作下拉与 detail 用）。</summary>
    internal static JsonArray AnimationsOf(Wz_Node body)
    {
        var arr = new JsonArray();
        foreach (var a in body.Nodes)
        {
            var frames = a.Nodes.Count;
            if (frames == 0)
            {
                var png = a.GetValue<Wz_Png>() ?? a.Value as Wz_Png;
                if (png is null) continue;
                arr.Add(new JsonObject { ["name"] = a.Text, ["frames"] = 1, ["w"] = png.Width, ["h"] = png.Height });
                continue;
            }
            var w = 0;
            var h = 0;
            foreach (var f in a.Nodes)
            {
                var r = ResolveCanvas(f);
                var png = r.GetValue<Wz_Png>() ?? r.Value as Wz_Png;
                if (png is null) continue;
                w = Math.Max(w, png.Width);
                h = Math.Max(h, png.Height);
            }
            arr.Add(new JsonObject { ["name"] = a.Text, ["frames"] = frames, ["w"] = w, ["h"] = h });
        }
        return arr;
    }

    /// <summary>mob / npc / eqp 的图片路径与节点（与旧实现的查找顺序一致）。</summary>
    internal static (string? Path, Wz_Node? Node) FindImg(string kind, string id)
    {
        var candidates = kind switch
        {
            "mob" => new[] { $"Mob\\{id}.img", $"Mob\\{id.PadLeft(7, '0')}.img" },
            "npc" => new[] { $"Npc\\{id}.img", $"Npc\\{id.PadLeft(7, '0')}.img" },
            "eqp" => new[] { $"Character\\{id}.img", $"Character\\{id.PadLeft(8, '0')}.img" },
            _ => Array.Empty<string>(),
        };
        var packName = kind switch { "mob" => "Mob", "npc" => "Npc", "eqp" => "Character", _ => "" };
        var root = FindTop(packName);
        if (root is null) return (null, null);
        foreach (var c in candidates)
        {
            var rel = c[(packName.Length + 1)..];
            var node = root.FindNodeByPath(rel, true);
            if (node is null) continue;
            var expanded = node.Value is Wz_Image ? ExpandImage(node, c) : node;
            return ($"{packName}/{rel}", expanded);
        }
        return (null, null);
    }

    /// <summary>跟随 info/link 指向的同型号 img。</summary>
    internal static string? LinkTarget(Wz_Node body)
    {
        var v = body.FindNodeByPath(@"info\link", true)?.Value?.ToString();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
