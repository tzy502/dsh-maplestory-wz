/**
 * cmsdl 资源更新 —— Host 侧执行器（长任务 + 内存进度状态）。
 *
 * 分工：
 *   py/cmsdl_support.py（worker）  定位二进制、跑 `--help` 解析真实开关面、拼 argv + 安全判定
 *   本文件（host）                 下载 cmsdl 本体、spawn cmsdl、把进度写进内存状态供面板轮询
 *
 * 为什么进度放在内存：`cmsdl <region> --download <dir>` 是几十分钟级的长任务，
 * HTTP 请求不能一直挂着；面板用 `update_status` 轮询这份快照。
 *
 * 安全（本文件是最后一道闸）：
 *   * 只允许从 cmsdl 的 gitee/github release 域名下载，**重定向后的最终域名也要校验**；
 *   * 落到 `~/.dsh/wz-assets/cmsdl/`（用户可写），下完校验体积 + chmod +x + 跑 `--version` 验证；
 *   * 绝不在未显式打开 allowDataDir 的情况下把 cmsdl 指向 WZ 数据源目录；
 *   * 执行前把完整命令行打进日志与状态里——「看到的就是跑的」。
 */

import { spawn } from 'node:child_process';
import { createWriteStream, existsSync } from 'node:fs';
import { statfs, chmod, mkdir, readdir, rename, rm, stat } from 'node:fs/promises';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';

/** cmsdl 本体所在的发布页与文件名（v0.2.5 实测：cmsdl_darwin 是 x86_64+arm64 通用二进制）。 */
const RELEASE_PAGE = 'https://gitee.com/HikariCalyx/cmsdl/releases';
const DEFAULT_RELEASE_TAG = 'v0.2.5';
const RELEASE_BASE = 'https://gitee.com/HikariCalyx/cmsdl/releases/download';

/**
 * 允许下载的域名白名单。gitee 的 release 会 302 到 foruda.gitee.com 上的签名 URL，
 * 所以**首跳域名与最终域名都要在这张表里**，否则拒绝落盘。
 */
const ALLOWED_HOSTS = new Set([
  'gitee.com',
  'foruda.gitee.com',
  'github.com',
  'objects.githubusercontent.com',
  'raw.githubusercontent.com',
]);

const ASSET_BY_PLATFORM = {
  'darwin:arm64': 'cmsdl_darwin',
  'darwin:x64': 'cmsdl_darwin',
  'linux:x64': 'cmsdl_x86-64-linux',
  'linux:arm64': 'cmsdl_aarch64-linux',
  'win32:x64': 'cmsdl_x86-64-windows.exe',
  'win32:arm64': 'cmsdl_aarch64-windows.exe',
  'win32:ia32': 'cmsdl_i686-windows.exe',
};

/** cmsdl_darwin 实测 6,241,888 B；低于 1 MB 一律当成下到了错误页。 */
const MIN_TOOL_BYTES = 1_000_000;
/** 日志尾部保留多少行给面板。 */
const LOG_KEEP = 400;
const LOG_TAIL = 40;
/** 目标目录体积采样上限：超过这么多条目就放弃（避免在超大目录上拖住 host）。 */
const DIR_WALK_MAX = 20_000;

function sleep(ms) {
  return new Promise((done) => setTimeout(done, ms));
}

function nowIso() {
  return new Date().toISOString();
}

/** 人类可读的体积，仅用于日志。 */
function human(bytes) {
  if (!Number.isFinite(bytes)) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i += 1;
  }
  return `${i === 0 ? v : v.toFixed(1)} ${units[i]}`;
}

/** 给「将执行的完整命令」加引号，只为显示。 */
function quoteToken(token) {
  return /^[A-Za-z0-9_@%+=:,./-]+$/.test(token) ? token : `'${String(token).replace(/'/g, "'\\''")}'`;
}

const RE_PERCENT = /(\d{1,3}(?:\.\d+)?)\s*%/g;
const RE_BYTES = /(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)\b/gi;
const UNIT = { b: 1, kb: 1024, mb: 1024 ** 2, gb: 1024 ** 3, tb: 1024 ** 4 };

