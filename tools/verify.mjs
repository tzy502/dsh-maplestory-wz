#!/usr/bin/env node
// 插件验收：宿主可达 + 每条命令能回 ok + 关键字段正确 + 渲染有内容。
//
// 跑法：
//   node tools/verify.mjs                       # 缺省 http://127.0.0.1:3099
//   node tools/verify.mjs http://192.168.3.96:3080
//
// 设计取向：只断言**语义**（有没有、对不对、非空不空白），不锁具体像素值 ——
// 像素由 tools/verify-render.mjs 负责，两者互补。
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
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const body = await res.json();
  if (!body.ok) throw new Error(typeof body.error === 'string' ? body.error : (body.error?.message || '接口失败'));
  return body.data;
}

console.log(`插件验收：${BASE}`);

// 0 宿主
try {
  const res = await fetch(`${BASE}/`, { redirect: 'manual' });
  check('宿主可达', res.status === 200 || res.status === 303 || res.status === 401, `http=${res.status}`);
} catch (e) {
  check('宿主可达', false, String(e.message).slice(0, 80));
  console.log('宿主不可达，后续检查跳过');
  process.exit(1);
}

// 1 数据源与浏览
try {
  const p = await api('packs');
  check('packs 有分类', (p.packs || []).length >= 10, `${(p.packs || []).length} 个分类`);
  check('packs 报告数据目录', typeof p.data_dir === 'string' && p.data_dir.length > 0, p.data_dir);
  check('packs 报告引擎', p.engine === 'wzlib', String(p.engine));
  const t = await api('tree');
  check('tree 根目录有分类', (t.children || []).length >= 10, `${(t.children || []).length} 项`);
  const t2 = await api('tree', { pack: 'Mob', limit: 5 });
  check('tree 能进 Mob', (t2.children || []).length > 0, `${(t2.children || []).length} 项`);
} catch (e) {
  check('packs 有分类', false, String(e.message).slice(0, 100));
}

// 2 搜索与图鉴
try {
  const byName = await api('search', { query: '花蘑菇', limit: 5 });
  check('按名字搜到花蘑菇', (byName.hits || []).some((h) => h.id === '1210102'), `${byName.count} 条`);
  const byId = await api('search', { query: '1210102', limit: 5 });
  check('按编号搜到 1210102', (byId.hits || []).some((h) => h.id === '1210102'), `${byId.count} 条`);
  const d = await api('detail', { id: '1210102', kind: 'mob' });
  check('图鉴有名字', d.name === '花蘑菇', String(d.name));
  check('图鉴有动作', (d.animations || []).length > 0, `${(d.animations || []).length} 个动作`);
  const s = await api('sources', { id: '1210102', kind: 'mob' });
  check('来源探测非空', (s.sources || []).length > 0, `${(s.sources || []).length} 条`);
} catch (e) {
  check('按名字搜到花蘑菇', false, String(e.message).slice(0, 100));
}

// 3 精灵图与 GIF
try {
  const fr = await api('frameSheet', { path: 'Mob/1210102.img', animation: 'stand' });
  check('frames 有帧', (fr.frames || []).length >= 2, `${(fr.frames || []).length} 帧`);
  const sp = await api('sprite', { path: 'Mob/1210102.img', animation: 'move', scale: 1 });
  check('sprite 出图', !!sp.pngUrl && sp.sheet?.w > 0, `${sp.sheet?.w}x${sp.sheet?.h}`);
  check('sprite 帧矩形齐全', (sp.frames || []).length >= 2, `${(sp.frames || []).length} 帧`);
  const gif = await api('sprite', { path: 'Mob/1210102.img', animation: 'move', scale: 1, gif: true });
  check('GIF 也出了', !!gif.gifUrl, String(gif.gifUrl || ''));
  const sheet = await fetch(`${BASE}${sp.pngUrl}`);
  check('雪碧图可下载且非空', sheet.ok && Number(sheet.headers.get('content-length') || 0) !== 0, `http=${sheet.status}`);
} catch (e) {
  check('frames 有帧', false, String(e.message).slice(0, 100));
}

