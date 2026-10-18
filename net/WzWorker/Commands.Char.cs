// 纸娃娃：char_parts / char_render / char_anim / char_ears / char_poses。
//
// 合成语义逐条移植自 旧实现：body 的 navel 是世界原点，其余部位按
// map/<anchor> 串到已注册的同名锚点上，最后按 Base/zmap.img 的槽位顺序排层。
// 跨帧画布取所有帧的并集，否则躯干呼吸的几像素位移会让整段动画抖动。
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WzComparerR2.WzLib;

namespace WzWorker;

internal static partial class Program
{
    // ── 与 旧实现 对齐的常量 ──────────────────────────────────────────────
    private static readonly string[] CharCategories = { "Body", "Head", "Hair", "Face", "Cap", "Coat", "Longcoat", "Pants", "Shoes", "Glove", "Cape", "Shield", "FaceAcc", "Glass", "Earring", "Weapon" };

    private static readonly Dictionary<string, string> CharCategoryDir = new()
    {
        ["Body"] = "",
        ["Head"] = "",
        ["Hair"] = "Hair",
        ["Face"] = "Face",
        ["Cap"] = "Cap",
        ["Coat"] = "Coat",
        ["Longcoat"] = "Longcoat",
        ["Pants"] = "Pants",
        ["Shoes"] = "Shoes",
        ["Glove"] = "Glove",
        ["Cape"] = "Cape",
        ["Shield"] = "Shield",
        ["FaceAcc"] = "Accessory",
        ["Glass"] = "Accessory",
        ["Earring"] = "Accessory",
        ["Weapon"] = "Weapon",
    };

    private static readonly Dictionary<string, string> CharDefaultAnchor = new()
    {
        ["Body"] = "navel",
        ["Head"] = "neck",
        ["Hair"] = "brow",
        ["Face"] = "brow",
        ["Cap"] = "navel",
        ["Coat"] = "navel",
        ["Longcoat"] = "navel",
        ["Pants"] = "navel",
        ["Shoes"] = "navel",
        ["Glove"] = "hand",
        ["Cape"] = "navel",
        ["Shield"] = "navel",
        ["FaceAcc"] = "brow",
        ["Glass"] = "brow",
        ["Earring"] = "brow",
        ["Weapon"] = "hand",
    };

    private static readonly string[] CharAnchorPriority = { "navel", "neck", "hand", "brow", "handMove" };

