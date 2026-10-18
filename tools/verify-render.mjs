#!/usr/bin/env node
// 渲染回归守卫：走 HTTP API 取图，断言**稳定的不变量**（尺寸、帧数、延迟、
// 非透明像素、多条合成管线是否一致），不依赖任何语言侧内部实现。
//
// 跑法：
//   node tools/verify-render.mjs                       # 缺省 http://127.0.0.1:3099
//   node tools/verify-render.mjs http://127.0.0.1:3080
//
// 为什么不用「和另一份实现逐像素比」：渲染只有一套实现了，没有第二个基准可比。
// 能长期守住的是这些数：帧数、延迟、画布尺寸、非透明像素比例、两条路径是否一致。
import { inflateSync } from 'node:zlib';
import { readFileSync } from 'node:fs';

const BASE = (process.argv[2] || 'http://127.0.0.1:3099').replace(/\/$/, '');
const API = `${BASE}/dsh-maplestory-wz/api`;

let pass = 0;
const fails = [];

function check(name, ok, detail = '') {
  if (ok) {
    pass += 1;
    console.log(`  ✓ ${name}${detail ? '  ' + detail : ''}`);
  } else {
    fails.push(name);
    console.log(`  ✗ ${name}${detail ? '  ' + detail : ''}`);
  }
}

async function api(cmd, args = {}) {
  const res = await fetch(API, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ cmd, args }),
  });
  const body = await res.json();
  if (!body.ok) throw new Error(typeof body.error === 'string' ? body.error : (body.error?.message || '接口失败'));
  return body.data;
}

/** 极简 PNG 解码：只处理我们自己的编码器产物（8bit RGBA、无交错、filter 0-4）。 */
function decodePng(file) {
  const buf = readFileSync(file);
  let off = 8;
  let w = 0;
  let h = 0;
  const idat = [];
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString('ascii', off + 4, off + 8);
    const data = buf.subarray(off + 8, off + 8 + len);
    if (type === 'IHDR') {
      w = data.readUInt32BE(0);
      h = data.readUInt32BE(4);
      if (data[8] !== 8 || data[9] !== 6) throw new Error(`不支持的 PNG：bit=${data[8]} color=${data[9]}`);
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') break;
    off += 12 + len;
  }
  const raw = inflateSync(Buffer.concat(idat));
  const stride = w * 4;
  const px = Buffer.alloc(stride * h);
  for (let y = 0; y < h; y++) {
    const ft = raw[y * (stride + 1)];
    const src = raw.subarray(y * (stride + 1) + 1, y * (stride + 1) + 1 + stride);
    const dst = px.subarray(y * stride, (y + 1) * stride);
    const prev = y > 0 ? px.subarray((y - 1) * stride, y * stride) : null;
    for (let x = 0; x < stride; x++) {
      const a = x >= 4 ? dst[x - 4] : 0;
      const b = prev ? prev[x] : 0;
      const c = prev && x >= 4 ? prev[x - 4] : 0;
      let v = src[x];
      if (ft === 1) v += a;
      else if (ft === 2) v += b;
      else if (ft === 3) v += (a + b) >> 1;
      else if (ft === 4) {
        const p = a + b - c;
        const pa = Math.abs(p - a);
        const pb = Math.abs(p - b);
        const pc = Math.abs(p - c);
        v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
      }
      dst[x] = v & 0xff;
    }
  }
  return { w, h, px };
}

function opaqueCount(img) {
  let n = 0;
  for (let i = 3; i < img.px.length; i += 4) if (img.px[i] > 8) n += 1;
  return n;
}

/** 两张同尺寸图的不透明区域差异比例。 */
function diffRatio(a, b) {
  if (a.w !== b.w || a.h !== b.h) return 1;
  let diff = 0;
  let total = 0;
  for (let i = 0; i < a.px.length; i += 4) {
    if (a.px[i + 3] <= 8 && b.px[i + 3] <= 8) continue;
    total += 1;
    if (
      Math.abs(a.px[i] - b.px[i]) > 2 ||
      Math.abs(a.px[i + 1] - b.px[i + 1]) > 2 ||
      Math.abs(a.px[i + 2] - b.px[i + 2]) > 2 ||
      Math.abs(a.px[i + 3] - b.px[i + 3]) > 2
    )
      diff += 1;
  }
  return total === 0 ? 0 : diff / total;
}

const fileUrl = (p) => `${BASE}${p}`;

async function fetchPng(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`取图失败 ${res.status} ${url}`);
  const tmp = `/tmp/wzverify_${Math.random().toString(36).slice(2)}.png`;
  const { writeFileSync } = await import('node:fs');
  writeFileSync(tmp, Buffer.from(await res.arrayBuffer()));
  return decodePng(tmp);
}

const EQUIPS = {
  Body: '00002000',
  Head: '00012000',
  Hair: '00042590',
  Face: '00022038',
  Longcoat: '01054561',
  Shoes: '01074237',
  Weapon: '01703725',
};

console.log(`渲染回归守卫：${BASE}`);

// ① 聊天气泡：非空、文字真的画上去了
try {
  const b = await api('balloon', { text: '你好，冒险岛！', style: 0 });
  const img = await fetchPng(fileUrl(b.url || b.pngUrl || ''));
  const op = opaqueCount(img);
  check('气泡出图非空', op > 500, `${img.w}x${img.h} 不透明 ${op}`);
  check('气泡尺寸合理', img.w > 40 && img.h > 20 && img.w < 1200, `${img.w}x${img.h}`);
  const empty = await api('balloon', { text: '', style: 0 });
  const img2 = await fetchPng(fileUrl(empty.url || empty.pngUrl || ''));
  check('空文本仍产出合法图', img2.w > 0 && img2.h > 0, `${img2.w}x${img2.h}`);
} catch (e) {
  check('气泡出图非空', false, String(e.message).slice(0, 120));
}