/**
 * 从一行 cmsdl 输出里尽力抠出进度。
 *
 * ⚠️ 本机连不上游戏 CDN（`mxdver0.jijiagames.com` 连接被重置），**没能观察到一次成功下载
 * 的真实进度格式**，所以这里是防御式解析：抠不到就不动数字，绝不让解析结果替代原始日志。
 * 面板同时显示「cmsdl 自己报告的最大值」和「目标目录当前落盘体积」，后者才是真累计值。
 */
function parseProgress(line) {
  const out = {};
  const percents = [...line.matchAll(RE_PERCENT)].map((m) => Number(m[1]));
  if (percents.length) {
    const p = percents[percents.length - 1];
    if (Number.isFinite(p) && p >= 0 && p <= 100) out.percent = p;
  }
  const bytes = [...line.matchAll(RE_BYTES)].map((m) => Number(m[1]) * (UNIT[m[2].toLowerCase()] ?? 1));
  if (bytes.length >= 2) {
    // 形如 "12.3 MB / 456.7 MB"：第一个是已完成，第二个是总量
    out.done = Math.min(bytes[0], bytes[1]);
    out.total = bytes[1];
  } else if (bytes.length === 1) {
    out.done = bytes[0];
  }
  return out;
}

/**
 * 目标目录当前落盘体积——cmsdl 不报告总量时，这是唯一真实的累计进度。
 * 有条目上限，超了就返回 null（宁可不显示，也不拖住 host）。
 */