    // Base.wz/zmap.img 不在 Character 包内，所以这份内置表就是实际使用的顺序；
    // 与 旧的内置副本保持一致，从后往前画。
    private static readonly string[] CharDefaultZmap =
    {
        "Bd",
        "Hd",
        "Hr",
        "Fc",
        "At",
        "Af",
        "Am",
        "Ae",
        "As",
        "Ay",
        "Cp",
        "Ri",
        "Gv",
        "Wp",
        "Si",
        "So",
        "Pn",
        "Ws",
        "Ma",
        "Wg",
        "Sr",
        "Tm",
        "Sd",
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

    private static readonly Dictionary<string, int> CharBackFacingZOverride = new()
    {
        ["backHairBelowCapWide"] = 250,
        ["backHairBelowCapNarrow"] = 251,
        ["backHairBelowCap"] = 252,
        ["backHairOverCape"] = 248,
        ["backHair"] = 254,
        ["backCap"] = 260,
        ["backCapOverHair"] = 261,
        ["backCapAccessory"] = 262,
        ["backAccessoryEar"] = 263,
        ["backShield"] = 271,
        ["backWeaponOverShield"] = 274,
        ["capeBelowBody"] = 235,
        ["backCape"] = 238,
        ["cape"] = 239,
        ["capeBelowHair"] = 240,
        ["capeOverBody"] = 244,
        ["capeOverWepon"] = 246,
        ["capeOverHead"] = 265,
        ["weaponOverGlove"] = 280,
    };

    private static readonly Dictionary<string, int> CharBackFacingAbsoluteZ = new()
    {
        ["characterStart"] = 999,
        ["characterEnd"] = -999,
    };

    private static readonly HashSet<string> CharCapHidePartial = new(StringComparer.Ordinal)
    {
        "hairOverHead", "backHair", "hairBelowBody", "backHairBelowCap",
    };

    private static readonly HashSet<string> CharCapHideTop = new(StringComparer.Ordinal)
    {
        "hairOverHead", "backHair",
    };

    private static readonly string[] CharSupportedPoses =
    {
        "stand1", "stand2", "walk1", "walk2", "alert", "jump", "prone", "proneStab",
        "sit", "fly", "ladder", "rope", "heal",
        "swingO1", "swingO2", "swingO3", "swingT1", "swingT2", "swingT3",
        "stabO1", "stabO2", "stabT1", "stabT2", "shoot1", "shoot2", "shootF", "dead",
    };

    private static readonly string[] CharWeaponFallbackPoses = { "stand1", "stand2" };

    private static readonly HashSet<string> CharWeaponOptionalPoses = new(StringComparer.Ordinal) { "rope", "ladder" };

    private static readonly Dictionary<int, string> CharCategoryByIdPrefix = new()
    {
        [0] = "Body",
        [1] = "Head",
        [2] = "Face",
        [3] = "Hair",
        [4] = "Hair",
        [5] = "Face",
        [6] = "Hair",
        [100] = "Cap",
        [101] = "FaceAcc",
        [102] = "Glass",
        [103] = "Earring",
        [104] = "Coat",
        [105] = "Longcoat",
        [106] = "Pants",
        [107] = "Shoes",
        [108] = "Glove",
        [109] = "Shield",
        [110] = "Cape",
        [121] = "Weapon",
        [122] = "Weapon",
        [123] = "Weapon",
        [124] = "Weapon",
        [125] = "Weapon",
        [126] = "Weapon",
        [127] = "Weapon",
        [128] = "Weapon",
        [129] = "Weapon",
        [130] = "Weapon",
        [131] = "Weapon",
        [132] = "Weapon",
        [133] = "Weapon",
        [134] = "Weapon",
        [135] = "Weapon",
        [136] = "Weapon",
        [137] = "Weapon",
        [138] = "Weapon",
        [139] = "Weapon",
        [140] = "Weapon",
        [141] = "Weapon",
        [142] = "Weapon",
        [143] = "Weapon",
        [144] = "Weapon",
        [145] = "Weapon",
        [146] = "Weapon",
        [147] = "Weapon",
        [148] = "Weapon",
        [149] = "Weapon",
        [150] = "Weapon",
        [151] = "Weapon",
        [152] = "Weapon",
        [153] = "Weapon",
        [154] = "Weapon",
        [155] = "Weapon",
        [156] = "Weapon",
        [157] = "Weapon",
        [158] = "Weapon",
        [159] = "Weapon",
        [160] = "Weapon",
        [170] = "Weapon",
    };

    private static readonly Dictionary<string, (string Target, string? Token)> CharOverCapRemap = new()
    {
        ["accessoryEyeOverCap"] = ("accessoryEye", "Ay"),
        ["accessoryFaceOverCap"] = ("accessoryFace", "Af"),
        ["accessoryFaceUpperOverCap"] = ("accessoryFace", "Af"),
        ["cap"] = ("capBelowHairOverHead", null),
    };

    private static readonly Dictionary<int, string> CharEffectPosAnchor = new()
    {
        [-1] = "navel",
        [0] = "navel",
        [1] = "brow",
        [2] = "navel",
        [3] = "navel",
    };

    private static readonly HashSet<string> CharDynamicAnimAnchors = new(StringComparer.Ordinal) { "hand", "lHand" };

    private static readonly HashSet<string> CharHeadDerivedAnchors = new(StringComparer.Ordinal) { "brow", "neck", "handMove" };

    private static readonly HashSet<string> CharFrontCapSlots = new(StringComparer.Ordinal) { "cap", "capOverHair", "capAccessory" };

    private static readonly HashSet<string> CharAlwaysBackBelow = new(StringComparer.Ordinal) { "capAccessoryBelowBody" };

    private static readonly string[] CharDefaultEquips = { "00002000", "00012000", "00030000", "00020000" };

    // ── 进程内状态 ──────────────────────────────────────────────────────
    private static bool _charEnvReady;
    private static Wz_Node? _charRootCache;
    private static string[]? _charZmapCache;
    private static bool _charItemEffTried;
    private static Wz_Node? _charItemEffRoot;
    private static Dictionary<string, (string Name, string Group)>? _charEqpNames;
    private static readonly Dictionary<string, List<string>> CharPartsCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Wz_Node?> CharPartOpenCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<Wz_Node, Bitmap32> CharPixelCache = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, (string Path, int W, int H)> CharRenderCache = new(StringComparer.Ordinal);

    // ── 命令 ────────────────────────────────────────────────────────────

    /// <summary>可选部位清单。名字取 String/Eqp.img，编号按类目前缀扫目录。</summary>
    internal static JsonNode CmdCharParts(JsonObject args)
    {
        CharEnsureEnv();
        var wanted = new List<string>();
        if (args["categories"] is JsonArray categoriesArg)
        {
            foreach (var node in categoriesArg)
            {
                var category = node?.GetValue<string>() ?? "";
                if (category.Length > 0) wanted.Add(category);
            }
        }
        if (wanted.Count == 0) wanted.AddRange(CharCategories);

        var limit = args["limit"]?.GetValue<int>() ?? 0;
        if (limit == 0) limit = 40;
        var q = (args["q"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        var names = CharEqpNameTable();

        var categoriesJson = new JsonObject();
        foreach (var category in wanted)
        {
            var ids = CharListParts(category);
            var named = new List<(string Id, string Short, string Name, string Group)>();
            foreach (var raw in ids)
            {
                var key = raw.TrimStart('0');
                if (key.Length == 0) key = "0";
                var hasEntry = names.TryGetValue(key, out var entry);
                if (!hasEntry) hasEntry = names.TryGetValue(raw, out entry);
                var name = hasEntry ? entry.Name ?? "" : "";
                var group = hasEntry && !string.IsNullOrEmpty(entry.Group) ? entry.Group! : category;
                named.Add((raw, key, name, group));
            }
            if (q.Length > 0)
            {
                named = named
                    .Where(r => r.Name.ToLowerInvariant().Contains(q)
                        || r.Id.Contains(q, StringComparison.Ordinal)
                        || r.Short.Contains(q, StringComparison.Ordinal))
                    .ToList();
            }

            var itemsJson = new JsonArray();
            foreach (var row in named.Take(Math.Max(0, limit)))
            {
                itemsJson.Add(new JsonObject
                {
                    ["id"] = row.Id,
                    ["short"] = row.Short,
                    ["name"] = row.Name,
                    ["group"] = row.Group,
                });
            }
            categoriesJson[category] = new JsonObject
            {
                ["total"] = ids.Count,
                ["matched"] = named.Count,
                ["items"] = itemsJson,
            };
        }

        return new JsonObject
        {
            ["categories"] = categoriesJson,
            ["defaults"] = new JsonObject
            {
                ["Body"] = "00002000",
                ["Head"] = "00012000",
                ["Hair"] = "00030000",
                ["Face"] = "00020000",
            },
            ["note"] = "name 取自 String/Eqp.img（有的部位如 Body 没有名字）",
        };
    }

    /// <summary>合成单帧纸娃娃。equips 传数组或「部位→编号」对象都接受。</summary>
    internal static JsonNode CmdCharRender(JsonObject args)
    {
        CharEnsureEnv();
        var equipsInput = CharEquipList(args);
        if (equipsInput.Count == 0) equipsInput = new List<string>(CharDefaultEquips);
        var poseArg = args["pose"]?.GetValue<string>();
        var pose = string.IsNullOrEmpty(poseArg) ? "stand1" : poseArg;
        var flip = args["flip"]?.GetValue<bool>() ?? false;
        var frame = args["frame"]?.GetValue<int>() ?? 0;
        var chair = ((args["chairId"] ?? args["chair"])?.GetValue<string>() ?? "").Trim();
        if (chair.Length > 0)
        {
            throw new NotSupportedException("椅子合成这轮没有移植：char_render 暂不支持 chairId / chair 参数");
        }
        var earRaw = ((args["earType"] ?? args["ear_type"])?.GetValue<string>() ?? "").Trim();
        string? earType = earRaw.Length > 0 ? earRaw : null;

        var resolved = CharResolveFaceEquips(equipsInput);
        var key = string.Join(",", resolved) + "|" + pose + "|" + flip + "|" + frame + "|" + (earType ?? "") + "|" + chair;
        var noCache = args["_noCache"]?.GetValue<bool>() ?? false;
        var outDir = CharOutDir();
        Directory.CreateDirectory(outDir);
        if (!noCache && CharRenderCache.TryGetValue(key, out var cached) && File.Exists(cached.Path))
        {
            return CharRenderResult(cached.Path, cached.W, cached.H, true, resolved, equipsInput, pose, flip, frame, earType);
        }

        var (images, _) = CharComposeTimeline(resolved, pose, earType, flip);
        Bitmap32 img;
        if (images.Count == 0)
        {
            img = CharComposeSingle(resolved, pose, earType, flip, frame);
        }
        else
        {
            var want = Math.Clamp(frame, 0, images.Count - 1);
            img = new Bitmap32(images.Max(i => i.W), images.Max(i => i.H));
            CharComposite(img, images[want], 0, 0);
        }
        if (img.W <= 1 && img.H <= 1)
        {
            throw new InvalidOperationException(
                $"合成结果为空（{equipsInput.Count} 个编号都没落上）。检查编号是否存在于 "
                + "Character/<类目>/<8位补零>.img，或用 char_parts 取合法编号。");
        }

        var path = Path.Combine(outDir, $"chara_{CharDigest(key)}.png");
        File.WriteAllBytes(path, Png.Encode(img));
        if (CharRenderCache.Count >= 120) CharRenderCache.Clear();
        CharRenderCache[key] = (path, img.W, img.H);
        return CharRenderResult(path, img.W, img.H, false, resolved, equipsInput, pose, flip, frame, earType);
    }

    /// <summary>逐帧合成横向雪碧图；各帧共用同一画布，锚点固定为 (w/2, h)。</summary>
    internal static JsonNode CmdCharAnim(JsonObject args)
    {
        CharEnsureEnv();
        var equips = CharEquipList(args);
        if (equips.Count == 0) equips = new List<string>(CharDefaultEquips);
        var poseArg = args["pose"]?.GetValue<string>();
        var pose = string.IsNullOrEmpty(poseArg) ? "stand1" : poseArg;
        var flip = args["flip"]?.GetValue<bool>() ?? false;
        var chair = ((args["chairId"] ?? args["chair"])?.GetValue<string>() ?? "").Trim();
        if (chair.Length > 0)
        {
            throw new NotSupportedException("椅子合成这轮没有移植：char_anim 暂不支持 chairId / chair 参数");
        }

        int[]? frames = null;
        if (args["frames"] is JsonArray framesArg && framesArg.Count > 0)
        {
            frames = framesArg.Select(n => n?.GetValue<int>() ?? 0).ToArray();
        }

        var stopwatch = Stopwatch.StartNew();
        var resolved = CharResolveFaceEquips(equips);
        var images = CharComposeAnimation(resolved, pose, "humanEar", flip, frames);
        if (images.Count == 0)
        {
            throw new InvalidOperationException("纸娃娃逐帧合成返回空列表（编号可能都不存在）");
        }

        var cellW = images[0].W;
        var cellH = images[0].H;
        const int pad = 2;
        var sheet = new Bitmap32(images.Count * (cellW + pad) + pad, cellH + pad * 2);
        var framesJson = new JsonArray();
        for (var i = 0; i < images.Count; i++)
        {
            var x = pad + i * (cellW + pad);
            CharComposite(sheet, images[i], x, pad);
            framesJson.Add(new JsonObject
            {
                ["index"] = i,
                ["x"] = x,
                ["y"] = pad,
                ["w"] = images[i].W,
                ["h"] = images[i].H,
                ["origin"] = new JsonArray(images[i].W / 2, images[i].H),
                // 旧实现把 (pose, body_id) 两个参数传反，延迟表恒为空，这里保持 180 常量。
                ["delay"] = 180,
            });
        }

        var outDir = CharOutDir();
        Directory.CreateDirectory(outDir);
        var digestKey = string.Join(",", equips) + "|" + pose + "|" + flip;
        var path = Path.Combine(outDir, $"chara_anim_{CharDigest(digestKey)}.png");
        File.WriteAllBytes(path, Png.Encode(sheet));
        stopwatch.Stop();

        return new JsonObject
        {
            ["png"] = path,
            ["sheet"] = new JsonObject { ["w"] = sheet.W, ["h"] = sheet.H },
            ["frame"] = new JsonObject { ["w"] = cellW, ["h"] = cellH },
            ["frames"] = framesJson,
            ["delay"] = 180,
            ["equips"] = CharStrArray(equips),
            ["pose"] = pose,
            ["flip"] = flip,
            ["elapsed"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
        };
    }

    /// <summary>可用姿势；与 旧实现 一样对 00000000 取不到武器姿态后回退两项。</summary>
    internal static JsonNode CmdCharPoses(JsonObject args)
    {
        CharEnsureEnv();
        var poses = CharGetWeaponPoses("00000000");
        if (poses.Count == 0) poses = new List<string> { "stand1", "stand2" };
        return new JsonObject { ["poses"] = CharStrArray(poses) };
    }

    /// <summary>当前头型可用的耳型（Head/<id>.img/front 下除 head 外的画布名）。</summary>
    internal static JsonNode CmdCharEars(JsonObject args)
    {
        CharEnsureEnv();
        var equips = CharEquipList(args);
        if (equips.Count == 0) equips = new List<string>(CharDefaultEquips);
        string? head = equips.FirstOrDefault(e =>
            e.TrimStart('0').StartsWith("120", StringComparison.Ordinal) || e == "00012000");
        var ears = head is null ? new List<string>() : CharGetEarTypes(head);
        if (ears.Count == 0) ears = new List<string> { "humanEar" };
        return new JsonObject
        {
            ["head"] = head,
            ["earTypes"] = CharStrArray(ears),
            ["default"] = "humanEar",
            ["note"] = "humanEar=人类自带圆耳；其余为额外耳型（精灵耳等）",
        };
    }

    internal static JsonNode? TryDispatchChar(string cmd, JsonObject args) => cmd switch
    {
        "char_parts" => CmdCharParts(args),
        "char_render" => CmdCharRender(args),
        "char_anim" => CmdCharAnim(args),
        "char_poses" => CmdCharPoses(args),
        "char_ears" => CmdCharEars(args),
        _ => null,
    };

    internal static JsonNode DebugRun(string cmd, JsonObject args) => TryDispatchChar(cmd, args)!;

    // ── 环境 / 根节点 ───────────────────────────────────────────────────

    // 命令也可能被 SelfTest/DebugRun 绕过 Main 直接调用，数据目录在这里补一次。
    private static void CharEnsureEnv()
    {
        if (_charEnvReady) return;
        _charEnvReady = true;
        if (string.IsNullOrWhiteSpace(_dataDir))
        {
            _dataDir = Environment.GetEnvironmentVariable("WZ_DATA_DIR") ?? "";
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("WZ_VERSION"), out var version) && version > 0)
        {
            _version = version;
        }
        var region = Environment.GetEnvironmentVariable("WZ_REGION");
        if (!string.IsNullOrWhiteSpace(region)) _region = region;
    }

    private static Wz_Node CharRoot()
    {
        CharEnsureEnv();
        if (_charRootCache is not null) return _charRootCache;
        _charRootCache = FindTop("Character")
            ?? throw new FileNotFoundException("数据里没有 Character 分类（检查 WZ_DATA_DIR / Base.wz 是否完整）");
        return _charRootCache;
    }

    private static string[] CharZmap()
    {
        if (_charZmapCache is not null) return _charZmapCache;
        var names = new List<string>();
        try
        {
            var baseZmap = CharRoot().FindNodeByPath(@"Base\zmap.img", true);
            if (baseZmap is not null) names.AddRange(baseZmap.Nodes.Select(n => n.Text));
        }
        catch
        {
            names.Clear();
        }
        // zmap.img 从前往后存，索引越小越靠前；翻成「越小越靠后」与合成器的 z 序一致。
        if (names.Count > 0) names.Reverse();
        _charZmapCache = names.Count > 0 ? names.ToArray() : CharDefaultZmap;
        return _charZmapCache;
    }

    private static string CharOutDir()
    {
        var dir = Environment.GetEnvironmentVariable("WZ_OUT_DIR");
        if (!string.IsNullOrWhiteSpace(dir)) return dir;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "wz-assets");
    }

    private static string CharDigest(string key)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        var value = BitConverter.ToUInt64(hash, 0) % 1_000_000_000_000UL;
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static JsonArray CharStrArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values) arr.Add(value);
        return arr;
    }

