using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WzWorker;

internal static partial class Program
{
    internal static JsonNode? TryDispatchCmsdl(string cmd, JsonObject args) => cmd switch
    {
        "cmsdl_probe" => CmdCmsdlProbe(args),
        "cmsdl_plan" => CmdCmsdlPlan(args),
        "cmsdl_status" => CmdCmsdlStatus(args),
        "cmsdl_start" => CmdCmsdlStart(args),
        _ => null,
    };

    private static JsonNode CmdCmsdlProbe(JsonObject args) => Cmsdl.Probe(args);

    private static JsonNode CmdCmsdlPlan(JsonObject args) => Cmsdl.Plan(args);

    private static JsonNode CmdCmsdlStatus(JsonObject args) => Cmsdl.Status();

    private static JsonNode CmdCmsdlStart(JsonObject args) => Cmsdl.Start(args);
}

/// <summary>
/// cmsdl 资源更新支持。
///
/// 前半段（probe/plan）是 py/cmsdl_support.py 的逐字移植：只在本地探测二进制、
/// 解析 --help 得到「这个版本真实支持的开关面」，再据此拼 argv 并做写数据源的安全判定。
/// 后半段（status/start）承接原 host 侧 lib/cmsdl.js 的职责：长任务状态机、日志尾部
/// 与进度解析，让宿主只做 HTTP 转发。
/// </summary>
internal static class Cmsdl
{
    private static readonly string CmsdlDir =
        Environment.GetEnvironmentVariable("WZ_CMSDL_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(HomeDir(), ".dsh", "wz-assets", "cmsdl");

    private static readonly string ExeName = OperatingSystem.IsWindows() ? "cmsdl.exe" : "cmsdl";

    private static readonly string DefaultTargetDir = Path.Combine(CmsdlDir, "client");

    private const double ProbeTimeout = 5.0;

    private const string ReleasePage = "https://gitee.com/HikariCalyx/cmsdl/releases";
    private const string ReleaseTag = "v0.2.5";

    private const int LogKeep = 400;
    private const int LogTail = 40;
    private const int DirWalkMax = 20_000;

    private static readonly object Sync = new();
    private static readonly Regex AnsiRx = new(@"\x1B\[[0-9;]*m");
    private static readonly Regex TimestampRx = new(@"^\[[^\]]*\]\s*");
    private static readonly Regex PercentRx = new(@"(\d{1,3}(?:\.\d+)?)\s*%");
    private static readonly Regex BytesRx = new(@"(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)\b", RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, double> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = 1,
        ["kb"] = 1024d,
        ["mb"] = 1024d * 1024,
        ["gb"] = 1024d * 1024 * 1024,
        ["tb"] = 1024d * 1024 * 1024 * 1024,
        ["kib"] = 1024d,
        ["mib"] = 1024d * 1024,
        ["gib"] = 1024d * 1024 * 1024,
        ["tib"] = 1024d * 1024 * 1024 * 1024,
    };

    private static TaskState Job = new();
    private static JsonNode? Tool;
    private static DateTimeOffset ToolProbedAt;
    private static Process? Child;
    private static CancellationTokenSource? DirCts;

    static Cmsdl()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                var proc = Child;
                if (proc is not null && !proc.HasExited) proc.Kill(true);
            }
            catch
            {
            }
        };
    }

    private sealed class TaskState
    {
        public string Phase = "idle";
        public bool Running;
        public string? Kind;
        public string Label = "";
        public string Mode = "";
        public string Region = "";
        public string TargetDir = "";
        public List<string> Argv = new();
        public string Command = "";
        public long Bytes;
        public long? TotalBytes;
        public long? DirBytes;
        public double? Percent;
        public List<string> Log = new();
        public List<string> LogTail = new();
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? EndedAt;
        public int? ExitCode;
        public string? Error;
        public List<string> Warnings = new();
        public List<string> Blocked = new();
        public JsonNode? Result;
    }

    private sealed class FlagInfo
    {
        public string Flag = "";
        public bool TakesValue;
        public bool OptionalValue;
        public string ValueSpec = "";
        public string Description = "";
    }

    private sealed class HelpSurface
    {
        public Dictionary<string, FlagInfo> Flags = new(StringComparer.Ordinal);
        public List<string> Order = new();
        public List<(string Value, string Description)> Regions = new();
        public List<string> Modes = new();
        public string HelpText = "";
        public int? ExitCode;
    }

    private sealed class RunResult
    {
        public List<string> Argv = new();
        public int? ExitCode;
        public string Stdout = "";
        public string Stderr = "";
        public bool Ok;
        public string? Error;
    }

    // ====== 探测 ======

    internal static JsonNode Probe(JsonObject args)
    {
        var overridePath = Str(args, "path").Trim();
        var (path, source, checkedCandidates) = FindCmsdl(overridePath);
        var result = new JsonObject
        {
            ["found"] = false,
            ["path"] = null,
            ["source"] = "",
            ["candidates"] = checkedCandidates,
            ["cmsdl_dir"] = CmsdlDir,
            ["default_target_dir"] = DefaultTargetDir,
            ["data_dir"] = DataDir,
            ["exe_name"] = ExeName,
            // probe 的 platform 沿用 旧的 os.name 口径；面板要的 darwin:arm64 在 status 里给。
            ["platform"] = OsName,
            ["note"] = "",
        };
        if (path is null)
        {
            result["note"] =
                $"本机没有 cmsdl。默认会找 {Path.Combine(CmsdlDir, ExeName)}，" +
                "也可以在面板里下载 cmsdl_darwin，或指定已有路径。";
            return result;
        }

        var version = VersionOf(path);
        var surface = HelpFor(path);
        result["found"] = true;
        result["path"] = path;
        result["source"] = source;
        result["size"] = new FileInfo(path).Length;
        result["executable"] = IsExecutable(path);
        result["version"] = version.Version;
        result["version_raw"] = version.Raw;
        result["flags"] = Arr(surface.Order.OrderBy(x => x, StringComparer.Ordinal));
        result["flag_detail"] = FlagDetail(surface);
        result["regions"] = RegionsJson(surface);
        result["modes"] = Arr(surface.Modes);
        result["help_text"] = surface.HelpText;

        if (surface.Order.Count == 0)
        {
            result["note"] = "cmsdl 能执行但 --help 没解析出任何开关，面板会退化成「只让用户填参数」。";
        }
        else if (surface.Regions.Count == 0)
        {
            result["note"] = "cmsdl --help 里没解析到区服取值，区服列表会由 host 侧的兜底值给出。";
        }
        return result;
    }

    private static (string? Path, string Source, JsonArray Checked) FindCmsdl(string overridePath)
    {
        var checkedList = new JsonArray();
        foreach (var (candidate, source) in Candidates(overridePath))
        {
            var exists = File.Exists(candidate);
            var executable = exists && IsExecutable(candidate);
            var item = new JsonObject
            {
                ["path"] = candidate,
                ["source"] = source,
                ["exists"] = exists,
                ["executable"] = executable,
            };
            checkedList.Add(item);
            if (!exists) continue;
            if (executable) return (candidate, source, checkedList);

            // 下载/解压后丢执行位是最常见的「装了却跑不了」，能补就补，补不上再如实报错。
            if (OperatingSystem.IsWindows()) continue;
            try
            {
                var mode = File.GetUnixFileMode(candidate);
                File.SetUnixFileMode(candidate,
                    mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                if (IsExecutable(candidate))
                {
                    item["executable"] = true;
                    item["note"] = "原本没有执行位，已 chmod +x";
                    return (candidate, source, checkedList);
                }
            }
            catch (Exception ex)
            {
                item["note"] = $"chmod +x 失败：{ex.Message}";
            }
        }
        return (null, "", checkedList);
    }

    private static List<(string Path, string Source)> Candidates(string overridePath)
    {
        var list = new List<(string Path, string Source)>();
        if (overridePath.Length > 0) list.Add((ExpandUser(overridePath), "调用方指定"));
        var env = (Environment.GetEnvironmentVariable("WZ_CMSDL") ?? "").Trim();
        if (env.Length > 0) list.Add((ExpandUser(env), "环境变量 WZ_CMSDL"));
        list.Add((Path.Combine(CmsdlDir, ExeName), $"插件默认目录 {CmsdlDir}"));
        list.Add((Path.Combine(CmsdlDir, "cmsdl_darwin"), $"{CmsdlDir}/cmsdl_darwin"));
        list.Add(("/opt/homebrew/bin/cmsdl", "常见安装路径"));
        list.Add(("/usr/local/bin/cmsdl", "常见安装路径"));
        var which = Which(ExeName);
        if (which is not null) list.Add((which, "PATH"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<(string Path, string Source)>();
        foreach (var item in list)
        {
            if (item.Path.Length == 0 || !seen.Add(item.Path)) continue;
            unique.Add(item);
        }
        return unique;
    }

    private static RunResult Run(List<string> argv, double timeoutSeconds)
    {
        var result = new RunResult { Argv = argv };
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = argv[0],
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
            {
                result.Stderr = "无法启动进程";
                result.Error = "OSError";
                return result;
            }
            // 探测命令绝不能碰 worker 自己的 stdin（那是 JSON 协议），统一给 /dev/null 语义。
            proc.StandardInput.Close();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var exited = proc.WaitForExit((int)Math.Ceiling(timeoutSeconds * 1000));
            if (!exited)
            {
                try { proc.Kill(true); } catch { }
                try { proc.WaitForExit(1000); } catch { }
                result.Stdout = "";
                result.Stderr = $"超时（>{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}s）";
                result.Error = "timeout";
                return result;
            }
            // 异步读输出时，带超时的 WaitForExit 返回不代表管道已经排空。
            proc.WaitForExit();
            result.Stdout = stdoutTask.GetAwaiter().GetResult();
            result.Stderr = stderrTask.GetAwaiter().GetResult();
            result.ExitCode = proc.ExitCode;
            result.Ok = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Stderr = ex.Message;
            result.Error = ex.GetType().Name;
            return result;
        }
    }

    private static HelpSurface HelpFor(string path)
    {
        var run = Run(new List<string> { path, "--help" }, ProbeTimeout);
        var text = run.Stdout + run.Stderr;
        var surface = ParseHelp(text);
        surface.HelpText = text;
        surface.ExitCode = run.ExitCode;
        return surface;
    }

    private static (string Raw, string Version, int? ExitCode) VersionOf(string path)
    {
        var run = Run(new List<string> { path, "--version" }, ProbeTimeout);
        var text = (run.Stdout + run.Stderr).Trim();
        var match = Regex.Match(text, @"(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)");
        return (text, match.Success ? match.Groups[1].Value : "", run.ExitCode);
    }

    private static JsonObject FlagDetail(HelpSurface surface)
    {
        var detail = new JsonObject();
        foreach (var name in surface.Order)
        {
            var flag = surface.Flags[name];
            detail[name] = new JsonObject
            {
                ["flag"] = flag.Flag,
                ["takes_value"] = flag.TakesValue,
                ["optional_value"] = flag.OptionalValue,
                ["value_spec"] = flag.ValueSpec,
                ["description"] = flag.Description,
            };
        }
        return detail;
    }

    private static JsonArray RegionsJson(HelpSurface surface)
    {
        var regions = new JsonArray();
        foreach (var (value, description) in surface.Regions)
        {
            regions.Add(new JsonObject { ["value"] = value, ["description"] = description });
        }
        return regions;
    }

    // ====== --help 解析 ======

    private static readonly Regex OptHeadRx = new(@"^\s{2,}(?:(-[A-Za-z]),\s+)?(--[a-z][a-z0-9-]*)\s*(.*)$");
    private static readonly Regex RegionValueRx = new(@"^\s+-\s+([a-z0-9_-]+):\s*(.*)$");
    private static readonly Regex TakesValueRx = new(@"^(=)?<[^>]+>");
    private static readonly Regex OptionalValueRx = new(@"^\[=<?");
    private static readonly Regex ValueSpecRx = new(@"^\[?=?<?([^>\]]*)>?\]?");
    private static readonly Regex UsageModesRx = new(@"<((?:--[^<>]*(?:<[^<>]*>)?[^<>]*\|?)+)>");
    private static readonly Regex ModeFlagRx = new(@"--[a-z][a-z0-9-]*");

    private static HelpSurface ParseHelp(string text)
    {
        var surface = new HelpSurface();
        var inOptions = false;
        string? last = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim() == "Options:")
            {
                inOptions = true;
                continue;
            }
            if (!inOptions) continue;

            var head = OptHeadRx.Match(line);
            if (head.Success)
            {
                var longName = head.Groups[2].Value;
                var rest = head.Groups[3].Value.Trim();
                var optional = OptionalValueRx.IsMatch(rest);
                var takes = TakesValueRx.IsMatch(rest) || optional;
                var specMatch = ValueSpecRx.Match(rest);
                var spec = specMatch.Success
                    ? specMatch.Groups[1].Value.Trim(' ', '[', ']', '<', '>', '=')
                    : "";
                if (!surface.Flags.ContainsKey(longName)) surface.Order.Add(longName);
                surface.Flags[longName] = new FlagInfo
                {
                    Flag = longName,
                    TakesValue = takes,
                    OptionalValue = optional,
                    ValueSpec = spec,
                    Description = "",
                };
                last = longName;
                continue;
            }

            var stripped = line.Trim();
            if (last is not null && stripped.Length > 0 && !stripped.StartsWith("-") && !line.StartsWith("  <"))
            {
                if (surface.Flags[last].Description.Length == 0) surface.Flags[last].Description = stripped;
            }
        }

        var inRegionValues = false;
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Contains("Possible values:"))
            {
                inRegionValues = true;
                continue;
            }
            if (!inRegionValues) continue;
            var match = RegionValueRx.Match(raw.TrimEnd());
            if (match.Success)
            {
                surface.Regions.Add((match.Groups[1].Value, match.Groups[2].Value.Trim()));
                continue;
            }
            if (raw.Trim().Length > 0 && !raw.StartsWith("          ")) inRegionValues = false;
        }

        var usage = UsageModesRx.Match(text);
        if (usage.Success)
        {
            foreach (var part in usage.Groups[1].Value.Split('|'))
            {
                var mode = ModeFlagRx.Match(part);
                if (mode.Success && !surface.Modes.Contains(mode.Value)) surface.Modes.Add(mode.Value);
            }
        }

        return surface;
    }

    // ====== 命令构造 ======

    internal static JsonNode Plan(JsonObject args)
    {
        var region = Str(args, "region").Trim().ToLowerInvariant();
        if (region.Length == 0) region = "cms";
        var mode = Str(args, "mode").Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = "download";

        var targetDir = Str(args, "target_dir").Trim();
        if (targetDir.Length == 0) targetDir = DefaultTargetDir;
        targetDir = ExpandUser(targetDir);
        if (targetDir.Length == 0) targetDir = DefaultTargetDir;

        var clientDir = Str(args, "client_dir").Trim();
        if (clientDir.Length > 0) clientDir = ExpandUser(clientDir);

        var wzSubdir = region == "cms" ? "mxd/Data" : "Data";
        var effectiveTarget = clientDir.Length > 0 ? Path.Combine(clientDir, wzSubdir) : targetDir;

        var allowDataDir = Bool(args, "allow_data_dir");
        var purge = Bool(args, "purge");
        var launchAfterPatching = Bool(args, "launch_after_patching");
        // 旧实现只认 wz_only；面板历史上传的是 download_wz_only，这里保持同一口径，
        // 避免同一个请求在两边拼出不同的命令。
        var wzOnly = Bool(args, "wz_only");
        var patchVersion = Str(args, "patch_version").Trim();
        if (patchVersion.Length == 0) patchVersion = "latest";
        var torrentOut = Str(args, "torrent_out").Trim();
        var build = NodeText(args, "build").Trim();
        var buildSince = NodeText(args, "build_since").Trim();
        var filterSpec = Str(args, "filter").Trim();
        var proxy = args["proxy"];
        var insecure = Bool(args, "insecure");
        var verbose = Int(args, "verbose");
        var extraArgs = ExtraArgs(args);

        var blocked = new List<string>();
        var warnings = new List<string>();

        var (path, source, _) = FindCmsdl(Str(args, "path").Trim());
        if (path is null)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["argv"] = new JsonArray(),
                ["blocked"] = Arr(new[] { $"本机没有可执行的 cmsdl（默认找 {Path.Combine(CmsdlDir, ExeName)}）。" }),
                ["warnings"] = new JsonArray(),
                ["mode"] = mode,
                ["region"] = region,
            };
        }

        var surface = HelpFor(path);
        var flags = surface.Flags;
        if (flags.Count == 0)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["argv"] = new JsonArray(),
                ["blocked"] = Arr(new[] { "cmsdl --help 没解析出任何开关，拒绝硬拼命令行。" }),
                ["warnings"] = new JsonArray(),
                ["mode"] = mode,
                ["region"] = region,
                ["help_text"] = surface.HelpText,
            };
        }

        var unknown = extraArgs
            .Where(a => a.StartsWith("--") && !flags.ContainsKey(a.Split('=')[0]))
            .ToList();
        if (unknown.Count > 0)
        {
            blocked.Add($"这些开关不在本机 cmsdl 的 --help 里：{string.Join(" ", unknown)}");
        }

        var knownRegions = surface.Regions.Select(r => r.Value).ToList();
        if (knownRegions.Count > 0 && !knownRegions.Contains(region))
        {
            blocked.Add($"区服 '{region}' 不在 cmsdl 支持的取值里：{string.Join(", ", knownRegions)}");
        }

        var writeMode = mode is "download" or "patch";
        var relation = PathRelation(effectiveTarget, DataDir);
        if (writeMode)
        {
            if (relation != "other" && !allowDataDir)
            {
                blocked.Add(
                    $"目标目录 {effectiveTarget} {RelationText(relation)}（{DataDir}）。" +
                    "要写它必须显式打开「允许写入数据源目录」。");
            }
            if (relation != "other")
            {
                warnings.Add(
                    $"目标目录 {effectiveTarget} {RelationText(relation)}；cmsdl 会在这里增删 WZ 文件，确认已备份再继续。");
            }
        }

        var opts = new List<string>();
        var modeArgs = new List<string>();

        bool AddFlag(string name, string? value = null)
        {
            if (!flags.ContainsKey(name)) return false;
            if (value is null)
            {
                opts.Add(name);
            }
            else if (flags[name].OptionalValue)
            {
                opts.Add($"{name}={value}");
            }
            else
            {
                opts.Add(name);
                opts.Add(value);
            }
            return true;
        }

        if (proxy is not null)
        {
            var handled = false;
            if (proxy is JsonValue value && value.TryGetValue<bool>(out var proxyBool))
            {
                handled = true;
                if (proxyBool)
                {
                    if (!AddFlag("--proxy")) warnings.Add("本机 cmsdl 不支持 --proxy，已忽略代理设置。");
                }
            }
            if (!handled)
            {
                var text = NodeText(args, "proxy");
                if (!AddFlag("--proxy", text.Length == 0 ? null : text))
                {
                    warnings.Add("本机 cmsdl 不支持 --proxy，已忽略代理设置。");
                }
            }
        }
        if (insecure)
        {
            if (!AddFlag("--allow-insecure")) warnings.Add("本机 cmsdl 不支持 --allow-insecure，已忽略。");
        }
        for (var i = 0; i < Math.Clamp(verbose, 0, 3); i++)
        {
            if (flags.ContainsKey("--verbose") || surface.HelpText.Contains("-v", StringComparison.Ordinal))
            {
                opts.Add("-v");
            }
        }

        // 非 Windows 上 cmsdl 本来也不弹窗；缺 --no-gui 只是老版本，警告即可。
        if (!AddFlag("--no-gui") && OsName != "nt")
        {
            warnings.Add("本机 cmsdl 没有 --no-gui；非 Windows 平台本来就不弹窗，可忽略。");
        }

        switch (mode)
        {
            case "check":
                modeArgs.Add("--check");
                if (build.Length > 0 && !AddFlag("--build", build))
                {
                    warnings.Add("本机 cmsdl 不支持 --build，已忽略。");
                }
                if (!AddFlag("--json")) warnings.Add("本机 cmsdl 不支持 --json，host 侧会退化成解析人读输出。");
                break;

            case "download":
                if (region == "manual")
                {
                    blocked.Add("region=manual 时 --download 收的是签名 URL，不是目标目录；本面板不支持这条路径。");
                }
                modeArgs.Add("--download");
                modeArgs.Add(targetDir);
                if (wzOnly && !AddFlag("--download-wz-only"))
                {
                    warnings.Add("本机 cmsdl 不支持 --download-wz-only，会下整个客户端（体积大得多）。");
                }
                if (filterSpec.Length > 0 && !AddFlag("--filter", filterSpec))
                {
                    warnings.Add("本机 cmsdl 不支持 --filter，已忽略。");
                }
                if (purge)
                {
                    if (!allowDataDir)
                    {
                        blocked.Add("--purge-wz-files 会删除数据源目录里不在清单中的文件，必须先打开「允许写入数据源目录」。");
                    }
                    else if (!AddFlag("--purge-wz-files"))
                    {
                        warnings.Add("本机 cmsdl 不支持 --purge-wz-files，已忽略。");
                    }
                    else
                    {
                        warnings.Add("已启用 --purge-wz-files：数据源目录里不在官方清单中的文件会被删除。");
                    }
                }
                if (build.Length > 0) AddFlag("--build", build);
                break;

            case "patch_list":
                modeArgs.Add("--patch");
                modeArgs.Add("list");
                if (buildSince.Length > 0) AddFlag("--build-since", buildSince);
                if (!AddFlag("--json")) warnings.Add("本机 cmsdl 不支持 --json，host 侧会退化成解析人读输出。");
                break;

            case "patch":
                modeArgs.Add("--patch");
                modeArgs.Add(patchVersion.Length > 0 ? patchVersion : "latest");
                modeArgs.Add(targetDir);
                if (launchAfterPatching)
                {
                    if (!AddFlag("--launch-after-patching"))
                    {
                        warnings.Add("本机 cmsdl 不支持 --launch-after-patching，已忽略。");
                    }
                    else
                    {
                        warnings.Add("已启用 --launch-after-patching：打完补丁会直接拉起游戏。");
                    }
                }
                else
                {
                    warnings.Add("未启用 --launch-after-patching：只打补丁，不启动游戏。");
                }
                break;

            case "torrent":
                modeArgs.Add("--get-bit-torrent");
                var outDir = torrentOut.Length > 0 ? torrentOut : CmsdlDir;
                if (!AddFlag("--output", outDir))
                {
                    warnings.Add("本机 cmsdl 不支持 -o/--output，.torrent 会落在进程当前目录。");
                }
                break;

            default:
                blocked.Add($"未知 mode '{mode}'（可用：check / download / patch_list / patch / torrent）。");
                break;
        }

        // 官方 usage 是 cmsdl [OPTIONS] <--download …> <REGION> [CLIENT_DIR]，
        // 顺序错会把目录当成区服或把区服当成目录。
        var positional = new List<string> { region };
        if (clientDir.Length > 0 && mode is "download" or "patch") positional.Add(clientDir);

        var argv = new List<string> { path };
        argv.AddRange(opts);
        argv.AddRange(modeArgs);
        argv.AddRange(positional);
        argv.AddRange(extraArgs);

        return new JsonObject
        {
            ["ok"] = blocked.Count == 0,
            ["argv"] = Arr(argv),
            ["argv_text"] = string.Join(" ", argv.Select(Quote)),
            ["blocked"] = Arr(blocked),
            ["warnings"] = Arr(warnings),
            ["mode"] = mode,
            ["region"] = region,
            ["cmsdl"] = new JsonObject
            {
                ["path"] = path,
                ["source"] = source,
                ["help_flags"] = Arr(surface.Order.OrderBy(x => x, StringComparer.Ordinal)),
                ["regions"] = Arr(knownRegions),
            },
            ["target"] = new JsonObject
            {
                ["dir"] = targetDir,
                ["client_dir"] = clientDir.Length > 0 ? clientDir : null,
                ["effective_dir"] = effectiveTarget,
                ["wz_subdir"] = wzSubdir,
                ["exists"] = Directory.Exists(effectiveTarget),
                ["relation_to_data_dir"] = relation,
                ["data_dir"] = DataDir,
                ["wz_only"] = wzOnly,
            },
        };
    }

    private static string Quote(string token)
    {
        if (token.Length > 0 && Regex.IsMatch(token, @"\A[A-Za-z0-9_@%+=:,./-]+\z", RegexOptions.CultureInvariant))
        {
            return token;
        }
        return "'" + token.Replace("'", "'\\''") + "'";
    }

    private static string PathRelation(string target, string dataDir)
    {
        var t = RealPath(target);
        var d = RealPath(dataDir);
        if (t == d) return "same";
        if (t.StartsWith(d.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return "inside";
        }
        if (d.StartsWith(t.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return "parent";
        }
        return "other";
    }

    private static string RelationText(string relation) => relation switch
    {
        "same" => "就是 WZ 数据源目录本身",
        "inside" => "在 WZ 数据源目录里面",
        "parent" => "是 WZ 数据源目录的上级目录",
        _ => "与 WZ 数据源目录无关",
    };

    private static string RealPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return NormalizePath(Environment.CurrentDirectory);
        var full = NormalizePath(Path.GetFullPath(ExpandUser(path)));
        try
        {
            FileSystemInfo? info = Directory.Exists(full)
                ? new DirectoryInfo(full)
                : File.Exists(full) ? new FileInfo(full) : null;
            if (info is not null)
            {
                var target = info.ResolveLinkTarget(true);
                return target is null ? full : NormalizePath(target.FullName);
            }
        }
        catch
        {
        }

        // realpath 对不存在的路径也会解析已存在的祖先（macOS 的 /tmp 就是 /private/tmp）。
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || parent == full) return full;
        return NormalizePath(Path.Combine(RealPath(parent), Path.GetFileName(full)));
    }

    private static string NormalizePath(string path)
    {
        if (path.Length <= 1) return path;
        var root = Path.GetPathRoot(path) ?? "";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? root : trimmed;
    }

    // ====== 任务状态机 ======

    internal static JsonNode Status()
    {
        EnsureTool();
        lock (Sync)
        {
            var task = new JsonObject
            {
                ["phase"] = Job.Phase,
                ["state"] = Job.Phase,
                ["running"] = Job.Running,
                ["kind"] = StrNode(Job.Kind),
                ["label"] = Job.Label,
                ["mode"] = Job.Mode,
                ["region"] = Job.Region,
                ["targetDir"] = Job.TargetDir,
                ["argv"] = Arr(Job.Argv),
                ["command"] = Job.Command,
                ["bytes"] = Job.Bytes,
                ["totalBytes"] = Num(Job.TotalBytes),
                ["dirBytes"] = Num(Job.DirBytes),
                ["percent"] = Num(Job.Percent),
                ["log"] = Arr(Job.LogTail),
                ["logLines"] = Job.Log.Count,
                ["startedAt"] = StrNode(Job.StartedAt is null ? null : Iso(Job.StartedAt.Value)),
                ["endedAt"] = StrNode(Job.EndedAt is null ? null : Iso(Job.EndedAt.Value)),
                ["exitCode"] = Num(Job.ExitCode),
                ["error"] = StrNode(Job.Error),
                ["warnings"] = Arr(Job.Warnings),
                ["blocked"] = Arr(Job.Blocked),
                ["result"] = Job.Result?.DeepClone(),
            };

            var tool = Tool;
            var status = new JsonObject
            {
                ["tool"] = tool?.DeepClone(),
                ["toolProbedAt"] = StrNode(ToolProbedAt == default ? null : Iso(ToolProbedAt)),
                ["found"] = tool?["found"]?.GetValue<bool>() ?? false,
                ["path"] = tool?["path"]?.DeepClone(),
                ["source"] = tool?["source"]?.DeepClone(),
                ["version"] = tool?["version"]?.DeepClone(),
                ["platform"] = PlatformTag,
                ["os"] = OsTag,
                ["arch"] = ArchTag,
                ["dataDir"] = DataDir,
                ["cmsdlDir"] = CmsdlDir,
                ["toolPath"] = Path.Combine(CmsdlDir, ExeName),
                ["defaultTargetDir"] = DefaultTargetDir,
                ["releasePage"] = ReleasePage,
                ["releaseTag"] = ReleaseTag,
                ["running"] = Job.Running,
                ["state"] = Job.Phase,
                ["log"] = Arr(Job.Log),
                ["task"] = task,
                ["now"] = NowIso(),
            };
            return status;
        }
    }

    internal static JsonNode Start(JsonObject args)
    {
        lock (Sync)
        {
            if (Job.Running)
            {
                return new JsonObject
                {
                    ["started"] = false,
                    ["blocked"] = Arr(new[] { $"已有任务在跑（{Job.Label}），先取消或等它结束。" }),
                    ["warnings"] = new JsonArray(),
                };
            }
        }

        var plan = Plan(args);
        var planOk = plan["ok"]?.GetValue<bool>() ?? false;
        if (!planOk)
        {
            return new JsonObject
            {
                ["started"] = false,
                ["blocked"] = plan["blocked"]?.DeepClone() ?? new JsonArray(),
                ["warnings"] = plan["warnings"]?.DeepClone() ?? new JsonArray(),
                ["plan"] = plan.DeepClone(),
                ["state"] = "idle",
            };
        }

        var mode = plan["mode"]?.GetValue<string>() ?? "download";
        var region = plan["region"]?.GetValue<string>() ?? "cms";
        var target = plan["target"] as JsonObject;
        var effective = target?["effective_dir"]?.GetValue<string>() ?? DefaultTargetDir;
        var argv = new List<string>();
        if (plan["argv"] is JsonArray argvArray)
        {
            foreach (var node in argvArray)
            {
                if (node is not null) argv.Add(node.GetValue<string>());
            }
        }
        if (argv.Count == 0)
        {
            return new JsonObject
            {
                ["started"] = false,
                ["blocked"] = Arr(new[] { "cmsdl 命令为空，拒绝执行。" }),
                ["warnings"] = new JsonArray(),
                ["plan"] = plan.DeepClone(),
            };
        }

        // worker 侧再独立挡一次写数据源：面板与 plan 的判断都不可全信。
        var writeMode = mode is "download" or "patch";
        var allowDataDir = Bool(args, "allow_data_dir");
        if (writeMode && !allowDataDir && SelfOrInside(RealPath(effective), RealPath(DataDir)))
        {
            var message =
                $"目标目录 {RealPath(effective)} 落在 WZ 数据源目录 {RealPath(DataDir)} 里，" +
                "未打开「允许写入数据源目录」时拒绝执行。";
            return new JsonObject
            {
                ["started"] = false,
                ["blocked"] = Arr(new[] { message }),
                ["warnings"] = plan["warnings"]?.DeepClone() ?? new JsonArray(),
                ["plan"] = plan.DeepClone(),
            };
        }

        var command = string.Join(" ", argv.Select(Quote));
        lock (Sync)
        {
            if (Job.Running)
            {
                return new JsonObject
                {
                    ["started"] = false,
                    ["blocked"] = Arr(new[] { $"已有任务在跑（{Job.Label}），先取消或等它结束。" }),
                    ["warnings"] = new JsonArray(),
                };
            }
            Job = new TaskState
            {
                Phase = "planning",
                Running = true,
                Kind = "cmsdl",
                Label = $"cmsdl {region} {mode}",
                Mode = mode,
                Region = region,
                TargetDir = effective,
                Argv = argv,
                Command = command,
                Warnings = (plan["warnings"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList()
                           ?? new List<string>(),
                StartedAt = DateTimeOffset.UtcNow,
            };
        }

        PushLog($"$ {command}");
        foreach (var warning in Job.Warnings) PushLog($"注意：{warning}");

        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TERM"] = "dumb";
        psi.Environment["RUST_BACKTRACE"] = "1";

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi };
            if (!proc.Start())
            {
                throw new InvalidOperationException("无法启动 cmsdl 进程");
            }
            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            PushLog($"启动失败：{ex.Message}");
            Finish("failed", null, ex.Message);
            return new JsonObject
            {
                ["started"] = false,
                ["error"] = ex.Message,
                ["plan"] = plan.DeepClone(),
            };
        }

        lock (Sync)
        {
            Child = proc;
            Job.Phase = "running";
        }
        if (writeMode && Directory.Exists(effective)) StartDirSampling(effective);

        _ = Task.Run(() => MonitorAsync(proc));
        return new JsonObject
        {
            ["started"] = true,
            ["argv"] = Arr(argv),
            ["command"] = command,
            ["state"] = "running",
            ["plan"] = plan.DeepClone(),
        };
    }

    private static async Task MonitorAsync(Process proc)
    {
        var stdout = PumpAsync(proc.StandardOutput, false);
        var stderr = PumpAsync(proc.StandardError, true);
        try
        {
            await proc.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch
        {
        }

        var exitCode = -1;
        try
        {
            exitCode = proc.ExitCode;
        }
        catch
        {
        }

        // --check --json 的 stdout 是一整行 JSON，退出时顺手解析给面板。
        JsonObject? parsed = null;
        lock (Sync)
        {
            foreach (var line in Job.Log.AsEnumerable().Reverse())
            {
                var raw = TimestampRx.Replace(line, "").Trim();
                if (!raw.StartsWith("{")) continue;
                try
                {
                    parsed = JsonNode.Parse(raw) as JsonObject;
                }
                catch
                {
                    parsed = null;
                }
                if (parsed is not null) break;
            }
        }
        if (parsed is not null)
        {
            lock (Sync)
            {
                if (parsed["total_size"] is JsonValue total && total.TryGetValue<long>(out var totalBytes))
                {
                    Job.TotalBytes = Math.Max(Job.TotalBytes ?? 0, totalBytes);
                }
                Job.Result = parsed;
            }
        }

        if (exitCode == 0)
        {
            PushLog("完成：cmsdl 退出码 0");
            Finish("done", 0, null);
        }
        else
        {
            PushLog($"失败：cmsdl 退出码 {exitCode}");
            Finish("failed", exitCode, $"cmsdl 退出码 {exitCode}——看日志尾部定位原因");
        }
    }

    private static async Task PumpAsync(StreamReader reader, bool isStderr)
    {
        var buffer = new char[4096];
        var pending = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0) break;
            pending.Append(buffer, 0, read);
            DrainLines(pending, isStderr);
            if (pending.Length > 65536)
            {
                Feed(pending.ToString(), isStderr);
                pending.Clear();
            }
        }
        if (pending.Length > 0) Feed(pending.ToString(), isStderr);
    }

    private static void DrainLines(StringBuilder pending, bool isStderr)
    {
        while (pending.Length > 0)
        {
            var text = pending.ToString();
            var index = text.IndexOfAny(new[] { '\r', '\n' });
            if (index < 0) return;
            var line = text[..index];
            var skip = index + 1;
            if (text[index] == '\r' && skip < text.Length && text[skip] == '\n') skip++;
            pending.Remove(0, skip);
            Feed(line, isStderr);
        }
    }

    private static void Feed(string line, bool isStderr)
    {
        var text = AnsiRx.Replace(line ?? "", "").TrimEnd();
        if (text.Trim().Length == 0) return;

        var percents = PercentRx.Matches(text);
        double? percent = null;
        if (percents.Count > 0)
        {
            var value = percents[^1].Groups[1].Value;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed is >= 0 and <= 100)
            {
                percent = parsed;
            }
        }

        var bytes = new List<double>();
        foreach (Match match in BytesRx.Matches(text))
        {
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            bytes.Add(value * (Units.TryGetValue(match.Groups[2].Value, out var unit) ? unit : 1));
        }

        lock (Sync)
        {
            if (percent is double p) Job.Percent = Math.Max(Job.Percent ?? 0, p);
            if (bytes.Count >= 2)
            {
                Job.Bytes = Math.Max(Job.Bytes, (long)Math.Min(bytes[0], bytes[1]));
                Job.TotalBytes = Math.Max(Job.TotalBytes ?? 0, (long)bytes[1]);
            }
            else if (bytes.Count == 1)
            {
                Job.Bytes = Math.Max(Job.Bytes, (long)bytes[0]);
            }
        }

        PushLog(isStderr ? $"! {text}" : text);
    }

    private static void PushLog(string line)
    {
        var text = AnsiRx.Replace(line ?? "", "").TrimEnd();
        if (text.Trim().Length == 0) return;
        var entry = $"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {text}";
        lock (Sync)
        {
            Job.Log.Add(entry);
            if (Job.Log.Count > LogKeep) Job.Log.RemoveRange(0, Job.Log.Count - LogKeep);
            Job.LogTail = Job.Log.Skip(Math.Max(0, Job.Log.Count - LogTail)).ToList();
        }
    }

    private static void Finish(string phase, int? exitCode, string? error)
    {
        lock (Sync)
        {
            Job.Phase = phase;
            Job.Running = false;
            Job.EndedAt = DateTimeOffset.UtcNow;
            Job.ExitCode = exitCode;
            Job.Error = error;
        }
        StopDirSampling();
        Child = null;
    }

    private static void EnsureTool()
    {
        lock (Sync)
        {
            if (Tool is not null && (DateTimeOffset.UtcNow - ToolProbedAt).TotalSeconds < 60) return;
        }
        var probe = Probe(new JsonObject());
        lock (Sync)
        {
            Tool = probe;
            ToolProbedAt = DateTimeOffset.UtcNow;
        }
    }

    private static void StartDirSampling(string dir)
    {
        StopDirSampling();
        var cts = new CancellationTokenSource();
        DirCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var size = DirBytes(dir);
                lock (Sync)
                {
                    if (!cts.IsCancellationRequested) Job.DirBytes = size;
                }
                try
                {
                    await Task.Delay(2000, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    private static void StopDirSampling()
    {
        var cts = DirCts;
        DirCts = null;
        try
        {
            cts?.Cancel();
        }
        catch
        {
        }
    }

    private static long? DirBytes(string root)
    {
        long total = 0;
        var seen = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch
            {
                continue;
            }
            foreach (var full in entries)
            {
                seen++;
                if (seen > DirWalkMax) return null;
                try
                {
                    var attributes = File.GetAttributes(full);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        stack.Push(full);
                    }
                    else
                    {
                        total += new FileInfo(full).Length;
                    }
                }
                catch
                {
                }
            }
        }
        return total;
    }

    // ====== 环境与平台 ======

    private static string DataDir => Environment.GetEnvironmentVariable("WZ_DATA_DIR") ?? "";

    private static string OsName => OperatingSystem.IsWindows() ? "nt" : "posix";

    private static string OsTag
    {
        get
        {
            if (OperatingSystem.IsMacOS()) return "darwin";
            if (OperatingSystem.IsWindows()) return "win32";
            if (OperatingSystem.IsLinux()) return "linux";
            if (OperatingSystem.IsFreeBSD()) return "freebsd";
            return "unknown";
        }
    }

    private static string ArchTag => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        Architecture.X86 => "ia32",
        Architecture.Arm => "arm",
        _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
    };

    private static string PlatformTag => $"{OsTag}:{ArchTag}";

    private static string NowIso() => Iso(DateTimeOffset.UtcNow);

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string HomeDir()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home)) return home;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string ExpandUser(string path)
    {
        if (path == "~") return HomeDir();
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(HomeDir(), path[2..]);
        }
        return path;
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in path.Split(Path.PathSeparator))
        {
            var dir = raw.Length == 0 ? "." : raw;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    private static bool SelfOrInside(string target, string root)
    {
        if (target == root) return true;
        return target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
    }

    // ====== JSON 小工具 ======

    private static string Str(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null) return "";
        if (node is JsonValue value)
        {
            try
            {
                return value.GetValue<string>();
            }
            catch
            {
            }
        }
        return node.ToJsonString().Trim('"');
    }

    private static string NodeText(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null) return "";
        if (node is JsonValue value)
        {
            try
            {
                return value.GetValue<string>();
            }
            catch
            {
            }
        }
        return node.ToJsonString();
    }

    private static bool Bool(JsonObject args, string key)
    {
        var node = args[key];
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<bool>(out var flag)) return flag;
        if (value.TryGetValue<string>(out var text)) return text.Length > 0;
        if (value.TryGetValue<double>(out var number)) return number != 0;
        return false;
    }

    private static int Int(JsonObject args, string key)
    {
        var node = args[key];
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<long>(out var longNumber)) return (int)longNumber;
        if (value.TryGetValue<double>(out var doubleNumber)) return (int)doubleNumber;
        if (value.TryGetValue<string>(out var text) && int.TryParse(text.Trim(), out var parsed)) return parsed;
        return 0;
    }

    private static List<string> ExtraArgs(JsonObject args)
    {
        var node = args["extra_args"];
        if (node is JsonArray array)
        {
            return array.Select(item => item is null ? "" : NodeTextValue(item)).ToList();
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return new List<string>();
    }

    private static string NodeTextValue(JsonNode node)
    {
        if (node is JsonValue value)
        {
            try
            {
                return value.GetValue<string>();
            }
            catch
            {
            }
        }
        return node.ToJsonString();
    }

    private static JsonArray Arr(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(item);
        return array;
    }

    private static JsonNode? StrNode(string? value) => value is null ? null : JsonValue.Create(value);

    private static JsonNode? Num(long? value) => value is long number ? JsonValue.Create(number) : null;

    private static JsonNode? Num(double? value) => value is double number ? JsonValue.Create(number) : null;
}