// 4 纸娃娃与椅子
try {
  const poses = await api('charPoses');
  check('姿势表非空', (poses.poses || []).length > 0, `${(poses.poses || []).length} 个姿势`);
  const parts = await api('charParts');
  const catN = Object.keys(parts.categories || {}).length;
  check('部位表非空', catN >= 10, `${catN} 类`);
  const c = await api('charRender', {
    equips: { Body: '00002000', Head: '00012000', Hair: '00042590', Face: '00022038', Longcoat: '01054561', Shoes: '01074237', Weapon: '01703725' },
    pose: 'stand1',
    flip: false,
  });
  check('纸娃娃出图', !!c.url && c.w > 40, `${c.w}x${c.h}`);
  const chairs = await api('chairList', { limit: 5 });
  check('椅子清单非空', (chairs.total || 0) >= 2000, `total=${chairs.total}`);
  const ch = await api('charRender', { chairId: '03019632', pose: 'sit' });
  const act = (ch.actions || [])[0] || {};
  check('椅子坐姿 12 帧', act.frames === 12 && ch.w === 244, `${act.frames} 帧 ${ch.w}x${ch.h}`);
} catch (e) {
  check('纸娃娃出图', false, String(e.message).slice(0, 100));
}

// 5 气泡
try {
  const st = await api('balloonStyles');
  check('气泡样式总数 489', st.total === 489, `total=${st.total} usable=${st.usable}`);
  const b = await api('balloon', { text: '你好，冒险岛！', style: 0 });
  check('气泡出图', !!b.url && b.w > 20, `${b.w}x${b.h}`);
} catch (e) {
  check('气泡出图', false, String(e.message).slice(0, 100));
}

// 6 地图与背景
try {
  const ml = await api('mapList', { limit: 5 });
  check('地图清单非空', (ml.maps || []).length > 0, `${(ml.maps || []).length} 条`);
  const md = await api('mapDetail', { id: '100000000' });
  check('地图详情有 VR 边界', !!md.bounds, JSON.stringify(md.bounds || {}).slice(0, 60));
  const mi = await api('mapCanvas', { id: '100000000', scale: 0.25 });
  check('地图出图', !!mi.url && mi.w > 0, `${mi.w}x${mi.h}`);
  const bg = await api('backgroundGet');
  check('背景配置可读', !!bg, `enabled=${bg.enabled}`);
} catch (e) {
  check('地图清单非空', false, String(e.message).slice(0, 100));
}

// 7 桌宠
try {
  const pet = await api('pet', { id: '1210102', actions: ['stand', 'move'], mode: 'mob' });
  const animKeys = Object.keys(pet.anims || {});
  const firstAnim = pet.anims?.[animKeys[0]] || {};
  check('桌宠出图', !!firstAnim.url && firstAnim.cell?.w > 0, `${firstAnim.cell?.w}x${firstAnim.cell?.h}`);
  const search = await api('petSearch', { query: '蘑菇', limit: 3 });
  check('桌宠换宠候选非空', (search.hits || []).length > 0, `${(search.hits || []).length} 条`);
  const info = await api('petGet');
  check('桌宠配置可读', typeof info.mobId === 'string' && info.mobId.length > 0, info.mobId);
  check('站定开关可读', typeof info.still === 'boolean', `still=${info.still}`);
} catch (e) {
  check('桌宠出图', false, String(e.message).slice(0, 100));
}

// 8 cmsdl
try {
  const probe = await api('updateProbe');
  check('cmsdl 探测', typeof probe.found === 'boolean', `found=${probe.found}`);
  const st = await api('updateStatus');
  check('cmsdl 状态可读', typeof st.running === 'boolean', `running=${st.running}`);
  const plan = await api('updatePlan', {});
  check('cmsdl 命令预览非空', JSON.stringify(plan).length > 20, 'ok');
} catch (e) {
  check('cmsdl 探测', false, String(e.message).slice(0, 100));
}

console.log('──────────────────────────────────────────────────────────────');
console.log(`合计 ${pass + fails.length} 项：${pass} 通过 / ${fails.length} 失败`);
if (fails.length) {
  console.log('失败项：');
  for (const f of fails) console.log(`  - ${f}`);
  process.exit(1);
}
