/**
 * 冒险岛 WZ 资源插件 —— Host 半边。
 *
 * 结构：
 *   Python worker（py/wz_worker.py，基于 wzpy 读本地 WZ 数据源）
 *     ⇅ JSON-line over stdio
 *   Host 插件（本文件）
 *     ├─ 工具：wz_packs / wz_tree / wz_search / wz_sprite / wz_item
 *     ├─ HTTP：/dsh-maplestory-wz/file/<name>      雪碧图与 GIF 静态文件
 *     ├─ HTTP：/dsh-maplestory-wz/image?...        单帧/图标直出 PNG
 *     └─ HTTP：/dsh-maplestory-wz/api          面板数据接口（packs/tree/detail/sprite…）
 *
 * 「资源更新」（cmsdl）：host 侧执行器在 ./lib/cmsdl.js，命令面在 py/cmsdl_support.py。
 *
 * 数据一切从本地 WZ 走：无数据库、无硬编码资源。
 */
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { createCmsdlManager } from './lib/cmsdl.js';

const HERE = dirname(fileURLToPath(import.meta.url));
const WORKER = join(HERE, 'py', 'wz_worker.py');
const ROUTE_PREFIX = '/dsh-maplestory-wz';
// 不设本机默认数据目录：硬编码某台机器的路径对别人无意义，
// 还会把「没配 dataDir」表现成「路径不存在」这种难查的现象。
const DEFAULT_DATA_DIR = '';
const OUT_DIR = join(homedir(), '.dsh', 'wz-assets');
const BG_CONFIG = join(OUT_DIR, 'background.json');
// 最近一次 plan 用的地区（状态卡推导 WZ 落盘位置时要它）
let cmsdlStatusRegion = 'cms';
/** cmsdl 本体与它下下来的客户端资源都落在这里（用户可写，**不是** WZ 数据源目录）。 */
const CMSDL_DIR = join(OUT_DIR, 'cmsdl');

/** 数字兜底（原 dsh-desktop-pet 的辅助函数，合并时一并带过来）。 */
function numOr(value, fallback) {
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
}

// ── 桌面宠物（原 @local/dsh-desktop-pet）───────────────────────────────
// 路径全部**保持与合并前一致**，这样已存在的产物与配置不用迁移。
const PET_WORKER = join(HERE, 'py', 'pet_worker.py');
/** pet worker 的输出目录（~/.dsh/desktop-pet）。 */
const PET_OUT_DIR = join(homedir(), '.dsh', 'desktop-pet');
const PET_CONFIG = join(PET_OUT_DIR, 'pet-config.json');

function optsOutDir(prefix, wzOutDir) {
  return prefix === 'PET' ? PET_OUT_DIR : wzOutDir;
}

// 解释器探测：配置/环境变量优先，再按平台找常见位置。
// Windows 上没有常用的 `python3` 命令（是 `python` / `py`），漏了这几项会让
// Windows 用户一装上就报「找不到 Python」。
const PYTHON_CANDIDATES = [
  process.env.WZ_PYTHON,
  join(homedir(), '.hermes', 'cache', 'scratch', 'wz-env', 'bin', 'python'),
  '/opt/homebrew/bin/python3',                                   // macOS Apple Silicon
  '/usr/local/bin/python3',                                      // macOS Intel / Linux
  '/usr/bin/python3',
  join(homedir(), '.hermes', 'cache', 'scratch', 'wz-env', 'Scripts', 'python.exe'),
  'py',                                                          // Windows launcher
  'python',
  'python3',
];

export const inject = ['tools', 'webServer'];

/** 与 Python worker 的一问一答通道。 */
class WzWorker {
  /**
   * @param {object} config  插件配置（dataDir/region/version/python/wzpyPath…）
   * @param {object} logger
   * @param {{script?: string, envPrefix?: string, id?: string}} [opts]
   *   合并 dsh-desktop-pet 后本类要同时驱动两个 Python worker（wz / pet）。
   *   两者的启动/通信/退出逻辑逐行相同，只差「脚本路径」与「环境变量前缀」，
   *   所以这里把它们参数化，而不是复制一份 PetWorker（复制会出现两份要同步维护的实现）。
   */
  constructor(config, logger, opts = {}) {
    this.script = opts.script || WORKER;
    this.envPrefix = opts.envPrefix || 'WZ';
    this.id = opts.id || 'maplestory-wz';
    this.config = config;
    this.logger = logger;
    this.child = null;
    this.seq = 0;
    this.pending = new Map();
    this.buffer = '';
    this.stderrTail = [];
    this.startPromise = null;
  }

  resolvePython() {
    if (this.config.python) return this.config.python;
    for (const cand of PYTHON_CANDIDATES) {
      if (cand && existsSync(cand)) return cand;
    }
    return 'python3';
  }

  async start() {
    if (this.startPromise) return this.startPromise;
    this.startPromise = (async () => {
      const python = this.resolvePython();
      const env = { ...process.env };
      // hermes 注入的 PYTHONPATH 会把 3.14 的 site-packages 塞给 3.11 解释器 → PIL 崩
      delete env.PYTHONPATH;
      env.PYTHONUNBUFFERED = '1';
      env.PYTHONDONTWRITEBYTECODE = '1';
      const P = this.envPrefix;
      env[`${P}_DATA_DIR`] = this.config.dataDir;
      env[`${P}_REGION`] = this.config.region || 'BMS';
      env[`${P}_VERSION`] = String(this.config.version ?? 228);
      env[`${P}_OUT_DIR`] = optsOutDir(this.envPrefix, OUT_DIR);
      if (this.config.wzpyPath) env.WZPY_PATH = this.config.wzpyPath;
      // cmsdl 探测用：显式指定二进制路径 / 存放目录（py/cmsdl_support.py 读这两个变量）
      if (this.config.cmsdlPath) env.WZ_CMSDL = this.config.cmsdlPath;
      if (this.config.cmsdlDir) env.WZ_CMSDL_DIR = this.config.cmsdlDir;

      const child = spawn(python, [this.script], { env, stdio: ['pipe', 'pipe', 'pipe'] });
      this.child = child;
      child.stdout.setEncoding('utf8');
      child.stdout.on('data', (chunk) => this.onData(chunk));
      child.stderr.setEncoding('utf8');
      child.stderr.on('data', (chunk) => {
        for (const line of String(chunk).split('\n')) {
          if (!line.trim()) continue;
          this.stderrTail.push(line);
          if (this.stderrTail.length > 40) this.stderrTail.shift();
        }
      });
      child.on('exit', (code, signal) => {
        const err = new Error(`WZ worker 退出（code=${code} signal=${signal}）`);
        for (const [, entry] of this.pending) entry.reject(err);
        this.pending.clear();
        this.child = null;
        this.startPromise = null;
      });
      const info = await this.call('stats');
      this.logger.info?.(`worker 就绪 python=${python} data=${info.data_dir} region=${info.region} v${info.version}`);
      // 与磁盘上的 py 源码比对指纹：不一致说明这个常驻 worker 跑的是旧代码
      // （改完 .py 没重启 worker 就会这样），直接说清楚，别让人对着旧结果猜。
      try {
        const fs = await import('node:fs/promises');
        const { createHash } = await import('node:crypto');
        const here = dirname(fileURLToPath(import.meta.url));
        const pyDir = join(here, 'py');
        const names = (await fs.readdir(pyDir)).filter((n) => n.endsWith('.py')).sort();
        const disk = {};
        for (const name of names) {
          const buf = await fs.readFile(join(pyDir, name));
          disk[name] = createHash('sha1').update(buf).digest('hex').slice(0, 12);
        }
        const stale = Object.keys(disk).filter((n) => info.fingerprint?.[n] && info.fingerprint[n] !== disk[n]);
        this.staleSources = stale;
        if (stale.length) {
          this.logger.warn?.(
            `WZ worker 跑的是旧代码（${stale.join(', ')} 已在磁盘上更新）；` +
            '执行 `pkill -f wz_worker.py` 让它按需重启即可生效',
          );
        }
      } catch {
        /* 指纹比对只是诊断，失败不影响功能 */
      }
      return info;
    })();
    return this.startPromise;
  }

  onData(chunk) {
    this.buffer += chunk;
    let idx;
    while ((idx = this.buffer.indexOf('\n')) >= 0) {
      const line = this.buffer.slice(0, idx).trim();
      this.buffer = this.buffer.slice(idx + 1);
      if (!line) continue;
      let msg;
      try {
        msg = JSON.parse(line);
      } catch {
        this.logger.warn?.(`worker 输出无法解析：${line.slice(0, 200)}`);
        continue;
      }
      const entry = this.pending.get(msg.id);
      if (!entry) continue;
      this.pending.delete(msg.id);
      if (msg.ok) entry.resolve(msg.data);
      else {
        const err = new Error(msg.error?.message || 'worker 错误');
        err.code = msg.error?.type;
        err.stderr = this.stderrTail.slice(-6);
        entry.reject(err);
      }
    }
  }