async function dirBytes(root) {
  let total = 0;
  let seen = 0;
  const stack = [root];
  while (stack.length) {
    const dir = stack.pop();
    let entries;
    try {
      entries = await readdir(dir, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const entry of entries) {
      seen += 1;
      if (seen > DIR_WALK_MAX) return null;
      const full = join(dir, entry.name);
      if (entry.isDirectory()) stack.push(full);
      else if (entry.isFile()) {
        try {
          total += (await stat(full)).size;
        } catch {
          /* 正在被 cmsdl 写、短暂拿不到就跳过 */
        }
      }
    }
  }
  return total;
}

export function createCmsdlManager({ worker, logger, dataDir, cmsdlDir }) {
  const exeName = process.platform === 'win32' ? 'cmsdl.exe' : 'cmsdl';
  const toolPath = join(cmsdlDir, exeName);
  const defaultTargetDir = join(cmsdlDir, 'client');

  /** 少于这个数就拒绝开下载：整包最低也有几十 GB。 */
  const MIN_FREE_BYTES = 20 * 1024 ** 3;

  /** 目标路径所在卷的可用字节数；拿不到返回 null（不因为探测失败就拦人）。 */
  async function freeBytesOf(target) {
    // 目标目录可能还不存在（第一次下载），逐级往上找到存在的祖先再问用量。
    let p = target;
    for (let i = 0; i < 10 && p && p !== '/' && p !== '.'; i += 1) {
      try {
        const st = await statfs(p);
        return Number(st.bavail) * Number(st.bsize);
      } catch {
        const up = p.replace(/\/[^/]+\/?$/, '');
        p = up && up !== p ? up : '/';
      }
    }
    return null;
  }

  function fmtBytes(n) {
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let v = n;
    let i = 0;
    while (v >= 1024 && i < units.length - 1) {
      v /= 1024;
      i += 1;
    }
    return `${v.toFixed(1)} ${units[i]}`;
  }

  /** 内存里的任务状态——面板 update_status 轮询的就是它。 */
  const state = {
    phase: 'idle', // idle | planning | downloading-tool | running | done | failed | cancelled
    running: false,
    kind: null, // tool | cmsdl
    label: '',
    mode: '',
    region: '',
    targetDir: '',
    argv: [],
    command: '',
    bytes: 0, // cmsdl / 下载器自己报告的最大值
    totalBytes: null,
    dirBytes: null, // 目标目录真实落盘体积
    percent: null,
    log: [],
    logTail: [],
    startedAt: null,
    endedAt: null,
    exitCode: null,
    error: null,
    warnings: [],
    blocked: [],
    cancelRequested: false,
    result: null,
    tool: null, // 最近一次 cmsdl_probe 结果
    toolProbedAt: null,
  };

  let child = null;
  let fetchAbort = null;
  let dirTimer = null;
  let dirTarget = null;

  const copy = (value) => JSON.parse(JSON.stringify(value ?? null));

  function pushLog(line) {
    const text = String(line).replace(/\u001b\[[0-9;]*m/g, '').trimEnd();
    if (!text.trim()) return;
    const stamp = new Date().toLocaleTimeString('zh-CN', { hour12: false });
    const entry = `[${stamp}] ${text}`;
    state.log.push(entry);
    if (state.log.length > LOG_KEEP) state.log.splice(0, state.log.length - LOG_KEEP);
    state.logTail = state.log.slice(-LOG_TAIL);
    logger.info?.(`cmsdl: ${text}`);
  }

  function resetTask(kind, label, extra = {}) {
    Object.assign(state, {
      phase: kind === 'tool' ? 'downloading-tool' : 'planning',
      running: true,
      kind,
      label,
      argv: [],
      command: '',
      bytes: 0,
      totalBytes: null,
      dirBytes: null,
      percent: null,
      log: [],
      logTail: [],
      startedAt: nowIso(),
      endedAt: null,
      exitCode: null,
      error: null,
      warnings: [],
      blocked: [],
      cancelRequested: false,
      result: null,
      ...extra,
    });
  }

  function finishTask(phase, extra = {}) {
    Object.assign(state, {
      phase,
      running: false,
      endedAt: nowIso(),
      ...extra,
    });
    stopDirSampling();
    child = null;
    fetchAbort = null;
  }

  /** 定期采一次目标目录体积（cmsdl 不报告总量时的累计进度来源）。 */
  function startDirSampling(dir) {
    stopDirSampling();
    dirTarget = dir;
    const tick = async () => {
      if (!state.running || dirTarget !== dir) return;
      const size = await dirBytes(dir);
      if (state.running && dirTarget === dir) state.dirBytes = size;
    };
    dirTimer = setInterval(tick, 2000);
    if (typeof dirTimer.unref === 'function') dirTimer.unref();
    tick();
  }

  function stopDirSampling() {
    if (dirTimer) clearInterval(dirTimer);
    dirTimer = null;
    dirTarget = null;
  }

  function feed(chunk, streamName) {
    for (const raw of String(chunk).split(/\r\n|\n|\r/)) {
      const line = raw.replace(/\u001b\[[0-9;]*m/g, '').trimEnd();
      if (!line.trim()) continue;
      const progress = parseProgress(line);
      if (progress.percent !== undefined) state.percent = Math.max(state.percent ?? 0, progress.percent);
      if (progress.done !== undefined) state.bytes = Math.max(state.bytes ?? 0, progress.done);
      if (progress.total !== undefined) state.totalBytes = Math.max(state.totalBytes ?? 0, progress.total);
      pushLog(streamName === 'stderr' ? `! ${line}` : line);
    }
  }

  const manager = {
    exeName,
    toolPath,
    defaultTargetDir,
    releasePage: RELEASE_PAGE,
    releaseTag: DEFAULT_RELEASE_TAG,

    /** 本机 cmsdl 探测（走 worker，不联网）。 */
    async probe(args = {}) {
      const forced = typeof args.path === 'string' ? args.path : '';
      const out = await worker.call('cmsdl_probe', forced ? { path: forced } : {});
      state.tool = out;
      state.toolProbedAt = nowIso();
      return out;
    },

    /** 面板轮询用的快照：工具状态 + 当前任务进度 + 日志尾部。 */
    async status() {
      return {
        tool: state.tool,
        toolProbedAt: state.toolProbedAt,
        platform: `${process.platform}:${process.arch}`,
        dataDir,
        cmsdlDir,
        toolPath,
        defaultTargetDir,
        releasePage: RELEASE_PAGE,
        releaseTag: DEFAULT_RELEASE_TAG,
        running: state.running,
        task: copy({
          phase: state.phase,
          running: state.running,
          kind: state.kind,
          label: state.label,
          mode: state.mode,
          region: state.region,
          targetDir: state.targetDir,
          argv: state.argv,
          command: state.command,
          bytes: state.bytes,
          totalBytes: state.totalBytes,
          dirBytes: state.dirBytes,
          percent: state.percent,
          log: state.logTail,
          logLines: state.log.length,
          startedAt: state.startedAt,
          endedAt: state.endedAt,
          exitCode: state.exitCode,
          error: state.error,
          warnings: state.warnings,
          blocked: state.blocked,
          result: state.result,
        }),
        now: nowIso(),
      };
    },

    /** 只拼命令、不执行——面板可以拿它做「预览将执行的命令」。 */
    async plan(args = {}) {
      const spec = { ...args };
      if (!spec.target_dir) spec.target_dir = defaultTargetDir;
      return worker.call('cmsdl_plan', spec);
    },

    /** 长任务：校验 → spawn cmsdl → 把 stdout/stderr 变成进度 + 日志。 */
    async run(args = {}) {
      if (state.running) {
        return { started: false, blocked: [`已有任务在跑（${state.label || state.kind}），先取消或等它结束。`], warnings: [] };
      }
      const mode = String(args.mode || 'download');
      const region = String(args.region || 'cms');
      const spec = { ...args, mode, region };
      if (!spec.target_dir) spec.target_dir = defaultTargetDir;

      // ① worker 侧拼 argv + 安全判定（只有它知道本机 cmsdl 的真实开关面）
      const plan = await manager.plan(spec);

      // 下载/打补丁要往目标卷写几十 GB；默认目标在 ~/.dsh（系统盘），
      // 系统盘常常只剩十几 GB —— 之前就是这么失败的，而且报错只在 cmsdl 的输出里。
      // 这里提前算一次：空间不够就明确拦住，并提示换到空间更大的盘。
      if (plan.ok && (mode === 'download' || mode === 'patch')) {
        const landing = plan?.target?.effective_dir || spec.target_dir;
        const free = await freeBytesOf(landing);
        if (free !== null && free < MIN_FREE_BYTES) {
          return {
            started: false,
            blocked: [
              `目标卷剩余 ${fmtBytes(free)}，下载整包需要几十 GB，空间不足。`,
              '把目标目录改到空间更大的盘（例如 /Volumes/SSD/mxd-latest），或先清理该卷。',
            ],
            warnings: plan.warnings ?? [],
            plan,
          };
        }
      }
      if (!plan.ok) {
        return {
          started: false,
          blocked: plan.blocked ?? ['cmsdl 命令没有通过安全校验。'],
          warnings: plan.warnings ?? [],
          plan,
        };
      }

      // ② host 侧再独立挡一次「写数据源目录」（纵深防御，不依赖下层 worker 的判断）。
      // 注意要比对 **实际落盘目录**：给了客户端目录时 WZ 落在
      // <client_dir>/mxd/Data（CMS）或 <client_dir>/Data（TMS），
      // 只比 target_dir 会漏判（用户完全可以把客户端指到数据源上）。
      const landing = plan.target?.effective_dir || spec.target_dir;
      const resolvedTarget = resolve(landing);
      const resolvedData = resolve(dataDir);
      const writeMode = mode === 'download' || mode === 'patch';
      const hitsData = resolvedTarget === resolvedData
        || resolvedTarget.startsWith(resolvedData.endsWith('/') ? resolvedData : `${resolvedData}/`);
      if (writeMode && hitsData && !spec.allow_data_dir) {
        return {
          started: false,
          blocked: [`目标目录 ${resolvedTarget} 落在 WZ 数据源目录 ${resolvedData} 里，未打开「允许写入数据源目录」时拒绝执行。`],
          warnings: plan.warnings ?? [],
          plan,
        };
      }

      const argv = plan.argv;
      const command = argv.map(quoteToken).join(' ');
      resetTask('cmsdl', `cmsdl ${region} ${mode}`, {
        mode,
        region,
        targetDir: resolvedTarget,
        argv,
        command,
        warnings: plan.warnings ?? [],
      });
      // 运行前打印将执行的完整命令（面板 + host 日志都能看到）
      pushLog(`$ ${command}`);
      for (const warning of state.warnings) pushLog(`注意：${warning}`);

      if (writeMode && existsSync(resolvedTarget)) {
        startDirSampling(resolvedTarget);
      }
      if (mode === 'check') state.phase = 'running';

      const childEnv = { ...process.env, NO_COLOR: '1', TERM: 'dumb', RUST_BACKTRACE: '1' };
      let proc;
      try {
        proc = spawn(argv[0], argv.slice(1), { env: childEnv, stdio: ['ignore', 'pipe', 'pipe'] });
      } catch (error) {
        pushLog(`启动失败：${error?.message ?? error}`);
        finishTask('failed', { error: String(error?.message ?? error) });
        return { started: false, error: String(error?.message ?? error), plan };
      }
      child = proc;
      state.phase = 'running';
      proc.stdout.setEncoding('utf8');
      proc.stdout.on('data', (chunk) => feed(chunk, 'stdout'));
      proc.stderr.setEncoding('utf8');
      proc.stderr.on('data', (chunk) => feed(chunk, 'stderr'));
      proc.on('error', (error) => {
        pushLog(`进程错误：${error?.message ?? error}`);
        finishTask('failed', { error: String(error?.message ?? error) });
      });
      proc.on('exit', (code, signal) => {
        // `--check --json` 的 stdout 是一整行 JSON，顺手解析出来给面板用
        const jsonLine = [...state.log].reverse().find((line) => /^\S*\{\s*"/.test(line.replace(/^\[[^\]]*\]\s*/, '').trim()));
        let parsed = null;
        if (jsonLine) {
          const raw = jsonLine.replace(/^\[[^\]]*\]\s*/, '').trim();
          try {
            parsed = JSON.parse(raw);
          } catch {
            parsed = null;
          }
        }
        if (parsed && typeof parsed === 'object') {
          if (Number.isFinite(parsed.total_size)) state.totalBytes = parsed.total_size;
          state.result = parsed;
        }
        if (state.cancelRequested) {
          pushLog(`已取消（exit=${code} signal=${signal}）`);
          return finishTask('cancelled', { exitCode: code, exitSignal: signal });
        }
        if (code === 0) {
          pushLog('完成：cmsdl 退出码 0');
          return finishTask('done', { exitCode: 0 });
        }
        pushLog(`失败：cmsdl 退出码 ${code}${signal ? ` signal=${signal}` : ''}`);
        return finishTask('failed', {
          exitCode: code,
          exitSignal: signal,
          error: `cmsdl 退出码 ${code}${signal ? ` signal=${signal}` : ''}——看日志尾部定位原因`,
        });
      });

      return { started: true, argv, command, plan };
    },

    /** 降级路径：本机没有 cmsdl 时，只把 cmsdl 本体下下来（不碰客户端资源）。 */
    async fetchTool(args = {}) {
      if (state.running) {
        return { started: false, blocked: [`已有任务在跑（${state.label || state.kind}），先取消或等它结束。`] };
      }
      const key = `${process.platform}:${process.arch}`;
      const asset = ASSET_BY_PLATFORM[key];
      const tag = String(args.tag || DEFAULT_RELEASE_TAG);
      const url = String(args.url || `${RELEASE_BASE}/${tag}/${asset || ''}`);
      if (!asset && !args.url) {
        return { started: false, blocked: [`没有为 ${key} 准备 cmsdl 资产，请手动填下载链接。`] };
      }
      let first;
      try {
        first = new URL(url);
      } catch {
        return { started: false, blocked: [`下载链接不是合法 URL：${url}`] };
      }
      if (!ALLOWED_HOSTS.has(first.hostname)) {
        return {
          started: false,
          blocked: [`只允许从 cmsdl 的发布域名下载（${[...ALLOWED_HOSTS].join(', ')}），拒绝 ${first.hostname}。`],
        };
      }

      resetTask('tool', `下载 cmsdl（${asset || url}）`);
      state.phase = 'downloading-tool';
      pushLog(`GET ${url}`);
      pushLog(`落盘：${toolPath}.part → ${toolPath}`);

      const partPath = `${toolPath}.part`;
      const controller = new AbortController();
      fetchAbort = controller;
      try {
        await mkdir(cmsdlDir, { recursive: true });
        const response = await fetch(url, {
          redirect: 'follow',
          signal: controller.signal,
          headers: {
            'user-agent': 'dsh-maplestory-wz/0.2 (+https://gitee.com/HikariCalyx/cmsdl)',
            accept: 'application/octet-stream,*/*',
          },
        });
        const finalHost = (() => {
          try {
            return new URL(response.url).hostname;
          } catch {
            return '';
          }
        })();
        if (finalHost && !ALLOWED_HOSTS.has(finalHost)) {
          throw new Error(`重定向落到非白名单域名 ${finalHost}，已中止`);
        }
        if (!response.ok) throw new Error(`HTTP ${response.status} ${response.statusText}`);
        const lengthHeader = Number(response.headers.get('content-length'));
        state.totalBytes = Number.isFinite(lengthHeader) && lengthHeader > 0 ? lengthHeader : null;

        const sink = createWriteStream(partPath);
        let received = 0;
        const source = Readable.fromWeb(response.body);
        source.on('data', (chunk) => {
          received += chunk.length;
          state.bytes = received;
          if (state.totalBytes) state.percent = Math.round((received / state.totalBytes) * 1000) / 10;
        });
        await pipeline(source, sink);

        const info = await stat(partPath);
        if (info.size < MIN_TOOL_BYTES) {
          await rm(partPath, { force: true });
          throw new Error(`下到的文件只有 ${human(info.size)}，不足 ${human(MIN_TOOL_BYTES)}——大概是错误页，已删除`);
        }
        await chmod(partPath, 0o755);
        await rm(toolPath, { force: true });
        await rename(partPath, toolPath);
        pushLog(`已落盘 ${toolPath}（${human(info.size)}），已 chmod +x`);

        // 跑一次探测：既验证真的能执行，也顺手把版本与开关面带回来
        const probed = await manager.probe({ path: toolPath });
        if (!probed.found) throw new Error(`下载完成但 cmsdl 无法执行：${probed.note || '原因未知'}`);
        pushLog(`验证通过：cmsdl ${probed.version || '（版本未知）'} @ ${probed.path}`);
        state.result = { path: probed.path, version: probed.version, size: info.size, flags: probed.flags };
        finishTask('done', { exitCode: 0 });
        return { started: true, url, path: toolPath, size: info.size, tool: probed };
      } catch (error) {
        const aborted = controller.signal.aborted || state.cancelRequested;
        pushLog(`${aborted ? '已取消' : '下载失败'}：${error?.message ?? error}`);
        finishTask(aborted ? 'cancelled' : 'failed', { error: String(error?.message ?? error) });
        return { started: true, url, error: String(error?.message ?? error), cancelled: aborted };
      }
    },

    /** 取消当前任务：先 SIGTERM，3 秒后还在就 SIGKILL。 */
    async cancel() {
      if (!state.running) return { cancelled: false, reason: '当前没有在跑的任务' };
      state.cancelRequested = true;
      pushLog('收到取消请求…');
      if (fetchAbort) fetchAbort.abort();
      const proc = child;
      if (proc) {
        try {
          proc.kill('SIGTERM');
        } catch {
          /* 已经退出 */
        }
        setTimeout(() => {
          if (child === proc) {
            try {
              proc.kill('SIGKILL');
            } catch {
              /* 已经退出 */
            }
          }
        }, 3000).unref?.();
      }
      return { cancelled: true, kind: state.kind, phase: state.phase };
    },

    dispose() {
      stopDirSampling();
      try {
        fetchAbort?.abort();
      } catch {
        /* 忽略 */
      }
      if (child) {
        try {
          child.kill('SIGTERM');
        } catch {
          /* 已经退出 */
        }
        child = null;
      }
    },
  };

  return manager;
}

export const CMSDL_ALLOWED_HOSTS = [...ALLOWED_HOSTS];
export const CMSDL_RELEASE_PAGE = RELEASE_PAGE;
export const CMSDL_ASSETS = ASSET_BY_PLATFORM;
export { human as humanBytes };