    private static JsonNode CharRenderResult(
        string path, int w, int h, bool cached, List<string> resolved, List<string> input,
        string pose, bool flip, int frame, string? earType)
    {
        return new JsonObject
        {
            ["png"] = path,
            ["w"] = w,
            ["h"] = h,
            ["cached"] = cached,
            ["equips"] = CharStrArray(resolved),
            ["equipsInput"] = CharStrArray(input),
            ["pose"] = pose,
            ["flip"] = flip,
            ["frame"] = frame,
            ["earType"] = earType,
        };
    }

    // ── 输入整理 ────────────────────────────────────────────────────────

    // 宿主面板把 equips 做成了「部位→编号」的对象；旧实现只吃数组，
    // 这里两种都接受，对象按 JSON 里的顺序取 value（顺序影响同 z 时的叠放）。
    private static List<string> CharEquipList(JsonObject args)
    {
        var list = new List<string>();
        switch (args["equips"])
        {
            case JsonArray arr:
                foreach (var node in arr)
                {
                    var value = node?.ToString();
                    if (!string.IsNullOrEmpty(value)) list.Add(value);
                }
                break;
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    var value = pair.Value?.ToString();
                    if (!string.IsNullOrEmpty(value)) list.Add(value);
                }
                break;
        }
        return list;
    }

    // 101 面饰与脸型是 face 图层的互斥变体，同时画会透出两层瞳仁；有面饰就丢脸型。
    private static List<string> CharResolveFaceEquips(List<string> equips)
    {
        var faceAcc = equips.Where(e => CharCategoryForId(e) == "FaceAcc").ToList();
        if (faceAcc.Count == 0) return new List<string>(equips);
        var drop = new HashSet<string>(faceAcc, StringComparer.Ordinal);
        foreach (var equip in equips)
        {
            if (CharCategoryForId(equip) == "Face") drop.Add(equip);
        }
        var result = equips.Where(e => !drop.Contains(e)).ToList();
        result.AddRange(faceAcc);
        if (!result.Any(e => CharCategoryForId(e) == "Body"))
        {
            result = equips.Where(e => CharCategoryForId(e) != "Face").ToList();
        }
        return result;
    }

    private static string? CharFirstCategory(List<string> equips, string category)
    {
        return equips.FirstOrDefault(e => CharCategoryForId(e) == category);
    }

    private static string? CharCategoryForId(string equipId)
    {
        if (equipId.Length == 0 || !CharIsDigits(equipId)) return null;
        return int.TryParse(equipId, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? CharCategoryByIdPrefix.GetValueOrDefault(value / 10000)
            : null;
    }

    private static bool CharIsDigits(string value)
    {
        if (value.Length == 0) return false;
        foreach (var ch in value)
        {
            if (ch < '0' || ch > '9') return false;
        }
        return true;
    }

    // ── 部位清单 / 名字表 ───────────────────────────────────────────────

    private static Dictionary<string, (string Name, string Group)> CharEqpNameTable()
    {
        if (_charEqpNames is not null) return _charEqpNames;
        var result = new Dictionary<string, (string Name, string Group)>(StringComparer.Ordinal);
        try
        {
            var img = ResolveNode("String/Eqp.img");
            if (img is not null)
            {
                var first = img.Nodes.Count == 1 ? img.Nodes[0] : null;
                var top = first is not null && CharIsSub(first)
                    && first.Text is "Eqp" or "Consume" or "Ins" or "Etc" or "Cash" or "Pet" or "Map" or "Skill"
                    ? first
                    : null;
                var groups = top is not null ? top.Nodes : img.Nodes;
                foreach (var group in groups)
                {
                    if (!CharIsSub(group)) continue;
                    if (top is null)
                    {
                        CharAddEqpEntry(result, group, "Eqp");
                    }
                    else
                    {
                        foreach (var entry in group.Nodes) CharAddEqpEntry(result, entry, group.Text);
                    }
                }
            }
        }
        catch
        {
            // 名字表只是锦上添花，读不到就让 name 为空，不影响合成。
        }
        _charEqpNames = result;
        return result;
    }

    private static void CharAddEqpEntry(
        Dictionary<string, (string Name, string Group)> result, Wz_Node entry, string group)
    {
        var name = entry.FindNodeByPath("name")?.Value?.ToString() ?? "";
        result[entry.Text] = (name, group);
    }

    private static List<string> CharListParts(string category)
    {
        if (CharPartsCache.TryGetValue(category, out var cached)) return cached;
        var ids = new List<string>();
        if (CharCategoryDir.TryGetValue(category, out var sub))
        {
            var root = CharRoot();
            var target = sub.Length == 0 ? root : root.Nodes[sub];
            if (target is not null)
            {
                foreach (var node in target.Nodes)
                {
                    var name = node.Text;
                    if (name.Length <= 4 || !name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) continue;
                    var stem = name[..^4];
                    if (!CharIsDigits(stem)) continue;
                    if (CharCategoryForId(stem) != category) continue;
                    ids.Add(stem);
                }
            }
        }
        ids.Sort((a, b) => int.Parse(a, CultureInfo.InvariantCulture)
            .CompareTo(int.Parse(b, CultureInfo.InvariantCulture)));
        if (category == "Hair") ids = CharDedupeByColor(ids, 1);
        else if (category == "Face") ids = CharDedupeByColor(ids, 100);
        CharPartsCache[category] = ids;
        return ids;
    }

    // 发色/肤色写在编号的某一位上，列表按「款式」折叠，保留最小编号作为代表。
    private static List<string> CharDedupeByColor(List<string> ids, int colorUnit)
    {
        var grouped = new Dictionary<int, (string Id, List<int> Colors)>();
        foreach (var id in ids)
        {
            var value = int.Parse(id, CultureInfo.InvariantCulture);
            var color = (value / colorUnit) % 10;
            var key = value - color * colorUnit;
            if (grouped.TryGetValue(key, out var existing))
            {
                existing.Colors.Add(color);
                if (value < int.Parse(existing.Id, CultureInfo.InvariantCulture)) grouped[key] = (id, existing.Colors);
            }
            else
            {
                grouped[key] = (id, new List<int> { color });
            }
        }
        var result = grouped.Values.Select(v => v.Id).ToList();
        result.Sort((a, b) => int.Parse(a, CultureInfo.InvariantCulture)
            .CompareTo(int.Parse(b, CultureInfo.InvariantCulture)));
        return result;
    }

    // ── 头部 / 武器 / 姿势 ──────────────────────────────────────────────

    private static List<string> CharGetEarTypes(string equipId)
    {
        var result = new List<string>();
        if (CharCategoryForId(equipId) != "Head") return result;
        var img = CharOpenPart(equipId);
        if (img is null) return result;
        var front = img.FindNodeByPath("front");
        if (!CharIsSub(front)) return result;
        foreach (var child in front!.Nodes)
        {
            if (child.Text == "head") continue;
            if (CharUol(child)?.Value is Wz_Png) result.Add(child.Text);
        }
        return result;
    }

    private static List<string> CharGetWeaponPoses(string equipId)
    {
        var result = new List<string>();
        if (CharCategoryForId(equipId) != "Weapon") return result;
        var img = CharOpenPart(equipId);
        if (img is null) return result;
        var roots = equipId.StartsWith("0170", StringComparison.Ordinal)
            ? img.Nodes.Where(n => CharIsDigits(n.Text) && CharIsSub(n)).ToList()
            : new List<Wz_Node> { img };
        foreach (var pose in CharSupportedPoses)
        {
            foreach (var root in roots)
            {
                if (!CharHasAction(root, pose)) continue;
                result.Add(pose);
                break;
            }
        }
        return result;
    }

    private static bool CharHasAction(Wz_Node node, string action)
    {
        var target = CharUol(node.FindNodeByPath(action));
        if (!CharIsSub(target)) return false;
        return CharIsSub(CharUol(target!.FindNodeByPath("0")));
    }

    private static List<int> CharPoseFrameDelays(string pose, string bodyId)
    {
        var result = new List<int>();
        var img = CharOpenPart(bodyId);
        if (img is null) return result;
        var node = CharUol(img.FindNodeByPath(pose));
        if (!CharIsSub(node)) return result;
        var frames = node!.Nodes
            .Where(n => CharIsDigits(n.Text))
            .OrderBy(n => int.Parse(n.Text, CultureInfo.InvariantCulture))
            .ToList();
        foreach (var frame in frames)
        {
            var ms = CharTryIntNode(CharUol(frame.FindNodeByPath("delay"))) ?? 0;
            result.Add(ms > 0 ? ms : 100);
        }
        return result;
    }

    private static string CharDetectPose(List<string> equips, string? requested)
    {
        var weapon = CharFirstCategory(equips, "Weapon");
        if (weapon is null)
        {
            return requested is not null && CharSupportedPoses.Contains(requested) ? requested : "stand1";
        }
        if (requested is not null && CharWeaponOptionalPoses.Contains(requested)) return requested;
        var poses = CharGetWeaponPoses(weapon);
        if (poses.Count == 0) poses = new List<string> { "stand1" };
        if (requested is not null && poses.Contains(requested)) return requested;
        return poses[0];
    }

    // ── 帽子遮发 ────────────────────────────────────────────────────────

    private static (bool Full, HashSet<string> Hidden, HashSet<string> Tokens) CharCapHairFilter(List<string> equips)
    {
        var capId = CharFirstCategory(equips, "Cap");
        if (capId is null)
        {
            return (false, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        }
        var img = CharOpenPart(capId);
        var vslot = img?.FindNodeByPath("info")?.FindNodeByPath("vslot")?.Value as string;
        var tokens = CharParseVslotTokens(vslot);
        var hidden = CharCapHiddenHair(vslot);
        if (hidden is null) return (true, new HashSet<string>(StringComparer.Ordinal), tokens);
        return (false, hidden, tokens);
    }

    private static HashSet<string> CharParseVslotTokens(string? vslot)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(vslot)) return tokens;
        for (var i = 0; i < vslot!.Length; i += 2)
        {
            tokens.Add(vslot.Substring(i, Math.Min(2, vslot.Length - i)));
        }
        return tokens;
    }

    private static HashSet<string>? CharCapHiddenHair(string? vslot)
    {
        var tokens = CharParseVslotTokens(vslot);
        if (tokens.Contains("H2")) return null;
        if (tokens.Contains("H1") && tokens.Contains("H3")) return new HashSet<string>(CharCapHidePartial, StringComparer.Ordinal);
        if (tokens.Contains("H1")) return new HashSet<string>(CharCapHideTop, StringComparer.Ordinal);
        return new HashSet<string>(StringComparer.Ordinal);
    }

    // ── z 排层 ──────────────────────────────────────────────────────────

    private static int CharZIndex(string? slot)
    {
        var zmap = CharZmap();
        if (string.IsNullOrEmpty(slot)) return zmap.Length;
        var index = Array.IndexOf(zmap, slot);
        return index >= 0 ? index : CharHeuristicAnchor(slot!);
    }

    private static int CharHeuristicAnchor(string slot)
    {
        var zmap = CharZmap();
        var n = zmap.Length;
        var lower = slot.ToLowerInvariant();
        if (lower.StartsWith("back", StringComparison.Ordinal))
        {
            if (slot.Length > 4)
            {
                var baseSlot = char.ToLowerInvariant(slot[4]) + slot[5..];
                var baseIndex = Array.IndexOf(zmap, baseSlot);
                if (baseIndex >= 0) return Math.Max(1, baseIndex - 5);
            }
            foreach (var (sep, delta) in new[] { ("Below", -1), ("Over", 1) })
            {
                if (!slot.Contains(sep, StringComparison.Ordinal)) continue;
                var tail = slot[(slot.LastIndexOf(sep, StringComparison.Ordinal) + sep.Length)..];
                var sibling = tail.Length > 0 ? "back" + char.ToUpperInvariant(tail[0]) + tail[1..] : "";
                var siblingIndex = Array.IndexOf(zmap, sibling);
                if (siblingIndex >= 0) return Math.Max(1, siblingIndex + delta);
            }
            var bodyIndex = Array.IndexOf(zmap, "body");
            return bodyIndex >= 0 ? Math.Max(1, bodyIndex - 1) : 1;
        }
        if (lower.StartsWith("cap", StringComparison.Ordinal) && lower.Contains("below", StringComparison.Ordinal))
        {
            var capIndex = Array.IndexOf(zmap, "cap");
            if (capIndex >= 0) return Math.Max(1, capIndex - 1);
        }
        foreach (var separator in new[] { "Below", "below" })
        {
            if (!slot.Contains(separator, StringComparison.Ordinal)) continue;
            var tail = slot[(slot.LastIndexOf(separator, StringComparison.Ordinal) + separator.Length)..];
            if (tail.Length == 0) continue;
            var target = char.ToLowerInvariant(tail[0]) + tail[1..];
            var index = Array.IndexOf(zmap, target);
            if (index >= 0) return Math.Max(1, index - 1);
        }
        foreach (var separator in new[] { "Over", "over" })
        {
            if (!slot.Contains(separator, StringComparison.Ordinal)) continue;
            var tail = slot[(slot.LastIndexOf(separator, StringComparison.Ordinal) + separator.Length)..];
            if (tail.Length == 0) continue;
            var target = char.ToLowerInvariant(tail[0]) + tail[1..];
            var index = Array.IndexOf(zmap, target);
            if (index >= 0) return Math.Min(n - 1, index + 1);
        }
        return n - 1;
    }

    // ── WZ 访问 ─────────────────────────────────────────────────────────

    private static Wz_Node? CharOpenPart(string equipId)
    {
        if (CharPartOpenCache.TryGetValue(equipId, out var cached)) return cached;
        Wz_Node? node = null;
        var category = CharCategoryForId(equipId);
        if (category is not null && CharCategoryDir.TryGetValue(category, out var sub))
        {
            var rel = sub.Length == 0 ? $"{equipId}.img" : $"{sub}\\{equipId}.img";
            node = CharRoot().FindNodeByPath(rel, true);
        }
        CharPartOpenCache[equipId] = node;
        return node;
    }

    // 现金武器的动作藏在数字子节点里，而且 stand1 / stand2 可能分属不同子节点。
    private static Wz_Node CharRenderRoot(Wz_Node img, string category, string equipId, string pose)
    {
        if (category != "Weapon" || !equipId.StartsWith("0170", StringComparison.Ordinal)) return img;
        var numeric = img.Nodes
            .Where(n => CharIsDigits(n.Text) && CharIsSub(n))
            .OrderBy(n => int.Parse(n.Text, CultureInfo.InvariantCulture))
            .ToList();
        foreach (var child in numeric)
        {
            var home = CharPoseDataHome(child, pose);
            if (home is not null) return home;
        }
        foreach (var fallback in CharWeaponFallbackPoses)
        {
            foreach (var child in numeric)
            {
                var home = CharPoseDataHome(child, fallback);
                if (home is not null) return home;
            }
        }
        return numeric.Count > 0 ? numeric[0] : img;
    }

    private static Wz_Node? CharPoseDataHome(Wz_Node numericRoot, string pose)
    {
        var poseNode = CharUol(numericRoot.FindNodeByPath(pose));
        if (!CharIsSub(poseNode)) return null;
        if (!CharIsSub(CharUol(poseNode!.FindNodeByPath("0")))) return null;
        var home = poseNode.ParentNode;
        return home is not null && CharIsSub(home) ? home : numericRoot;
    }

    private static string[] CharFramePaths(string category, string pose, int frame)
    {
        var path = $"{pose}/{frame}";
        return category switch
        {
            "Body" => new[] { path },
            "Head" => new[] { path, "front" },
            "Hair" => new[] { path, "default" },
            "Face" => new[] { "default" },
            "Cap" => new[] { path, "default" },
            "Cape" or "Coat" or "Longcoat" or "Pants" or "Shoes" or "Glove" or "Shield" or "Weapon" => new[] { path },
            "FaceAcc" or "Glass" or "Earring" => new[] { "default" },
            _ => new[] { path, "default" },
        };
    }

    private static List<(string Name, Wz_Node Canvas, Wz_Node Pixel)> CharCollectPartCanvases(
        Wz_Node img, string category, string equipId, string pose, string? earType,
        HashSet<string> hideHair, int frame)
    {
        var baseNode = CharRenderRoot(img, category, equipId, pose);
        var result = new List<(string Name, Wz_Node Canvas, Wz_Node Pixel)>();
        var seenCanvases = new HashSet<Wz_Node>();
        var seenSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in CharFramePaths(category, pose, frame))
        {
            var node = CharUol(baseNode.FindNodeByPath(path.Replace('/', '\\')));
            if (!CharIsSub(node)) continue;
            var before = result.Count;
            foreach (var child in node!.Nodes)
            {
                if (category == "Head" && child.Text != "head" && child.Text != earType) continue;
                if (category == "Hair" && hideHair.Contains(child.Text)) continue;
                var target = CharUol(child);
                if (target?.Value is not Wz_Png) continue;
                if (seenCanvases.Contains(target)) continue;
                var zSlot = CharZSlotOf(target);
                if (zSlot is not null && seenSlots.Contains(zSlot)) continue;
                var pixels = target;
                if (target.FindNodeByPath("_outlink") is not null || target.FindNodeByPath("_inlink") is not null)
                {
                    var resolved = CharResolveCanvasLink(target, img);
                    if (resolved?.Value is Wz_Png) pixels = resolved;
                }
                if (!CharHasPixels(pixels)) continue;
                seenCanvases.Add(target);
                if (zSlot is not null) seenSlots.Add(zSlot);
                result.Add((child.Text, target, pixels));
            }
            // 首个产出画布的路径即定版，避免同一个帽子在两个路径各画一遍。
            if (result.Count > before) break;
        }
        return result;
    }

    private static Wz_Node? CharResolveCanvasLink(Wz_Node prop, Wz_Node imageRoot)
    {
        var seen = new HashSet<Wz_Node>(ReferenceEqualityComparer.Instance);
        var current = prop;
        for (var hop = 0; hop < 8 && current is not null; hop++)
        {
            if (!seen.Add(current)) return null;
            if (current.Value is Wz_Uol)
            {
                current = CharUol(current);
                continue;
            }
            var outlink = current.FindNodeByPath("_outlink")?.Value as string;
            if (!string.IsNullOrEmpty(outlink))
            {
                var target = CharNavigateLink(outlink);
                if (target is not null)
                {
                    current = target;
                    continue;
                }
            }
            var inlink = current.FindNodeByPath("_inlink")?.Value as string;
            if (!string.IsNullOrEmpty(inlink))
            {
                var target = imageRoot.FindNodeByPath(inlink.Replace('/', '\\'), true);
                if (target is not null)
                {
                    current = target;
                    continue;
                }
            }
            return CharHasPixels(current) ? current : null;
        }
        return null;
    }

    private static Wz_Node? CharNavigateLink(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        for (var skip = 0; skip <= 1 && skip < parts.Length - 1; skip++)
        {
            var root = FindTop(parts[skip]);
            if (root is null) continue;
            var node = root.FindNodeByPath(string.Join('\\', parts.Skip(skip + 1)), true);
            if (node is not null) return node;
        }
        return null;
    }

    private static Wz_Node? CharUol(Wz_Node? node)
    {
        var seen = new HashSet<Wz_Node>(ReferenceEqualityComparer.Instance);
        for (var hop = 0; hop < 16 && node is not null; hop++)
        {
            if (node.Value is not Wz_Uol uol) return node;
            if (!seen.Add(node)) return null;
            if (string.IsNullOrEmpty(uol.Uol) || node.ParentNode is null) return null;
            node = CharWalkRelative(node.ParentNode, uol.Uol);
        }
        return null;
    }

    private static Wz_Node? CharWalkRelative(Wz_Node start, string path)
    {
        var current = start;
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            current = part == ".." ? current.ParentNode! : current.FindNodeByPath(part)!;
            if (current is null) return null;
        }
        return current;
    }

    private static bool CharIsSub(Wz_Node? node) => node is not null && node.Value is null;

    private static bool CharHasPixels(Wz_Node node)
    {
        return node.Value is Wz_Png png && png.DataLength > 0 && png.Width > 0 && png.Height > 0;
    }

    private static (int X, int Y)? CharVecAt(Wz_Node? node)
    {
        return CharUol(node)?.Value is Wz_Vector vec ? (vec.X, vec.Y) : null;
    }

    private static (int X, int Y) CharOriginOf(Wz_Node canvas)
    {
        return CharVecAt(canvas.FindNodeByPath("origin")) ?? (0, 0);
    }

    private static Dictionary<string, (int X, int Y)> CharMapAnchorsOf(Wz_Node canvas)
    {
        var result = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        var map = canvas.FindNodeByPath("map");
        if (map is null) return result;
        foreach (var child in map.Nodes)
        {
            if (CharVecAt(child) is { } vec) result[child.Text] = vec;
        }
        return result;
    }

    private static string? CharZSlotOf(Wz_Node canvas)
    {
        return CharUol(canvas.FindNodeByPath("z"))?.Value as string;
    }

    private static string CharDetermineAnchor(Wz_Node canvas, string category)
    {
        var anchors = CharMapAnchorsOf(canvas);
        foreach (var name in CharAnchorPriority)
        {
            if (anchors.ContainsKey(name)) return name;
        }
        return CharDefaultAnchor.GetValueOrDefault(category, "navel");
    }

    private static int? CharTryIntNode(Wz_Node? node)
    {
        var value = CharUol(node)?.Value;
        return value switch
        {
            int i => i,
            short s => s,
            long l => (int)l,
            float f => (int)f,
            double d => (int)d,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    // ── 合成核心 ────────────────────────────────────────────────────────

    private static List<CharPlacement> CharBuildPlacements(
        List<string> equipIds, string pose, string? earType,
        bool hideHairFull, HashSet<string> hideHairSet, HashSet<string> capTokens,
        int frame, Dictionary<string, (int X, int Y)>? frozenAnchors,
        out Dictionary<string, (int X, int Y)> anchors,
        int? bodyFrame, bool stabilizeEffects,
        Dictionary<string, int>? effectFrameOverrides)
    {
        anchors = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        var placements = new List<CharPlacement>();
        foreach (var equipId in equipIds)
        {
            var category = CharCategoryForId(equipId);
            if (category is null) continue;
            if (category == "Hair" && hideHairFull) continue;
            var img = CharOpenPart(equipId);
            if (img is null) continue;
            var partFrame = bodyFrame is not null && category == "Body" ? bodyFrame.Value : frame;
            foreach (var (name, canvas, pixel) in
                CharCollectPartCanvases(img, category, equipId, pose, earType, hideHairSet, partFrame))
            {
                var png = pixel.Value as Wz_Png;
                placements.Add(new CharPlacement
                {
                    EquipId = equipId,
                    Category = category,
                    Name = name,
                    Canvas = canvas,
                    PixelCanvas = pixel,
                    Origin = CharOriginOf(canvas),
                    MapAnchors = CharMapAnchorsOf(canvas),
                    ZSlot = CharZSlotOf(canvas),
                    PixelW = png?.Width ?? 0,
                    PixelH = png?.Height ?? 0,
                });
            }
        }
        if (placements.Count == 0) return placements;

        var hasCape = placements.Any(p => p.Category == "Cape");

        // body 必须先定位（它定义世界原点），head 的 head 画布也要排在耳朵之前。
        placements = placements
            .OrderBy(p => p.Category == "Body" ? 0 : p.Category == "Head" ? 1 : 2)
            .ThenBy(p => p.Category == "Body" ? (p.Name == "body" ? 0 : 1)
                : p.Category == "Head" ? (p.Name == "head" ? 0 : 1) : 0)
            .ToList();

        var world = frozenAnchors is null
            ? new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal)
            : new Dictionary<string, (int X, int Y)>(frozenAnchors, StringComparer.Ordinal);
        var bodyAnchored = false;
        var bodyNavel = (X: 0, Y: 0);
        var dynamicSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var placement in placements)
        {
            if (placement.Category == "Body" && placement.Name == "body" && !bodyAnchored)
            {
                var navel = placement.MapAnchors.GetValueOrDefault("navel");
                placement.TopLeft = (-placement.Origin.X - navel.X, -placement.Origin.Y - navel.Y);
                bodyNavel = navel;
                if (frozenAnchors is null) CharRegisterAnchors(placement, world, overwrite: true);
                else CharRefreshDynamic(placement, world, dynamicSeen);
                bodyAnchored = true;
                continue;
            }

            var anchorName = CharDetermineAnchor(placement.Canvas, placement.Category);
            var mapPoint = placement.MapAnchors.GetValueOrDefault(anchorName);
            if (world.TryGetValue(anchorName, out var worldPoint))
            {
                placement.TopLeft = (
                    worldPoint.X - placement.Origin.X - mapPoint.X,
                    worldPoint.Y - placement.Origin.Y - mapPoint.Y);
            }
            else if (anchorName == "handMove")
            {
                placement.TopLeft = (-placement.Origin.X - bodyNavel.X, -placement.Origin.Y - bodyNavel.Y);
            }
            else
            {
                placement.TopLeft = (-placement.Origin.X, -placement.Origin.Y);
            }

            if (frozenAnchors is null) CharRegisterAnchors(placement, world, overwrite: false);
            else CharRefreshDynamic(placement, world, dynamicSeen);
        }

        if (CharItemEffRoot() is not null)
        {
            placements.AddRange(CharBuildEffectPlacements(
                equipIds, pose, frame, world, stabilizeEffects, effectFrameOverrides));
        }

        var capCoversHair = hideHairFull || hideHairSet.Count > 0;
        var demote = CharCapDemoteSet(placements);

        int SlotZ(CharPlacement placement)
        {
            if (demote.Contains(placement)) return CharZIndex("body") - 1;
            var slot = placement.ZSlot;
            if (slot is null) return CharZIndex(null);
            if (pose == "stand1" && placement.Category == "Weapon" && slot == "weaponOverArm")
            {
                slot = "weapon";
            }
            else if (pose is "prone" or "proneStab" or "shoot1" or "shoot2" or "shootF"
                     or "swingT1" or "stabO1" or "stabO2")
            {
                if (placement.Category == "Weapon" && (slot is "weapon" or "weaponBelowArm"))
                {
                    slot = "weaponOverHand";
                }
                else if (placement.Category == "Body" && (slot is "arm" or "armOverHair"))
                {
                    slot = "handOverHair";
                }
            }
            else if ((pose is "swingT2" or "stabT1" or "stabT2")
                     && placement.Category == "Body"
                     && (slot is "arm" or "armBelowHead" or "armBelowHeadOverMailChest"))
            {
                slot = "handOverHair";
            }
            else if (pose == "swingT3" && placement.Category == "Body"
                     && (slot is "arm" or "armBelowHeadOverMailChest"))
            {
                slot = "handOverHair";
            }
            else if (pose == "stand2" && placement.Category == "Weapon" && slot == "weaponBelowArm")
            {
                slot = "weaponOverArm";
            }
            if (hasCape && placement.Category == "Hair" && slot == "hairBelowBody") slot = "backHair";
            if (CharOverCapRemap.TryGetValue(slot, out var rule)
                && (!capCoversHair || (rule.Token is not null && capTokens.Contains(rule.Token))))
            {
                slot = rule.Target;
            }
            return CharZIndex(slot);
        }

        var backFacing = pose is "ladder" or "rope";
        const int backFloor = -1;

        int EffectiveSlotZ(CharPlacement placement)
        {
            var z = SlotZ(placement);
            if (!backFacing) return z;
            var slot = placement.ZSlot;
            if (slot is not null && CharBackFacingAbsoluteZ.TryGetValue(slot, out var absolute)) return absolute;
            if (slot is not null && CharBackFacingZOverride.TryGetValue(slot, out var overridden)) return overridden;
            if (string.IsNullOrEmpty(slot) || !slot!.StartsWith("back", StringComparison.Ordinal)) return backFloor;
            return z;
        }

        int effectFrontBase;
        int effectBackBase;
        if (backFacing && CharBackFacingZOverride.Count > 0)
        {
            effectFrontBase = CharBackFacingZOverride.Values.Max() + 1;
            effectBackBase = CharBackFacingAbsoluteZ["characterEnd"] - 1;
        }
        else
        {
            effectFrontBase = CharZmap().Length;
            effectBackBase = 0;
        }

        int ZFor(CharPlacement placement)
        {
            if (placement.Category != "Effect") return EffectiveSlotZ(placement);
            var extra = placement.ExtraZ ?? 0;
            return extra >= 0 ? effectFrontBase + extra : effectBackBase + extra;
        }

        placements = placements.OrderBy(ZFor).ToList();
        anchors = world;
        return placements;
    }

    private static void CharRegisterAnchors(
        CharPlacement placement, Dictionary<string, (int X, int Y)> world, bool overwrite)
    {
        if (placement.TopLeft is not { } topLeft) return;
        foreach (var (name, vec) in placement.MapAnchors)
        {
            if (!overwrite && world.ContainsKey(name)) continue;
            world[name] = (
                topLeft.X + placement.Origin.X + vec.X,
                topLeft.Y + placement.Origin.Y + vec.Y);
        }
    }

    private static void CharRefreshDynamic(
        CharPlacement placement, Dictionary<string, (int X, int Y)> world, HashSet<string> seen)
    {
        if (placement.Category != "Body" || placement.TopLeft is not { } topLeft) return;
        foreach (var (name, vec) in placement.MapAnchors)
        {
            if (!CharDynamicAnimAnchors.Contains(name) || seen.Contains(name)) continue;
            world[name] = (
                topLeft.X + placement.Origin.X + vec.X,
                topLeft.Y + placement.Origin.Y + vec.Y);
            seen.Add(name);
        }
    }

    // 帽子的 *Below* 画布有时是主体、有时是后脑勺配件；用正面画布的面积和顶边区分。
    private static HashSet<CharPlacement> CharCapDemoteSet(List<CharPlacement> placements)
    {
        var demote = new HashSet<CharPlacement>();
        foreach (var group in placements.Where(p => p.Category == "Cap").GroupBy(p => p.EquipId))
        {
            CharPlacement? front = null;
            var frontArea = -1;
            foreach (var placement in group)
            {
                if (placement.TopLeft is null || placement.ZSlot is null) continue;
                if (!CharFrontCapSlots.Contains(placement.ZSlot)) continue;
                var area = placement.PixelW * placement.PixelH;
                if (area > frontArea)
                {
                    front = placement;
                    frontArea = area;
                }
            }
            if (front is null) continue;
            var frontTop = front.TopLeft!.Value.Y;
            foreach (var placement in group)
            {
                if (ReferenceEquals(placement, front) || placement.TopLeft is null) continue;
                var slot = placement.ZSlot;
                if (slot is null || !slot.StartsWith("cap", StringComparison.Ordinal)) continue;
                if (!slot.Contains("Below", StringComparison.Ordinal)) continue;
                if (CharAlwaysBackBelow.Contains(slot))
                {
                    demote.Add(placement);
                    continue;
                }
                if (frontArea >= placement.PixelW * placement.PixelH
                    && placement.TopLeft.Value.Y >= frontTop)
                {
                    demote.Add(placement);
                }
            }
        }
        return demote;
    }

    // ── ItemEff 特效层 ──────────────────────────────────────────────────

    private static Wz_Node? CharItemEffRoot()
    {
        if (_charItemEffTried) return _charItemEffRoot;
        _charItemEffTried = true;
        try
        {
            _charItemEffRoot = FindTop("Effect")?.FindNodeByPath("ItemEff.img", true);
        }
        catch
        {
            _charItemEffRoot = null;
        }
        return _charItemEffRoot;
    }

    private static Wz_Node? CharFindEffect(string equipId)
    {
        var root = CharItemEffRoot();
        if (root is null) return null;
        if (!int.TryParse(equipId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        var node = root.FindNodeByPath(id.ToString(CultureInfo.InvariantCulture));
        return node?.FindNodeByPath("effect");
    }

    private static Wz_Node? CharEffectPoseTree(Wz_Node effect, string pose)
    {
        var backPose = pose is "ladder" or "rope";
        var tree = CharUol(effect.FindNodeByPath(pose));
        if (!CharIsSub(tree) && backPose) tree = CharUol(effect.FindNodeByPath("backDefault"));
        if (!CharIsSub(tree)) tree = CharUol(effect.FindNodeByPath("default"));
        return CharIsSub(tree) ? tree : null;
    }

    private static List<Wz_Node> CharEffectFrames(Wz_Node poseTree)
    {
        return poseTree.Nodes
            .Where(n => CharIsDigits(n.Text))
            .OrderBy(n => int.Parse(n.Text, CultureInfo.InvariantCulture))
            .ToList();
    }

    private static List<int> CharEffectFrameDelays(string equipId, string pose)
    {
        var result = new List<int>();
        var effect = CharFindEffect(equipId);
        if (!CharIsSub(effect)) return result;
        var poseTree = CharEffectPoseTree(effect!, pose);
        if (poseTree is null) return result;
        foreach (var frame in CharEffectFrames(poseTree))
        {
            var target = CharUol(frame);
            var ms = target is null ? 0 : CharTryIntNode(CharUol(target.FindNodeByPath("delay"))) ?? 0;
            result.Add(ms > 0 ? ms : 100);
        }
        return result;
    }

    private static List<CharPlacement> CharBuildEffectPlacements(
        List<string> equipIds, string pose, int frame,
        Dictionary<string, (int X, int Y)> worldAnchors,
        bool pinOriginToFrame0, Dictionary<string, int>? effectFrameOverrides)
    {
        var result = new List<CharPlacement>();
        var imageRoot = CharItemEffRoot();
        if (imageRoot is null) return result;
        foreach (var equipId in equipIds)
        {
            var effect = CharFindEffect(equipId);
            if (!CharIsSub(effect)) continue;
            var poseTree = CharEffectPoseTree(effect!, pose);
            if (poseTree is null) continue;
            var frames = CharEffectFrames(poseTree);
            if (frames.Count == 0) continue;

            var index = effectFrameOverrides is not null && effectFrameOverrides.TryGetValue(equipId, out var overrideFrame)
                ? CharMod(overrideFrame, frames.Count)
                : CharMod(frame, frames.Count);
            var target = CharUol(frames[index]);
            if (target?.Value is not Wz_Png) continue;
            var pixels = CharResolveCanvasLink(target, imageRoot);
            if (pixels is null || !CharHasPixels(pixels)) continue;

            var originSource = target;
            if (pinOriginToFrame0 && index != 0)
            {
                var first = CharUol(frames[0]);
                if (first?.Value is Wz_Png) originSource = first;
            }

            var pos = CharTryIntNode(poseTree.FindNodeByPath("pos"));
            if (pos is null && poseTree.Text != "default")
            {
                var defaultTree = CharUol(effect!.FindNodeByPath("default"));
                if (CharIsSub(defaultTree)) pos = CharTryIntNode(defaultTree!.FindNodeByPath("pos"));
            }
            var anchorName = pos is not null && CharEffectPosAnchor.TryGetValue(pos.Value, out var mapped)
                ? mapped
                : "navel";
            var fallbackAnchor = worldAnchors.TryGetValue("navel", out var navelWorld)
                ? navelWorld
                : (X: 0, Y: 0);
            var anchorWorld = worldAnchors.TryGetValue(anchorName, out var anchorPoint)
                ? anchorPoint
                : fallbackAnchor;
            if (pos != 1) anchorWorld = (anchorWorld.X + 8, anchorWorld.Y + 21);

            Bitmap32? decodedOverride = null;
            int? widthOverride = null;
            int? heightOverride = null;
            (int X, int Y) origin;
            (int X, int Y) topLeft;
            if (pinOriginToFrame0 && frames.Count > 1)
            {
                var composite = CharBuildStabilizedEffectComposite(frames, index);
                if (composite is { } built)
                {
                    decodedOverride = built.Image;
                    widthOverride = built.W;
                    heightOverride = built.H;
                    origin = built.Origin;
                    topLeft = (anchorWorld.X - origin.X, anchorWorld.Y - origin.Y);
                }
                else
                {
                    origin = CharOriginOf(originSource);
                    topLeft = (anchorWorld.X - origin.X, anchorWorld.Y - origin.Y);
                }
            }
            else
            {
                origin = CharOriginOf(originSource);
                topLeft = (anchorWorld.X - origin.X, anchorWorld.Y - origin.Y);
            }

            var z = CharTryIntNode(CharUol(poseTree.FindNodeByPath("z")));
            if (z is null)
            {
                var defaultTree = CharUol(effect!.FindNodeByPath("default"));
                if (CharIsSub(defaultTree)) z = CharTryIntNode(CharUol(defaultTree!.FindNodeByPath("z")));
            }
            if (z is null) z = CharTryIntNode(CharUol(effect!.FindNodeByPath("z")));
            if (z is null) z = CharTryIntNode(CharUol(target.FindNodeByPath("z")));

            result.Add(new CharPlacement
            {
                EquipId = equipId,
                Category = "Effect",
                Name = $"effect/{poseTree.Text}/{target.Text}",
                Canvas = target,
                PixelCanvas = pixels,
                Origin = origin,
                ZSlot = null,
                TopLeft = topLeft,
                ExtraZ = z,
                AnchorOverride = anchorName,
                DecodedOverride = decodedOverride,
                WidthOverride = widthOverride,
                HeightOverride = heightOverride,
                PixelW = (pixels.Value as Wz_Png)?.Width ?? 0,
                PixelH = (pixels.Value as Wz_Png)?.Height ?? 0,
            });
        }
        return result;
    }

    // 站立呼吸时锚点被冻结，但特效各帧 origin 有 1-2px 差异；把整轮并集做成
    // 固定画布再取当前帧，特效才会「原地播」而不是围着帽子滑动。
    private static (Bitmap32 Image, (int X, int Y) Origin, int W, int H)? CharBuildStabilizedEffectComposite(
        List<Wz_Node> frames, int index)
    {
        var imageRoot = CharItemEffRoot();
        if (imageRoot is null) return null;
        var meta = new List<(Wz_Node Target, (int X, int Y) Origin, int W, int H)>();
        foreach (var child in frames)
        {
            var target = CharUol(child);
            if (target?.Value is not Wz_Png) return null;
            var pixels = CharResolveCanvasLink(target, imageRoot);
            if (pixels?.Value is not Wz_Png png) return null;
            meta.Add((target, CharOriginOf(target), png.Width, png.Height));
        }
        var minX = meta.Min(m => -m.Origin.X);
        var minY = meta.Min(m => -m.Origin.Y);
        var maxX = meta.Max(m => -m.Origin.X + m.W);
        var maxY = meta.Max(m => -m.Origin.Y + m.H);
        var width = maxX - minX;
        var height = maxY - minY;
        if (width <= 0 || height <= 0) return null;

        var compositeOrigin = (-minX, -minY);
        var targetMeta = meta[index];
        var targetPixels = CharResolveCanvasLink(targetMeta.Target, imageRoot);
        if (targetPixels?.Value is not Wz_Png) return null;
        var composite = new Bitmap32(width, height);
        CharComposite(composite, CharDecodePixel(targetPixels),
            -targetMeta.Origin.X - minX, -targetMeta.Origin.Y - minY);
        return (composite, compositeOrigin, width, height);
    }

    private static int CharMod(int value, int modulus)
    {
        return ((value % modulus) + modulus) % modulus;
    }

    // ── 逐帧渲染 ────────────────────────────────────────────────────────

    private static List<Bitmap32> CharComposeAnimation(
        List<string> equips, string? poseArg, string? earType, bool flip, int[]? frames)
    {
        var pose = CharDetectPose(equips, poseArg);
        var (hideFull, hideSet, capTokens) = CharCapHairFilter(equips);

        var frameList = frames;
        if (frameList is null)
        {
            var bodyId = CharFirstCategory(equips, "Body") ?? "00002000";
            var count = CharPoseFrameDelays(pose, bodyId).Count;
            frameList = count > 0 ? Enumerable.Range(0, count).ToArray() : new[] { 0 };
        }

        var stabilize = pose is "stand1" or "stand2";
        var perFrame = new List<List<CharPlacement>>();
        Dictionary<string, (int X, int Y)>? frozen = null;
        var frame0Canvases = new Dictionary<(string Equip, string Name), Wz_Node>();
        (int X, int Y)? bodyAnchor0 = null;

        foreach (var frame in frameList)
        {
            var placements = CharBuildPlacements(
                equips, pose, earType, hideFull, hideSet, capTokens,
                frame, frozen, out var anchors, null, stabilize, null);
            if (!stabilize)
            {
                perFrame.Add(placements);
                continue;
            }

            if (frozen is null)
            {
                frozen = anchors;
                foreach (var placement in placements)
                {
                    frame0Canvases[(placement.EquipId, placement.Name)] = placement.PixelCanvas;
                }
                bodyAnchor0 = CharBodyAnchor(placements);
            }
            else
            {
                var bodyAnchorNow = CharBodyAnchor(placements);
                var dx = 0;
                var dy = 0;
                if (bodyAnchor0 is { } first && bodyAnchorNow is { } now)
                {
                    dx = first.X - now.X;
                    dy = first.Y - now.Y;
                }
                foreach (var placement in placements)
                {
                    if (placement.TopLeft is not { } topLeft) continue;
                    if (placement.Category == "Effect") continue;
                    frame0Canvases.TryGetValue((placement.EquipId, placement.Name), out var frame0Canvas);
                    var anchorName = placement.AnchorOverride
                        ?? CharDetermineAnchor(placement.Canvas, placement.Category);
                    if (ReferenceEquals(frame0Canvas, placement.PixelCanvas)
                        && CharHeadDerivedAnchors.Contains(anchorName)) continue;
                    if (CharHeadDerivedAnchors.Contains(anchorName)) continue;
                    if (dx != 0 || dy != 0) placement.TopLeft = (topLeft.X + dx, topLeft.Y + dy);
                }
            }
            perFrame.Add(placements);
        }

        var all = perFrame.SelectMany(p => p).Where(p => p.TopLeft is not null).ToList();
        if (all.Count == 0) return frameList.Select(_ => new Bitmap32(1, 1)).ToList();
        var bbox = CharUnionBox(all);

        var images = new List<Bitmap32>(perFrame.Count);
        foreach (var placements in perFrame)
        {
            if (placements.Count == 0)
            {
                images.Add(new Bitmap32(Math.Max(1, bbox.MaxX - bbox.MinX), Math.Max(1, bbox.MaxY - bbox.MinY)));
            }
            else
            {
                images.Add(CharRenderPlacements(placements, flip, bbox));
            }
        }
        return images;
    }

    private static Bitmap32 CharComposeSingle(
        List<string> equips, string? poseArg, string? earType, bool flip, int frame)
    {
        var pose = CharDetectPose(equips, poseArg);
        var (hideFull, hideSet, capTokens) = CharCapHairFilter(equips);
        var placements = CharBuildPlacements(
            equips, pose, earType, hideFull, hideSet, capTokens,
            frame, null, out _, null, false, null);
        return placements.Count == 0 ? new Bitmap32(1, 1) : CharRenderPlacements(placements, flip, null);
    }

    // 时间轴把 body 与每个特效各自的周期铺到同一条时间轴上；没有特效时等价于 compose_animation。
    private static (List<Bitmap32> Images, List<int> Delays) CharComposeTimeline(
        List<string> equips, string? poseArg, string? earType, bool flip)
    {
        var pose = CharDetectPose(equips, poseArg);
        var bodyId = CharFirstCategory(equips, "Body") ?? "00002000";
        var bodyDelays = CharPoseFrameDelays(pose, bodyId);
        if (bodyDelays.Count == 0) bodyDelays = new List<int> { 100 };

        var bodySeq = (pose is "stand1" or "stand2") && bodyDelays.Count == 3
            ? new[] { 0, 1, 2, 1 }
            : Enumerable.Range(0, bodyDelays.Count).ToArray();
        var bodyCycle = bodySeq.Sum(s => bodyDelays[s]);

        var effectCycles = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var equipId in equips)
        {
            var delays = CharEffectFrameDelays(equipId, pose);
            if (delays.Count > 0) effectCycles[equipId] = delays;
        }

        if (effectCycles.Count == 0)
        {
            var images = CharComposeAnimation(equips, pose, earType, flip, null);
            var timeline = new List<Bitmap32>(bodySeq.Length);
            var delays = new List<int>(bodySeq.Length);
            foreach (var step in bodySeq)
            {
                timeline.Add(step < images.Count ? images[step] : images[^1]);
                delays.Add(bodyDelays[step]);
            }
            return (timeline, delays);
        }

        long total = bodyCycle;
        foreach (var delays in effectCycles.Values) total = CharLcm(total, delays.Sum());
        if (total > 6000) total = 6000;

        var timestamps = new SortedSet<int> { 0, (int)total };
        var bodyTable = new List<(int Start, int End, int Frame)>();
        {
            var t = 0;
            foreach (var step in bodySeq)
            {
                bodyTable.Add((t, t + bodyDelays[step], step));
                t += bodyDelays[step];
            }
        }
        var effectTables = new Dictionary<string, List<(int Start, int End, int Frame)>>(StringComparer.Ordinal);
        foreach (var (equipId, delays) in effectCycles)
        {
            var table = new List<(int Start, int End, int Frame)>();
            var t = 0;
            for (var i = 0; i < delays.Count; i++)
            {
                table.Add((t, t + delays[i], i));
                t += delays[i];
            }
            effectTables[equipId] = table;
        }
        foreach (var entry in bodyTable)
        {
            for (var t = entry.Start; t < total; t += bodyCycle) timestamps.Add(t);
        }
        foreach (var (equipId, table) in effectTables)
        {
            var cycle = effectCycles[equipId].Sum();
            foreach (var entry in table)
            {
                for (var t = entry.Start; t < total; t += cycle) timestamps.Add(t);
            }
        }

        var sorted = timestamps.ToList();
        var perFrame = new List<List<CharPlacement>>();
        var delaysOut = new List<int>();
        var (hideFull, hideSet, capTokens) = CharCapHairFilter(equips);
        var stabilize = pose is "stand1" or "stand2";
        Dictionary<string, (int X, int Y)>? frozen = null;
        var frame0Canvases = new Dictionary<(string Equip, string Name), Wz_Node>();
        (int X, int Y)? bodyAnchor0 = null;

        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var when = sorted[i];
            var duration = sorted[i + 1] - when;
            if (duration <= 0) continue;
            var bodyFrame = CharFrameAt(bodyTable, bodyCycle, when);
            var overrides = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (equipId, table) in effectTables)
            {
                overrides[equipId] = CharFrameAt(table, effectCycles[equipId].Sum(), when);
            }

            var placements = CharBuildPlacements(
                equips, pose, earType, hideFull, hideSet, capTokens,
                bodyFrame, frozen, out var anchors, null, stabilize, overrides);

            if (stabilize)
            {
                if (frozen is null)
                {
                    frozen = anchors;
                    foreach (var placement in placements)
                    {
                        frame0Canvases[(placement.EquipId, placement.Name)] = placement.PixelCanvas;
                    }
                    bodyAnchor0 = CharBodyAnchor(placements);
                }
                else
                {
                    var bodyAnchorNow = CharBodyAnchor(placements);
                    var dx = 0;
                    var dy = 0;
                    if (bodyAnchor0 is { } first && bodyAnchorNow is { } now)
                    {
                        dx = first.X - now.X;
                        dy = first.Y - now.Y;
                    }
                    foreach (var placement in placements)
                    {
                        if (placement.TopLeft is not { } topLeft) continue;
                        if (placement.Category == "Effect") continue;
                        frame0Canvases.TryGetValue((placement.EquipId, placement.Name), out var frame0Canvas);
                        var anchorName = placement.AnchorOverride
                            ?? CharDetermineAnchor(placement.Canvas, placement.Category);
                        if (ReferenceEquals(frame0Canvas, placement.PixelCanvas)
                            && CharHeadDerivedAnchors.Contains(anchorName)) continue;
                        if (CharHeadDerivedAnchors.Contains(anchorName)) continue;
                        if (dx != 0 || dy != 0) placement.TopLeft = (topLeft.X + dx, topLeft.Y + dy);
                    }
                }
            }

            perFrame.Add(placements);
            delaysOut.Add(duration);
        }

        var all = perFrame.SelectMany(p => p).Where(p => p.TopLeft is not null).ToList();
        if (all.Count == 0)
        {
            return (perFrame.Select(_ => new Bitmap32(1, 1)).ToList(), delaysOut);
        }
        var bbox = CharUnionBox(all);
        var rendered = new List<Bitmap32>(perFrame.Count);
        foreach (var placements in perFrame)
        {
            if (placements.Count == 0)
            {
                rendered.Add(new Bitmap32(Math.Max(1, bbox.MaxX - bbox.MinX), Math.Max(1, bbox.MaxY - bbox.MinY)));
            }
            else
            {
                rendered.Add(CharRenderPlacements(placements, flip, bbox));
            }
        }
        return (rendered, delaysOut);
    }

    private static long CharGcd(long a, long b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            var t = a % b;
            a = b;
            b = t;
        }
        return a;
    }

    private static long CharLcm(long a, long b)
    {
        return a != 0 && b != 0 ? a / CharGcd(a, b) * b : Math.Max(a, b);
    }

    private static int CharFrameAt(List<(int Start, int End, int Frame)> table, int cycle, int when)
    {
        var wrapped = cycle != 0 ? CharMod(when, cycle) : 0;
        foreach (var (start, end, frame) in table)
        {
            if (start <= wrapped && wrapped < end) return frame;
        }
        return table.Count > 0 ? table[^1].Frame : 0;
    }

    private static (int X, int Y)? CharBodyAnchor(List<CharPlacement> placements)
    {
        foreach (var placement in placements)
        {
            if (placement.Category != "Body" || placement.Name != "body") continue;
            if (placement.TopLeft is not { } topLeft) continue;
            return (topLeft.X + placement.PixelW, topLeft.Y);
        }
        return null;
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) CharUnionBox(List<CharPlacement> placements)
    {
        var visible = placements.Where(p => p.TopLeft is not null).ToList();
        return (
            visible.Min(p => p.TopLeft!.Value.X),
            visible.Min(p => p.TopLeft!.Value.Y),
            visible.Max(p => p.TopLeft!.Value.X + p.Width),
            visible.Max(p => p.TopLeft!.Value.Y + p.Height));
    }

    private static Bitmap32 CharRenderPlacements(
        List<CharPlacement> placements, bool flip, (int MinX, int MinY, int MaxX, int MaxY)? bbox)
    {
        var visible = placements.Where(p => p.TopLeft is not null).ToList();
        if (visible.Count == 0) return new Bitmap32(1, 1);
        var box = bbox ?? CharUnionBox(visible);
        var width = Math.Max(1, box.MaxX - box.MinX);
        var height = Math.Max(1, box.MaxY - box.MinY);
        var canvas = new Bitmap32(width, height);
        foreach (var placement in visible)
        {
            var topLeft = placement.TopLeft!.Value;
            var layer = placement.DecodedOverride ?? CharDecodePixel(placement.PixelCanvas);
            CharComposite(canvas, layer, topLeft.X - box.MinX, topLeft.Y - box.MinY);
        }
        return flip ? CharMirror(canvas) : canvas;
    }

    private static Bitmap32 CharDecodePixel(Wz_Node node)
    {
        if (CharPixelCache.TryGetValue(node, out var cached)) return cached;
        if (node.Value is not Wz_Png png) throw new InvalidOperationException("画布没有像素数据");
        if (png.ActualScale > 1)
        {
            throw new NotSupportedException($"暂不支持带缩放的画布（format={png.Format}, scale={png.Scale}）");
        }
        var bitmap = Png.Decode(png);
        CharPixelCache[node] = bitmap;
        return bitmap;
    }

    // 逐像素照抄 Pillow 12.x 的 alpha_composite：7 位精度 + 四舍五入。
    // 用 Sheet.Blit 的整数公式会在半透明边缘差 1 个色阶（实测 42 个像素）。
    private static void CharComposite(Bitmap32 dst, Bitmap32 src, int px, int py)
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
                var outa255 = sa * 255u + (uint)(da * (255 - sa));
                if (outa255 == 0) continue;
                var coef1 = sa * 255u * 255u * 128u / outa255;
                var coef2 = 255u * 128u - coef1;
                for (var c = 0; c < 3; c++)
                {
                    var value = src.Px[si + c] * coef1 + dst.Px[di + c] * coef2 + (0x80u << 7);
                    dst.Px[di + c] = (byte)(CharShiftForDiv255(value) >> 7);
                }
                dst.Px[di + 3] = (byte)CharShiftForDiv255(outa255 + 0x80u);
            }
        }
    }

    // Pillow 的 SHIFTFORDIV255：不是普通的 floor(x/255)，低值区差 1，必须照抄。
    private static uint CharShiftForDiv255(uint value) => ((value >> 8) + value) >> 8;

    private static Bitmap32 CharMirror(Bitmap32 source)
    {
        var target = new Bitmap32(source.W, source.H);
        for (var y = 0; y < source.H; y++)
        {
            for (var x = 0; x < source.W; x++)
            {
                var sourceIndex = (y * source.W + (source.W - 1 - x)) * 4;
                var targetIndex = (y * target.W + x) * 4;
                target.Px[targetIndex] = source.Px[sourceIndex];
                target.Px[targetIndex + 1] = source.Px[sourceIndex + 1];
                target.Px[targetIndex + 2] = source.Px[sourceIndex + 2];
                target.Px[targetIndex + 3] = source.Px[sourceIndex + 3];
            }
        }
        return target;
    }

    private sealed class CharPlacement
    {
        public string EquipId = "";
        public string Category = "";
        public string Name = "";
        public Wz_Node Canvas = null!;
        public Wz_Node PixelCanvas = null!;
        public (int X, int Y) Origin;
        public Dictionary<string, (int X, int Y)> MapAnchors = new(StringComparer.Ordinal);
        public string? ZSlot;
        public (int X, int Y)? TopLeft;
        public int? ExtraZ;
        public string? AnchorOverride;
        public Bitmap32? DecodedOverride;
        public int? WidthOverride;
        public int? HeightOverride;
        public int PixelW;
        public int PixelH;
        public int Width => WidthOverride ?? PixelW;
        public int Height => HeightOverride ?? PixelH;
    }
}