  async call(cmd, args = {}) {
    if (!this.child) await this.start();
    const child = this.child;
    if (!child) throw new Error(`${this.id} worker 未启动`);
    const id = ++this.seq;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      child.stdin.write(`${JSON.stringify({ id, cmd, args })}\n`, (err) => {
        if (err) {
          this.pending.delete(id);
          reject(err);
        }
      });
    });
  }

  dispose() {
    const child = this.child;
    this.child = null;
    if (child) {
      try {
        child.stdin.end();
        child.kill('SIGTERM');
      } catch {
        /* 已经退出 */
      }
    }
  }
}

/** 工具输出：结构化 {summary, data}，render 负责转成人读文本。 */
function textResult(summary, data) {
  return { summary, data: data ?? null };
}

/** 统一的 render：摘要 + 折叠的 JSON。 */
function renderResult(_args, value) {
  return [
    { type: 'text', text: `${value?.summary ?? ''}\n\n\`\`\`json\n${JSON.stringify(value?.data ?? null, null, 2)}\n\`\`\`` },
  ];
}

const OUTPUT_SCHEMA = {
  type: 'object',
  properties: { summary: { type: 'string' }, data: { type: 'object', additionalProperties: true } },
  required: ['summary', 'data'],
  additionalProperties: false,
};

function jsonBody(res, status, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(body),
    'cache-control': 'no-store',
  });
  res.end(body);
}