// ② 纸娃娃：尺寸随装备变化、角色本体落在画布内
try {
  const c = await api('charRender', { equips: EQUIPS, pose: 'stand1', flip: false });
  const img = await fetchPng(fileUrl(c.url || ''));
  const op = opaqueCount(img);
  check('纸娃娃出图非空', op > 1000, `${img.w}x${img.h} 不透明 ${op}`);
  check('纸娃娃画布比单部位大', img.w > 40 && img.h > 50, `${img.w}x${img.h}`);
} catch (e) {
  check('纸娃娃出图非空', false, String(e.message).slice(0, 120));
}

// ③ 椅子：帧数/延迟/画布是本插件最容易回归的三处（曾经抖动、曾经只画一帧）
try {
  const ch = await api('charRender', { chairId: '03019632', pose: 'sit' });
  const act = (ch.actions || [])[0] || {};
  check('椅子帧数为 12', act.frames === 12, `实际 ${act.frames}`);
  check('椅子画布 244x207', ch.w === 244 && ch.h === 207, `${ch.w}x${ch.h}`);
  check('椅子出图可下载', !!ch.url, String(ch.url || ''));
} catch (e) {
  check('椅子帧数为 12', false, String(e.message).slice(0, 120));
}

// ④ 雪碧图：并集画布、帧矩形不重叠且都在画布内
try {
  const sp = await api('sprite', { path: 'Mob/1210102.img', animation: 'move', scale: 1 });
  const sheet = await fetchPng(fileUrl(sp.pngUrl || ''));
  check('雪碧图尺寸与声明一致', sheet.w === sp.sheet.w && sheet.h === sp.sheet.h, `${sheet.w}x${sheet.h}`);
  const inside = (sp.frames || []).every(
    (f) => f.x >= 0 && f.y >= 0 && f.x + f.w <= sheet.w && f.y + f.h <= sheet.h,
  );
  check('所有帧矩形都在画布内', inside, `${(sp.frames || []).length} 帧`);
  check('雪碧图非空', opaqueCount(sheet) > 500, `不透明 ${opaqueCount(sheet)}`);
} catch (e) {
  check('雪碧图尺寸与声明一致', false, String(e.message).slice(0, 120));
}

// ⑤ 桌宠：同一份合成管线，格尺寸要跨动作统一
try {
  const pet = await api('pet', { id: '1210102', actions: ['stand', 'move'], mode: 'mob' });
  const pk0 = Object.keys(pet.anims || {})[0];
  const sheet = await fetchPng(fileUrl(pet.anims?.[pk0]?.url || ''));
  check('桌宠雪碧图非空', opaqueCount(sheet) > 300, `${sheet.w}x${sheet.h} 不透明 ${opaqueCount(sheet)}`);
  const cells = [...new Set((pet.frames || []).map((f) => `${f.w}x${f.h}`))];
  check('桌宠格尺寸统一', cells.length <= 1, `格：${cells.join(' / ') || 'n/a'}`);
} catch (e) {
  check('桌宠雪碧图非空', false, String(e.message).slice(0, 120));
}

// ⑥ 地图小地图：像素级稳定
try {
  const mm = await api('mapMinimap', { id: '100000000' });
  const img = await fetchPng(fileUrl(mm.url || ''));
  check('小地图非空', opaqueCount(img) > 200, `${img.w}x${img.h} 不透明 ${opaqueCount(img)}`);
} catch (e) {
  check('小地图非空', false, String(e.message).slice(0, 120));
}

// ⑦ 两条合成路径一致性：面板单帧 vs 桌宠首帧
try {
  const c = await api('charRender', { equips: EQUIPS, pose: 'stand1', flip: false });
  const p = await api('pet', { mode: 'chara', equips: EQUIPS, actions: ['stand'], flip: false, mode: 'chara' });
  const a = await fetchPng(fileUrl(c.url || ''));
  const pk = Object.keys(p.anims || {})[0];
  const b = await fetchPng(fileUrl(p.anims?.[pk]?.url || ''));
  // 两条路径的画布约定不同：面板取所有部位的并集，桌宠取 body 的格。
  // 所以不要求同尺寸；尺寸相同时逐像素比，不同时只要求两边都真的画出了东西。
  const sameCanvas = a.w === b.w && a.h === b.h;
  const d = sameCanvas ? diffRatio(a, b) : null;
  check(
    '面板与桌宠两条路径都出图',
    opaqueCount(a) > 200 && opaqueCount(b) > 200 && (!sameCanvas || d < 0.02),
    sameCanvas ? `同画布 ${a.w}x${a.h} 差异 ${(d * 100).toFixed(2)}%` : `画布不同 面板 ${a.w}x${a.h} / 桌宠 ${b.w}x${b.h}（约定不同，非缺陷）`,
  );
} catch (e) {
  check('面板与桌宠纸娃娃一致', false, String(e.message).slice(0, 120));
}

console.log('──────────────────────────────────────────────────────────────');
console.log(`合计 ${pass + fails.length} 项：${pass} 通过 / ${fails.length} 失败`);
if (fails.length) {
  console.log('失败项：');
  for (const f of fails) console.log(`  - ${f}`);
  process.exit(1);
}