export function apply(ctx, rawConfig = {}) {
  const logger = ctx.logger ?? console;
  const config = {
    dataDir: typeof rawConfig.dataDir === 'string' && rawConfig.dataDir ? rawConfig.dataDir : DEFAULT_DATA_DIR,
    wzpyPath: typeof rawConfig.wzpyPath === 'string' ? rawConfig.wzpyPath : undefined,
    python: typeof rawConfig.python === 'string' ? rawConfig.python : undefined,
    region: typeof rawConfig.region === 'string' && rawConfig.region ? rawConfig.region : 'BMS',
    version: Number.isFinite(rawConfig.version) ? Number(rawConfig.version) : 228,
    cmsdlPath: typeof rawConfig.cmsdlPath === 'string' && rawConfig.cmsdlPath ? rawConfig.cmsdlPath : undefined,
    cmsdlDir: typeof rawConfig.cmsdlDir === 'string' && rawConfig.cmsdlDir ? join(rawConfig.cmsdlDir) : CMSDL_DIR,
  };
  const worker = new WzWorker(config, logger);
  /** cmsdl 资源更新：下载 cmsdl 本体 + 跑 cmsdl，进度写内存供面板轮询。 */
  const cmsdl = createCmsdlManager({
    worker,
    logger,
    dataDir: config.dataDir,
    cmsdlDir: config.cmsdlDir,
  });
  /** cmsdl_probe 的 host 侧缓存标记（真正结果在 cmsdl 管理器里）。 */
  const cmsdlState = { tool: false, at: 0 };

  ctx.effect(() => () => worker.dispose(), 'maplestory-wz: worker');
  ctx.effect(() => () => cmsdl.dispose(), 'maplestory-wz: cmsdl');

  // ── 面板数据接口（HTTP /api，Client 用 fetch）──────────────────────
  /** 详情短缓存：同一编号的连续点击不必重复解析 WZ。 */
  const detailCache = new Map();

  /**
   * 探测游戏客户端目录。cmsdl 没有探测子命令，也没有配置文件，所以按
   * 「客户端根下存在 mxd/Data 或 Data」这个特征，在常见位置里找。
   */
  function detectClientDirs() {
    const out = [];
    // 数据源是 <client>/mxd/Data，所以客户端根 = 上两级
    // dataDir 没配置时不能拿空串去 dirname(dirname(...)) —— 会得到 "." 这种无意义候选
    const parent = DEFAULT_DATA_DIR ? dirname(dirname(DEFAULT_DATA_DIR)) : '';
    const cands = [
      parent !== '.' ? parent : '',
      join(homedir(), 'Applications', '冒险岛'),
      '/Applications/冒险岛',
      '/Applications/MapleStory',
      join(homedir(), 'Games', 'Maplestory'),
    ];
    for (const cand of cands) {
      if (!cand) continue;
      try {
        if (!statSync(cand).isDirectory()) continue;
      } catch {
        continue;
      }
      const sub = ['mxd/Data', 'Data'].find((x) => {
        try {
          return statSync(join(cand, x)).isDirectory();
        } catch {
          return false;
        }
      });
      if (sub || cand === parent) {
        out.push({ dir: cand, wzSub: sub || null, isDataParent: cand === parent });
      }
    }
    return out;
  }

  const api = {
    async packs() {
      return worker.call('packs');
    },
    async tree(args) {
      return worker.call('tree', args ?? {});
    },
    async search(args) {
      return worker.call('search', args ?? {});
    },
    async get(args) {
      const info = await worker.call('get', args ?? {});
      if ((!info.animations || info.animations.length === 0) && args?.path) {
        const preview = await worker.call('sprite', { path: args.path, animation: '', scale: 1 });
        info.preview = preview;
      }
      return info;
    },
    async sprite(args) {
      const spec = args ?? {};
      const out = await worker.call('sprite', spec);
      const parts = String(spec.path ?? '').split('/');
      const stamp = createHash('sha1')
        .update(JSON.stringify({ ...spec, t: out.sheet }))
        .digest('hex')
        .slice(0, 10);
      const stem = `${(parts.at(-1) ?? 'img').replace('.img', '')}_${out.animation || 'frame'}_x${out.scale}_${stamp}`;
      const pngName = `${stem}.png`;
      const gifName = out.gif ? `${stem}.gif` : null;
      const fs = await import('node:fs/promises');
      await fs.copyFile(out.png, join(OUT_DIR, pngName));
      if (gifName) await fs.copyFile(out.gif, join(OUT_DIR, gifName));
      return {
        ...out,
        pngUrl: `${ROUTE_PREFIX}/file/${encodeURIComponent(pngName)}`,
        gifUrl: gifName ? `${ROUTE_PREFIX}/file/${encodeURIComponent(gifName)}` : null,
        png: undefined,
        gif: undefined,
      };
    },
    async imageUrl(args) {
      const q = new URLSearchParams();
      for (const [key, value] of Object.entries(args ?? {})) {
        if (value !== undefined && value !== null && value !== '') q.set(key, String(value));
      }
      return { url: `${ROUTE_PREFIX}/image?${q.toString()}` };
    },
    async stats() {
      return worker.call('stats');
    },
    async detail(args) {
      const spec = args ?? {};
      const key = `${spec.kind ?? ''}:${spec.id ?? ''}:${spec.path ?? ''}`;
      const cached = detailCache.get(key);
      if (cached && Date.now() - cached.at < 120_000) return cached.value;
      const value = await worker.call('detail', spec);
      detailCache.set(key, { at: Date.now(), value });
      if (detailCache.size > 200) detailCache.delete(detailCache.keys().next().value);
      return value;
    },
    async sources(args) {
      return worker.call('sources', args ?? {});
    },
    // ── Packs/*.ms（结构 / Spine 骨骼；像素仍在 _Canvas）─────────────
    async msPacks() {
      return worker.call('packs_ms', {});
    },
    async msMob(args) {
      return worker.call('ms_mob', args ?? {});
    },
    async msStats() {
      return worker.call('ms_stats', {});
    },
    // ── 聊天气泡（UI/ChatBalloon.img 九宫格）──────────────────────────
    async balloonStyles() {
      const key = 'balloonStyles';
      const hit = detailCache.get(key);
      if (hit && Date.now() - hit.at < 600_000) return hit.value;
      const value = await worker.call('balloon_styles', {});
      detailCache.set(key, { at: Date.now(), value });
      return value;
    },
    async balloon(args) {
      const out = await worker.call('balloon', args ?? {});
      const fs = await import('node:fs/promises');
      const buf = await fs.readFile(out.png);
      const stamp = createHash('sha1').update(buf).digest('hex').slice(0, 12);
      const name = `balloon_${stamp}.png`;
      await fs.writeFile(join(OUT_DIR, name), buf);
      return { ...out, png: undefined, url: `${ROUTE_PREFIX}/file/${encodeURIComponent(name)}` };
    },
    // ── 纸娃娃（按装备编号合成角色帧）────────────────────────────────
    async charParts(args) {
      const key = `cparts:${JSON.stringify(args ?? {}).slice(0, 80)}`;
      const hit = detailCache.get(key);
      if (hit && Date.now() - hit.at < 600_000) return hit.value;
      const value = await worker.call('char_parts', args ?? {});
      detailCache.set(key, { at: Date.now(), value });
      return value;
    },
    async charPoses() {
      return worker.call('char_poses', {});
    },
    /** 纸娃娃**逐帧**合成（横向雪碧图），桌宠直接吃这个格式。 */
    async charAnim(args) {
      const spec = args ?? {};
      const out = await worker.call('char_anim', spec);
      const fs = await import('node:fs/promises');
      const buf = await fs.readFile(out.png);
      const stamp = createHash('sha1').update(buf).digest('hex').slice(0, 12);
      const name = `chara_anim_${stamp}.png`;
      await fs.writeFile(join(OUT_DIR, name), buf);
      return { ...out, png: undefined, url: `${ROUTE_PREFIX}/file/${encodeURIComponent(name)}` };
    },
    /** 合成纸娃娃并落盘，返回可直接 <img> 的 URL。 */
    /** 椅子清单（纸娃娃「椅子」槽位用；椅子在 Item/Install，不是 Character 部件）。 */
    async chairList(args) {
      return worker.call('chair_list', args ?? {});
    },
    async charRender(args) {
      const spec = args ?? {};
      const out = await worker.call('char_render', spec);
      const stamp = createHash('sha1')
        .update(JSON.stringify({ e: spec.equips, p: spec.pose, f: spec.flip, fr: spec.frame, c: spec.chairId }))
        .digest('hex').slice(0, 10);
      const name = `chara_${stamp}.png`;
      const fs = await import('node:fs/promises');
      await fs.copyFile(out.png, join(OUT_DIR, name));
      return { ...out, png: undefined, url: `${ROUTE_PREFIX}/file/${encodeURIComponent(name)}` };
    },
    /** 当前头型可用的耳型清单（Head 里的 humanEar/ear/lefEar/highlefEar）。 */
    async charEars(args) {
      const spec = args ?? {};
      const key = `ears:${(spec.equips || []).join(',')}`;
      const hit = detailCache.get(key);
      if (hit && Date.now() - hit.at < 600_000) return hit.value;
      const value = await worker.call('char_ears', spec);
      detailCache.set(key, { at: Date.now(), value });
      return value;
    },
    // ── 地图（背景层与地图浏览用）──────────────────────────────────
    async mapList(args) {
      const spec = args ?? {};
      const key = `mlist:${spec.query ?? ''}:${spec.limit ?? ''}`;
      const hit = detailCache.get(key);
      if (hit && Date.now() - hit.at < 300_000) return hit.value;
      const value = await worker.call('map_list', spec);
      detailCache.set(key, { at: Date.now(), value });
      if (detailCache.size > 200) detailCache.delete(detailCache.keys().next().value);
      return value;
    },
    /** 经典户外地图（含中文名），背景选择器默认展示这一批。 */
    async mapFeatured() {
      const key = 'mfeatured';
      const hit = detailCache.get(key);
      if (hit && Date.now() - hit.at < 600_000) return hit.value;
      const value = await worker.call('map_featured', {});
      detailCache.set(key, { at: Date.now(), value });
      return value;
    },
    /** 随机一张能当背景的地图（排除 exclude 里的编号，避免连着抽到同一张）。 */
    async mapRandom(args) {
      return worker.call('map_random', args ?? {});
    },
    /** 下一张背景地图：优先城镇（「走到头就换一张，优先 town」）。 */
    async mapNext(args) {
      return worker.call('map_next', args ?? {});
    },
    async mapDetail(args) {
      const spec = args ?? {};
      const key = `map:${spec.id}:${JSON.stringify(spec.view ?? {})}`;
      const hit = detailCache.get(key);
      if (hit && Date.now() - hit.at < 300_000) return hit.value;
      const value = await worker.call('map_detail', spec);
      detailCache.set(key, { at: Date.now(), value });
      if (detailCache.size > 200) detailCache.delete(detailCache.keys().next().value);
      return value;
    },
    /**
     * 固定视口相机用的**整幅地图画布**：返回一张 PNG（按内容边界裁掉透明行）
     * 与世界矩形 / scale。前端按 MiniPet 的公式自己做变换：
     *   sx = (worldX - camX) * zoom + screenW/2
     */
    async mapCanvas(args) {
      const spec = args ?? {};
      const key = `mcanvas:${spec.id}:${spec.scale ?? 1}`;
      const hit = detailCache.get(key);
      if (hit && Date.now() - hit.at < 600_000) return hit.value;
      const out = await worker.call('map_canvas', spec);
      const fs = await import('node:fs/promises');
      const buf = await fs.readFile(out.png);
      const stamp = createHash('sha1').update(buf).digest('hex').slice(0, 12);
      const name = `mapcanvas_${spec.id ?? 'map'}_${stamp}.png`;
      await fs.writeFile(join(OUT_DIR, name), buf);
      const value = { ...out, png: undefined, url: `${ROUTE_PREFIX}/file/${encodeURIComponent(name)}` };
      detailCache.set(key, { at: Date.now(), value });
      return value;
    },
    /** 地图实际有像素的范围（世界坐标）。VR 边界比画面大，背景要用这个。 */
    async mapContentBounds(args) {
      const spec = args ?? {};
      const key = `cbox:${spec.id}`;
      const hit = detailCache.get(key);
      if (hit && Date.now() - hit.at < 600_000) return hit.value;
      const value = await worker.call('map_content_bounds', spec);
      detailCache.set(key, { at: Date.now(), value });
      return value;
    },
    /**
     * 背景图：按内容边界渲染一张 PNG 并落盘（带内存缓存，同一张只渲染一次）。
     * 渲染分辨率按 worldScale 自适应：地图世界坐标可能超过纹理上限，所以图里
     * 每世界单位占 worldScale 像素——相机换算时用同一个 worldScale。
     */
    async mapBackground(args) {
      const spec = args ?? {};
      if (spec.contentBounds) {
        // worldScale = 1 世界单位对应多少图像像素。
        // 没指定就按长边 4096 自适应；指定了（面板按视口宽算的）也要保证
        // 两头都在纹理上限内，否则一缩放就超出浏览器单轴限制。
        const b = spec.contentBounds;
        const MAXPX = 4096;
        let ws = Number(spec.worldScale) || Math.min(1, MAXPX / Math.max(1, Math.max(b.width, b.height)));
        ws = Math.min(ws, MAXPX / Math.max(1, b.width), MAXPX / Math.max(1, b.height));
        spec.worldScale = ws;
        spec.scale = ws;
      }
      if (spec.contentBounds) {
        spec.left = spec.contentBounds.left;
        spec.top = spec.contentBounds.top;
        spec.right = spec.contentBounds.right;
        spec.bottom = spec.contentBounds.bottom;
      }
      const out = await worker.call('map_viewport_b64', spec);
      const buf = Buffer.from(out.png_base64, 'base64');
      // 文件名必须用**内容哈希**：`/file` 路由带 `immutable` 缓存，
      // 同名文件内容一变，浏览器和用户都还会拿旧图（实测截图与上一张字节完全相同）。
      const stamp = createHash('sha1').update(buf).digest('hex').slice(0, 12);
      const name = `mapbg_${spec.id ?? 'map'}_${stamp}.png`;
      const fs = await import('node:fs/promises');
      await fs.writeFile(join(OUT_DIR, name), buf);
      return {
        mapId: spec.id, w: out.w, h: out.h, bytes: out.bytes, cached: out.cached,
        // padded 描述空档是怎么补的（edges=两端渐变延伸 / crop=裁切 / null=不用补），
        // 面板与验收脚本都要用它判断渲染走的是哪条分支
        padded: out.padded ?? null,
        scale: out.scale ?? spec.scale ?? 1,
        name: out.name ?? null,
        viewport: out.viewport ?? spec,
        worldScale: spec.worldScale ?? spec.scale ?? 1,
        contentBounds: spec.contentBounds ?? null,
        url: `${ROUTE_PREFIX}/file/${encodeURIComponent(name)}`,
      };
    },
    async mapMinimap(args) {
      const out = await worker.call('map_minimap', args ?? {});
      const stamp = createHash('sha1').update(String(args?.id)).digest('hex').slice(0, 8);
      const name = `minimap_${args?.id}_${stamp}.png`;
      const fs = await import('node:fs/promises');
      await fs.copyFile(out.png, join(OUT_DIR, name));
      return { ...out, png: undefined, url: `${ROUTE_PREFIX}/file/${encodeURIComponent(name)}` };
    },
    /** 相机 clamp —— 与 MiniPet MapService.ClampCamera 同一套公式，面板侧直接复用。 */
    async mapCamera(args) {
      const spec = args ?? {};
      const info = await api.mapDetail({ id: spec.id, view: spec.view });
      const b = info.bounds;
      const zoom = Number(spec.zoom ?? 1) || 1;
      const w = Number(spec.view?.w ?? 1920);
      const h = Number(spec.view?.h ?? 1080);
      const halfW = w / (2 * zoom);
      const halfH = h / (2 * zoom);
      const minX = b.left + halfW;
      const maxX = b.right - halfW;
      const minY = b.top + halfH;
      const maxY = b.bottom - halfH;
      const clamp = (v, lo, hi, mid) => (lo > hi ? mid : Math.min(Math.max(v, lo), hi));
      return {
        bounds: b,
        center: { x: (b.left + b.right) / 2, y: (b.top + b.bottom) / 2 },
        range: {
          x: [minX > maxX ? (b.left + b.right) / 2 : minX, minX > maxX ? (b.left + b.right) / 2 : maxX],
          y: [minY > maxY ? (b.top + b.bottom) / 2 : minY, minY > maxY ? (b.top + b.bottom) / 2 : maxY],
          narrowerThanViewport: minX > maxX || minY > maxY,
        },
        clamp: (x, y) => ({
          x: clamp(x, minX, maxX, (b.left + b.right) / 2),
          y: clamp(y, minY, maxY, (b.top + b.bottom) / 2),
        }),
      };
    },
    /**
     * 背景设置：读写 `~/.dsh/wz-assets/background.json`，供「选冒险岛地图当 DSH 背景」持久化。
     * 默认关闭——开背景是显式选择，不静默改变任何人的界面。
     */
    async backgroundGet() {
      const fs = await import('node:fs/promises');
      try {
        const raw = await fs.readFile(BG_CONFIG, 'utf8');
        return { ...JSON.parse(raw), configPath: BG_CONFIG };
      } catch {
        return { enabled: false, mapId: null, speed: 1, zoom: 1, configPath: BG_CONFIG };
      }
    },
    async backgroundSet(args) {
      const fs = await import('node:fs/promises');
      const current = JSON.parse(JSON.stringify(await api.backgroundGet()));
      const next = { ...current, ...(args ?? {}) };
      delete next.configPath;
      await fs.mkdir(OUT_DIR, { recursive: true });
      await fs.writeFile(BG_CONFIG, JSON.stringify(next, null, 2), 'utf8');
      return { ...next, configPath: BG_CONFIG };
    },
    async frameSheet(args) {
      const spec = args ?? {};
      const out = await worker.call('sprite', spec);
      const parts = String(spec.path ?? '').split('/');
      const stamp = createHash('sha1')
        .update(JSON.stringify({ ...spec, t: out.frames?.length, s: out.sheet }))
        .digest('hex')
        .slice(0, 10);
      const stem = `${(parts.at(-1) ?? 'img').replace('.img', '')}_${out.animation || 'frame'}_x${out.scale}_${stamp}`;
      const pngName = `${stem}.png`;
      const fs = await import('node:fs/promises');
      await fs.copyFile(out.png, join(OUT_DIR, pngName));
      return { ...out, png: undefined, gif: undefined, pngUrl: `${ROUTE_PREFIX}/file/${encodeURIComponent(pngName)}` };
    },

    // ── 资源更新（cmsdl）────────────────────────────────────────────
    /**
     * 进度查询：面板轮询这个。返回 cmsdl 工具状态 + 当前任务快照
     * （阶段 / 已下载字节 / 总字节 / 目标目录已落盘体积 / 日志尾部）。
     */
    async updateStatus() {
      if (!cmsdlState.tool || Date.now() - cmsdlState.at > 60_000) {
        await cmsdl.probe({ path: config.cmsdlPath ?? '' });
        cmsdlState.tool = true;
        cmsdlState.at = Date.now();
      }
      const st = await cmsdl.status();
      // cmsdl **没有自己的配置文件**（`--help` 里只有 check/download/patch/torrent/shortcut），
      // 客户端目录只能由我们存 —— 就存在 background.json 里，和背景配置同一份，
      // 状态卡里跟 cmsdl 的信息一起显示。
      try {
        const fs = await import('node:fs/promises');
        const bg = JSON.parse(await fs.readFile(BG_CONFIG, 'utf8'));
        st.clientDir = typeof bg?.clientDir === 'string' ? bg.clientDir : '';
      } catch {
        st.clientDir = '';
      }
      // 落盘位置按「地区」推导：CMS 是 <client>/mxd/Data，TMS 是 <client>/Data。
      // 地区从最近一次 plan 里取（status() 本身不带 region），没有就默认 cms。
      const region = String(cmsdlStatusRegion || 'cms');
      st.region = region;
      st.wzLanding = st.clientDir
        ? join(st.clientDir, region === 'tms' ? 'Data' : 'mxd/Data')
        : (st.defaultTargetDir || null);
      st.detected = detectClientDirs();
      return st;
    },
    /** 重新探测本机 cmsdl（用户在别处装了新的，不必重启 host）。 */
    async updateProbe(args) {
      const out = await cmsdl.probe({ path: args?.path ?? config.cmsdlPath ?? '' });
      cmsdlState.tool = true;
      cmsdlState.at = Date.now();
      return out;
    },
    /** 只拼命令不执行：面板「预览将执行的命令」用。 */
    async updatePlan(args) {
      const out = await cmsdl.plan(args ?? {});
      if (out?.target?.region || args?.region) {
        // 记住最近一次选的地区，状态卡用它推导落盘位置
        cmsdlStatusRegion = String(args?.region || out?.target?.region || 'cms');
      }
      return out;
    },
    /**
     * 启动 cmsdl 长任务（下载 / 校验 / 打补丁）。立刻返回，进度靠 updateStatus 轮询。
     * 安全判定不通过时 started=false + blocked 原因，**不抛异常**，面板直接显示。
     */
    async updateResources(args) {
      return cmsdl.run(args ?? {});
    },
    /** 降级路径：只下载 cmsdl 本体（不碰任何客户端资源）。 */
    async updateFetchTool(args) {
      return cmsdl.fetchTool(args ?? {});
    },
    /** 取消当前长任务。 */
    async updateCancel() {
      return cmsdl.cancel();
    },
  };

  // ── HTTP：静态雪碧图/GIF ───────────────────────────────────────────
  const MIME = {
    '.png': 'image/png',
    '.gif': 'image/gif',
    '.webp': 'image/webp',
    '.json': 'application/json; charset=utf-8',
  };

  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'prefix',
        path: `${ROUTE_PREFIX}/file`,
        async handler(req, res) {
          const url = new URL(req.url ?? '/', 'http://localhost');
          const name = decodeURIComponent(url.pathname.slice(`${ROUTE_PREFIX}/file/`.length));
          if (!name || name.includes('/') || name.includes('..')) return jsonBody(res, 400, { error: '非法文件名' });
          // 合并 dsh-desktop-pet 后这里有**两个产物目录**，且两边的客户端都用
          // `${ROUTE_PREFIX}/file/<name>` 取图（同一个路由，不能注册两次）。
          // 所以按顺序找：本插件产物目录 → 桌宠产物目录。文件名各自带前缀
          // （chara_/mapcanvas_/… vs pet_*），不会互相遮蔽。
          const full = [join(OUT_DIR, name), join(PET_OUT_DIR, name)]
            .find((cand) => existsSync(cand));
          if (!full) return jsonBody(res, 404, { error: '文件不存在', name });
          const bytes = await readFile(full);
          const etag = `W/"${bytes.length.toString(16)}-${createHash('sha1').update(bytes).digest('hex').slice(0, 12)}"`;
          // 带上 ETag 回源校验时，内容没变就 304 —— 否则 2MB 的怪物雪碧图
          // 每次轮询都要重传一遍（桌宠每秒都在换帧，但不换文件）
          if (req.headers['if-none-match'] === etag) {
            res.writeHead(304, { etag, 'cache-control': 'no-cache' });
            res.end();
            return;
          }
          res.writeHead(200, {
            'content-type': MIME[extname(name).toLowerCase()] ?? 'application/octet-stream',
            'content-length': bytes.length,
            // **不要 immutable**：这些文件名是按「部位 + 动作 + 版本」生成的，
            // 内容一变（改了合成逻辑、换了椅子帧数）文件名可能不变，而
            // `immutable` + `max-age=86400` 会让浏览器 24 小时不再回源 ——
            // 表现就是「明明改了、刷新了还是旧的/空白」（用户反馈过）。
            // 用 no-cache：仍然允许缓存，但每次都会带 ETag 回源校验，
            // 内容没变就 304（省流量），变了就拿新的。
            'cache-control': 'no-cache',
            etag,
          });
          res.end(bytes);
        },
      }),
    'maplestory-wz: file route',
  );

  // ── HTTP：面板 JSON API（type/cmd 分发，Client 侧用 fetch）─────────
  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: `${ROUTE_PREFIX}/api`,
        async handler(req, res) {
          let body = '';
          for await (const chunk of req) body += chunk;
          let payload = {};
          try {
            payload = body ? JSON.parse(body) : {};
          } catch {
            return jsonBody(res, 400, { ok: false, error: '请求体不是合法 JSON' });
          }
          const args = payload.args ?? {};
          const method = api[payload.cmd];
          if (typeof method !== 'function') {
            return jsonBody(res, 400, { ok: false, error: `未知命令 ${payload.cmd}`, available: Object.keys(api) });
          }
          try {
            jsonBody(res, 200, { ok: true, data: await method(args) });
          } catch (error) {
            jsonBody(res, 200, { ok: false, error: String(error?.message ?? error), stderr: error?.stderr });
          }
        },
      }),
    'maplestory-wz: api route',
  );

  // ── HTTP：单帧/图标直出 PNG ────────────────────────────────────────
  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: `${ROUTE_PREFIX}/image`,
        async handler(req, res) {
          const url = new URL(req.url ?? '/', 'http://localhost');
          const spec = {
            path: url.searchParams.get('path') ?? '',
            animation: url.searchParams.get('animation') ?? '',
            scale: Number(url.searchParams.get('scale') ?? 1) || 1,
          };
          if (!spec.path) return jsonBody(res, 400, { error: '缺少 path 参数' });
          try {
            const out = await worker.call('image', spec);
            const bytes = Buffer.from(out.png_base64, 'base64');
            res.writeHead(200, {
              'content-type': 'image/png',
              'content-length': bytes.length,
              'cache-control': 'public, max-age=3600',
            });
            res.end(bytes);
          } catch (error) {
            jsonBody(res, 404, { error: String(error?.message ?? error) });
          }
        },
      }),
    'maplestory-wz: image route',
  );

  // ── 工具 ──────────────────────────────────────────────────────────
  const register = (definition) => ctx.effect(() => ctx.tools.register(definition), `tool:${definition.name}`);

  register({
    name: 'wz_packs',
    description:
      '列出本地冒险岛 WZ 数据源的分类（Mob/Npc/Character/Item/Map/UI/...）与各自的分卷文件。' +
      '任何 WZ 读取都从这里开始：先看清数据源里到底有什么，再决定路径。',
    parameters: { type: 'object', properties: {}, additionalProperties: false },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute() {
      const info = await worker.call('packs');
      const lines = info.packs.map((p) => `${p.pack}: ${p.volumes.length} 项`);
      return textResult(`数据源 ${info.data_dir}（region=${info.region} v${info.version}，wzpy=${info.wzpy}）\n${lines.join('\n')}`, info);
    },
  });

  register({
    name: 'wz_tree',
    description:
      '浏览 WZ 节点树。给出分类与相对路径（如 pack="Mob" path="_Canvas"，或 pack="Character" path="Weapon/01332020.img"），' +
      '目录返回子目录与图片名，图片返回其属性子节点（动作/帧/info 字段）。',
    parameters: {
      type: 'object',
      properties: {
        pack: { type: 'string', description: '分类名：Mob / Npc / Character / Item / Map / UI / String / Sound / Effect / Skill / Etc / Base …' },
        path: { type: 'string', description: '分类内相对路径，缺省为根；如 "_Canvas"、"Weapon/01332020.img"、"stand"' },
        limit: { type: 'integer', description: '最多返回多少个子项（缺省 400）' },
      },
      required: ['pack'],
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      const out = await worker.call('tree', args);
      const names = out.children.slice(0, 60).map((c) => (c.kind === 'dir' ? `${c.name}/` : c.name));
      return textResult(
        `${out.pack}/${out.path} 是${out.kind === 'dir' ? '目录' : '图片'}，共 ${out.total} 个子项（列出 ${Math.min(60, names.length)} 个）：${names.join('  ')}`,
        out,
      );
    },
  });

  register({
    name: 'wz_search',
    description:
      '按名字或编号在 WZ 字符串表里搜索（怪物/ NPC / 装备 / 地图 / 技能 / 消耗品等）。' +
      '名字是中文时直接传中文；编号可传 "1210102" 或 "01332020"。返回 id、类别与命中的字段。',
    parameters: {
      type: 'object',
      properties: {
        query: { type: 'string', description: '中文名、英文名或编号' },
        kind: { type: 'string', description: '限定类别：mob / npc / eqp / map / skill / consume / etc / cash / ins，缺省全部' },
        limit: { type: 'integer', description: '最多返回条数，缺省 30' },
      },
      required: ['query'],
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      const out = await worker.call('search', args);
      const rows = out.hits.map((h) => `${h.kind} ${h.id} ${h.name}${h.matched === 'id' ? '（编号精确命中）' : ''}`);
      return textResult(`"${out.query}" 命中 ${out.count} 条（${out.elapsed}s）：\n${rows.join('\n') || '无'}`, out);
    },
  });

  register({
    name: 'wz_sprite',
    description:
      '取一个 WZ 图片的动作帧，按 origin 锚点对齐拼成雪碧图 PNG，并可选生成循环 GIF。' +
      '动画名可用 "stand"/"move"/"attack1"/"swingO1" 等，也可用 "#2" 指定单帧（如 "stand#1"）；留空自动取第一个可用动作。' +
      '产出的 PNG/GIF 落在 ~/.dsh/wz-assets/，可直接用 read_image 查看。',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string', description: '形如 "Mob/_Canvas/1210102.img"、"Character/Weapon/01332020.img"、"Npc/0011000.img"' },
        animation: { type: 'string', description: '动作名，可带 #帧号；缺省自动选第一个动作' },
        scale: { type: 'integer', description: '整数放大倍率 1-8（像素风用最近邻），缺省 1' },
        gif: { type: 'boolean', description: '是否同时生成循环 GIF，缺省 true' },
        save_dir: { type: 'string', description: '额外把 PNG/GIF 复制到该目录（如交付目录）' },
      },
      required: ['path'],
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      const spec = { gif: true, ...args };
      const out = await worker.call('sprite', spec);
      const saved = [out.saved_png, out.saved_gif].filter(Boolean).map((p) => `，已复制到 ${p}`).join('');
      return textResult(
        `${out.path} 的「${out.animation}」共 ${out.frames.length} 帧（${out.scale}×）→ 雪碧图 ${out.png}` +
          `${out.gif ? `，GIF ${out.gif}` : ''}${saved}`,
        out,
      );
    },
  });

  register({
    name: 'wz_item',
    description:
      '查一个冒险岛编号的名字、中文名与属性：装备读 String/Eqp.img + Character/<类目>/<8位补零>.img 的 info（reqLevel/incPAD/incMAD/tuc…），' +
      '怪物/NPC 读名称表。返回属性表与 icon 图片 URL。',
    parameters: {
      type: 'object',
      properties: {
        id: { type: 'string', description: '编号，如 "1332020"、"1372008"、"1210102"' },
        kind: { type: 'string', description: '类别：eqp / mob / npc，缺省自动判断' },
      },
      required: ['id'],
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      const id = String(args.id).trim();
      const kind = args.kind || (/^\d{7}$/.test(id) || /^\d{6,7}$/.test(id) ? 'mob' : 'eqp');
      const found = await worker.call('search', { query: id, kind, limit: 5 });
      const exact = found.hits.find((h) => h.matched === 'id') ?? found.hits[0];
      if (!exact) return textResult(`WZ 里没有编号 ${id}（类别 ${kind}）`, { id, kind, found: null });
      let body = null;
      if (exact.kind === 'eqp' && exact.group) {
        const padded = id.padStart(8, '0');
        try {
          body = await worker.call('get', { path: `Character/${exact.group}/${padded}.img` });
        } catch {
          body = null;
        }
      } else if (exact.kind === 'mob' || exact.kind === 'npc') {
        const pack = exact.kind === 'mob' ? 'Mob' : 'Npc';
        const candidates = [id, id.padStart(7, '0')];
        for (const cand of candidates) {
          for (const p of [`${pack}/${cand}.img`, `${pack}/_Canvas/${cand}.img`]) {
            try {
              body = await worker.call('get', { path: p });
              break;
            } catch {
              /* 换个候选 */
            }
          }
          if (body) break;
        }
      }
      // 用真实命中的路径，别凭编号拼：傲天客户端怪物图只在 Mob/_Canvas/，
      // 拼成 Mob/1210102.img 会 404（body.path 是 worker 探测出来的可用路径）。
      const iconPath = body?.path
        ?? (exact.kind === 'eqp' ? `Character/${exact.group}/${id.padStart(8, '0')}.img` : null);
      const iconAnim = exact.kind === 'eqp' ? 'info/icon' : 'stand';
      const icon = `${ROUTE_PREFIX}/image?path=${encodeURIComponent(iconPath)}&animation=${encodeURIComponent(iconAnim)}&scale=4`;
      let ms = null;
      if (exact.kind === 'mob') {
        try {
          const d = await worker.call('ms_mob', { id });
          if (d?.found) {
            ms = {
              file: d.file,
              animations: Object.fromEntries(Object.entries(d.animations).map(([k, v]) => [k, v.length])),
              frames: d.frames_total,
              spine: d.spine,
            };
          }
        } catch {
          ms = null;
        }
      }
      const summary = [
        `${exact.kind} ${exact.id}：${exact.name || '（无名字）'}${exact.group ? `（${exact.group}）` : ''}`,
        Object.entries(exact.info || {}).map(([k, v]) => `${k}=${v}`).join('  '),
        body?.animations?.length ? `动作：${body.animations.map((a) => `${a.name}(${a.frames})`).join(' ')}` : '',
        ms ? `.ms 结构：${Object.entries(ms.animations).map(([k, n]) => `${k}(${n})`).join(' ')}` +
          (ms.spine ? `；Spine ${ms.spine.bones} 骨骼` : '') : '',
        iconPath ? `icon：${icon}` : '',
      ].filter(Boolean).join('\n');
      return textResult(summary, { id, kind: exact.kind, icon, entry: exact, body, ms });
    },
  });

  register({
    name: 'wz_map',
    description:
      '冒险岛地图：列名录（中文名/街区名过滤）或查一张图的详情。' +
      '详情返回 VR 边界（left/top/right/bottom）、层与物件统计、出生点、传送门数量，以及按 MiniPet 相机规则算出的默认镜头位置。' +
      '拿 bounds 就能当背景层：视口宽高决定镜头可移动范围（地图比视口窄时居中）。',
    parameters: {
      type: 'object',
      properties: {
        id: { type: 'string', description: '地图编号，如 "100000000"（射手村）。给了 id 就查详情，否则列名录' },
        query: { type: 'string', description: '按中文名/街区名过滤，如 "射手"、"魔法密林"' },
        limit: { type: 'integer', description: '名录返回条数，缺省 60' },
        view_w: { type: 'integer', description: '视口宽（算相机用），缺省 1920' },
        view_h: { type: 'integer', description: '视口高（算相机用），缺省 1080' },
      },
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      if (args.id) {
        const d = await worker.call('map_detail', {
          id: String(args.id),
          view: { w: args.view_w ?? 1920, h: args.view_h ?? 1080, zoom: 1 },
        });
        const b = d.bounds;
        return textResult(
          `地图 ${d.id}「${d.name || '—'}」（${d.street || '—'}）${b.width}×${b.height}` +
            `，VR 边界 left=${b.left} top=${b.top} right=${b.right} bottom=${b.bottom}` +
            `，默认镜头 (${Math.round(d.camera.x)}, ${Math.round(d.camera.y)})` +
            `，镜头可动范围 x∈[${Math.round(d.camera.x - b.width / 2 + d.camera.halfW)}, …]` +
            `，出生点 ${(d.life || []).length}，传送门 ${(d.portals || []).length}`,
          d,
        );
      }
      const out = await worker.call('map_list', { query: args.query ?? '', limit: args.limit ?? 60 });
      const rows = out.maps.map((m) => `${m.id}  ${m.name}${m.street ? `  （${m.street}）` : ''}`);
      return textResult(`地图名录「${out.query || '全部'}」共 ${out.total} 条：\n${rows.join('\n')}`, out);
    },
  });

  register({
    name: 'wz_map_sprite',
    description:
      '渲染一张冒险岛地图为 PNG：整图或指定视口窗口。产出落在 ~/.dsh/wz-assets/，可用 read_image 看。' +
      '视口渲染快得多（整图 5830×1764 约 1.6s，单窗口约 20ms），做背景层就用视口。',
    parameters: {
      type: 'object',
      properties: {
        id: { type: 'string', description: '地图编号，如 "100000000"' },
        scale: { type: 'number', description: '缩放 0.1–4，缺省 1' },
        left: { type: 'integer', description: '视口左（世界坐标）。四个都给了就只渲这个窗口' },
        top: { type: 'integer', description: '视口上' },
        right: { type: 'integer', description: '视口右' },
        bottom: { type: 'integer', description: '视口下' },
        minimap: { type: 'boolean', description: 'true 则出小地图（地图自带 minimap 时）' },
      },
      required: ['id'],
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      if (args.minimap) {
        const out = await worker.call('map_minimap', { id: String(args.id) });
        return textResult(`地图 ${args.id} 小地图 → ${out.png}（${(out.bytes / 1024).toFixed(0)} KB）`, out);
      }
      const out = await worker.call('map_image', { ...args, id: String(args.id) });
      const scope = out.viewport ? `视口 ${out.w}×${out.h}` : `整图 ${out.w}×${out.h}`;
      return textResult(`地图 ${args.id} ${scope} → ${out.png}`, out);
    },
  });

  register({
    name: 'wz_char',
    description:
      '冒险岛**纸娃娃**：给一串装备编号合成角色帧（Body/Head/Hair/Face/Coat/Pants/Shoes/Weapon/Cap…，8 位补零）。' +
      '不给编号就用默认裸装角色。先用 action:"parts" 拿合法编号清单（Hair 2153 项 / Face 1431 / Weapon 7373…），' +
      '再用 action:"render" 出图；产物落在 ~/.dsh/wz-assets/，可用 read_image 看。' +
      '合成语义与 mapleStoryMiniPet 的 CharaSim/CharacterRenderer 一致（按 zmap 排层）。',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', description: 'parts=列部位编号（默认）/ render=合成单帧 / anim=逐帧雪碧图 / poses=可用姿势' },
        categories: { type: 'array', items: { type: 'string' }, description: 'parts：要列哪些类目，缺省给常用 12 类' },
        limit: { type: 'integer', description: 'parts：每类最多返回多少条，缺省 40' },
        equips: { type: 'array', items: { type: 'string' }, description: 'render：装备编号列表，如 ["00002000","00012000","00030000","00020000"]' },
        pose: { type: 'string', description: 'render：姿势，缺省 stand1' },
        flip: { type: 'boolean', description: 'render：是否水平翻转（朝右）' },
        frame: { type: 'integer', description: 'render：帧号，缺省 0' },
      },
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      const action = String(args.action || 'parts');
      if (action === 'poses') {
        const d = await worker.call('char_poses', {});
        return textResult(`可用姿势：${d.poses.join(', ')}`, d);
      }
      if (action === 'anim') {
        const out = await worker.call('char_anim', {
          equips: args.equips ?? [], pose: args.pose ?? 'stand1',
          flip: args.flip ?? false, frames: undefined,
        });
        return textResult(
          `纸娃娃逐帧 ${out.sheet.w}×${out.sheet.h}（${out.frames.length} 帧，单元 ${out.frame.w}×${out.frame.h}，${out.pose}）→ ${out.png}`,
          out,
        );
      }
      if (action === 'render') {
        const out = await worker.call('char_render', {
          equips: args.equips ?? [], pose: args.pose ?? 'stand1',
          flip: args.flip ?? false, frame: args.frame ?? 0,
        });
        return textResult(
          `纸娃娃 ${out.w}×${out.h}（${out.equips.length} 个部位，${out.pose}${out.flip ? '，已翻转' : ''}）→ ${out.png}`,
          out,
        );
      }
      const d = await worker.call('char_parts', { categories: args.categories, limit: args.limit ?? 40 });
      const lines = Object.entries(d.categories).map(([cat, v]) =>
        `${cat}（共 ${v.total}）：${v.items.map((i) => i.id).slice(0, 8).join(' ')}…`);
      return textResult(
        `纸娃娃部位清单：\n${lines.join('\n')}\n默认裸装：${Object.entries(d.defaults).map(([k, v]) => `${k}=${v}`).join(' ')}`,
        d,
      );
    },
  });

  register({
    name: 'wz_balloon',
    description:
      '把一句话渲染成**冒险岛聊天气泡**（UI/ChatBalloon.img 九宫格拼接）：给文本出 PNG，' +
      '产物落在 ~/.dsh/wz-assets/，可用 read_image 看。' +
      '不指定 style 就用第一个可用样式；想知道有哪些样式用 action:"styles"（实测 489 个样式里 466 个可拼）。',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', description: 'make=出气泡（默认）/ styles=列可用样式' },
        text: { type: 'string', description: '气泡里的文字，支持 \\n 换行' },
        style: { type: 'string', description: '样式 id（ChatBalloon.img 下的节点名），缺省自动挑' },
        fontSize: { type: 'integer', description: '字号，缺省 13' },
        maxWidth: { type: 'integer', description: '预留（当前按文本自然宽度）' },
      },
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      if (String(args.action || 'make') === 'styles') {
        const d = await worker.call('balloon_styles', {});
        return textResult(
          `聊天气泡样式：共 ${d.total} 个，可用 ${d.usable} 个。可用样例：${d.styles.slice(0, 12).map((s) => s.id).join(', ')}`,
          d,
        );
      }
      const out = await worker.call('balloon', {
        text: args.text ?? '你好，冒险岛！', style: args.style, fontSize: args.fontSize ?? 13,
      });
      return textResult(
        `气泡 ${out.w}×${out.h}（样式 ${out.style}，${out.colCount}×${out.rowCount} 格平铺，${out.lines?.length ?? 1} 行，文字色 rgb(${(out.textColor || []).slice(0,3).join(',')})）→ ${out.png}`,
        out,
      );
    },
  });

  register({
    name: 'wz_ms',
    description:
      '读 Data/Packs/*.ms（结构容器，非 .wz）：列文件、查某怪物在 .ms 里的「动作 → 帧」结构、' +
      '是否为 Spine 骨骼、以及已打开容器的规模。' +
      '分工要分清：.ms 给**结构/骨骼**，像素仍然经 _outlink 取自 <分类>/_Canvas；' +
      '.ms 里的 canvas 是 1×1 占位且像素编解码未识别。首次打开 394MB 的 Mob_00000.ms 约 2~3 秒（之后缓存）。',
    parameters: {
      type: 'object',
      properties: {
        file: { type: 'string', description: '.ms 文件名，缺省 Mob_00000.ms' },
        id: { type: 'string', description: '怪物编号，如 "1210102"；给了就查它在 .ms 里的结构，否则只列文件' },
      },
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      if (args.id) {
        const d = await worker.call('ms_mob', { file: args.file ?? 'Mob_00000.ms', id: String(args.id) });
        if (!d.found) {
          return textResult(
            `${args.id} 在 ${d.file} 里没有结构（该容器共 ${d.total_mobs} 个 mob）` +
              (d.near?.length ? `；相近编号：${d.near.join(', ')}` : ''),
            d,
          );
        }
        const anims = Object.entries(d.animations).map(([k, v]) => `${k}(${v.length})`).join(' ');
        return textResult(
          `${d.file} 里的 ${d.key}：${Object.keys(d.animations).length} 个动作 / ${d.frames_total} 帧 —— ${anims}` +
            (d.spine ? `；Spine ${d.spine.bones} 骨骼，动画 ${d.spine.animations.join(', ')}` : '；非 Spine'),
          d,
        );
      }
      const out = await worker.call('packs_ms', {});
      const rows = out.packs.map((p) => `${p.file}  ${p.mb}MB${p.opened ? '（已打开）' : ''}`);
      return textResult(`${out.dir} 下的 .ms 容器：\n${rows.join('\n')}\n${out.note}`, out);
    },
  });

  register({
    name: 'wz_sources',
    description:
      '看一个怪物/NPC/装备编号在本地 WZ 里的**全部候选来源**，并区分「信息主源」与「PNG 仓库」：' +
      '{type}/{id}.img 是本体（动作结构 + origin/delay/z/map），_Canvas/{id}.img 多数条目只有像素、没有 info。' +
      '要判断「这个编号到底有没有属性数据」先跑这个，别直接假设。' +
      '怪物还会额外附上它在 Packs/*.ms 里的结构（动作→帧、是否 Spine）。',
    parameters: {
      type: 'object',
      properties: {
        id: { type: 'string', description: '编号，如 "1210102"、"1033113"、"1332020"' },
        kind: { type: 'string', description: '类别：mob / npc / eqp，缺省按编号推断' },
      },
      required: ['id'],
      additionalProperties: false,
    },
    output: { schema: OUTPUT_SCHEMA, render: renderResult },
    async execute(args) {
      const id = String(args.id).trim();
      const kind = args.kind || (id.length >= 8 ? 'eqp' : 'mob');
      const out = await worker.call('sources', { kind, id });
      const lines = out.sources.map((s) =>
        `[${s.exists ? (s.has_info ? '有 info' : '仅图像') : '不存在'}] ${s.label}  ${s.path}\n      ${s.note}`);
      let ms = null;
      if (kind === 'mob') {
        try {
          ms = await worker.call('ms_mob', { id });
        } catch {
          ms = null;
        }
        if (ms?.found) {
          const anims = Object.entries(ms.animations).map(([k, v]) => `${k}(${v.length})`).join(' ');
          lines.push(`[结构] Packs/${ms.file}  ${Object.keys(ms.animations).length} 动作 / ${ms.frames_total} 帧  ${anims}` +
            (ms.spine ? `  Spine ${ms.spine.bones} 骨骼` : ''));
        }
      }
      return textResult(`${kind} ${id} 的来源探测：\n${lines.join('\n')}`, { ...out, ms });
    },
  });

  // ── Client ⇄ Host：面板数据通道 ───────────────────────────────────
  if (typeof ctx.provide === 'function') {
    ctx.effect(() => ctx.provide('maplestoryWz', api), 'maplestory-wz: api');
  } else {
    ctx.set?.('maplestoryWz', api);
  }

  // 预热 worker（失败只记录，不影响其它功能）
  worker.start().catch((error) => logger.warn?.(`WZ worker 预热失败：${error?.message ?? error}`));

  // ══════════════════════════════════════════════════════════════════════
  //  桌面宠物（原 @local/dsh-desktop-pet，已合并进来）
  //
  //  为什么合并：它本来就依赖本插件的产物 ——
  //    · 纸娃娃配置读的就是本插件的 ~/.dsh/wz-assets/background.json
  //    · 共享 py/doll_shared.py 与 py/effect_support.py（渲染语义必须一致）
  //  拆成两个包只会让「改一处要同步两处」的风险一直存在。
  //
  //  worker 复用上面的 WzWorker 类（两个 worker 的启动/通信逻辑逐行相同，
  //  只差脚本路径与环境变量前缀），不另复制一份实现。
  // ══════════════════════════════════════════════════════════════════════
  const petConfig = {
    dataDir: config.dataDir,
    region: config.region,
    version: config.version,
    python: config.python,
    wzpyPath: config.wzpyPath,
    mobId: String(rawConfig.mobId ?? '1210102'),
    // 'mob' = 怪物桌宠；'chara' = 纸娃娃桌宠（可换部位）
    mode: rawConfig.mode === 'chara' ? 'chara' : 'mob',
    originFallback: rawConfig.originFallback === 'topleft' ? 'topleft' : 'center',
  };
  const petWorker = new WzWorker(petConfig, logger, {
    script: PET_WORKER,
    envPrefix: 'PET',
    id: 'desktop-pet',
  });
  ctx.effect(() => () => petWorker.dispose(), 'desktop-pet: worker');

  // 客户端表现默认值（CSS px / ms，可被 /api 的单次请求覆盖）
  const petDefaults = {
    scale: numOr(rawConfig.scale, 1),          // 1:1 原始像素，不放大
    speed: numOr(rawConfig.speed, 100),        // WZ px/s（C# MapWalker.HSpeedMax=125 同口径）
    // 下面两个此前**漏定义**（defaults 里返回 config.flySpeed/flyChance 恒为 undefined），
    // 合并时补上：客户端用它决定飞行的速度与触发概率。
    flySpeed: numOr(rawConfig.flySpeed, 60),
    flyChance: numOr(rawConfig.flyChance, 0.35),
    groundMargin: numOr(rawConfig.groundMargin, 6),
    delayScale: numOr(rawConfig.delayScale, 1),
    idleMinMs: numOr(rawConfig.idleMinMs, 900),
    idleMaxMs: numOr(rawConfig.idleMaxMs, 2800),
    minLeg: numOr(rawConfig.minLeg, 140),      // 一次「走一段」的最短距离（CSS px）
    maxLeg: numOr(rawConfig.maxLeg, 420),
    edgeInset: numOr(rawConfig.edgeInset, 8),
  };
  // 两套动作**不能混用**（用户口径：「纸娃娃和 mob 的动作不一样 不要混用」）：
  // 怪物没有 `sit`（椅子只作用于纸娃娃的坐姿），纸娃娃也没有怪物的全部动作。
  // 所以基础动作表两种模式共用，而 `sit` 只在纸娃娃模式下追加。
  const petActions = Array.isArray(rawConfig.actions) && rawConfig.actions.length
    ? rawConfig.actions.map(String)
    : ['stand', 'move', 'hit1', 'fly', 'die'];
  /** 纸娃娃模式额外要 `sit`（坐椅子用；只有 1 帧，坐下来就静止）。 */
  const CHARA_EXTRA_ACTIONS = ['sit'];
    
  const petEquipsDefault = Array.isArray(rawConfig.equips) && rawConfig.equips.length
    ? rawConfig.equips.map(String)
    : ['00002000', '00012000', '00030000', '00020000'];

  // 当前宠物：patch 里的 mobId 只是初始值，之后由面板/接口改，并落到 pet-config.json
  let currentMobId = petConfig.mobId;
  let currentMode = petConfig.mode;
  let currentEquips = [...petEquipsDefault];
  let petConfigLoaded = false;

  // 纸娃娃配置**只有一处**：本插件的 ~/.dsh/wz-assets/background.json 里的 doll 字段。
  // 桌宠每次都从这里读，所以「在纸娃娃面板改完 → 桌宠跟着变」，不用两处同步。
  let dollCache = { at: 0, doll: null };
  function loadDoll() {
    // 800ms 缓存：桌宠刷新很频繁，不必每次读盘
    if (Date.now() - dollCache.at < 800) return dollCache.doll;
    try {
      const raw = JSON.parse(readFileSync(BG_CONFIG, 'utf8'));
      dollCache = { at: Date.now(), doll: raw?.doll ?? null };
    } catch {
      dollCache = { at: Date.now(), doll: null };
    }
    return dollCache.doll;
  }
  /**
   * 把纸娃娃（部位 + 椅子）写回 `background.json` 的 `doll`。
   * 纸娃娃面板与桌宠**共用这一份**，所以这里写一次两边都变。
   */
  function saveDoll(next) {
    try {
      let raw = {};
      try { raw = JSON.parse(readFileSync(BG_CONFIG, 'utf8')) || {}; } catch { raw = {}; }
      raw.doll = { ...(raw.doll || {}), ...next };
      writeFileSync(BG_CONFIG, JSON.stringify(raw, null, 2), 'utf8');
      dollCache = { at: 0, doll: raw.doll };   // 让下次 loadDoll 立刻读到新值
      return raw.doll;
    } catch (e) {
      logger.warning?.(`写 doll 失败：${e.message}`);
      return null;
    }
  }

  /** 纸娃娃面板里真正选了编号的部位（去掉空值）。 */
  function dollEquips() {
    const d = loadDoll();
    if (!d?.equips) return null;
    // **必须排除 Chair**：椅子不是 Character 部件（在 Item/Install/…），
    // 它有独立字段 `doll.chair`。两者混在一个数组里会让 worker 拿椅子编号去
    // Character 里找 → 整只渲染退化（实测格子从 241×192 掉到 68×71，椅子没了）。
    const list = Object.entries(d.equips)
      .filter(([k, v]) => k !== 'Chair' && typeof v === 'string' && v)
      .map(([, v]) => v);
    return list.length ? list : null;
  }

  const loadPetConfig = async () => {
    try {
      const raw = await readFile(PET_CONFIG, 'utf8');
      const parsed = JSON.parse(raw);
      if (parsed && typeof parsed.mobId === 'string' && parsed.mobId) currentMobId = parsed.mobId;
      if (parsed && (parsed.mode === 'chara' || parsed.mode === 'mob')) currentMode = parsed.mode;
      if (parsed && Array.isArray(parsed.equips) && parsed.equips.length) currentEquips = parsed.equips.map(String);
      return parsed ?? {};
    } catch {
      return {};
    }
  };
  const savePetConfig = async (patch) => {
    const next = { mobId: currentMobId, ...patch };
    if (typeof next.mobId === 'string' && next.mobId) currentMobId = next.mobId;
    await mkdir(PET_OUT_DIR, { recursive: true });
    await writeFile(PET_CONFIG, JSON.stringify(next, null, 2), 'utf8');
    return { ...next, configPath: PET_CONFIG };
  };

  const petApi = {
    /** 桌宠主数据：怪物名 + 动作表 + 客户端要的每个动作的雪碧图元数据（URL 已转好）。 */
    async pet(args = {}) {
      if (!args.id && !petConfigLoaded) { await loadPetConfig(); petConfigLoaded = true; }
      const id = String(args.id ?? currentMobId);
      // 纸娃娃模式：逐帧合成角色图，产物结构与怪物一致，客户端不用改渲染
      const mode = args.mode ?? currentMode;
      // 动作按模式给：`sit` 只属于纸娃娃（否则怪物模式会请求一个不存在的动作）
      const want = Array.isArray(args.actions) && args.actions.length
        ? args.actions.map(String)
        : (mode === 'chara' ? [...petActions, ...CHARA_EXTRA_ACTIONS] : petActions);
      const originFallback = args.originFallback === 'topleft' ? 'topleft'
        : args.originFallback === 'center' ? 'center' : petConfig.originFallback;
      // 优先级：显式传参 > 纸娃娃面板的配置 > 自身旧配置。
      // 用户口径：「那边怎么改桌宠的配置就和修改的一致」——所以默认吃纸娃娃的配置。
      const fromDoll = dollEquips();
      const equips = Array.isArray(args.equips) && args.equips.length
        ? args.equips.map(String)
        : (fromDoll || currentEquips);
      const earType = args.earType ?? loadDoll()?.earType ?? 'humanEar';
      // 椅子从**同一份**纸娃娃配置读（background.json 的 doll.chair），不设第二处。
      // 挂了椅子 → worker 自动改坐姿 + 叠椅子层；坐姿只有 1 帧，桌宠天然静止。
      const chairId = String(args.chairId ?? loadDoll()?.chair ?? '').trim();
      const data = mode === 'chara'
        ? await petWorker.call('chara', { equips, actions: want, flip: !!args.flip, earType,
                                         chairId })
        : await petWorker.call('pet', { id, actions: want, originFallback });
      const anims = {};
      for (const [key, value] of Object.entries(data.anims ?? {})) {
        const { file, ...rest } = value;
        // 客户端 `advanceFrame` 读的是 `sheet.frames.length`，而 worker 给的是
        // `count` + 表格元数据 → 形状不一致时桌宠会崩
        // （`Cannot read properties of undefined (reading 'length')`，实测纸娃娃模式必崩）。
        const count = Number(rest.count ?? 0);
        const cell = rest.cell ?? { w: 0, h: 0 };
        const anchor = rest.anchor ?? { x: 0, y: 0 };
        const delays = Array.isArray(rest.delays) ? rest.delays : [];
        const frames = rest.frames ?? Array.from({ length: count }, (_, i) => ({
          index: i,
          x: (rest.pad ?? 2) + i * (cell.w + (rest.pad ?? 2)),
          y: rest.pad ?? 2,
          w: cell.w, h: cell.h,
          origin: [anchor.x, anchor.y],
          delay: delays[i] ?? rest.defaultDelay ?? 120,
        }));
        const entry = {
          ...rest,
          frames,
          // 旧客户端读 `sheet`（外层画布尺寸）与 `frame`（单元格）——一并给了，兼容
          sheet: rest.sheet ?? { w: rest.cols ? count * cell.w : cell.w, h: cell.h },
          frame: rest.frame ?? cell,
          count,
          url: `${ROUTE_PREFIX}/file/${encodeURIComponent(file)}`,
        };
        // **别名**：客户端 state 里存的可能是 WZ 真姿势名（walk1/stand1/…），
        // 而图集的 key 是语义名（move/stand/…）。只挂语义名的话
        // `anims['walk1']` 就是 undefined → 帧推进/尺寸/延迟全错，画面就是乱的。
        anims[key] = entry;
        if (rest.action && rest.action !== key) anims[rest.action] = entry;
      }
      return {
        ...data,
        mode,
        equips,
        earType,
        equipsFrom: Array.isArray(args.equips) && args.equips.length ? 'args' : (fromDoll ? 'doll-config' : 'config'),
        anims,
        urlPrefix: ROUTE_PREFIX,
        defaults: petDefaults,
      };
    },
    async info(args = {}) {
      return petWorker.call('info', { id: String(args.id ?? currentMobId) });
    },
    /** 换宠候选：按中文名/编号搜怪物。 */
    async search(args = {}) {
      const out = await petWorker.call('search', {
        query: String(args.query ?? ''),
        limit: numOr(args.limit, 40),
      });
      return { ...out, current: currentMobId };
    },
    /** 一批经典候选（换宠面板的默认列表）。 */
    async presets() {
      const out = await petWorker.call('presets', {});
      return { ...out, current: currentMobId };
    },
    /** 当前宠物与持久化路径。 */
    async petGet() {
      await loadPetConfig();
      return { mobId: currentMobId, mode: currentMode, equips: currentEquips, configPath: PET_CONFIG };
    },
    /** 切到纸娃娃模式并（可选）指定部位。 */
    async petSetChara(args = {}) {
      // 取值顺序必须和 `pet()` **一致**：显式传参 → 纸娃娃面板配置 → 自身旧配置。
      // 之前这里写的是 `args.equips || currentEquips`，**漏掉了纸娃娃配置** →
      // 面板里改了脸/发型，桌宠却一直吃 `pet-config.json` 里的旧默认值
      // （用户反馈「脸渲染的是不对的，参考纸娃娃那边修改」）。
      const fromDoll = dollEquips();
      const equips = (Array.isArray(args.equips) && args.equips.length
        ? args.equips
        : (fromDoll || currentEquips)).map(String);
      // 椅子：存进 background.json 的 doll.chair（纸娃娃面板与桌宠**共用同一份配置**，
      // 不设第二处），并立刻作用于本次渲染。
      const chair = String(args.chairId ?? loadDoll()?.chair ?? '').trim();
      const doll = loadDoll() || {};
      // equips 在 doll 里是**按部位名索引的对象**（面板从它恢复每个槽位），
      // 这里只更新椅子 —— 之前顺手把 equips 也写成数组，面板读到后所有槽位都空了（实测回归）。
      saveDoll({ ...doll, chair });
      const data = await petApi.pet({ mode: 'chara', equips, chairId: chair });
      const usable = Object.keys(data.anims ?? {});
      if (!usable.length) throw new Error('这组部位没有可用动作，检查编号是否存在');
      currentMode = 'chara';
      currentEquips = equips;
      await savePetConfig({ mode: 'chara', equips, mobId: currentMobId, name: '纸娃娃' });
      return { mode: 'chara', equips, chair, pet: data };
    },
    /** 切回怪物模式。 */
    async petSetMob(args = {}) {
      const id = String(args.id ?? currentMobId);
      const data = await petApi.pet({ mode: 'mob', id });
      if (!Object.keys(data.anims ?? {}).length) throw new Error(`${id} 没有可用动作`);
      currentMode = 'mob';
      currentMobId = id;
      await savePetConfig({ mode: 'mob', mobId: id });
      return { mode: 'mob', mobId: id, pet: data };
    },
    /** 换宠：写持久化并返回新宠物的渲染数据（面板据此立刻换图）。 */
    async petSet(args = {}) {
      const id = String(args.id ?? '').replace(/^0+(?=\d)/, '') || null;
      if (!id) throw new Error('petSet 需要一个 id');
      // **显式切到怪物模式**：客户端「换宠」列表选的就是怪物编号。
      // 不切的话，在纸娃娃模式下选怪物只会改 mobId 而画面不变
      // （verify.sh 实测：「切到 1210102，取回 chara:…」）。
      currentMode = 'mob';
      // 先验证目标真的能渲染，且**有能走路的动作**：只带 attack 的 boss 编号
      // （实测 8880853 残酷冬季的卡琳）会渲染出一张空表，桌宠就变成隐形。
      const data = await petApi.pet({ id, mode: 'mob' });
      const usable = Object.keys(data.anims ?? {});
      if (usable.length === 0) {
        throw new Error(
          `${data.name || id}（${id}）没有可用的站立/行走动作（只有 ${(data.actions || []).map((a) => a.name).join('/')}），` +
          '换一只吧——桌宠需要 stand 或 move。',
        );
      }
      await savePetConfig({ mode: 'mob', mobId: id, name: data.name ?? null, at: new Date().toISOString() });
      return { mobId: id, name: data.name ?? null, pet: data };
    },
    async petStats() {
      return petWorker.call('stats');
    },
  };

  // 桌宠接口并入同一个 /api（命令名以 pet/petSet… 开头，与 wz 的命令不重名）
  Object.assign(api, petApi);

  // 预热桌宠 worker（失败只记录，面板加载时会重试）
  loadPetConfig().then(() => { petConfigLoaded = true; }).catch(() => {});
  petWorker.start().catch((error) => logger.warn?.(`桌宠 worker 预热失败：${error?.message ?? error}`));

  return api;
}
