/**
 * 冒险岛 WZ 资源浏览器 —— Web 面板（Client 半边）。
 *
 * 数据全部经 Host 的 HTTP API 拿（POST /dsh-maplestory-wz/api），
 * 图片由 /dsh-maplestory-wz/file/<name> 提供；只使用主题 token 配色。
 */
window.__ModuleLoader__.load({
  id: '@local/dsh-maplestory-wz',
  factory(require) {
    const React = require('react');
    const h = React.createElement;
    // 全文件统一用 el() 建元素：h 这个名字容易被局部变量遮蔽/在 TDZ 里被提前用到，
    // 已经因为 `const h = (img.h||vh)*zoom` 遮蔽过一次，直接把 React 调用固定成 el()。
    const el = h;
    // react-dom 在浏览器模块表里可用；拿不到就退回「原地渲染」（不致命）
    let createPortal = null;
    try {
      createPortal = require('react-dom').createPortal ?? null;
    } catch {
      createPortal = null;
    }
    // useRef 之前没解构：新加的换图过渡用它记「上一张图」
    const { useState, useEffect, useMemo, useCallback, useRef } = React;

    const PANEL_ID = 'maplestory-wz';
    const API = '/dsh-maplestory-wz/api';

    /** 面板与背景层（shell.overlay）之间的最小桥：同包内共享，不引入全局 store。 */
    const bgBridge = { get: () => null, set: () => {} };

    const T = {
      bg: 'var(--dsw-alias-bg-base)',
      layer1: 'var(--dsw-alias-bg-layer-1)',
      layer2: 'var(--dsw-alias-bg-layer-2)',
      border: 'var(--dsw-alias-border-l1)',
      border2: 'var(--dsw-alias-border-l2)',
      text: 'var(--dsw-alias-label-primary)',
      dim: 'var(--dsw-alias-label-secondary)',
      brand: 'var(--dsw-alias-brand-primary)',
      error: 'var(--dsw-alias-state-error-primary)',
      success: 'var(--dsw-alias-state-success-primary)',
    };

    async function callApi(cmd, args) {
      const res = await fetch(API, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ cmd, args: args || {} }),
      });
      const payload = await res.json();
      if (!payload.ok) throw new Error(payload.error || '接口失败');
      return payload.data;
    }

    const S = {
      root: { display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0, background: T.bg, color: T.text },
      bar: { display: 'flex', gap: 8, alignItems: 'center', padding: '10px 14px', borderBottom: `1px solid ${T.border}`, flexWrap: 'wrap' },
      input: {
        flex: '1 1 220px', minWidth: 160, padding: '7px 10px', borderRadius: 8, fontSize: 13,
        border: `1px solid ${T.border2}`, background: T.layer2, color: T.text, outline: 'none',
      },
      select: {
        padding: '7px 10px', borderRadius: 8, fontSize: 13, border: `1px solid ${T.border2}`,
        background: T.layer2, color: T.text,
      },
      btn: {
        padding: '7px 12px', borderRadius: 8, fontSize: 13, cursor: 'pointer',
        border: `1px solid ${T.border2}`, background: T.layer2, color: T.text,
      },
      btnOn: {
        padding: '7px 12px', borderRadius: 8, fontSize: 13, cursor: 'pointer',
        border: `1px solid ${T.brand}`, background: T.layer1, color: T.brand, fontWeight: 600,
      },
      body: { display: 'flex', flex: 1, minHeight: 0 },
      left: { width: 320, minWidth: 220, borderRight: `1px solid ${T.border}`, display: 'flex', flexDirection: 'column', minHeight: 0 },
      right: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', minHeight: 0 },
      listHead: { padding: '8px 12px', fontSize: 11, letterSpacing: 0.6, textTransform: 'uppercase', color: T.dim, borderBottom: `1px solid ${T.border}` },
      list: { overflow: 'auto', flex: 1, minHeight: 0, padding: 6 },
      row: {
        display: 'flex', alignItems: 'center', gap: 8, padding: '6px 8px', borderRadius: 6,
        fontSize: 13, cursor: 'pointer', color: T.text,
      },
      rowOn: {
        display: 'flex', alignItems: 'center', gap: 8, padding: '6px 8px', borderRadius: 6,
        fontSize: 13, cursor: 'pointer', background: T.layer2, color: T.text, fontWeight: 600,
      },
      tag: { fontSize: 11, color: T.dim, marginLeft: 'auto', flexShrink: 0 },
      stage: { flex: 1, minHeight: 0, overflow: 'auto', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 18 },
      checker: {
        backgroundImage: 'linear-gradient(45deg, rgba(128,128,128,.16) 25%, transparent 25%, transparent 75%, rgba(128,128,128,.16) 75%), linear-gradient(45deg, rgba(128,128,128,.16) 25%, transparent 25%, transparent 75%, rgba(128,128,128,.16) 75%)',
        backgroundSize: '16px 16px', backgroundPosition: '0 0, 8px 8px', borderRadius: 10, padding: 14,
      },
      zoom: { imageRendering: 'pixelated', display: 'block', maxWidth: '100%', maxHeight: '100%' },
      meta: { padding: '10px 14px', borderTop: `1px solid ${T.border}`, fontSize: 12, color: T.dim, display: 'flex', gap: 14, flexWrap: 'wrap', alignItems: 'center' },
      hint: { padding: 18, color: T.dim, fontSize: 13, lineHeight: 1.7 },
      err: { padding: '8px 12px', margin: 8, borderRadius: 8, fontSize: 12, color: T.error, background: T.layer2, border: `1px solid ${T.border}` },
      chip: {
        padding: '4px 9px', borderRadius: 999, fontSize: 12, cursor: 'pointer',
        border: `1px solid ${T.border2}`, background: T.layer1, color: T.text,
      },
      chipOn: {
        padding: '4px 9px', borderRadius: 999, fontSize: 12, cursor: 'pointer',
        border: `1px solid ${T.brand}`, background: T.layer1, color: T.brand, fontWeight: 600,
      },
      crumb: { fontSize: 12, color: T.dim, cursor: 'pointer', textDecoration: 'underline' },
      card: { background: T.layer1, border: `1px solid ${T.border}`, borderRadius: 12, padding: 14 },
      iconBox: {
        display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 12,
        background: T.layer2, border: `1px solid ${T.border}`, borderRadius: 10, minWidth: 128, minHeight: 128,
      },
      kv: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '4px 14px', fontSize: 12.5, alignItems: 'baseline' },
      kvKey: { color: T.dim, whiteSpace: 'nowrap' },
      kvVal: { color: T.text, wordBreak: 'break-all' },
      animRow: {
        display: 'flex', alignItems: 'center', gap: 8, padding: '5px 8px', borderRadius: 6,
        fontSize: 12.5, cursor: 'pointer', border: `1px solid transparent`,
      },
      section: { fontSize: 11, letterSpacing: 0.6, textTransform: 'uppercase', color: T.dim, margin: '4px 0 8px' },
      tile: { border: `1px solid ${T.border}`, borderRadius: 8, background: T.layer2, overflow: 'hidden' },
      status: {
        display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap',
        padding: '5px 12px', borderTop: `1px solid ${T.border}`,
        fontSize: 11.5, color: T.dim, background: T.layer1, flexShrink: 0,
      },
    };

    // ── 地图背景层（相机规则复刻 MiniPet Services/MapService.ClampCamera）──
    //
    // ClampCamera：camX ∈ [MinX + halfW, MaxX − halfW]，halfW = viewW/(2*zoom)；
    // 地图比视口窄（minX > maxX）时居中。Y 同理。背景层把相机世界坐标换算成
    // 图片像素偏移：offset = -(camX - halfW - bounds.left) * scale，Y 轴同理。

    /**
     * 把背景挂到 document.body 下的独立容器里。
     *
     * 为什么不用 shell.overlay 的节点：那里是 [data-shell-overlay]（absolute + z-index:20）
     * 的子节点，而布局三列带不透明底色、DOM 顺序在 overlay 之前——层级/底色任何一处
     * 与预期不符，背景就被盖住（实测就踩到了）。挂到 body 下 + z-index 抬高后，
     * 它只跟「页面根容器」比层级，不再受列影响；再用 CSS 把列改半透明就一定能看见。
     */
    function useBodyMount() {
      const [host] = useState(() => {
        if (typeof document === 'undefined') return null;
        const el = document.createElement('div');
        el.setAttribute('data-ms-wz-bg-host', 'on');
        // 关键：**插到 body 第一个子节点** + 负 z-index。
        // 之前 appendChild 到末尾 + z-index:0，它排在页面根容器之后、又和根容器同为
        // z-index 0/auto，实测把整个界面盖住（用户只能把内容 display:none 才能对话）。
        // 放最前 + z-index:-1：地图在页面背景之上、所有内容之下，列靠半透明透出。
        el.style.cssText = 'position:fixed;inset:0;z-index:-1;pointer-events:none;';
        document.body.insertBefore(el, document.body.firstChild);
        return el;
      });
      useEffect(() => () => { if (host?.parentNode) host.parentNode.removeChild(host); }, [host]);
      return host;
    }

    /**
     * 取一张地图当全屏背景：全图铺底 + 相机按 ClampCamera 规则缓慢漂移。
     * 自动轮换、中文名显示、随机换图都在这里；换图时用淡入避免闪一下。
     */
    function MapBackground({ config, onConfig }) {
      const host = useBodyMount();
      const [map, setMap] = useState(null);
      const [error, setError] = useState(null);
      const [size, setSize] = useState({ w: 0, h: 0 });
      const [t, setT] = useState(0);
      const rafRef = React.useRef(0);
      const lastRef = React.useRef(0);

      // ── 换图过渡（交叉淡入）──
      // **必须放在这里**（所有 hooks 都在早返回 `if (!config?.enabled) return null` 之前）：
      // 之前加在早返回之后，禁用背景时 hook 数量变化 → React error #310
      // 「Rendered more hooks than during the previous render」，整个 shell.overlay 崩掉（实测）。
      // 几何值（drawW/originPx…）在下面才算得出来，所以用 ref 承载：
      // 等渲染时把本帧的值写进 ref，再由 effect 在换图时取上一张的快照。
      const prevUrlRef = React.useRef(null);
      const prevMetaRef = React.useRef(null);
      const curMetaRef = React.useRef(null);
      const [fade, setFade] = useState(null);
      const mapFadeMs = Math.max(0, Number(config?.mapFadeMs ?? 1200));
      useEffect(() => {
        const meta = curMetaRef.current;
        if (!meta) return;
        const prev = prevUrlRef.current;
        if (prev && prev !== meta.url && mapFadeMs > 0) {
          setFade({ ...meta, from: prevMetaRef.current, key: Date.now() });
        }
        prevUrlRef.current = meta.url;
        prevMetaRef.current = meta;
      }, [config?.mapId, mapFadeMs]);
      useEffect(() => {
        if (!fade) return undefined;
        const t = setTimeout(() => setFade(null), mapFadeMs + 80);
        return () => clearTimeout(t);
      }, [fade, mapFadeMs]);

      useEffect(() => {
        const onResize = () => setSize({ w: window.innerWidth, h: window.innerHeight });
        onResize();
        window.addEventListener('resize', onResize);
        return () => window.removeEventListener('resize', onResize);
      }, []);

      useEffect(() => {
        if (!config?.enabled || !config.mapId) { setMap(null); return undefined; }
        let alive = true;
        setError(null);
        // 只换图时才重拉画布。**不要依赖 size**：画布是世界坐标系的一张整图，
        // 跟视口尺寸无关，挂上 size 只会让每次窗口变化都重拉一遍
        // （中途 setMap(null) 清空 → 白屏，用户反馈"地图有时候不显示"）。
        (async () => {
          const d = await callApi('mapCanvas', { id: config.mapId });
          if (!alive) return;
          setMap({
            url: d.url, w: d.w, h: d.h, scale: d.scale,
            content: d.contentBounds, vr: d.vrBounds, name: d.name,
          });
        })().catch((e) => {
          if (!alive) return;
          // 失败**不清空已有地图**（保留上一张继续显示），只记错误提示
          setError(`地图加载失败（保留上一张）：${String(e.message)}`);
        });
        return () => { alive = false; };
      }, [config?.enabled, config?.mapId]);

      // 漂移：相机沿 clamp 后的范围做往返（三角波），速度来自配置
      useEffect(() => {
        if (!map || !config?.enabled) return undefined;
        const speed = Number(config.speed ?? 1) || 1;
        // 一个来回的周期。原来固定 60s，用户反馈「地图切换太快了」，
        // 改成可配（面板里的「速度」），默认 4 分钟一个来回。
        const periodSec = Number(config?.periodSec ?? 240) || 240;
        const periodMs = (periodSec * 1000) / Math.max(0.1, speed);
        // **切图时把相位归零**（原来是全局累加，换地图后会从周期里任意位置开始）。
        // 纵向地图的 k=0 对应图像**底部**、k=1 对应顶部，所以归零会让镜头从最下面起 ——
        // 用户要求「从上到下时默认从中间开始」，因此纵向图起始相位取 0.5。
        // 横图 k 不作用于纵向（ky 恒为 0.5 居中），起始相位无所谓，同样给 0.5 保持一致。
        setT(0.5);
        const step = (now) => {
          if (!lastRef.current) lastRef.current = now;
          const dt = now - lastRef.current;
          lastRef.current = now;
          setT((prev) => prev + dt / periodMs);
          rafRef.current = window.requestAnimationFrame(step);
        };
        rafRef.current = window.requestAnimationFrame(step);
        return () => {
          window.cancelAnimationFrame(rafRef.current);
          lastRef.current = 0;
        };
      }, [map, config?.enabled, config?.speed, config?.periodSec]);

      if (!config?.enabled) {
        if (typeof document !== 'undefined') {
          const root = document.documentElement;
          root.style.backgroundImage = '';
          root.style.backgroundSize = '';
          root.style.backgroundPosition = '';
          root.style.backgroundAttachment = '';
        }
        return null;
      }

      const wrap = (node) => {
        if (!node) return null;
        if (host && createPortal) return createPortal(node, host);
        return node;   // 没有 react-dom 时原地渲染（至少还能靠 shell.overlay 显示）
      };

      // 没有地图可画时才占位。**有图就不要因为一次加载失败把画面整个换掉**
      // （旧写法 error 优先，导致任何一次失败都直接白屏，用户反馈"地图有时候不显示"）。
      if (!map) {
        return wrap(el('div', {
          'data-ms-wz-bg-layer': 'on',
          'data-ms-wz-bg-state': error ? `error:${error}` : 'loading',
          style: { position: 'fixed', inset: 0, zIndex: 0, pointerEvents: 'none' },
        }));
      }

      // ── MiniPet 相机（照 MapService.cs ClampCamera + MapService.Render.cs）──
      //   halfW = viewW/(2*zoom)；minX = MinX+halfW, maxX = MaxX-halfW；minX>maxX → 取中心
      //   screen = (world - cam) * zoom + half（half = 屏幕半宽/半高）
      const { content, vr, scale: imgScale } = map;
      const vw = size.w || window.innerWidth || 1920;
      const vh = size.h || window.innerHeight || 1080;
      const cw0 = Math.max(1, content.width);
      const ch0 = Math.max(1, content.height);
      // 屏幕上一个像素最多对应一个世界单位：世界比视口小 → 1:1 原样；比视口大 → 按需缩小。
      // 之前固定 zoom=1，3220 世界单位铺在 1280 视口上 = 2.5 倍放大，画面糊（用户反馈）。
      // 用 cover（取较大比例）而不是 contain：要**铺满视口**且不放大。
      // contain 会让小地图四周露空（实测 3040×2335 只占 751px 宽）。
      const zoomConf = Number(config.zoom ?? 1) || 1;
      const coverZoom = Math.max(vw / Math.max(1, cw0), vh / Math.max(1, ch0));
      const zoom = Math.min(zoomConf, Math.max(0.05, Math.min(coverZoom, 1)));
      const halfW = vw / 2;
      const halfH = vh / 2;

      // 相机在**世界坐标**里 clamp：用 VR 边界（MiniPet 的 MapInfo.MinX/MaxX 就是 VR）
      const camHalfW = vw / (2 * zoom);
      const camHalfH = vh / (2 * zoom);
      const useB = vr || content;
      const minX = useB.left + camHalfW, maxX = useB.right - camHalfW;
      const midX = (useB.left + useB.right) / 2;
      // 竖直方向别用「VR 边界 + camHalfH」直接 clamp：遇到 51149×3659 这种超长条地图
      // （宽高比 13.98），VR 里大片没画东西，算出来的相机位会让画面几乎全空。
      // 改成围绕**内容**上下各留一点余量（最多半屏），既保留上下漂移又保证看得见画面。
      // 竖直可漂移范围：内容上下各留 ySlack（不超过半屏），并在内容比视口矮时取内容中心。
      // 内容尺寸要先算：下面竖直 clamp 和方向规则都要用（之前 chh 声明在方向那段，
      // 这里先用就报 `Cannot access 'chh' before initialization`，组件直接崩、背景全没）。
      const cw = cw0;
      const chh = ch0;
      const ySlack = Math.min(camHalfH, Math.max(0, chh * 0.15));
      const minY = content.top - ySlack;
      const maxY = content.bottom + ySlack;
      const midY = (content.top + content.bottom) / 2;

      // ── 运动方向（用户口径）──
      // 看地图的两个边：**长边 / 短边 > 1.5 就走长边方向**（横图左右、竖图上下）；
      // 接近正方形（比值 ≤ 1.5，即两边相差不到 50%）一律**默认从左往右**。
      const taller = chh > cw * 1.5;                 // 竖图（高比宽多 50% 以上）→ 上下
      const dir = taller ? 'y' : 'x';                // 其余（含正方形）→ 左右
      const dirInfo = { dir, ratio: Number((Math.max(cw, chh) / Math.min(cw, chh)).toFixed(2)),
                        cw, ch: chh, reason: taller ? '高/宽 > 1.5，上下走' : '宽/高 ≥ 1 或接近正方形，左右走' };

      // 镜头位置 k ∈ [0,1]：auto=三角波往返；manual=用户拖的 pos；off=冻结
      const motion = config?.motion || 'auto';
      const pos = Math.min(1, Math.max(0, Number(config?.pos ?? 0.5)));
      const phase = Math.abs(((t % 1) + 1) % 1);
      let k = motion === 'auto' ? (phase < 0.5 ? phase * 2 : (1 - phase) * 2) : pos;
      // 竖直地图从左往右没意义（宽度不够平移），k 用在 Y 上；且上下用 1-k 让首帧偏下（地面）
      const kx = dir === 'x' ? k : 0.5;
      const ky = dir === 'y' ? 1 - k : 0.5;

      // 竖直：内容 + 余量都装不满一屏时（minY > maxY），直接居中，否则 clamp。
      // 之前没处理这种情况，反了的上下界会把相机推到图外（实测整屏一片白）。
      const yFits = minY <= maxY;
      const wantX = minX > maxX ? midX : minX + (maxX - minX) * kx;
      const wantY = yFits ? minY + (maxY - minY) * ky : midY;
      const camX = minX > maxX ? midX : Math.min(Math.max(wantX, minX), maxX);
      const camY = yFits ? Math.min(Math.max(wantY, minY), maxY) : midY;

      // 世界 → 屏幕：画布左上角（世界 content.left/top）画在哪里。
      // 就是 MiniPet 的 sx = (worldX - camX) * zoom + halfW，代入 worldX = content.left：
      //   originPx = -(camX - content.left) * zoom + halfW
      // 注意符号：写成 (content.left - camX) 会把整幅图推到屏幕外（实测一片白）。
      const originPx = -(camX - content.left) * zoom + halfW;
      const originPy = -(camY - content.top) * zoom + halfH;
      const drawW = map.w * (zoom / imgScale);
      const drawH = map.h * (zoom / imgScale);
      // 交给上面的过渡 effect：换图时它取上一张的快照做交叉淡入
      curMetaRef.current = {
        url: map.url, w: drawW, h: drawH, x: originPx, y: originPy,
        sat: config?.saturate ?? 0.92, bri: config?.brightness ?? 0.9,
      };

      // 把地图画到 **html 元素的固定背景** 上。
      // 实测教训：画到 .frame 上没用——内容区里有不透明的后代把整个 frame 背景盖住
      // （连纯红背景都看不见）。html 背景在最底层，任何元素都不可能盖住它；
      // 再把应用里所有"不透明底"改成磨砂半透明，地图就能透出来。
      if (typeof document !== 'undefined') {
        const root = document.documentElement;
        root.style.backgroundImage = `url("${map.url}")`;
        root.style.backgroundRepeat = 'no-repeat';
        root.style.backgroundAttachment = 'fixed';
        root.style.backgroundSize = `${drawW}px ${drawH}px`;
        root.style.backgroundPosition = `${originPx}px ${originPy}px`;
      }

      // 把「世界 → 屏幕」暴露给桌宠：**同一个函数**，避免两边各写一套相机导致漂移。
      // 公式就是 MiniPet 的 sx = (worldX - camX) * zoom + halfW。
      if (typeof window !== 'undefined') {
        window.__msWzToScreen = (wx, wy) => ({
          x: (wx - camX) * zoom + halfW,
          y: (wy - camY) * zoom + halfH,
        });
        window.__msWzBackground = {
          mapId: config.mapId, scale: imgScale, contentBounds: content,
          cam: { x: camX, y: camY }, zoom,
          dir: dirInfo,
          viewport: { w: vw, h: vh }, at: Date.now(),
        };
      }

      // 走到头就换地图（用户口径：从左到右走到头了就换一张，不要一直用一个，优先城镇）。
      // 判定方式改过一版：原来只在 k 恰好等于 1 的那一帧触发，rAF 很容易错过那一帧，
      // 结果「换地图没有换」（用户反馈）。现在跟踪**上一个 k**，只要 k 掉头（到达端点在往回走）
      // 就触发，并且每端每次往返只触发一次。
      if (typeof window !== 'undefined' && config?.autoNext !== false) {
        const st = window.__msWzEdge || (window.__msWzEdge = {
          lastK: null, dirUp: true, firedLeft: false, firedRight: false, busy: false,
        });
        // 记录本帧 k 与上一帧比较，得到"是否折返"
        const rising = st.lastK === null ? true : k > st.lastK;
        const turned = st.lastK !== null && (rising !== st.dirUp);
        if (rising !== st.dirUp) st.dirUp = rising;
        const atRight = k > 0.985 && !st.firedRight;
        const atLeft = k < 0.015 && !st.firedLeft;
        // 折返点必然在两端之一；用「刚折返 且 离那一端很近」来确认是端点
        const hitEdge = turned && (st.lastK > 0.9 || st.lastK < 0.1);
        if (!st.busy && (atRight || atLeft || hitEdge)) {
          st.busy = true;
          if (atRight || (hitEdge && st.lastK > 0.9)) st.firedRight = true;
          if (atLeft || (hitEdge && st.lastK < 0.1)) st.firedLeft = true;
          const recent = [...(config?.recent || []), config.mapId].slice(-20);
          setTimeout(() => {
            callApi('mapNext', { exclude: recent })
              .then((d) => {
                onConfig?.({ ...config, mapId: d.id, mapName: d.name, enabled: true, recent });
                st.busy = false;
                st.firedLeft = false;
                st.firedRight = false;
              })
              .catch(() => { st.busy = false; });
          }, Number(config?.switchDelayMs ?? 12000));
        }
        // 离开端点后允许再次触发（下一趟）
        if (k < 0.9 && k > 0.1) { st.firedLeft = false; st.firedRight = false; }
        st.lastK = k;
      }

      const layer = el('div', {
        'aria-hidden': true,
        'data-ms-wz-bg-layer': 'on',
        'data-ms-wz-bg-state': (error ? `ready+err(${error.slice(0, 60)}):` : 'ready:')
          + `${map.w}x${map.h}@${map.scale} cam=${Math.round(camX)},${Math.round(camY)} dir=${dirInfo.dir}(${dirInfo.ratio}) k=${k.toFixed(2)}`,
        style: {
          position: 'absolute', inset: 0, zIndex: 0, pointerEvents: 'none',
          overflow: 'hidden', background: 'transparent',
        },
      },
        // 过渡期间的「旧图」：垫在新图下面，等新图淡完就被丢弃
        fade && fade.from
          ? el('div', {
              key: `fade-prev-${fade.key}`,
              style: {
                position: 'absolute', inset: 0,
                backgroundImage: `url("${fade.from.url}")`,
                backgroundRepeat: 'no-repeat',
                backgroundSize: `${fade.from.w}px ${fade.from.h}px`,
                backgroundPosition: `${fade.from.x}px ${fade.from.y}px`,
                filter: `saturate(${fade.from.sat}) brightness(${fade.from.bri})`,
              },
            })
          : null,
        el('div', {
          key: fade ? `fade-cur-${fade.key}` : 'map-cur',
          style: {
            position: 'absolute', inset: 0,
            // 固定视口相机：整幅地图画布按 (world-cam)*zoom+half 定位
            backgroundImage: `url("${map.url}")`,
            backgroundRepeat: 'no-repeat',
            backgroundSize: `${drawW}px ${drawH}px`,
            backgroundPosition: `${originPx}px ${originPy}px`,
            filter: `saturate(${config.saturate ?? 0.92}) brightness(${config.brightness ?? 0.9})`,
            // 只在过渡期间挂动画；动画结束（fade=null）时这一层就是普通地图层。
            // key 随 fade 变化 → 重新挂载 → keyframes 从头播，不会因为复用节点而不播。
            opacity: fade ? 0 : 1,
            animation: fade ? `msWzMapIn ${mapFadeMs}ms ease-in-out forwards` : undefined,
          },
        }),
        el('div', {
          style: {
            position: 'absolute', inset: 0,
            background: `rgba(0,0,0,${config.scrim ?? 0.1})`,
          },
        }),
      );
      // 挂到 body 下的独立容器（见 useBodyMount 的注释：不受布局三列层级影响）
      return wrap(layer);
    }

    // ── 地图行走图（照 MiniPet Services/Walk/{WalkGraph,MapWalker} 的语义）──
    //
    // foothold 是「一段地面」：{x1,y1,x2,y2,prev,next}。相邻段用 prev/next 连成链，
    // 竖直段（x1==x2）是墙/落差，不能站。走到段尾就顺着 next 换段——这就是
    // MiniPet「沿 foothold 走、落地对齐」的最小实现：不寻路，只沿链走。

    /** 只保留能站的水平/斜坡段，并按 next 建索引。 */
    function buildWalkGraph(footholds) {
      const segs = new Map();
      for (const f of footholds || []) {
        if (f.x1 === f.x2) continue;                 // 竖直：墙，不站
        segs.set(f.id, { ...f, lo: Math.min(f.x1, f.x2), hi: Math.max(f.x1, f.x2) });
      }
      return segs;
    }

    /** 在 (x, y) 附近找一条可站的段（容差 tol 世界单位）。 */
    function findGround(graph, x, y, tol = 240) {
      let best = null;
      let bestD = Infinity;
      for (const seg of graph.values()) {
        if (x < seg.lo - 40 || x > seg.hi + 40) continue;
        // 段在 x 处的地面高度（线性插值）
        const t = (x - seg.x1) / (seg.x2 - seg.x1 || 1);
        const gy = seg.y1 + (seg.y2 - seg.y1) * t;
        const d = Math.abs(gy - y);
        if (d < bestD && d <= tol) { bestD = d; best = seg; }
      }
      return best;
    }

    /** 段上 x 处的地面 y（线性插值）。 */
    function groundYAt(seg, x) {
      const t = (x - seg.x1) / (seg.x2 - seg.x1 || 1);
      return seg.y1 + (seg.y2 - seg.y1) * Math.max(0, Math.min(1, t));
    }

    /** 沿链前进：走到段尾就换 next（没有 next 就掉头）。 */
    function stepAlongChain(graph, seg, x) {
      if (!seg) return { seg: null, x };
      if (seg.next && graph.has(seg.next)) return { seg: graph.get(seg.next), x };
      if (seg.prev && graph.has(seg.prev)) {
        const p = graph.get(seg.prev);
        return { seg: p, x: p.x2 };                  // 反向：从上一段末端继续
      }
      return { seg, x: seg.x1 };                     // 孤立段：回到起点
    }

    /**
     * 把地图画到**应用自己的 frame 元素**上。
     *
     * 为什么不用 body 下的独立浮层：实测把它 fixed 到 body（z-index -1 或 0）都看不见——
     * 它和 #root 是兄弟，而 #root/.frame 在 DOM 里排在后面且同为 z-index auto，
     * 后画的就把先画的盖掉；把 z-index 抬上去又会盖住内容。画到 .frame 的
     * background 上则天然处在「页面背景之上、三列之下」，列半透明就能透出来——
     * 这也正是「DSH 背景」该有的层级。
     */
    function frameElement() {
      if (typeof document === 'undefined') return null;
      return document.querySelector('[class*="frame"]') || document.getElementById('root')?.firstElementChild || null;
    }

    /** 侧边栏图标：枫叶轮廓（随主题文字色）。 */
    function WzIcon(props) {
      const size = typeof props?.size === 'number' ? props.size : 18;
      return el(
        'svg',
        { width: size, height: size, viewBox: '0 0 24 24', 'aria-hidden': true, style: { display: 'block' } },
        el('path', {
          d: 'M12 2l2.4 4.1 3.2-1.6-.7 3.6 4.1 1.3-3.3 2 2.3 3.4-4.2-.3.4 4.2-3.6-2.1-1.9 3.7-1.9-3.7-3.6 2.1.4-4.2-4.2.3 2.3-3.4-3.3-2 4.1-1.3-.7-3.6 3.2 1.6z',
          fill: 'none', stroke: 'currentColor', strokeWidth: 1.5, strokeLinejoin: 'round',
        }),
        el('path', { d: 'M12 15.5V21', stroke: 'currentColor', strokeWidth: 1.5, strokeLinecap: 'round' }),
      );
    }

    function TreeList({ pack, path, onOpen, onPick, onPickPack }) {
      const [state, setState] = useState({ loading: true });
      useEffect(() => {
        let alive = true;
        setState({ loading: true });
        callApi('tree', { pack, path })
          .then((data) => alive && setState({ data }))
          .catch((error) => alive && setState({ error: String(error.message) }));
        return () => { alive = false; };
      }, [pack, path]);
      if (state.loading) return el('div', { style: S.hint }, '读取中…');
      if (state.error) return el('div', { style: S.err }, state.error);
      const data = state.data;
      const items = data.children;
      return el(
        'div',
        { style: S.list },
        data.path || data.kind === 'root'
          ? el('div', { style: { padding: '4px 8px 8px' } },
              data.path
                ? el('span', { style: S.crumb, onClick: () => onOpen(pack, data.path.split('/').slice(0, -1).join('/')) }, '↑ 上一层')
                : el('span', { style: { color: T.dim } }, '数据源根目录'),
              el('span', { style: { margin: '0 8px', color: T.dim } }, data.path ? `${pack}/${data.path}` : '选一个分类进入'))
          : null,
        items.map((item) => {
          const key = `${item.kind}:${item.name}`;
          if (item.kind === 'dir') {
            // 根目录模式（pack 为空）：点一行就是**选中这个分类**并进入它的根
            const atRoot = !pack;
            const go = atRoot
              ? () => onPickPack?.(item.name)
              : () => onOpen(pack, data.path ? `${data.path}/${item.name}` : item.name);
            return el('div', { key, style: S.row, onClick: go, title: atRoot ? `进入 ${item.name}` : undefined },
              el('span', { style: { color: T.dim } }, atRoot ? '◆' : '▸'),
              el('span', null, item.name));
          }
          if (item.kind === 'image') {
            return el('div', { key, style: S.row, onClick: () => onPick(`${pack}/${data.path ? `${data.path}/` : ''}${item.name}`), title: '点击预览' },
              el('span', { style: { color: T.brand } }, '◆'), el('span', { style: { overflow: 'hidden', textOverflow: 'ellipsis' } }, item.name));
          }
          return el('div', { key, style: S.row },
            el('span', { style: { color: T.dim, fontSize: 11, minWidth: 46 } }, item.kind),
            el('span', { style: { overflow: 'hidden', textOverflow: 'ellipsis' } }, item.name),
            item.value !== null && item.value !== undefined && typeof item.value !== 'object'
              ? el('span', { style: S.tag }, String(item.value))
              : null);
        }),
        items.length === 0 ? el('div', { style: S.hint }, '（空）') : null,
      );
    }

    function SearchList({ query, kind, onPick }) {
      const [state, setState] = useState({ loading: true });
      useEffect(() => {
        let alive = true;
        setState({ loading: true });
        callApi('search', { query, kind, limit: 60 })
          .then((data) => alive && setState({ data }))
          .catch((error) => alive && setState({ error: String(error.message) }));
        return () => { alive = false; };
      }, [query, kind]);
      if (state.loading) return el('div', { style: S.hint }, '搜索中…');
      if (state.error) return el('div', { style: S.err }, state.error);
      const hits = state.data.hits;
      if (!hits.length) return el('div', { style: S.hint }, `没有命中「${query}」`);
      return el('div', { style: S.list }, hits.map((hit, i) =>
        el('div', { key: `${hit.kind}-${hit.id}-${i}`, style: i === 0 ? S.rowOn : S.row, onClick: () => onPick(hit) },
          el('span', { style: { color: T.brand, fontSize: 11, minWidth: 34 } }, hit.kind),
          el('span', { style: { fontVariantNumeric: 'tabular-nums', color: T.dim } }, hit.id),
          el('span', { style: { overflow: 'hidden', textOverflow: 'ellipsis' } }, hit.name || '—'),
        )));
    }

    function Preview({ target, onError }) {
      const [info, setInfo] = useState({ loading: true });
      const [anim, setAnim] = useState(null);
      const [scale, setScale] = useState(1);   // 默认原始像素
      const [sprite, setSprite] = useState(null);
      const [playGif, setPlayGif] = useState(true);

      useEffect(() => {
        let alive = true;
        setInfo({ loading: true });
        setSprite(null);
        setAnim(null);
        callApi('get', { path: target })
          .then((data) => {
            if (!alive) return;
            setInfo({ data });
            if (data.animations && data.animations.length) setAnim(data.animations[0].name);
            else if (data.preview) setSprite(data.preview);
          })
          .catch((error) => alive && setInfo({ error: String(error.message) }));
        return () => { alive = false; };
      }, [target]);

      useEffect(() => {
        if (!anim) return undefined;
        let alive = true;
        setSprite(null);
        callApi('sprite', { path: target, animation: anim, scale, gif: true })
          .then((data) => alive && setSprite(data))
          .catch((error) => { if (alive) { setSprite({ error: String(error.message) }); onError?.(String(error.message)); } });
        return () => { alive = false; };
      }, [target, anim, scale]);

      if (info.loading) return el('div', { style: S.hint }, '读取中…');
      if (info.error) return el('div', { style: S.err }, info.error);
      const data = info.data;
      const animations = data.animations || [];

      return el(
        'div',
        { style: S.right },
        el('div', { style: S.bar },
          el('strong', { style: { fontSize: 14 } }, data.name ? `${data.name}` : data.id),
          el('span', { style: { fontSize: 12, color: T.dim } }, data.path),
          // R2 的「引用/链接」信息：本体是 1×1 占位、真图在 _Canvas 时，这里能看出来
          data.animations?.some?.((a) => a.link)
            ? el('span', { style: { fontSize: 11, color: T.dim } }, '含 _outlink')
            : null,
          el('span', { style: { marginLeft: 'auto' } }),
          scale !== 1 ? el('span', { style: { fontSize: 12, color: T.dim } }, `缩放 ${scale}×`) : null,
          el('button', { style: S.btn, onClick: () => setScale((v) => Math.max(1, v - 1)), type: 'button' }, '缩小'),
          el('button', { style: S.btn, onClick: () => setScale((v) => Math.min(8, v + 1)), type: 'button' }, '放大'),
          sprite?.gifUrl
            ? el('button', { style: playGif ? S.btnOn : S.btn, onClick: () => setPlayGif((v) => !v), type: 'button' }, playGif ? 'GIF 动画中' : '静态帧')
            : null,
          sprite?.pngUrl
            ? el('a', { style: { ...S.btn, textDecoration: 'none' }, href: sprite.pngUrl, download: '' }, '下载雪碧图')
            : null,
        ),
        animations.length
          ? el('div', { style: { ...S.bar, borderBottom: `1px solid ${T.border}` } },
              animations.map((a) => el('button', {
                key: a.name, type: 'button', style: a.name === anim ? S.chipOn : S.chip, onClick: () => setAnim(a.name),
              }, `${a.name}${a.frames > 1 ? ` ×${a.frames}` : ''}`)))
          : null,
        el('div', { style: S.stage },
          sprite === null
            ? el('div', { style: S.hint }, '渲染中…')
            : sprite.error
              ? el('div', { style: S.err }, sprite.error)
              : el('div', { style: S.checker },
                  el('img', {
                    style: S.zoom,
                    alt: `${data.id} ${anim || ''}`,
                    src: playGif && sprite.gifUrl ? sprite.gifUrl : sprite.pngUrl,
                  })),
        ),
        el('div', { style: S.meta },
          sprite && !sprite.error
            ? [
                el('span', { key: 'f' }, `帧数 ${sprite.frames?.length ?? 0}`),
                el('span', { key: 'c' }, `单帧 ${sprite.frame?.w}×${sprite.frame?.h}`),
                el('span', { key: 's' }, `雪碧图 ${sprite.sheet?.w}×${sprite.sheet?.h}`),
                el('span', { key: 'sc' }, `缩放 ${sprite.scale}×`),
                el('span', { key: 'n' }, `动作 ${data.animations?.length ?? 0}`),
              ]
            : el('span', null, '—'),
          Object.keys(data.info || {}).length
            ? el('span', { style: { marginLeft: 'auto', color: T.dim } },
                Object.entries(data.info).slice(0, 6).map(([k, v]) => `${k}=${v}`).join('  '))
            : null,
        ),
      );
    }

    const KIND_LABEL = { mob: '怪物', npc: 'NPC', eqp: '装备' };

    /** 单个小图：雪碧图切片。 */
    function Tile({ url, meta, tileW, tileH, zoom, title }) {
      const scale = meta.padding + meta.frame.w + meta.padding;
      const inner = meta.frames[0];
      return el('div', {
        title,
        style: {
          ...S.tile, width: tileW, height: tileH,
          backgroundImage: `url("${url}")`, imageRendering: 'pixelated',
          backgroundRepeat: 'no-repeat',
          backgroundSize: `${meta.sheet.w * zoom}px ${meta.sheet.h * zoom}px`,
          backgroundPosition: `${-inner.x * zoom}px ${-inner.y * zoom}px`,
          outline: scale > 0 ? 'none' : 'none',
        },
      });
    }

    function FrameGrid({ target, animation, frameCount, max = 24 }) {
      const [sheet, setSheet] = useState(null);
      const [err, setErr] = useState(null);
      useEffect(() => {
        let alive = true;
        setSheet(null); setErr(null);
        callApi('frameSheet', { path: target, animation, scale: 1, gif: false })
          .then((d) => { if (alive) setSheet(d); })
          .catch((e) => alive && setErr(String(e.message)));
        return () => { alive = false; };
      }, [target, animation]);
      if (err) return el('div', { style: S.err }, err);
      if (!sheet) return el('div', { style: S.hint }, '取帧中…');
      const shown = Math.min(sheet.frames.length, max);
      const zoom = 2;
      const tw = sheet.frame.w * zoom + 8;
      const th = sheet.frame.h * zoom + 8;
      return el('div', { style: { display: 'flex', flexWrap: 'wrap', gap: 8 } },
        sheet.frames.slice(0, shown).map((f) => el('div', {
          key: f.index, title: `第 ${f.index + 1} 帧 · ${f.w}×${f.h}`,
          style: {
            width: tw, height: th, border: `1px solid ${T.border}`, borderRadius: 6,
            background: T.layer2, imageRendering: 'pixelated', backgroundRepeat: 'no-repeat',
            backgroundImage: `url("${sheet.pngUrl}")`,
            backgroundSize: `${sheet.sheet.w * zoom}px ${sheet.sheet.h * zoom}px`,
            backgroundPosition: `${-f.x * zoom + 4}px ${-f.y * zoom + 4}px`,
          },
        })),
        frameCount > shown ? el('div', { style: { ...S.hint, width: '100%' } }, `（共 ${frameCount} 帧，显示前 ${shown} 帧）`) : null,
      );
    }

    /** R2 的「导出全部动画」：逐个动作出 GIF，落盘后给出文件清单。 */
    function BulkExport({ target, animations }) {
      const [busy, setBusy] = useState(false);
      const [done, setDone] = useState(null);
      const [err, setErr] = useState(null);
      const run = async () => {
        setBusy(true); setErr(null); setDone([]);
        const out = [];
        for (const a of animations) {
          try {
            const d = await callApi('sprite', { path: target, animation: a.name, scale: 1, gif: true, save_dir: '~/.dsh/wz-assets/export' });
            out.push({ name: a.name, frames: d.frames?.length ?? 0, gif: d.saved_gif || d.gif, png: d.saved_png || d.png });
          } catch (e) {
            out.push({ name: a.name, error: String(e.message) });
          }
          setDone([...out]);
        }
        setBusy(false);
      };
      return el('div', null,
        el('div', { style: { display: 'flex', gap: 8, alignItems: 'center', marginBottom: 8 } },
          el('button', { type: 'button', style: S.btn, disabled: busy, onClick: run },
            busy ? '导出中…' : `导出全部 ${animations.length} 个动作为 GIF`),
          el('span', { style: { fontSize: 11, color: T.dim } }, '落在 ~/.dsh/wz-assets/export/'),
        ),
        err ? el('div', { style: S.err }, err) : null,
        done
          ? el('div', { style: { display: 'flex', flexDirection: 'column', gap: 3, fontSize: 11.5, color: T.dim, maxHeight: 180, overflow: 'auto' } },
              done.map((d) => el('div', { key: d.name },
                `${d.name} · ${d.error ? `失败：${d.error}` : `${d.frames} 帧 → ${d.gif || d.png}`}`)))
          : null,
      );
    }

    function Detail({ kind, id, onOpenPath }) {
      const [state, setState] = useState({ loading: true });
      const [anim, setAnim] = useState(null);
      const [showFrames, setShowFrames] = useState(true);
      const [msInfo, setMsInfo] = useState(null);

      // 怪物的结构在 Packs/Mob_00000.ms 里；单独查一次（首次约 2~3s，之后 worker 缓存）
      useEffect(() => {
        if (kind !== 'mob') { setMsInfo(null); return undefined; }
        let alive = true;
        setMsInfo(null);
        callApi('msMob', { id })
          .then((d) => alive && setMsInfo(d))
          .catch(() => alive && setMsInfo({ found: false, total_mobs: 0, near: [] }));
        return () => { alive = false; };
      }, [kind, id]);

      useEffect(() => {
        let alive = true;
        setState({ loading: true }); setAnim(null);
        callApi('detail', { kind, id })
          .then((d) => {
            if (!alive) return;
            setState({ data: d });
            setAnim((d.animations || [])[0]?.name ?? null);
          })
          .catch((e) => alive && setState({ error: String(e.message) }));
        return () => { alive = false; };
      }, [kind, id]);

      if (state.loading) return el('div', { style: S.hint }, '读取中…');
      if (state.error) return el('div', { style: S.err }, state.error);
      const d = state.data;
      const info = { ...(d.info || {}), ...(d.link_info || {}) };
      const infoEntries = Object.entries(info).filter(([, v]) => v !== '' && v !== null);
      const iconUrl = d.icon && !d.icon.transparent
        ? `/dsh-maplestory-wz/image?path=${encodeURIComponent(d.path)}&animation=${encodeURIComponent(d.kind === 'eqp' ? 'info/icon' : 'stand')}&scale=1`
        : null;

      return el('div', { style: { ...S.right, overflow: 'auto' } },
        el('div', { style: { ...S.bar, position: 'sticky', top: 0, background: T.bg, zIndex: 2 } },
          el('strong', { style: { fontSize: 15 } }, d.name || '(无名字)'),
          el('span', { style: { fontSize: 12, color: T.dim } }, `${KIND_LABEL[d.kind] || d.kind} ${d.id}`),
          el('span', { style: { marginLeft: 'auto' } }),
          d.animations.length
            ? el('button', {
                type: 'button', style: showFrames ? S.btnOn : S.btn,
                onClick: () => setShowFrames((v) => !v),
              }, showFrames ? '帧网格开' : '帧网格关')
            : null,
          d.path ? el('button', { type: 'button', style: S.btn, onClick: () => onOpenPath(d.path) }, '在浏览里打开') : null,
        ),
        el('div', { style: { padding: 14, display: 'flex', flexDirection: 'column', gap: 14 } },
          d.error ? el('div', { style: S.err }, d.error) : null,
          el('div', { style: { display: 'flex', gap: 14, flexWrap: 'wrap' } },
            el('div', { style: S.iconBox },
              iconUrl
                ? el('img', { src: iconUrl, alt: d.name || d.id, style: { maxWidth: 160, maxHeight: 160, imageRendering: 'pixelated' } })
                : el('span', { style: { color: T.dim, fontSize: 12 } }, '无可显示图像')),
            el('div', { style: { ...S.card, flex: 1, minWidth: 240 } },
              el('div', { style: S.section }, '基本信息'),
              el('div', { style: S.kv },
                ...[
                  ['中文名', d.name || '—'],
                  ['编号', d.id],
                  ['类别', KIND_LABEL[d.kind] || d.kind],
                  ['WZ 路径', d.path || '—'],
                  ['字符串表', d.string ? Object.entries(d.string).map(([k, v]) => `${k}=${v}`).join('  ') : '—'],
                  ['属性来源', d.info_source || (d.link ? `link → ${d.link}${d.link_path ? ` (${d.link_path})` : ''}` : '本客户端无 info（只有图）')],
                  ['动作 / 帧', `${d.animations.length} 个动作，共 ${d.total_frames || 0} 帧`],
                  ['图像尺寸', d.icon ? `${d.icon.w}×${d.icon.h}` : '—'],
                ].map(([k, v]) => [el('div', { key: `${k}k`, style: S.kvKey }, k), el('div', { key: `${k}v`, style: S.kvVal }, String(v))])),
            ),
          ),
          kind === 'mob' && msInfo
            ? el('div', { style: S.card },
                el('div', { style: S.section }, '.ms 结构（Packs/Mob_00000.ms）'),
                msInfo.found
                  ? el('div', { style: { fontSize: 12.5, lineHeight: 1.8 } },
                      el('div', null, `${Object.keys(msInfo.animations).length} 个动作 / ${msInfo.frames_total} 帧`),
                      el('div', { style: { color: T.dim } },
                        Object.entries(msInfo.animations).map(([k, v]) => `${k}(${v.length})`).join('  ')),
                      el('div', { style: { marginTop: 6, color: msInfo.spine ? T.success : T.dim } },
                        msInfo.spine
                          ? `Spine 骨骼：${msInfo.spine.bones} 根，动画 ${msInfo.spine.animations.join(', ')}`
                          : '非 Spine（普通帧动画）'))
                  : el('div', { style: { fontSize: 12.5, color: T.dim } },
                      `该容器（${msInfo.total_mobs} 个 mob）里没有这个编号的结构` +
                      (msInfo.near?.length ? `；相近：${msInfo.near.slice(0, 5).join(', ')}` : '')),
                el('div', { style: { marginTop: 6, fontSize: 11.5, color: T.dim } },
                  '结构/骨骼来自 .ms；像素经 _outlink 取自 Mob/_Canvas（.ms 里的 canvas 是 1×1 占位）'))
            : null,
          (d.sources || []).length
            ? el('div', { style: S.card },
                el('div', { style: S.section }, '数据来源（info 与 _Canvas 的区别）'),
                el('div', { style: { fontSize: 12, color: T.dim, marginBottom: 8, lineHeight: 1.7 } },
                  '「信息主源」是本体 .img：动作结构 + origin/delay/z/map；「PNG 仓库」_Canvas 多数条目只有像素，没有 info。'),
                el('div', { style: { display: 'flex', flexDirection: 'column', gap: 6 } },
                  d.sources.map((src) => el('div', {
                    key: src.path,
                    style: {
                      display: 'flex', alignItems: 'center', gap: 10, fontSize: 12.5,
                      padding: '6px 8px', borderRadius: 6, background: T.layer2,
                      opacity: src.exists ? 1 : 0.55,
                    },
                  },
                    el('span', { style: { color: src.exists ? (src.has_info ? T.success : T.dim) : T.dim, minWidth: 14 } },
                      src.exists ? (src.has_info ? '●' : '○') : '·'),
                    el('span', { style: { minWidth: 150, color: T.text } }, src.label),
                    el('span', { style: { fontFamily: 'ui-monospace, monospace', color: T.dim, overflow: 'hidden', textOverflow: 'ellipsis' } }, src.path),
                    el('span', { style: { marginLeft: 'auto', color: T.dim, whiteSpace: 'nowrap' } }, src.note),
                  ))))
            : null,
          infoEntries.length
            ? el('div', { style: S.card },
                el('div', { style: S.section }, `属性（${infoEntries.length} 项）`),
                el('div', { style: { ...S.kv, gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))' } },
                  ...infoEntries.map(([k, v]) => el('div', { key: k, style: { display: 'contents' } },
                    el('div', { style: S.kvKey }, k),
                    el('div', { style: S.kvVal }, Array.isArray(v) ? `(${v.join(', ')})` : String(v))))))
            : el('div', { style: S.card },
                el('div', { style: S.section }, '属性'),
                el('div', { style: { fontSize: 12.5, color: T.dim, lineHeight: 1.7 } },
                  d.kind === 'mob'
                    ? '本客户端（傲天 BMS 228）的 Mob 只有 _Canvas 图像数据，没有 info 属性块；想看数值请到 wiki 后端的数据表。'
                    : '该资源没有可读的属性块。')),
          d.animations.length
            ? el('div', { style: S.card },
                el('div', { style: S.section }, `动作（${d.animations.length}）`),
                el('div', { style: { display: 'flex', flexWrap: 'wrap', gap: 6 } },
                  d.animations.map((a) => el('button', {
                    key: a.name, type: 'button',
                    style: a.name === anim ? S.chipOn : S.chip,
                    onClick: () => setAnim(a.name),
                  }, `${a.name} · ${a.frames}帧 · ${a.w}×${a.h}`))),
                el('div', { style: { marginTop: 12, marginBottom: 4 } },
                  el(BulkExport, { target: d.path, animations: d.animations })),
                anim && showFrames
                  ? el('div', { style: { marginTop: 12 } },
                      el(FrameGrid, {
                        target: d.path, animation: anim,
                        frameCount: d.animations.find((a) => a.name === anim)?.frames ?? 0,
                      }))
                  : null)
            : null,
        ),
      );
    }

    /**
     * 一键启动器：侧栏面板行在某些窗口宽度下会被折叠/挤掉，这里在 shell.overlay
     * 左上角常驻一个小按钮，点一下直接切到本面板（走 layout.selectPanel，
     * 它就是布局自己的面板切换入口，不是模拟点击）。
     */
    function PanelLauncher({ layout }) {
      const open = () => {
        try {
          layout?.selectPanel?.(PANEL_ID);
        } catch (e) {
          console.error('[maplestory-wz] selectPanel 失败:', e);
        }
      };
      return el('button', {
        type: 'button',
        title: '打开冒险岛资源面板（浏览 / 搜索 / 图鉴 / 背景）',
        onClick: open,
        style: {
          position: 'fixed', left: 12, bottom: 12, zIndex: 5,
          display: 'flex', alignItems: 'center', gap: 6,
          padding: '6px 10px', borderRadius: 999, cursor: 'pointer',
          border: `1px solid ${T.border2}`, background: T.layer1, color: T.text,
          fontSize: 12, opacity: 0.92, boxShadow: '0 2px 10px rgba(0,0,0,.18)',
        },
      }, el(WzIcon, { size: 14 }), '冒险岛资源');
    }

    /**
     * 纸娃娃：给部位挑编号，实时合成角色帧。
     * 与 mapleStoryMiniPet 的 CharaSim 同一套合成语义（zmap 排层）。
     */
    // wzpy 的 CATEGORIES 一共 16 类，面板要全部放出来（之前只放 12 类，
    // 漏了套服/脸饰/眼镜/耳环，用户反馈「素材少了」就是这个）。
    const CHAR_SLOTS = [
      ['Body', '身体'], ['Head', '头'], ['Hair', '发型'], ['Face', '脸'],
      ['Cap', '帽子'], ['Coat', '上衣'], ['Longcoat', '套服'], ['Pants', '裤子'],
      ['Shoes', '鞋'], ['Glove', '手套'], ['Cape', '披风'], ['Shield', '盾'],
      ['FaceAcc', '脸饰'], ['Glass', '眼镜'], ['Earring', '耳环'], ['Weapon', '武器'],
    ];
    const EMPTY_EQUIPS = {
      Body: '00002000', Head: '00012000', Hair: '00030000', Face: '00020000',
      Cap: '', Coat: '', Longcoat: '', Pants: '', Shoes: '', Glove: '',
      Cape: '', Shield: '', FaceAcc: '', Glass: '', Earring: '', Weapon: '',
      Chair: '',
    };

    /**
     * 把存下来的 `doll.equips` 归一化成「按部位名索引」的对象。
     *
     * 为什么要这一步：早期版本存的是**数字键**（`{0:'00002000', 1:'00012000', …}`），
     * 现在存的是命名键（`{Body:'00002000', Face:'00022038', …}`）。
     * 而面板恢复用的是 `{...EMPTY_EQUIPS, ...d.equips}` —— 数字键**覆盖不到**任何命名键，
     * 于是脸保持默认 `00020000`；接着 400ms 的自动保存又把这份默认值写回盘，
     * **把用户真实的脸永久覆盖掉**。桌宠读同一份配置，于是「桌宠的脸不对」。
     * 这里两种形状都认：数字键按 CHAR_SLOTS 的顺序回填。
     */
    function normalizeEquips(raw) {
      const out = { ...EMPTY_EQUIPS };
      if (!raw) return out;
      if (Array.isArray(raw)) {
        CHAR_SLOTS.forEach(([slot], i) => { if (raw[i]) out[slot] = String(raw[i]); });
        return out;
      }
      for (const [k, v] of Object.entries(raw)) {
        if (!v) continue;
        if (/^\d+$/.test(k)) {
          const slot = CHAR_SLOTS[Number(k)]?.[0];
          if (slot) out[slot] = String(v);
        } else if (k in out) {
          out[k] = String(v);
        }
      }
      return out;
    }

    function PaperDoll() {
      // 统一用 EMPTY_EQUIPS（含 16 类）。之前这里是旧的字面量，只有 12 类，
      // 导致套服/脸饰/眼镜/耳环这几个 key 一开始不存在。
      const [equips, setEquips] = useState({ ...EMPTY_EQUIPS });
      const [dollLoaded, setDollLoaded] = useState(false);
      const [parts, setParts] = useState({});
      const [img, setImg] = useState(null);
      const [busy, setBusy] = useState(false);
      const [err, setErr] = useState(null);
      const [pose, setPose] = useState('stand1');
      const [flip, setFlip] = useState(false);
      const [openSlot, setOpenSlot] = useState(null);
      const [q, setQ] = useState('');
      const [balloonText, setBalloonText] = useState('你好，冒险岛！');
      const [balloonStyle, setBalloonStyle] = useState('5');
      const [balloon, setBalloon] = useState(null);
      // 「应用到桌宠」的反馈状态（桌宠本来就会自动读这份配置，
      // 这个按钮是给用户一个**显式确认**：立刻推送 + 能看到结果）
      const [applyState, setApplyState] = useState(null);
      // 服务端全量搜索的结果，独立存放：清空关键词即丢，不污染本地列表
      const [searchParts, setSearchParts] = useState({});
      // 耳型（humanEar=头型自带圆耳；ear/lefEar/highlefEar 为额外耳型）。
      // 注意：这两个 state **必须在这里声明** —— 之前漏了，导致
      // `ReferenceError: earType is not defined`，整个面板崩成空白。
      const [earType, setEarType] = useState('humanEar');
      const [earTypes, setEarTypes] = useState(['humanEar']);
      // 椅子清单（2500 把，来自 Item/Install + String/Ins.img）。
      // **懒加载**：一次全拉会拖慢面板，所以默认不载入，用户点「载入椅子列表」或输入关键词才拉。
      const [chairs, setChairs] = useState([]);
      const [chairTotal, setChairTotal] = useState(0);
      const [chairQ, setChairQ] = useState('');

      // 纸娃娃配置持久化：打开面板恢复上次的样子（部位 + 耳型 + 姿势 + 翻转）。
      useEffect(() => {
        callApi('backgroundGet', {})
          .then((c) => {
            const d = c?.doll;
            if (d) {
              // 用 normalizeEquips：老配置的数字键也能正确还原（否则脸会变默认，见函数注释）
            if (d.equips) setEquips(normalizeEquips(d.equips));
              if (d.earType) setEarType(d.earType);
              if (d.pose) setPose(d.pose);
              if (d.flip !== undefined) setFlip(!!d.flip);
            }
          })
          .catch(() => {})
          .finally(() => setDollLoaded(true));
      }, []);

      // 改动即保存（载入完成前不写，免得用默认值把已存的配置覆盖掉）
      useEffect(() => {
        if (!dollLoaded) return undefined;
        const t = setTimeout(() => {
          callApi('backgroundSet', { doll: { equips, earType, pose, flip } }).catch(() => {});
        }, 400);
        return () => clearTimeout(t);
      }, [dollLoaded, equips, earType, pose, flip]);

      useEffect(() => {
        // 一次把 16 类都拉够（每类 400 条）：只拉 120 条的话，帽子里第 121 个之后
        // 的素材本地筛不出来，「芙莉莲」这类就永远搜不到（用户反馈"筛选不生效"）。
        // 每类 400 条 ≈ 6400 项，纯前端过滤，输入即时响应。
        // 启动时已预取过：直接用缓存（同步出画面，不再等网络）
        if (PREFETCH.parts) { setParts(PREFETCH.parts); return; }
        startPrefetch()
          .then(() => {
            if (PREFETCH.parts) setParts(PREFETCH.parts);
            else callApi('charParts', { limit: 400 })
              .then((d) => setParts(d.categories || {}))
              .catch((e) => setErr(String(e.message)));
          });
      }, []);

      // 搜索必须走**后端全量**：前端只过滤已拉到的那 120 条，
      // 「芙莉莲」这种排在 4000+ 条帽子中间的永远轮不到（用户实测搜不到）。
      useEffect(() => {
        const kw = (typeof q === 'string' ? q : '').trim();
        if (!kw) { setSearchParts({}); return undefined; }
        let alive = true;
        const t = setTimeout(() => {
          callApi('charParts', { q: kw, limit: 60 })
            .then((d) => {
              if (!alive) return;
              // 服务端是按**全量**（4000+ 条/类）搜的，把命中的类目整体替换进来，
              // 补齐本地 400 条窗口之外的结果（本地筛 + 全库搜，两者互补）。
              // 放**独立 state**，不并进 parts：并进去之后清空搜索框，
              // 那些项还会留在列表里（用户截图里那排高亮框，看着像"选中了没清掉"）。
              setSearchParts(d.categories || {});
            })
            .catch(() => {});
        }, 250);
        return () => { alive = false; clearTimeout(t); };
      }, [q]);

      const render = useCallback(() => {
        const list = CHAR_SLOTS.map(([k]) => equips[k]).filter(Boolean);
        setBusy(true); setErr(null);
        // 椅子单独传（它不是 Character 部件，混进 equips 会让 wzpy 找不到而整只渲染失败）
        callApi('charRender', { equips: list, pose, flip, chairId: equips.Chair || '' })
          .then(setImg)
          .catch((e) => setErr(String(e.message)))
          .finally(() => setBusy(false));
      }, [equips, pose, flip, earType]);

      useEffect(() => { render(); }, [render]);

      /**
       * 拉椅子清单。
       *
       * **搜索走服务端**：椅子 2500 把，本地没有全量（面板也不该一次吃 2500 条），
       * 所以按关键词问 `chairList {query}`。之前把椅子塞进部件的搜索框里、
       * 只筛本地 `parts`（没有 Chair 这一类）→ 永远搜不到（用户反馈「椅子无法搜索」）。
       */
      const loadChairs = useCallback((query) => {
        const keyword = String(query ?? '').trim();
        // 无关键词且已预取：直接用缓存（启动就加载好了，点开即有）
        if (!keyword && PREFETCH.chairs) {
          setChairs(PREFETCH.chairs.chairs || []);
          setChairTotal(Number(PREFETCH.chairs.total) || 0);
          return;
        }
        callApi('chairList', { query: keyword, limit: keyword ? 40 : 300 })
          .then((d) => {
            setChairs(d.chairs || []);
            setChairTotal(Number(d.total) || 0);
          })
          .catch((e) => setErr(String(e.message)));
      }, []);

      // 输入关键词 → 防抖 250ms 后问服务端
      useEffect(() => {
        if (!chairQ.trim()) return undefined;
        const t = setTimeout(() => loadChairs(chairQ), 250);
        return () => clearTimeout(t);
      }, [chairQ, loadChairs]);

      /** 已选椅子的中文名（清单里查不到就返回空串）。 */
      const chairName = useCallback(
        (id) => (chairs.find((c) => c.id === id) || {}).name || '', [chairs]);

      // 头型变了就重新取可用耳型；取不到就退回 humanEar
      useEffect(() => {
        const head = equips.Head ? [equips.Body, equips.Head].filter(Boolean) : [];
        if (!head.length) return;
        callApi('charEars', { equips: [equips.Body || '00002000', equips.Head].filter(Boolean) })
          .then((d) => {
            const list = d?.earTypes || ['humanEar'];
            setEarTypes(list);
            if (!list.includes(earType)) setEarType(list[0]);
          })
          .catch(() => {});
      }, [equips.Head, equips.Body]);

      /**
       * 把当前纸娃娃（部位 + 耳型）**显式应用**到桌宠。
       *
       * 桌宠本来每次取数据都会读 ~/.dsh/wz-assets/background.json 的 doll 字段
       * （唯一配置源，不设第二份），所以改动其实会自动生效；这个按钮解决的是
       * 「改完不确定桌宠有没有跟上」——它立刻推送一次并把桌宠的实际状态回显出来。
       */
      const applyToPet = useCallback(() => {
        setApplyState({ busy: true });
        const list = CHAR_SLOTS.map(([k]) => equips[k]).filter(Boolean);
        // 椅子也要带上（之前漏了 → 面板挂了椅子、应用后桌宠还是站着）
        callApi('petSetChara', { equips: list, earType, chairId: equips.Chair || '' })
          .then((d) => {
            const n = Object.keys(d?.pet?.anims || {}).length;
            // 通知桌宠立刻重拉：否则要手动刷新页面才看得到（用户反馈过）
            bumpPetReload();
            setApplyState({
              ok: true,
              msg: `已应用到桌宠：${list.length} 个部位${equips.Chair ? ' · 含椅子' : ''} · ${n} 个动作`,
            });
          })
          .catch((e) => setApplyState({ ok: false, msg: String(e.message || e) }));
      }, [equips, earType]);

      /** 清空某个部位（与 `pick` 的切换语义区分开：这里**无条件**置空）。 */
      const clearSlot = (slot) => {
        setEquips((prev) => ({ ...prev, [slot]: '' }));
        setOpenSlot(null);
      };

      const pick = (slot, id) => {
        setEquips((prev) => ({ ...prev, [slot]: prev[slot] === id ? '' : id }));
        // 用户口径：**保留关键词**。之前理解反了（做成选完清空），结果
        // 「芙莉莲」要反复重打五六次，用户反馈"恶心死了"。
        // 现在选完只收起展开，关键词与结果都留着，可以连着换好几件。
        setOpenSlot(null);
      };

      const makeBalloon = useCallback(() => {
        callApi('balloon', { text: balloonText, style: balloonStyle })
          .then(setBalloon)
          .catch((e) => setErr(String(e.message)));
      }, [balloonText, balloonStyle]);

      // 部位编号 → 中文名（从已拉的 parts 里查；没拉到就空）
      const nameOf = (slot, id) => {
        const hit = (parts[slot]?.items || []).find((it) => it.id === id);
        return hit?.name || '';
      };

      return el('div', { style: { display: 'flex', flexDirection: 'column', gap: 12, padding: 14, overflow: 'auto' } },
        el('div', { style: S.card },
          el('div', { style: S.section }, '纸娃娃'),
          el('div', { style: { display: 'flex', gap: 16, flexWrap: 'wrap' } },
            el('div', { style: { ...S.iconBox, minWidth: 150, minHeight: 150 } },
              img
                ? el('img', { src: img.url, alt: 'paper doll', style: { imageRendering: 'pixelated', maxWidth: 220, maxHeight: 220 } })
                : el('span', { style: { fontSize: 12, color: T.dim } }, busy ? '合成中…' : '等待合成')),
            el('div', { style: { flex: 1, minWidth: 240 } },
              el('div', { style: S.kv },
                el('div', { style: S.kvKey }, '尺寸'), el('div', { style: S.kvVal }, img ? `${img.w}×${img.h}（原始像素，未放大）` : '—'),
                el('div', { style: S.kvKey }, '部位'), el('div', { style: S.kvVal }, `${CHAR_SLOTS.filter(([k]) => equips[k]).length} 个`),
              ),
              applyState?.msg
                ? el('div', {
                    style: {
                      marginTop: 8, fontSize: 11.5,
                      color: applyState.ok ? T.dim : '#c0392b',
                    },
                  }, applyState.msg)
                : null,
              el('div', { style: { display: 'flex', gap: 8, marginTop: 10, flexWrap: 'wrap' } },
                el('button', { type: 'button', style: S.btn, onClick: () => setPose((p) => (p === 'stand1' ? 'stand2' : 'stand1')) }, `姿势 ${pose}`),
                el('button', { type: 'button', style: flip ? S.btnOn : S.btn, onClick: () => setFlip((v) => !v) }, flip ? '朝右' : '朝左'),
                el('label', { style: { display: 'flex', gap: 6, alignItems: 'center', fontSize: 12, color: T.dim } },
                  '耳朵',
                  el('select', {
                    style: S.select, value: earType,
                    onChange: (e) => setEarType(e.target.value),
                    title: 'humanEar=头型自带圆耳；其余为额外耳型',
                  }, earTypes.map((t) => el('option', { key: t, value: t },
                    t === 'humanEar' ? '自带圆耳' : t === 'ear' ? '尖耳' : t === 'lefEar' ? '精灵长耳' : t === 'highlefEar' ? '高位精灵耳' : t)))),
                el('button', {
                  type: 'button', style: S.btn,
                  onClick: () => setEquips({ Body: '00002000', Head: '00012000', Hair: '00030000', Face: '00020000', Coat: '', Pants: '', Shoes: '', Weapon: '', Cap: '', Cape: '', Glove: '', Shield: '' }),
                }, '重置'),
                el('button', {
                  type: 'button',
                  style: applyState?.ok ? S.btnOn : S.btn,
                  disabled: applyState?.busy,
                  title: '把当前部位与耳型立刻推给桌面宠物（桌宠本来就会自动读这份配置）',
                  onClick: applyToPet,
                }, applyState?.busy ? '应用中…' : '应用到桌宠')),
              err ? el('div', { style: { ...S.err, marginTop: 8 } }, err) : null,
            ),
          ),
        ),
        el('div', { style: S.card },
          el('div', { style: S.section }, '聊天气泡（UI/ChatBalloon.img 九宫格）'),
          el('div', { style: { display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' } },
            el('input', {
              style: { ...S.input, flex: '1 1 220px' }, placeholder: '输入一句话，回车出气泡',
              value: balloonText, onChange: (e) => setBalloonText(e.target.value),
              onKeyDown: (e) => { if (e.key === 'Enter') makeBalloon(); },
            }),
            el('button', { type: 'button', style: S.btn, onClick: makeBalloon }, '出气泡'),
            el('button', { type: 'button', style: S.btn, onClick: () => setBalloonStyle((v) => (v === '5' ? '0' : '5')) }, `样式 ${balloonStyle}`)),
          balloon
            ? el('div', { style: { marginTop: 10 } },
                el('img', { src: balloon.url, alt: 'balloon', style: { imageRendering: 'pixelated' } }),
                // 字段名与 worker 的新返回对齐（旧字段 `clr` 已废弃 → 会显示 undefined）
                el('div', { style: { fontSize: 11.5, color: T.dim, marginTop: 4 } },
                  `${balloon.w}×${balloon.h} · 样式 ${balloon.style} · `
                  + `${balloon.lines?.length ?? 1} 行 · 文字色 rgb(${(balloon.textColor || []).slice(0, 3).join(',') || '?'})`))
            : null,
        ),
        el('div', { style: S.card },
          el('div', { style: S.section }, '部位（点编号挑，或用上面的搜索框全库筛）'),
          // 全局搜索：**不用先点开某个部位**就能筛。
          // 之前搜索框只在"点开部位"之后才出现，用户直接输「芙莉莲」毫无反应（反馈"筛选不生效"）。
          el('div', { style: { marginBottom: 8 } },
            el('input', {
              style: { ...S.input, width: '100%' },
              placeholder: '全库搜索：中文名或编号（如 芙莉莲 / 1332020），所有部位一起筛',
              value: q,
              onChange: (e) => { setQ(e.target.value); if (e.target.value) setOpenSlot(null); },
            }),
            q
              ? el('div', { style: { fontSize: 11.5, color: T.dim, marginTop: 4 } }, (() => {
                  const hits = CHAR_SLOTS
                    .map(([slot]) => (parts[slot]?.items || []).filter((it) => it.id.includes(q) || String(it.name || '').includes(q)))
                    .filter((list) => list.length);
                  const total = hits.reduce((n, l) => n + l.length, 0);
                  return total
                    ? `命中 ${total} 项，分布在 ${hits.length} 个部位`
                    : '没有命中（换个关键词；注意只有已加载的部位能搜到，滚动到该部位会自动加载）';
                })())
              : null),
          el('div', { style: { display: 'flex', flexDirection: 'column', gap: 6 } },
            CHAR_SLOTS.map(([slot, label]) => {
              // 椅子不在 Character 部件表里（它来自 Item/Install），候选单独一个来源
              const all = slot === 'Chair' ? chairs : (parts[slot]?.items || []);
              const localHits = q
                ? all.filter((it) => it.id.includes(q) || String(it.name || '').includes(q))
                : [];
              // 服务端命中补进来并去重（本地只有 400 条/类，芙莉莲的发型/鞋/衣服/武器
              // 都排在窗口之外，只靠本地筛会漏）
              const seenIds = new Set(localHits.map((it) => it.id));
              const srvHits = q
                ? (searchParts[slot]?.items || []).filter((it) => !seenIds.has(it.id))
                : [];
              const list = q
                ? [...localHits, ...srvHits]
                : (openSlot === slot ? all.slice(0, 200) : []);
              return el('div', { key: slot, style: { display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' } },
                el('span', { style: { ...S.kvKey, minWidth: 44 } }, label),
                el('span', {
                  style: { ...S.chip, fontFamily: 'ui-monospace, monospace' },
                  title: '点一下展开/收起该部位的列表',
                  onClick: () => setOpenSlot(openSlot === slot ? null : slot),
                }, equips[slot] ? `${equips[slot]}${nameOf(slot, equips[slot]) ? ` ${nameOf(slot, equips[slot])}` : ''}` : '（无）'),
                slot === 'Chair'
                  ? el('span', { style: { fontSize: 11, color: T.dim } }, `共 ${chairs.length || '…'} 把`)
                  : parts[slot]
                  ? el('span', { style: { fontSize: 11, color: T.dim } },
                      q ? `命中 ${list.length}/${parts[slot].total}` : `共 ${parts[slot].total}`)
                  : null,
                // 「清空」：把该部位的选择去掉（此前只能换成别的编号，无法恢复「无」）
                equips[slot]
                  ? el('span', {
                      style: { ...S.chip, fontSize: 11, color: T.dim },
                      title: `清空「${label}」的选择`,
                      onClick: () => clearSlot(slot),
                    }, '清空')
                  : null,
                (openSlot === slot || (q && list.length))
                  ? el('div', { style: { width: '100%', marginTop: 6 } },
                      el('div', { style: { display: 'flex', flexWrap: 'wrap', gap: 4, maxHeight: 190, overflow: 'auto' } },
                        list.map((it) => el('span', {
                          key: it.id,
                          style: equips[slot] === it.id ? S.chipOn : S.chip,
                          title: `${it.name || '（无名字）'} · ${it.id}`,
                          onClick: () => pick(slot, it.id),
                        }, it.name || it.short || it.id))))
                  : null)
              ;
            })),
            // ── 椅子（独立在最后）──────────────────────────────────────
            // 椅子不是 Character 部件（在 `Item/Install`），所以有**自己的搜索框**：
            // 部件的「全库搜索」只筛 Character 的 16 类，管不到椅子；
            // 而且椅子有 2500 把，共用搜索框会把部件结果淹没。
            el('div', {
              style: {
                display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap',
                marginTop: 10, paddingTop: 10,
                borderTop: '1px solid var(--dsw-alias-border-l1)',
              },
            },
              el('span', { style: { ...S.kvKey, minWidth: 44 } }, '椅子'),
              el('span', {
                style: { ...S.chip, fontFamily: 'ui-monospace, monospace' },
                title: '点一下展开/收起椅子列表',
                onClick: () => setOpenSlot(openSlot === 'Chair' ? null : 'Chair'),
              }, equips.Chair
                ? `${equips.Chair}${chairName(equips.Chair) ? ` ${chairName(equips.Chair)}` : ''}`
                : '（无）'),
              el('span', { style: { fontSize: 11, color: T.dim } },
                chairTotal ? `共 ${chairTotal} 把` : '…'),
              equips.Chair
                ? el('span', {
                    style: { ...S.chip, fontSize: 11, color: T.dim },
                    title: '清空「椅子」的选择',
                    onClick: () => clearSlot('Chair'),
                  }, '清空')
                : null,
              el('input', {
                style: { ...S.input, flex: '1 1 260px', minWidth: 200 },
                placeholder: '搜椅子名（如 花田 / 王座 / 靠垫）',
                value: chairQ,
                onChange: (e) => setChairQ(e.target.value),
              }),
              !chairs.length && !chairQ
                ? el('button', {
                    type: 'button', style: S.btn,
                    onClick: () => loadChairs(''),
                  }, '载入椅子列表')
                : null,
              (openSlot === 'Chair' || (chairQ && chairs.length))
                ? el('div', {
                    style: {
                      width: '100%', display: 'flex', flexWrap: 'wrap', gap: 4,
                      maxHeight: 190, overflow: 'auto', marginTop: 6,
                    },
                  }, chairs.length
                    ? chairs.map((it) => el('span', {
                        key: it.id,
                        style: equips.Chair === it.id ? S.chipOn : S.chip,
                        title: `${it.name || '（无名字）'} · ${it.id}`,
                        onClick: () => pick('Chair', it.id),
                      }, it.name || it.id))
                    : el('span', { style: { fontSize: 11, color: T.dim, padding: '4px 0' } },
                        chairQ ? '没有匹配的椅子' : '点「载入椅子列表」或直接输入关键词'))
                : null)
        ),
      );
    }

    /**
     * 资源更新（cmsdl）：状态 + 进度 + 日志。
     * 后端接口早就有（updateStatus/updateProbe/updatePlan/updateResources/updateFetchTool/updateCancel），
     * 这里只是把面板补上——之前一直"看不到 cmsdl"就是因为缺这一块。
     */
    function ResourceUpdate() {
      const [st, setSt] = useState(null);
      const [err, setErr] = useState(null);
      const [msg, setMsg] = useState(null);
      const [region, setRegion] = useState('cms');
      // 游戏客户端目录（cmsdl 的 CLIENT_DIR 位置参数）。存到 background.json，
      // 下次打开还在。WZ 会下到 <客户端>/mxd/Data(CMS) 或 <客户端>/Data(TMS)。
      const [clientDir, setClientDir] = useState('');
      const [allowDataDir, setAllowDataDir] = useState(false);
      const [busy, setBusy] = useState(false);

      const refresh = useCallback(() => {
        callApi('updateStatus', {}).then(setSt).catch((e) => setErr(String(e.message)));
      }, []);

      // 客户端目录从背景配置里读（复用同一个配置文件，不再多开一个）
      useEffect(() => {
        callApi('backgroundGet', {})
          .then((c) => { if (c && typeof c.clientDir === 'string') setClientDir(c.clientDir); })
          .catch(() => {});
      }, []);
      const saveClientDir = (v) => {
        setClientDir(v);
        callApi('backgroundSet', { clientDir: v }).catch(() => {});
      };
      // 实际落盘位置（按地区推导，与 worker 的判定保持一致）
      const wzLanding = clientDir
        ? `${clientDir.replace(/\/+$/, '')}/${region === 'cms' ? 'mxd/Data' : 'Data'}`
        : (st?.defaultTargetDir || '—');

      useEffect(() => { refresh(); }, [refresh]);

      // 任务在跑就轮询（1.5s）；页面切走（组件卸载）自动停
      useEffect(() => {
        if (!st?.running) return undefined;
        const timer = setInterval(refresh, 1500);
        return () => clearInterval(timer);
      }, [st?.running, refresh]);

      const act = async (cmd, args, label) => {
        setBusy(true); setMsg(null); setErr(null);
        try {
          const d = await callApi(cmd, args);
          if (d?.blocked?.length) setMsg(`${label}被拦下：${d.blocked.join('；')}`);
          else setMsg(`${label}：${d?.started === false ? '未启动' : '已触发'}`);
          refresh();
        } catch (e) {
          setErr(String(e.message));
        } finally {
          setBusy(false);
        }
      };

      if (!st && !err) return el('div', { style: S.hint }, '读取 cmsdl 状态…');
      const t = st?.task || {};
      const pct = Number.isFinite(t.percent) ? Math.max(0, Math.min(100, t.percent)) : null;
      const target = allowDataDir ? st?.dataDir : st?.defaultTargetDir;

      return el('div', { style: { display: 'flex', flexDirection: 'column', gap: 12, padding: 14, overflow: 'auto' } },
        err ? el('div', { style: S.err }, err) : null,

        el('div', { style: S.card },
          el('div', { style: S.section }, 'cmsdl 资源更新'),
          el('div', { style: S.kv },
            el('div', { style: S.kvKey }, '工具'),
            el('div', { style: S.kvVal }, st?.tool
              ? `已就位 · ${st.toolPath}`
              : '未就位（可先「下载 cmsdl 工具」）'),
            el('div', { style: S.kvKey }, '平台'), el('div', { style: S.kvVal }, st?.platform || '—'),
            el('div', { style: S.kvKey }, '发布版'), el('div', { style: S.kvVal }, st?.releaseTag || '—'),
            el('div', { style: S.kvKey }, '客户端'), el('div', { style: S.kvVal }, clientDir || '（未设置）'),
            el('div', { style: S.kvKey }, '下载到'), el('div', { style: S.kvVal },
              wzLanding || target || '—',
              // cmsdl 既没有配置文件也没有探测命令，客户端目录只能我们存；
              // 探测到的候选给个一键采用
              (st?.detected || []).length
                ? el('span', {
                    style: { marginLeft: 8, cursor: 'pointer', textDecoration: 'underline', color: T.brand },
                    title: (st.detected || []).map((d) => d.dir).join('\n'),
                    onClick: () => { saveClientDir(st.detected[0].dir); setMsg(`客户端已设为 ${st.detected[0].dir}`); },
                  }, `用探测到的：${st.detected[0].dir}`)
                : null),
            el('div', { style: S.kvKey }, '数据源'), el('div', { style: S.kvVal }, st?.dataDir || '—'),
          ),
          el('div', { style: { marginTop: 10 } },
            el('div', { style: { fontSize: 12, color: T.dim, marginBottom: 4 } }, '游戏客户端目录（下载 / 补丁的目标）'),
            el('div', { style: { display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' } },
              el('input', {
                style: { ...S.input, flex: '1 1 320px' },
                placeholder: '例如 /Volumes/SSD/mxd（留空 = 只下到 cmsdl 目录）',
                value: clientDir, onChange: (e) => saveClientDir(e.target.value),
              }),
              el('button', { type: 'button', style: S.btn, onClick: () => saveClientDir('') }, '清空')),
            el('div', { style: { fontSize: 11.5, color: T.dim, marginTop: 4 } },
              `WZ 实际落盘：${wzLanding}`)),
          el('div', { style: { display: 'flex', gap: 8, flexWrap: 'wrap', marginTop: 10, alignItems: 'center' } },
            el('button', { type: 'button', style: S.btn, disabled: busy, onClick: () => act('updateProbe', {}, '探测') }, '探测工具'),
            el('button', { type: 'button', style: S.btn, disabled: busy, onClick: () => act('updateFetchTool', {}, '下载工具') }, '下载 cmsdl 工具'),
            el('select', { style: S.select, value: region, onChange: (e) => setRegion(e.target.value) },
              [['cms', '国服 cms'], ['tms', '台服 tms']].map(([v, l]) => el('option', { key: v, value: v }, l))),
            el('button', {
              type: 'button', style: S.btn, disabled: busy,
              onClick: () => act('updatePlan', {
                region, mode: 'download', target_dir: target, client_dir: clientDir,
                download_wz_only: true,
              }, '预览命令'),
            }, '预览命令'),
            el('button', {
              type: 'button', style: S.btnOn, disabled: busy || st?.running,
              onClick: () => act('updateResources', {
                region, mode: 'download', target_dir: target, client_dir: clientDir,
                download_wz_only: true, allow_data_dir: allowDataDir,
              }, '开始更新'),
            }, st?.running ? '更新中…' : '开始更新'),
            st?.running
              ? el('button', { type: 'button', style: S.btn, onClick: () => act('updateCancel', {}, '取消') }, '取消')
              : null,
          ),
          el('label', { style: { display: 'flex', gap: 6, alignItems: 'center', marginTop: 8, fontSize: 12, color: T.dim } },
            el('input', { type: 'checkbox', checked: allowDataDir, onChange: (e) => setAllowDataDir(e.target.checked) }),
            '允许写入 WZ 数据源目录（默认关闭；勾上会覆盖/新增数据源里的文件）'),
          msg ? el('div', { style: { fontSize: 12, color: T.dim, marginTop: 8 } }, msg) : null,
        ),

        el('div', { style: S.card },
          el('div', { style: S.section }, '任务'),
          t.command
            ? el('div', { style: { fontFamily: 'ui-monospace, monospace', fontSize: 11.5, color: T.dim, marginBottom: 8, wordBreak: 'break-all' } }, t.command)
            : el('div', { style: { fontSize: 12.5, color: T.dim } }, '还没有任务。点「预览命令」可以看到将执行的完整命令。'),
          t.phase
            ? el('div', { style: { fontSize: 12.5, marginBottom: 6 } },
                `${t.phase}${t.kind ? ` · ${t.kind}` : ''}${t.region ? ` · ${t.region}` : ''}` +
                (t.exitCode !== null && t.exitCode !== undefined ? ` · 退出码 ${t.exitCode}` : ''))
            : null,
          pct !== null
            ? el('div', null,
                el('div', { style: { height: 6, borderRadius: 3, background: T.layer2, overflow: 'hidden' } },
                  el('div', { style: { width: `${pct}%`, height: '100%', background: T.brand } })),
                el('div', { style: { fontSize: 11.5, color: T.dim, marginTop: 4 } },
                  `${pct.toFixed(1)}% · ${t.bytes || 0} / ${t.totalBytes || '?'} 字节`))
            : null,
          (t.blocked?.length || t.warnings?.length)
            ? el('div', { style: { marginTop: 8, fontSize: 12 } },
                ...(t.blocked || []).map((b, i) => el('div', { key: `b${i}`, style: { color: T.error } }, `✗ ${b}`)),
                ...(t.warnings || []).map((wn, i) => el('div', { key: `w${i}`, style: { color: T.dim } }, `! ${wn}`)))
            : null,
          (t.log?.length)
            ? el('pre', {
                style: {
                  marginTop: 8, maxHeight: 220, overflow: 'auto', fontSize: 11,
                  background: T.layer2, border: `1px solid ${T.border}`, borderRadius: 6, padding: 8,
                  whiteSpace: 'pre-wrap', wordBreak: 'break-all', color: T.text,
                },
              }, t.log.slice(-40).join('\n'))
            : null,
          el('div', { style: { marginTop: 8, fontSize: 11.5, color: T.dim } },
            '本机若连不上游戏 CDN，会在这里看到真实报错文本——那是网络问题，不是插件坏了。'),
        ),
      );
    }

    /** 背景设置：选一张冒险岛地图当整个 DSH 的背景（相机按 MiniPet 规则漂移）。 */
    function BackgroundSettings() {
      const [config, setConfig] = useState(null);
      const [query, setQuery] = useState('');
      const [maps, setMaps] = useState([]);
      const [busy, setBusy] = useState(false);
      const [msg, setMsg] = useState(null);
      // 当前是否处于「无毛玻璃」。抽成命名常量而不是到处写 `config.blur === false`：
      // 一是可读，二是避免把取反表达式内联进 patch 实参（那会被打包后错误求值）。
      const noFrostConf = config?.blur === false;

      useEffect(() => {
        callApi('backgroundGet').then(setConfig).catch((e) => setMsg(String(e.message)));
      }, []);
      useEffect(() => {
        let alive = true;
        const q = query.trim();
        // 没输关键词就先给「经典户外地图」，而不是名录里前 40 条（那些多是室内/剧情图）
        // 无关键词时用启动预取的精选地图（点开背景 tab 即有列表）
        if (!q && PREFETCH.featured) { setMaps(PREFETCH.featured); return; }
        const p = q ? callApi('mapList', { query: q, limit: 60 }) : callApi('mapFeatured', {});
        p.then((d) => alive && setMaps(d.maps || []))
         .catch((e) => alive && setMsg(String(e.message)));
        return () => { alive = false; };
      }, [query]);

      const patch = (next) => {
        setConfig((prev) => ({ ...(prev || {}), ...next }));
        bgBridge.set(next);            // 立刻反映到背景层
        callApi('backgroundSet', next).catch((e) => setMsg(String(e.message)));
      };

      // 换一张（随机；优先经典地图池，并排除最近用过的，避免连着重复）
      const shuffleMap = useCallback(async () => {
        setBusy(true); setMsg(null);
        try {
          const d = await callApi('mapRandom', {
            featured: config?.featured !== false,
            exclude: config?.recent || [],
          });
          // 最近 20 张都记着（用户要求「最近二十次随机的地图完全不能重复」）
          const recent = [d.id, ...(config?.recent || [])].slice(0, 20);
          patch({ mapId: d.id, mapName: d.name, enabled: true, recent });
          setMsg(`已换到「${d.name}」（${d.street || '—'}）`);
        } catch (e) {
          setMsg(String(e.message));
        } finally {
          setBusy(false);
        }
      }, [config?.featured, config?.recent]);

      // 自动轮换：每 intervalMin 分钟换一张
      useEffect(() => {
        const mins = Number(config?.intervalMin ?? 0);
        if (!config?.enabled || !mins) return undefined;
        const timer = setInterval(() => { shuffleMap(); }, mins * 60_000);
        return () => clearInterval(timer);
      }, [config?.enabled, config?.intervalMin, shuffleMap]);

      if (!config) return el('div', { style: S.hint }, '读取背景配置…');

      return el('div', { style: { display: 'flex', flexDirection: 'column', gap: 12, padding: 14, overflow: 'auto' } },
        el('div', { style: S.card },
          el('div', { style: S.section }, 'DSH 背景：冒险岛地图'),
          el('div', { style: { fontSize: 12.5, color: T.dim, lineHeight: 1.7, marginBottom: 10 } },
            '开启后整个 DSH 背景变成选定的地图，镜头按 MiniPet 的 ClampCamera 规则在 VR 边界内往返漂移' +
            '（地图比视口窄时自动居中）。配置写在 ', el('code', null, config.configPath || '~/.dsh/wz-assets/background.json'), '。'),
          el('div', { style: { display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' } },
            el('button', {
              type: 'button', style: config.enabled ? S.btnOn : S.btn,
              onClick: () => patch({ enabled: !config.enabled }),
            }, config.enabled ? '背景已开' : '背景已关'),
            el('span', { style: { fontSize: 12, color: T.dim } },
              config.mapId ? `当前：${config.mapName || '（未知）'} · ${config.mapId}` : '未选地图'),
            el('button', { type: 'button', style: S.btn, disabled: busy, onClick: shuffleMap },
              busy ? '换图中…' : '随机换一张'),
            // 「去除毛玻璃」：默认开（=保留磨砂）。点一下把磨砂层与模糊一起去掉，
            // 地图就是原像素、不被模糊 —— 项目里地图最清楚的状态。
            el('button', {
              type: 'button',
              style: noFrostConf ? S.btnOn : S.btn,
              title: '去掉面板底层的模糊与白纱，地图按原始像素显示（最清楚）',
              // 先算出目标值再传：不要在 patch 的实参里写内联取反表达式 ——
              // 实测那样的表达式在打包后会被错误求值（点一次把 blur 又写成 false，
              // 表现为「点了没反应」，详见本文件里 noFrostConf 的注释）。
              onClick: () => patch({ blur: noFrostConf ? true : false }),
            }, noFrostConf ? '无毛玻璃' : '毛玻璃'),
            el('label', {
              style: { fontSize: 12, color: T.dim, display: 'flex', gap: 6, alignItems: 'center' },
              // 这个滑块被误解过两次（用户以为往左=更透，其实往左=更实、地图更看不见）。
              // 现在标注两端含义，并把「地图不可见」一侧显式说出来。
              title: '往右 = 地图更明显；拉到最左 = 地图完全看不见。',
            },
              '地图清晰度',
              el('span', { style: { fontSize: 11, color: T.dim } }, '看不清'),
              el('input', {
                type: 'range', min: 0, max: 100, step: 2,
                style: { width: 110 },
                value: String(config.bgVisibility ?? 60),
                onChange: (e) => patch({ bgVisibility: Number(e.target.value) }),
              }),
              el('span', { style: { fontSize: 11, color: T.dim } }, '很清楚'),
              el('span', { style: { minWidth: 34 } }, `${config.bgVisibility ?? 60}%`)),
            el('label', { style: { fontSize: 12, color: T.dim, display: 'flex', gap: 6, alignItems: 'center' } },
              '一轮时长',
              el('select', {
                style: S.select, value: String(config.periodSec ?? 240),
                onChange: (e) => patch({ periodSec: Number(e.target.value) }),
              }, [60, 120, 180, 240, 360, 600, 900].map((v) => el('option', { key: v, value: v },
                v < 60 ? `${v} 秒` : `${Math.round(v / 60)} 分钟`)))),
            el('label', { style: { fontSize: 12, color: T.dim, display: 'flex', gap: 6, alignItems: 'center' } },
              '到头后停顿',
              el('select', {
                style: S.select, value: String(config.switchDelayMs ?? 12000),
                onChange: (e) => patch({ switchDelayMs: Number(e.target.value) }),
              }, [1000, 3000, 5000, 8000, 12000, 20000, 30000, 60000].map((v) => el('option', { key: v, value: v },
                v < 60000 ? `${v / 1000} 秒` : '1 分钟')))),
            el('label', {
              style: { fontSize: 12, color: T.dim, display: 'flex', gap: 6, alignItems: 'center' },
              title: '换图时的交叉淡入时长；选「关」就是硬切',
            },
              '换图过渡',
              el('select', {
                style: S.select, value: String(config.mapFadeMs ?? 1200),
                onChange: (e) => patch({ mapFadeMs: Number(e.target.value) }),
              }, [0, 400, 800, 1200, 2000, 3000].map((v) => el('option', { key: v, value: v },
                v === 0 ? '关' : `${(v / 1000).toFixed(1)} 秒`)))),
            el('label', { style: { fontSize: 12, color: T.dim, display: 'flex', gap: 6, alignItems: 'center' } },
              '自动轮换',
              el('select', {
                style: S.select, value: String(config.intervalMin ?? 0),
                onChange: (e) => patch({ intervalMin: Number(e.target.value) }),
              }, [0, 1, 3, 5, 10, 30].map((m) => el('option', { key: m, value: m }, m === 0 ? '关' : `${m} 分钟`)))),
            el('span', { style: { marginLeft: 'auto' } }),
            el('label', { style: { fontSize: 12, color: T.dim, display: 'flex', gap: 6, alignItems: 'center' } },
              '镜头速度',
              el('input', {
                type: 'range', min: 0.2, max: 4, step: 0.1, value: config.speed ?? 1,
                onChange: (e) => patch({ speed: Number(e.target.value) }),
              }),
              el('span', null, `${(config.speed ?? 1).toFixed(1)}×`)),
            el('label', { style: { fontSize: 12, color: T.dim, display: 'flex', gap: 6, alignItems: 'center' } },
              '压暗',
              el('input', {
                type: 'range', min: 0, max: 0.6, step: 0.05, value: config.scrim ?? 0.12,
                onChange: (e) => patch({ scrim: Number(e.target.value) }),
              })),
            el('label', { style: { fontSize: 12, color: T.dim, display: 'flex', gap: 6, alignItems: 'center' } },
              '缩放',
              el('input', {
                type: 'range', min: 0.6, max: 2.2, step: 0.1, value: config.zoom ?? 1,
                onChange: (e) => patch({ zoom: Number(e.target.value) }),
              }),
              el('span', null, `${(config.zoom ?? 1).toFixed(1)}×`)),
          ),
          el('div', { style: { display: 'flex', gap: 12, alignItems: 'center', marginTop: 10, flexWrap: 'wrap' } },
            el('span', { style: { fontSize: 12, color: T.dim } }, '镜头'),
            ['auto', 'manual', 'off'].map((m) => el('button', {
              key: m, type: 'button',
              style: (config.motion || 'auto') === m ? S.chipOn : S.chip,
              onClick: () => patch({ motion: m }),
            }, m === 'auto' ? '自动漂移' : m === 'manual' ? '手动' : '冻结')),
            el('input', {
              type: 'range', min: 0, max: 1, step: 0.01, value: config.pos ?? 0.5,
              disabled: (config.motion || 'auto') === 'auto',
              style: { flex: 1, minWidth: 160 },
              onChange: (e) => patch({ motion: 'manual', pos: Number(e.target.value) }),
            }),
            el('span', { style: { fontSize: 11, color: T.dim } }, `${Math.round((config.pos ?? 0.5) * 100)}%`),
          ),
          msg ? el('div', { style: { ...S.err, marginTop: 10 } }, msg) : null,
        ),
        el('div', { style: S.card },
          el('div', { style: S.section }, '选地图'),
          el('input', {
            style: { ...S.input, width: '100%', marginBottom: 10 },
            placeholder: '搜中文名/街区名：射手、魔法密林、天空之城、自由市场…（留空显示经典地图）',
            value: query, onChange: (e) => setQuery(e.target.value),
          }),
          el('div', { style: { display: 'flex', flexDirection: 'column', gap: 4, maxHeight: 380, overflow: 'auto' } },
            maps.map((m) => el('div', {
              key: m.id,
              style: m.id === config.mapId ? S.rowOn : S.row,
              onClick: () => patch({ mapId: m.id, mapName: m.name, enabled: true }),
            },
              el('span', { style: { fontVariantNumeric: 'tabular-nums', color: T.dim, minWidth: 82 } }, m.id),
              el('span', { style: { color: T.text } }, m.name || '—'),
              el('span', { style: { marginLeft: 'auto', fontSize: 11, color: T.dim } }, m.street || ''))),
            maps.length === 0 ? el('div', { style: S.hint }, '没有匹配的地图') : null,
          ),
        ),
      );
    }

    function WzPanel() {
      // 背景配置经 bgBridge 与 shell.overlay 里的背景层通信
      const [packs, setPacks] = useState(null);
      const [error, setError] = useState(null);
      const [mode, setMode] = useState('browse'); // browse | search
      // 默认停在**数据源根目录**（列出全部分类），不是直接钻进 Mob/。
      // 用户口径：「切换浏览默认不是在根目录」——之前一进来就是 Mob 的文件列表。
      const [pack, setPack] = useState('');
      const [path, setPath] = useState('');
      const [query, setQuery] = useState('');
      const [kind, setKind] = useState('all');
      const [target, setTarget] = useState(null);
      const [catalogOpen, setCatalogOpen] = useState(false);
      const [atlas, setAtlas] = useState(null); // {kind, id} —— 图鉴详情目标

      useEffect(() => {
        // 启动时已预取过 packs
        if (PREFETCH.packs) { setPacks(PREFETCH.packs); return; }
        startPrefetch().then(() => {
          if (PREFETCH.packs) setPacks(PREFETCH.packs);
          else callApi('packs').then(setPacks).catch((e) => setError(String(e.message)));
        });
      }, []);

      const onPick = useCallback((picked) => {
        if (typeof picked === 'string') { setTarget(picked); setMode('browse'); return; }
        // 搜索结果：mob/npc/eqp 直接进图鉴详情页
        if (picked.kind === 'mob' || picked.kind === 'npc' || picked.kind === 'eqp') {
          setAtlas({ kind: picked.kind, id: picked.id });
          setMode('atlas');
        } else {
          setAtlas(null);
          setTarget(null);
        }
      }, []);

      const content = useMemo(() => {
        if (mode === 'resource') {
          return el('div', { style: S.list },
            el('div', { style: { padding: '6px 8px', fontSize: 12, color: T.dim, lineHeight: 1.7 } },
              el('div', null, '资源更新在右侧：cmsdl 状态、进度、日志。'),
              el('div', { style: { marginTop: 8 } }, '默认只下载到 ~/.dsh/wz-assets/cmsdl/；要写进 WZ 数据源目录必须显式勾选。')));
        }
        if (mode === 'paperdoll') {
          return el('div', { style: S.list },
            el('div', { style: { padding: '6px 8px', fontSize: 12, color: T.dim, lineHeight: 1.7 } },
              el('div', null, '纸娃娃在右侧：挑身体/发型/脸/装备，实时合成角色帧。'),
              el('div', { style: { marginTop: 8 } },
                '合成语义与 mapleStoryMiniPet 的 CharaSim 一致（按 zmap 排层），图像按原始像素输出，不放大。')));
        }
        if (mode === 'background') {
          return el('div', { style: S.list },
            el('div', { style: { padding: '6px 8px', fontSize: 12, color: T.dim, lineHeight: 1.7 } },
              '背景设置都在右侧。这里只放「地图」快捷入口，方便边看边换。'));
        }
        if (mode === 'atlas') {
          return el('div', { style: S.list },
            el('div', { style: { padding: '4px 6px 10px' } },
              el('div', { style: S.section }, '类别'),
              el('div', { style: { display: 'flex', gap: 6, marginBottom: 10 } },
                ['mob', 'npc', 'eqp'].map((k) => el('button', {
                  key: k, type: 'button',
                  style: atlas?.kind === k ? S.chipOn : S.chip,
                  onClick: () => setAtlas({ kind: k, id: atlas?.id || '' }),
                }, k === 'mob' ? '怪物' : k === 'npc' ? 'NPC' : '装备'))),
              el('div', { style: S.section }, '编号'),
              el('input', {
                style: { ...S.input, width: '100%' }, placeholder: '输入编号后回车，如 1210102 / 1033113 / 1332020',
                defaultValue: atlas?.id || '',
                onKeyDown: (e) => {
                  if (e.key === 'Enter') setAtlas({ kind: atlas?.kind || 'mob', id: e.currentTarget.value.trim() });
                },
              }),
            ),
            el('div', { style: { padding: '2px 8px', fontSize: 11, color: T.dim } }, '也可以从「搜索」里点结果直接进图鉴'),
          );
        }
        if (mode === 'search') {
          return query.trim()
            ? el(SearchList, { query, kind, onPick })
            : el('div', { style: S.hint }, '输入中文名或编号搜索，例如「花蘑菇」「路西德」「1332020」。');
        }
        if (catalogOpen) {
          return el('div', { style: S.list },
            (packs?.packs || []).map((p) => el('div', {
              key: p.pack, style: p.pack === pack ? S.rowOn : S.row,
              onClick: () => { setPack(p.pack); setPath(''); setCatalogOpen(false); },
            },
              el('span', { style: { color: T.brand } }, '▣'),
              el('span', null, p.pack),
              el('span', { style: S.tag }, `${p.volumes.length} 卷`),
            )));
        }
        return el(TreeList, {
          pack, path,
          onOpen: (p, next) => { setPack(p); setPath(next); },
          onPickPack: (name) => { setPack(name); setPath(''); },
          onPick,
        });
      }, [mode, query, kind, catalogOpen, packs, pack, path, onPick, atlas]);

      // 面板在渲染 → 在 **DOM 上**打标记（比 window 变量可靠：面板只要挂着就在）。
      // 背景层轮询这个属性，把中/右栏改成实底。
      if (typeof window !== 'undefined') window.__msWzPanelAt = Date.now();

      return el(
        'div',
        { 'data-ms-wz-panel': 'on', style: S.root },
        el('div', { style: S.bar },
          el('strong', { style: { fontSize: 14, marginRight: 4 } }, '冒险岛资源'),
          el('button', { type: 'button', style: mode === 'browse' ? S.btnOn : S.btn, onClick: () => setMode('browse') }, '浏览'),
          el('button', { type: 'button', style: mode === 'search' ? S.btnOn : S.btn, onClick: () => setMode('search') }, '搜索'),
          el('button', {
            type: 'button', style: mode === 'atlas' ? S.btnOn : S.btn,
            onClick: () => { setMode('atlas'); if (!atlas) setAtlas({ kind: 'mob', id: '1210102' }); },
          }, '图鉴'),
          el('button', {
            type: 'button', style: mode === 'paperdoll' ? S.btnOn : S.btn,
            onClick: () => setMode('paperdoll'),
          }, '纸娃娃'),
          el('button', {
            type: 'button', style: mode === 'background' ? S.btnOn : S.btn,
            onClick: () => setMode('background'),
          }, '背景'),
          el('button', {
            type: 'button', style: mode === 'resource' ? S.btnOn : S.btn,
            onClick: () => setMode('resource'),
          }, '资源'),
          mode === 'search'
            ? [
                el('input', {
                  key: 'q', style: S.input, placeholder: '中文名 / 编号，回车搜索', value: query,
                  onChange: (e) => setQuery(e.target.value),
                  onKeyDown: (e) => { if (e.key === 'Enter') setQuery(e.currentTarget.value); },
                }),
                el('select', { key: 'k', style: S.select, value: kind, onChange: (e) => setKind(e.target.value) },
                  ['all', 'mob', 'npc', 'eqp', 'map', 'skill', 'consume', 'etc', 'cash', 'ins'].map((k) =>
                    el('option', { key: k, value: k }, k === 'all' ? '全部类别' : k))),
              ]
            : el('span', { style: { fontSize: 12, color: T.dim } }, `${pack}/  ${packs ? '' : '读取数据源…'}`),
          el('span', { style: { marginLeft: 'auto' } }),
          // 顶部工具条右侧不再显示 `${region} v${version}`（用户要求去掉版本号）
        ),
        error ? el('div', { style: S.err }, error) : null,
        el('div', { style: S.body },
          el('div', { style: mode === 'atlas' || mode === 'background' || mode === 'paperdoll' || mode === 'resource' ? { ...S.left, width: 260 } : S.left },
            el('div', { style: S.listHead },
              mode === 'browse'
                ? el('span', null,
                    el('span', { style: { cursor: 'pointer', color: catalogOpen ? T.brand : T.text }, onClick: () => setCatalogOpen((v) => !v) }, catalogOpen ? '▾ 分类' : '▸ 分类'),
                    el('span', { style: { margin: '0 6px', color: T.dim } }, '/'),
                    el('span', { onClick: () => { setCatalogOpen(false); setPath(''); }, style: { cursor: 'pointer' } }, pack),
                    path ? el('span', { style: { color: T.dim } }, `/${path}`) : null)
                : el('span', null, '搜索结果')),
            content,
          ),
          mode === 'resource'
            ? el(ResourceUpdate)
            : mode === 'paperdoll'
            ? el(PaperDoll)
            : mode === 'background'
            ? el(BackgroundSettings)
            : mode === 'atlas' && atlas && atlas.id
            ? el(Detail, {
                kind: atlas.kind, id: atlas.id,
                onOpenPath: (p) => { setTarget(p); setMode('browse'); },
              })
            : target
            ? el(Preview, { target, onError: setError })
            : el('div', { style: S.right },
                el('div', { style: S.stage },
                  el('div', { style: S.hint },
                    el('div', { style: { fontSize: 15, marginBottom: 8, color: T.text } }, '选一个资源开始'),
                    el('div', null, '左边「分类」挑 Mob / Npc / Character / Item / Map / UI …'),
                    el('div', null, '或用「搜索」按中文名、编号查（花蘑菇、路西德、1332020）。'),
                    el('div', null, '选中图片后自动列出动作，可逐帧看雪碧图或播放 GIF。')))),
        ),
        el('div', { style: S.status },
          el('span', null, packs ? `数据源 ${packs.data_dir}` : '读取数据源…'),
          // 状态栏不再显示 `${region} v${version}`（用户要求去掉版本号）
          packs ? el('span', null, `wzpy ${packs.wzpy}`) : null,
          el('span', { style: { marginLeft: 'auto' } }),
          el('span', null, mode === 'browse'
            ? `浏览 ${pack}${path ? `/${path}` : ''}`
            : mode === 'search' ? `搜索 ${query || '—'}`
            : mode === 'atlas' ? `图鉴 ${atlas?.kind || ''} ${atlas?.id || ''}`
            : '背景设置'),
          target ? el('span', null, `预览 ${target}`) : null,
        ),
      );
    }

    /**
     * 背景层宿主组件：自己读背景配置，并把「改配置」的回调交给面板。
     * shell.overlay 是根作用域，直接挂 MapBackground。
     */
    // 探针：任何 window 级错误都记下来（排查背景组件为何静默消失）。
    // 浏览器加载本 bundle 时就会执行，不依赖事后挂钩。
    if (typeof window !== 'undefined' && !window.__msWzProbe) {
      window.__msWzProbe = [];
      window.addEventListener('error', (e) => {
        window.__msWzProbe.push({
          m: String((e.error && e.error.message) || e.message),
          at: (e.filename || '').split('/').pop() + ':' + e.lineno + ':' + e.colno,
        });
      });
      const _ce = console.error;
      console.error = function (...a) {
        try { window.__msWzProbe.push({ ce: a.map((x) => String(x)).join(' ').slice(0, 220) }); } catch (_) {}
        return _ce.apply(console, a);
      };
    }

    function BackgroundHost() {
      // 组件体内任何异常都转成可见输出：交给 slot 错误边界只会留一个空 div，
      // 完全看不出原因（这一轮排查卡在这里很久）。
      try {
        return BackgroundHostInner();
      } catch (e) {
        return el('div', {
          'data-ms-wz-bg-state': `crash:${String(e && e.message)}`,
          style: {
            position: 'fixed', left: 8, bottom: 8, zIndex: 60, maxWidth: 640,
            background: '#2b1d1d', color: '#ffd9d9', border: '1px solid #a33',
            borderRadius: 8, padding: '8px 10px', font: '12px/1.5 ui-monospace, monospace',
            whiteSpace: 'pre-wrap',
          },
        }, `背景组件崩溃：${String(e && e.message)}\n${String(e && e.stack).split('\n').slice(0, 4).join('\n')}`);
      }
    }

    function BackgroundHostInner() {
      const [config, setConfig] = useState(null);
      const [fetchErr, setFetchErr] = useState(null);

      useEffect(() => {
        let alive = true;
        callApi('backgroundGet')
          .then((c) => { if (alive) setConfig(c && typeof c === 'object' ? c : {}); })
          .catch((e) => { if (alive) { setFetchErr(String(e.message)); setConfig({}); } });
        return () => { alive = false; };
      }, []);

      useEffect(() => {
        bgBridge.set = (patch) => {
          setConfig((prev) => ({ ...(prev || {}), ...patch }));
          callApi('backgroundSet', patch).catch(() => {});
        };
        bgBridge.get = () => config;
      }, [config]);

      const enabled = !!config?.enabled;
      // 背景可见度（0-100）= 地图能透出多少。
      // 语义说明：之前这个滑块是「磨砂层的不透明度」，**方向和直觉相反** ——
      // 拉到 0% 反而完全不透明（地图全没了，用户实测就是这样）。
      // 现在统一成「可见度」：0=完全看不到地图，100=几乎全透。
      // CSS 里要的是磨砂层的**不透明度**，所以这里是 100 - 可见度。
      useEffect(() => {
        const root = document.documentElement;
        // 背景可见度 0..100（越大越透）→ 磨砂不透明度，**直接给带 % 的值**，
        // 避免在 CSS 里用 calc() 拼百分比（兼容性风险）。
        const vis = Math.max(0, Math.min(100, Number(config?.bgVisibility ?? 38)));
        // 滑块「地图清晰度」0..100 → 磨砂不透明度 **100%..10%**（保守映射）。
        // 为什么不用 100%..0%：实测 0% 与 10% 在画面上几乎无差别（纯白实底与 90% 白
        // 看不出区别），那 10 档里有 1 档是浪费的。映到 10% 起步后，
        // 每一档都能看出变化，且最低档仍保留一点地图。
        // 「去除毛玻璃」打开时直接给 0%（磨砂层全透），面板只剩上面那些清底规则，
        // 地图既是原像素、也不被模糊 —— 这是本项目里地图最清楚的状态。
        const noFrost = config?.blur === false;
        const frost = noFrost ? 0 : Math.round(100 - vis * 0.9);
        root.style.setProperty('--ms-wz-chrome', `${frost}%`);
        // 模糊只在「去除毛玻璃」时被显式关掉；其余情况**移除变量**，
        // 让 CSS 里的兜底 blur(...) 生效（避免写死一份样式与 CSS 不同步）。
        if (noFrost) root.style.setProperty('--ms-wz-blur', 'none');
        else root.style.removeProperty('--ms-wz-blur');
        return () => {
          root.style.removeProperty('--ms-wz-chrome');
          root.style.removeProperty('--ms-wz-blur');
        };
      }, [config?.bgVisibility, config?.blur]);
      useEffect(() => {
        const root = document.documentElement;
        if (enabled) root.setAttribute('data-ms-wz-bg', 'on');
        else root.removeAttribute('data-ms-wz-bg');
        return () => root.removeAttribute('data-ms-wz-bg');
      }, [enabled]);

      // 面板开着时加 data-ms-wz-panel，CSS 把中/右栏改成实底（用户口径：
      // 「这里就不需要有地图内容了」——插件是整页面板，底下露地图没法看）。
      useEffect(() => {
        if (!enabled) return undefined;
        const root = document.documentElement;
        const timer = setInterval(() => {
          // 只要面板 DOM 还在（或刚刚渲染过），就算面板开着
          const inDom = !!document.querySelector('[data-ms-wz-panel]');
          const recent = Date.now() - (window.__msWzPanelAt || 0) < 800;
          if (inDom || recent) root.setAttribute('data-ms-wz-panel', 'on');
          else root.removeAttribute('data-ms-wz-panel');
          // ── 插件页 / 设置页：全实底白（用户口径：「指定如果是插件的时候默认也是全白底」）──
          // 这类页面是列表 + 表单，透出地图会花得看不清；地图只在对话页有意义。
          // 判定「不是对话页」用几个**行为特征**，不猜路由：
          //   ① URL 含 plugins/settings/preferences/mcp/skills 等
          //   ② 中栏里找不到对话输入框（textarea）但页面已渲染出内容
          const path = (location.pathname + location.hash).toLowerCase();
          // ① URL 带这些词就是插件/设置类页面
          const byPath = /plugins|settings|preferences|\/mcp|market/.test(path);
          // ② 结构特征：插件页有「添加插件」这类专属入口。
          //    **不要**用「没有 textarea」反推 —— 新会话页本来就没有 textarea（还没开始输入），
          //    那样会把新会话也判成插件页（实测踩到：`/` 路径下 solid=on）。
          const byPluginUi = [...document.querySelectorAll('button')]
            .some((b) => /添加插件|插件市场/.test(b.textContent || ''));
          if (byPath || byPluginUi) root.setAttribute('data-ms-wz-solid', 'on');
          else root.removeAttribute('data-ms-wz-solid');
        }, 250);
        return () => {
          clearInterval(timer);
          root.removeAttribute('data-ms-wz-panel');
          root.removeAttribute('data-ms-wz-solid');
        };
      }, [enabled]);

      let body = null;
      if (config && enabled) body = el(MapBackground, { config, onConfig: setConfig });

      return el('div', {
        'data-ms-wz-diag': JSON.stringify({
          cfg: config === null ? 'loading' : 'ok', enabled,
          mapId: config?.mapId ?? null, fetchErr,
        }),
        style: { display: 'contents' },
      },
        enabled ? el('style', { key: 'bg-css', dangerouslySetInnerHTML: { __html: BACKGROUND_CSS } }) : null,
        body,
      );
    }

    const BACKGROUND_CSS = [
      /* 这套 CSS 只做一件事：让地图从「结构层」透出来，同时保证文字可读。
         ── 单一变量 ─────────────────────────────────────────────
         `--ms-wz-chrome` = 磨砂层的不透明度（0..1）。
         由面板的「背景可见度」推导：chrome = 100% - 可见度。
         **所有**磨砂规则都必须用它，不要再写死百分比 ——
         之前中栏写死 62%，导致拖滑块时中栏纹丝不动（用户反馈"0 和 100 一个效果"）。 */
      'html[data-ms-wz-bg="on"] { background-color: transparent !important; }',
      'html[data-ms-wz-bg="on"] body { background-color: transparent !important; }',
      /* 结构层：清底 + 禁模糊（地图靠它们透出来） */
      'html[data-ms-wz-bg="on"] #root,',
      'html[data-ms-wz-bg="on"] [class*="frame"],',
      'html[data-ms-wz-bg="on"] [class*="centerCol"],',
      'html[data-ms-wz-bg="on"] [class*="rightbarCol"],',
      'html[data-ms-wz-bg="on"] [data-rightbar-col],',
      'html[data-ms-wz-bg="on"] [class*="overlayLayer"],',
      'html[data-ms-wz-bg="on"] [class*="rbar"],',
      'html[data-ms-wz-bg="on"] [class*="card"],',
      'html[data-ms-wz-bg="on"] [class*="composer"],',
      'html[data-ms-wz-bg="on"] [class*="hero"],',
      'html[data-ms-wz-bg="on"] [class*="body"],',
      'html[data-ms-wz-bg="on"] [class*="root"] {',
      '  background-color: transparent !important;',
      '  backdrop-filter: none !important;',
      '  -webkit-backdrop-filter: none !important;',
      '}',
      /* 顶栏 + 对话正文流：吃同一个变量 */
      'html[data-ms-wz-bg="on"] [class*="header"],',
      'html[data-ms-wz-bg="on"] [class*="scrollBody"] {',
      '  background-color: color-mix(in srgb, var(--dsw-alias-bg-base) var(--ms-wz-chrome, 62%), transparent) !important;',
      '  backdrop-filter: var(--ms-wz-blur, blur(12px) saturate(1.05)) !important;',
      '  -webkit-backdrop-filter: var(--ms-wz-blur, blur(12px) saturate(1.05)) !important;',
      '}',
      /* 中栏（对话区 / 面板所在）：也吃这个变量 —— 拉滑块时它必须跟着变 */
      'html[data-ms-wz-bg="on"] [class*="centerCol"] {',
      '  background-color: color-mix(in srgb, var(--dsw-alias-bg-base) var(--ms-wz-chrome, 62%), transparent) !important;',
      '  backdrop-filter: var(--ms-wz-blur, blur(12px) saturate(1.04)) !important;',
      '  -webkit-backdrop-filter: var(--ms-wz-blur, blur(12px) saturate(1.04)) !important;',
      '}',
      /* 侧栏：白底（用户明确要求过两次，别再改成磨砂） */
      'html[data-ms-wz-bg="on"] [class*="sidebarCol"] {',
      '  background-color: var(--dsw-alias-bg-base) !important;',
      '  backdrop-filter: none !important;',
      '  -webkit-backdrop-filter: none !important;',
      '}',
      /* ── 插件页 / 设置页：**全实底白**，不给地图透出来 ────────────────
         用户口径：「指定如果是插件的时候默认也是全白底」。
         这类页面是列表 + 表单（插件列表、设置），透出地图会花得看不清；
         而地图背景只在对话页有意义。所以由 JS 按当前路由打 `data-ms-wz-solid`，
         这里把中栏/右栏/顶栏/正文流全部拉回和侧栏一样的实底，并关掉模糊。 */
      'html[data-ms-wz-bg="on"][data-ms-wz-solid="on"] [class*="header"],',
      'html[data-ms-wz-bg="on"][data-ms-wz-solid="on"] [class*="scrollBody"],',
      'html[data-ms-wz-bg="on"][data-ms-wz-solid="on"] [class*="centerCol"],',
      'html[data-ms-wz-bg="on"][data-ms-wz-solid="on"] [class*="rightbarCol"],',
      'html[data-ms-wz-bg="on"][data-ms-wz-solid="on"] [class*="frame"],',
      'html[data-ms-wz-bg="on"][data-ms-wz-solid="on"] [data-rightbar-col] {',
      '  background-color: var(--dsw-alias-bg-base) !important;',
      '  backdrop-filter: none !important;',
      '  -webkit-backdrop-filter: none !important;',
      '}',
      /* 代码块：白底 + 细边框 */
      'html[data-ms-wz-bg="on"] pre,',
      'html[data-ms-wz-bg="on"] pre code,',
      'html[data-ms-wz-bg="on"] [class*="code"] {',
      '  background-color: var(--dsw-alias-bg-base) !important;',
      '  backdrop-filter: none !important;',
      '  -webkit-backdrop-filter: none !important;',
      '}',
      'html[data-ms-wz-bg="on"] pre {',
      '  border: 1px solid var(--dsw-alias-border-l1, rgba(128,128,128,.25)) !important;',
      '  border-radius: 8px;',
      '}',
      /* 换图交叉淡入：新图从透明淡到不透明，旧图垫在下面 */
      '@keyframes msWzMapIn { from { opacity: 0; } to { opacity: 1; } }',
      /* 背景层浮层（暴露 toScreen 用）不吃事件 */
      '[data-ms-wz-bg-host] > [data-ms-wz-bg-layer] { pointer-events: none !important; }',
    ].join('\n');

    // ══════════════════════════════════════════════════════════════════
    //  桌面宠物 client（原 @local/dsh-desktop-pet/client.js）
    //
    //  为什么包在 IIFE 里：两边模块级私有名字有重名（API / h / el /
    //  buildWalkGraph / groundYAt / findGround / frameElement / clamp …）。
    //  而 host 只服务入口清单里的 `client.js`（同插件目录下第二个 .js 会 404，
    //  已实测），所以只能合成一个模块 —— 用 IIFE 给 pet 一份独立作用域，
    //  它的 877 行代码**一行都不用改**，也没有遮蔽风险。
    //
    //  slot 注册不在这里执行，而是把它的 apply 交给下面 wz 的 apply 一起调，
    //  保证两条注册落在同一个 fiber 生命周期内。
    // ══════════════════════════════════════════════════════════════════
    // ══════════════════════════════════════════════════════════════════
    // 插件启动即**异步预取**（用户口径：「这个等插件启动的时候就异步加载，
    // 而不是点进页面才加载，其他几个也是」）。
    //
    // 之前这些都是组件挂载后才拉：部位表（16 类 × 400 条 ≈ 6400 项）、椅子清单、
    // 资源包列表、精选地图。先点进哪个 tab 就等哪个，所以「点进去才加载」很慢。
    // 这里在 apply() 时就把它们并发发出去，结果存模块级缓存；
    // 组件挂载时先用缓存（同步、瞬时出画面），缓存没到再退回自己拉。
    // 放模块级：wz 的组件与 petClient 都能读（petClient 是 IIFE，但能读外层）。
    // ══════════════════════════════════════════════════════════════════
    const PREFETCH = {
      parts: null,      // charParts 的 categories
      chairs: null,     // chairList 的 { chairs, total }
      packs: null,      // packs（区域/版本）
      featured: null,   // mapFeatured 的 maps
    };
    let prefetchPromise = null;
    let prefetchAt = 0;

    /** 启动时调用一次：并发预取上面四份（单项失败静默，组件会自己兜底重拉）。 */
    function startPrefetch() {
      if (prefetchPromise) return prefetchPromise;
      const safe = (p) => p.catch(() => null);
      prefetchAt = Date.now();
      prefetchPromise = Promise.all([
        safe(callApi('charParts', { limit: 400 })
          .then((d) => { PREFETCH.parts = (d && d.categories) || {}; })),
        safe(callApi('chairList', { query: '', limit: 300 })
          .then((d) => { PREFETCH.chairs = d || null; })),
        safe(callApi('packs')
          .then((d) => { PREFETCH.packs = d || null; })),
        safe(callApi('mapFeatured', {})
          .then((d) => { PREFETCH.featured = (d && d.maps) || null; })),
      ]);
      return prefetchPromise;
    }

    /**
     * 桌宠「重新拉取」的信号（**必须放在 petClient IIFE 之外**）。
     *
     * 纸娃娃面板点「应用到桌宠」后要立刻看到效果，但桌宠的数据只在 `mobId` 变化时重拉，
     * 应用只改了配置、桌宠不知道 → 必须手动刷新页面（用户反馈「点击应用以后必须刷新」）。
     * 面板 ++ 这个计数，桌宠用 effect 监听它重拉。放 IIFE 里面面板就拿不到了。
     */
    let PET_RELOAD_SEQ = 0;
    const bumpPetReload = () => { PET_RELOAD_SEQ += 1; };

    const petClient = (() => {

    const React = require('react');
    const h = React.createElement;
    // 全文件统一用 el() 建元素：h 这个名字容易被局部变量遮蔽/在 TDZ 里被提前用到，
    // 已经因为 `const h = (img.h||vh)*zoom` 遮蔽过一次，直接把 React 调用固定成 el()。
    const el = h;
    const { useEffect, useRef, useState, useCallback } = React;

    // 合并后与资源面板**同一个 /api**（命令名 pet/petSet… 不重名），
    // 旧的 /dsh-desktop-pet/api 路由已随包一起删除。
    const API = '/dsh-maplestory-wz/api';

    // ── C# 常量 ───────────────────────────────────────────────────────
    const DEFAULT_FRAME_DELAY = 120;  // WzService.BuildSpriteStrip：delay 缺省 120
    const FALLBACK_FRAME_DELAY = 240; // SpriteStrip.DefaultDelay（帧数据缺失时的兜底）
    const FALLBACK_DELTA = 33;        // PetWindow.OnTimerTick：delta <= 0 → 33ms
    const MAX_DELTA = 500;            // PetWindow.OnTimerTick：delta 封顶 500ms
    const MAX_ADVANCES = 8;           // AnimService.Tick：单 tick 最多追 8 帧
    const DRAG_THRESHOLD = 4;         // PetWindow：|dx|+|dy| < 4 → 点击

    /** AnimService 构造函数里的动作定义（Loop 集合 + fallback 链）。 */
    const ACTION_DEFS = {
      stand: { loop: true, fallback: null },
      move: { loop: true, fallback: 'stand' },
      fly: { loop: true, fallback: 'stand' },
      say: { loop: false, fallback: 'stand' },
      mouse: { loop: false, fallback: 'stand' },
      hit1: { loop: false, fallback: 'stand' },
      hit: { loop: false, fallback: 'stand' },
      die1: { loop: false, fallback: null },
      die: { loop: false, fallback: null },
    };
    // C# LoadStripWithFallback 的 A9 兜底顺序
    const ALT_ACTIONS = ['move', 'fly', 'walk', 'walk1'];

    async function callApi(cmd, args) {
      const res = await fetch(API, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ cmd, args: args || {} }),
      });
      const payload = await res.json();
      if (!payload.ok) throw new Error(payload.error || '接口失败');
      return payload.data;
    }

    // ── 动作解析（AnimationDef / fallback 链）──────────────────────────
    /** 数据里真有这个动作吗？（exact=false 表示 worker 已按 fallback 顶替） */
    function realAction(anims, name) {
      const sheet = anims[name];
      return sheet && sheet.exact !== false ? name : null;
    }

    function resolveAction(anims, name) {
      if (!name) return Object.keys(anims)[0] || null;
      if (realAction(anims, name)) return name;
      const def = ACTION_DEFS[name];
      const fb = def && def.fallback ? def.fallback : name === 'stand' ? null : 'stand';
      if (fb && fb !== name) {
        const got = resolveAction(anims, fb);
        if (got) return got;
      }
      for (const alt of ALT_ACTIONS) {
        if (realAction(anims, alt)) return alt;
      }
      return Object.keys(anims)[0] || null;
    }

    const rand = (min, max) => min + Math.random() * Math.max(0, max - min);
    const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);

    function delayOf(sheet, index) {
      const frame = sheet && sheet.frames && sheet.frames[index];
      const value = frame && Number(frame.delay);
      if (Number.isFinite(value) && value > 0) return value;
      return sheet && sheet.frames && sheet.frames.length ? FALLBACK_FRAME_DELAY : DEFAULT_FRAME_DELAY;
    }

    /** 播放动作（AnimService.Play）：重置帧号与累积延迟，按动作定义决定 Loop/OneShot。 */
    /**
     * 切动作。
     *
     * `forceSit`：坐着的时候**只允许 sit**。三处「走完一段 → 回站姿」都没守椅子，
     * 结果桌宠会在 sit 与 stand1 之间来回切（实测两个动作交替出现）。
     * 注意 play 定义在组件**外面**，拿不到组件内的 sittingRef，所以由调用方传进来。
     */
    /**
     * 「当前是否坐着」的模块级标记。
     *
     * play() 定义在 DesktopPet **外面**，拿不到组件里的 sittingRef；
     * 而调用点有十几处（拖拽/点击/悬浮/走完一段…），逐个加参数必漏。
     * 所以用一个模块级变量：组件在 sitting 变化时写它，play 统一读它。
     */
    let PET_SITTING = false;


    function play(st, anims, name, delayScale, forceSit) {
      // 坐着就只允许 sit（forceSit 显式覆盖，供「起身」用）
      if ((forceSit === undefined ? PET_SITTING : forceSit) && realAction(anims, 'sit')) name = 'sit';
      const key = resolveAction(anims, name);
      if (!key) return;
      const def = ACTION_DEFS[key];
      st.action = key;
      st.frame = 0;
      st.acc = 0;
      st.oneShot = !(def ? def.loop : false);
      st.pending = null;
      st.delayScale = delayScale;
    }

    /** 帧推进（AnimService.Tick 的帧循环部分）。 */
    function advanceFrame(st, anims, deltaMs) {
      const sheet = anims[st.action];
      if (!sheet) return;
      const count = sheet.frames.length;
      const scale = st.delayScale || 1;
      st.acc += deltaMs;
      let advances = 0;
      while (advances < MAX_ADVANCES && st.acc >= delayOf(sheet, st.frame) * scale) {
        st.acc -= delayOf(sheet, st.frame) * scale;
        st.frame += 1;
        advances += 1;
        if (st.frame >= count) {
          if (st.oneShot) {
            // OneShot 播完 → pending ?? "stand"（C# Tick 内 SwitchTo(nextAction)）
            st.oneShot = false;
            const next = st.pending || 'stand';
            st.pending = null;
            const done = st.onComplete;
            st.onComplete = null;
            play(st, anims, next, st.delayScale);
            if (done) done();
            return;
          }
          st.frame = 0;   // Loop 到头回第 0 帧
        }
      }
      if (st.frame >= count) st.frame = 0;
    }

    // ── 几何：锚点（origin 补偿口径）───────────────────────────────────
    /** 站姿锚点几何作为稳定基准（对应 C# 的 _anchorActionName = stand1 基准帧）。 */
    function baseSheet(data) {
      const anims = data.anims;
      const key = realAction(anims, 'stand') || Object.keys(anims)[0];
      return anims[key];
    }

    /** 宠物可走的水平范围：视觉外框不越界（视觉尺寸 = 单元格 × scale）。 */
    // ── 地图 foothold 行走（照 MiniPet Services/Walk 的语义）──────────
    // 背景地图开着时，宠物改走地图地面而不是屏幕底边：位置用**世界坐标**，
    // 再按背景的 camera/scale 变换到屏幕像素。没背景就退回原来的屏幕底部行走。

    /** 可站段（去掉竖直段），按 next 建索引。 */
    function buildWalkGraph(footholds) {
      const segs = new Map();
      for (const f of footholds || []) {
        if (f.x1 === f.x2) continue;
        segs.set(f.id, { ...f, lo: Math.min(f.x1, f.x2), hi: Math.max(f.x1, f.x2) });
      }
      return segs;
    }

    /** 段上 x 处的地面高度。 */
    function groundYAt(seg, x) {
      const t = (x - seg.x1) / ((seg.x2 - seg.x1) || 1);
      return seg.y1 + (seg.y2 - seg.y1) * Math.max(0, Math.min(1, t));
    }

    /** 沿 next/prev 链换段；孤立段就掉头。 */
    function nextSeg(graph, seg, dir) {
      if (dir >= 0 && seg.next && graph.has(seg.next)) return graph.get(seg.next);
      if (dir < 0 && seg.prev && graph.has(seg.prev)) return graph.get(seg.prev);
      return seg;
    }

    function xRange(data, viewW) {
      const sheet = baseSheet(data);
      // 用户口径：**不放大**，原始像素多大就多大。缩放只允许 ≤1（大地图才可能缩小）。
      const k = Math.min(1, Number(data.defaults.scale) || 1);
      const inset = data.defaults.edgeInset;
      const left = sheet.anchor.x * k;
      const right = (sheet.cell.w - sheet.anchor.x) * k;
      const minX = inset + left;
      const maxX = Math.max(minX, viewW - inset - right);
      return { minX, maxX };
    }

    /** 地面线：锚点固定（C# 的 origin 补偿），外框底边贴地。 */
    /**
     * 站在屏幕底边时的 **左上角** y。
     * 口径统一说明：渲染是 `top = snap.y - anchor.y`（按脚底对齐），
     * 所以这里直接返回「左上角」y = 地面 y − cell.h，
     * 渲染那边减 anchor.y 恰好把脚底落在地面线上。
     * 之前这里返回的是「脚底 y」，和渲染口径差一个 cell.h → 换帧即跳（用户反馈"抖动"）。
     */
    function groundY(data, viewH) {
      const sheet = baseSheet(data);
      // 用户口径：**不放大**，原始像素多大就多大。缩放只允许 ≤1（大地图才可能缩小）。
      const k = Math.min(1, Number(data.defaults.scale) || 1);
      const groundLine = viewH - data.defaults.groundMargin;
      // 渲染是 top = snap.y - anchor.y×k、高度 cell.h×k，要脚底落在地面线上：
      //   snap.y = groundLine - cell.h×k + anchor.y×k
      return groundLine - sheet.cell.h * k + sheet.anchor.y * k;
    }

    /** 拖拽动作：C# actions.Contains("fly") ? "fly" : actions.Contains("move") ? "move" : "stand" */
    function dragAction(anims) {
      return realAction(anims, 'fly') || realAction(anims, 'move') || realAction(anims, 'stand')
        || Object.keys(anims)[0];
    }

    // ── 组件 ──────────────────────────────────────────────────────────
    function DesktopPet() {
      const [data, setData] = useState(null);
      const [error, setError] = useState(null);
      const [snap, setSnap] = useState(null);
      // 换宠：mobId 变化就重新拉数据（不用刷新页面）
      const [mobId, setMobId] = useState(null);
      const [pickerOpen, setPickerOpen] = useState(false);
      // 桌宠数据加载中：纸娃娃模式下第一次合成要好几秒，没有提示就像「坏了/幻觉」
      const [petLoading, setPetLoading] = useState(true);
      const [query, setQuery] = useState('');
      const [candidates, setCandidates] = useState([]);
      // 快速选择行用的两份数据：经典候选（presets）与当前怪物编号。
      // presets 打开面板时就拉（见下面 effect），所以即使用户不搜索也能点。
      const [presetList, setPresetList] = useState([]);
      const [currentMobId, setCurrentMobId] = useState(null);
      const [pickErr, setPickErr] = useState(null);
      const [busy, setBusy] = useState(false);
      // 换宠面板的加载态：打开面板、输入搜索词、切换纸娃娃/怪物都会发请求，
      // 此前**没有任何提示** —— 候选列表在返回前是空的，看起来像「面板坏了/空白」。
      const [pickLoading, setPickLoading] = useState(false);
      // 地图行走：{graph, seg, cfg} —— 背景地图开着且拿到 foothold 时启用
      const walkRef = useRef(null);
      // 地图行走总开关：**默认关**（用户口径：桌宠不用跟着地图走，随意走）。
      // foothold 那套代码保留着，想开就把下面改成 true。
      // 是否坐了椅子：坐椅子就不该再自己走动（用户口径：「上了椅子以后就不用动了」）。
      // 数据来源与纸娃娃面板同一份配置（background.json 的 doll.chair），
      // 面板一改这边跟着变，不设第二份。
      const [sitting, setSitting] = useState(false);
      const sittingRef = useRef(false);
      const [walkOn, setWalkOn] = useState(false);
      const [walkInfo, setWalkInfo] = useState(null);
      const [walkState, setWalkState] = useState('初始化');

      const dataRef = useRef(null);
      const stRef = useRef(null);
      const viewRef = useRef({ w: 0, h: 0 });
      const dragRef = useRef(null);
      const lastSnapRef = useRef('');

      // 面板点「应用到桌宠」时它会 ++（见 bumpPetReload 注释）——
      // 这个 state 只为让下面的取数 effect 重跑。
      const [reloadSeq, setReloadSeq] = useState(0);
      useEffect(() => {
        const t = setInterval(() => {
          setReloadSeq((n) => (n === PET_RELOAD_SEQ ? n : PET_RELOAD_SEQ));
        }, 500);
        return () => clearInterval(t);
      }, []);

      // 取数据（Host HTTP）
      useEffect(() => {
        let alive = true;
        setPetLoading(true);
        setError(null);
        callApi('pet', mobId ? { id: mobId } : {})
          .then((payload) => {
            if (!alive) return;
            dataRef.current = payload;
            stRef.current = null;          // 换宠后行走状态重建（尺寸/锚点都变了）
            setData(payload);
            setMobId(payload.id);
          })
          .catch((err) => alive && setError(String(err && err.message ? err.message : err)))
          .finally(() => { if (alive) setPetLoading(false); });
        return () => { alive = false; };
      }, [mobId, reloadSeq]);

      // 打开选择器时拉一批经典候选
      useEffect(() => {
        if (!pickerOpen) return undefined;
        let alive = true;
        setPickLoading(true);
        callApi('presets', {})
          .then((d) => {
            if (!alive) return;
            setCandidates(d.presets || []);
            // 同一份结果给快速选择行（原来只喂列表，快速行就拿不到）
            setPresetList(d.presets || []);
            if (d.current) setCurrentMobId(String(d.current));
          })
          .catch((e) => { if (alive) setPickErr(String(e.message || e)); })
          .finally(() => { if (alive) setPickLoading(false); });
        return () => { alive = false; };
      }, [pickerOpen]);

      // 搜怪物（按中文名/编号）
      useEffect(() => {
        if (!pickerOpen) return undefined;
        const q = query.trim();
        // 清空搜索框要**先复位 loading**，否则上一次搜索的 true 会卡住
        // （这个 effect 在 q 为空时直接 return，没人再把它设回 false）。
        if (!q) { setPickLoading(false); return undefined; }
        let alive = true;
        setPickLoading(true);
        const timer = setTimeout(() => {
          callApi('search', { query: q, limit: 30 })
            .then((d) => { if (alive) setCandidates(d.hits || []); })
            .catch((e) => { if (alive) setPickErr(String(e.message || e)); })
            .finally(() => { if (alive) setPickLoading(false); });
        }, 250);
        return () => { alive = false; clearTimeout(timer); };
      }, [query, pickerOpen]);

      const pickPet = useCallback((id) => {
        setBusy(true); setPickErr(null);
        callApi('petSet', { id })
          .then(() => { setMobId(String(id)); setPickerOpen(false); })
          .catch((e) => setPickErr(String(e.message || e)))
          .finally(() => setBusy(false));
      }, []);

      /**
       * 切到纸娃娃模式（「快速选择」与「当前」区共用）。
       * key 用一个会变的串：mobId 是拉取数据的触发条件，纸娃娃没有编号，
       * 用时间戳保证每次点都能重新拉（否则连点两次第二次不会刷新）。
       */
      const useChara = useCallback(() => {
        setBusy(true); setPickErr(null);
        callApi('petSetChara', {})
          .then(() => { setMobId('chara:' + Date.now()); setPickerOpen(false); })
          .catch((e) => setPickErr(String(e.message || e)))
          .finally(() => setBusy(false));
      }, []);

      /** 切回上次那只怪物（「快速选择」用）。 */
      const useMob = useCallback((id) => {
        setBusy(true); setPickErr(null);
        callApi('petSetMob', id ? { id: String(id) } : {})
          .then((d) => { setMobId(String(d.mobId)); setPickerOpen(false); })
          .catch((e) => setPickErr(String(e.message || e)))
          .finally(() => setBusy(false));
      }, []);

      useEffect(() => {
        let alive = true;
        const read = () => {
          callApi('backgroundGet', {})
            .then((c) => {
              const on = !!(c && c.doll && c.doll.chair);
              if (!alive) return;
              const was = sittingRef.current;
              sittingRef.current = on;
              PET_SITTING = on;      // 让 play() 也能看到（它在组件外）
              setSitting(on);
              // **起身（坐 → 不坐）时立刻把 idleRemaining 复位**：
              // 坐着期间它被续期成 60000，若不复位就要再等 60 秒才肯走动，
              // 表现为「清空椅子后宠物永远不动」（实测踩到过）。
              const st = stRef.current;
              const D = (dataRef.current && dataRef.current.defaults) || {};
              if (st) {
                if (on) {
                  // 坐下：切坐姿并停住（坐姿只有 1 帧，天然静止）
                  play(st, dataRef.current.anims, 'sit', st.delayScale, true);
                  st.mode = 'idle';
                  st.idleRemaining = 60000;
                } else if (was && !on && st.mode === 'idle') {
                  // 起身：回到站姿，并把 idleRemaining 复位 ——
                  // 坐着期间它被续期成 60000，不复位就要再等 60 秒才肯走动，
                  // 表现为「清空椅子后宠物永远不动」（实测踩到过）。
                  play(st, dataRef.current.anims, 'stand', st.delayScale, false);
                  st.idleRemaining = rand(D.idleMinMs ?? 900, D.idleMaxMs ?? 2800);
                }
              }
            })
            .catch(() => {});
        };
        read();
        const t = setInterval(read, 2000);
        return () => { alive = false; clearInterval(t); };
      }, []);

      // 地图行走已关闭（walkOn=false），不再去拉 foothold，省掉一轮请求。
      useEffect(() => {
        setWalkState(walkOn ? '准备中' : 'off:随意走');
        if (!walkOn) { walkRef.current = null; setWalkInfo(null); }
      }, [walkOn]);

      const viewport = useCallback(() => ({
        w: window.innerWidth || document.documentElement.clientWidth || 0,
        h: window.innerHeight || document.documentElement.clientHeight || 0,
      }), []);

      // 视口尺寸（改完位置由主循环下一帧自然推给渲染，无需强制重渲染）
      useEffect(() => {
        const sync = () => {
          const v = viewport();
          viewRef.current = v;
          const st = stRef.current;
          if (st && dataRef.current) {
            const { minX, maxX } = xRange(dataRef.current, v.w);
            const gy = groundY(dataRef.current, v.h);
            st.x = clamp(st.x, minX, maxX);
            st.y = gy;
            st.groundY = gy;
          }
        };
        sync();
        window.addEventListener('resize', sync);
        return () => window.removeEventListener('resize', sync);
      }, [viewport]);

      // 初始化状态（数据就绪后一次）
      useEffect(() => {
        if (!data) return;
        const view = viewport();
        viewRef.current = view;
        const { minX, maxX } = xRange(data, view.w);
        const groundTop = groundY(data, view.h);      // 站在屏幕底边时的左上角 y
        const st = {
          mode: 'idle',
          x: clamp(view.w / 2, minX, maxX),
          groundY: groundTop,                          // 飞行的下界（落回这里）
          hoverHold: false,                            // 鼠标悬浮时保持 stand
          landedY: groundTop,                          // 上次飞行停住的高度
          y: groundY(data, view.h),
          facing: Math.random() < 0.5 ? -1 : 1,
          action: null,
          frame: 0,
          acc: 0,
          oneShot: false,
          pending: null,
          onComplete: null,
          idleRemaining: 600,
          leg: 0,
          delayScale: data.defaults.delayScale || 1,
        };
        // 坐椅子就**直接进坐姿**（否则先站一下再切，看着像"点了没反应"）
        play(st, data.anims, 'stand', st.delayScale, sittingRef.current);
        stRef.current = st;
      }, [data, viewport]);

      // 主循环（对应 C# 的 33ms 定时器 + AnimService.Tick；这里用 rAF 带真实 delta）
      useEffect(() => {
        if (!data) return undefined;
        const anims = data.anims;
        const D = data.defaults;
        let raf = 0;
        let last = 0;
        let stopped = false;

        const push = () => {
          const st = stRef.current;
          if (!st) return;
          // 自测用：把内部状态挂出来（世界坐标/屏幕坐标/所在段）
          if (typeof window !== 'undefined') {
            const live = window.__msWzToScreen;
            window.__petState = {
              x: Math.round(st.x), y: Math.round(st.y), mode: st.mode,
              worldX: st.worldX !== undefined ? Math.round(st.worldX) : null,
              seg: walkRef.current?.seg ? { id: walkRef.current.seg.id, x1: walkRef.current.seg.x1, y1: walkRef.current.seg.y1, x2: walkRef.current.seg.x2, y2: walkRef.current.seg.y2 } : null,
              toScreen: (live && st.worldX !== undefined) ? (() => { const g = walkRef.current?.seg ? Math.round(walkRef.current.seg.y1 + ((walkRef.current.seg.y2 - walkRef.current.seg.y1) * 0)) : 0; const p = live(st.worldX, g); return { x: Math.round(p.x), y: Math.round(p.y) }; })() : null,
            };
          }
          const key = `${st.action}|${st.frame}|${Math.round(st.x)}|${Math.round(st.y)}|${st.facing}|${st.mode === 'drag' ? 1 : 0}`;
          if (key === lastSnapRef.current) return;
          lastSnapRef.current = key;
          // **必须取整**：st.x/st.y 在走动（尤其地图模式）下是小数，
          // 小数像素 + `image-rendering: pixelated` 会让像素图在半像素间来回吸附，
          // 看起来就是"走动时一直在抖"。判断变化用的 key 本来就是取整的，渲染也必须取整。
          setSnap({
            action: st.action,
            frame: st.frame,
            x: Math.round(st.x),
            y: Math.round(st.y),
            facing: st.facing,
            dragging: st.mode === 'drag',
          });
        };

        // 有 fly 动作的宠物可以飞（用户口径：纸娃娃允许用 fly 上下移动）
        const canFly = !!realAction(anims, 'fly');

        const startWalk = (st) => {
          const view = viewRef.current;
          const { minX, maxX } = xRange(data, view.w);
          // 有 fly 的话，一部分待机结束改为**起飞**（概率可配，默认 35%）
          if (canFly && Math.random() < (D.flyChance ?? 0.35)) {
            const cell = (anims[realAction(anims, 'fly')] || {}).cell || { h: 40 };
            st.mode = 'fly';
            st.flyTop = 8;                                   // 上界：离顶 8px
            st.flyBottom = st.groundY != null ? st.groundY : (view.h - cell.h - D.groundMargin);
            st.flyDir = -1;                                  // 先上升
            st.flyRemaining = rand(1500, 3500);              // 这一段飞多久
            st.x = clamp(st.x, minX, maxX);
            play(st, anims, 'fly', D.delayScale);
            return;
          }
          // 朝向：优先随机；该方向空间不足一个最短腿长就掉头（C# 走到尽头掉头）
          let facing = Math.random() < 0.5 ? -1 : 1;
          const room = (dir) => (dir > 0 ? maxX - st.x : st.x - minX);
          if (room(facing) < D.minLeg && room(-facing) > room(facing)) facing = -facing;
          st.facing = facing;
          st.leg = Math.min(room(facing), rand(D.minLeg, D.maxLeg));
          st.mode = 'walk';
          play(st, anims, 'move', D.delayScale);
        };

        const step = (deltaMs) => {
          const st = stRef.current;
          if (!st) return;
          const view = viewRef.current;
          let dt = deltaMs;
          if (!(dt > 0)) dt = FALLBACK_DELTA;     // C# delta <= 0 → 33
          if (dt > MAX_DELTA) dt = MAX_DELTA;     // C# delta 封顶 500
          advanceFrame(st, anims, dt);

          // ── 地图模式：走背景地图上的地面（世界坐标 → 屏幕像素）──────────
          // 用户口径：**不要上下乱跳**，只沿当前高度的地面左右走；
          // 方向规则（背景层已算好并挂在 __msWzBackground.dir）：
          //   宽/高 > 1.5 或接近正方形 → 左右；竖图 → 仍沿地面左右（相机负责上下）。
          const W = walkRef.current;
          const toScreen = (typeof window !== 'undefined') ? window.__msWzToScreen : null;
          if (walkOn && W && W.graph.size && toScreen) {
            // 只收集**水平段**（y1≈y2），它们才是能站的地面
            if (!W.flat) {
              const flat = [];
              for (const seg of W.graph.values()) {
                if (Math.abs(seg.y2 - seg.y1) <= 4) flat.push({ ...seg, groundY: (seg.y1 + seg.y2) / 2 });
              }
              flat.sort((a, b) => a.lo - b.lo);
              W.flat = flat;
            }
            const flats = W.flat;
            if (flats.length) {
              if (!W.seg) {
                // 落地：视野内最长的水平段，起点取相机 x
                const cam = (window.__msWzBackground || {}).cam || { x: 0, y: 0 };
                const vw = view.w || 1280;
                let best = null;
                for (const seg of flats) {
                  if (seg.hi < cam.x - vw || seg.lo > cam.x + vw) continue;
                  if (!best || (seg.hi - seg.lo) > (best.hi - best.lo)) best = seg;
                }
                if (!best) best = flats.reduce((a, b) => ((b.hi - b.lo) > (a.hi - a.lo) ? b : a), flats[0]);
                W.seg = best;
                st.worldX = Math.max(best.lo, Math.min(best.hi, cam.x));
                st.facing = 1;                                  // 默认从左往右
                st.leg = rand(D.minLeg, D.maxLeg);
                st.mode = 'walk';
                play(st, anims, 'move', D.delayScale);
              }
              if (st.mode === 'walk') {
                const worldPerSec = (D.speed * D.scale) / Math.max(0.05, W.worldScale || 1);
                const stepW = st.facing * worldPerSec * (dt / 1000);
                st.worldX += stepW;
                st.leg -= Math.abs(stepW);
                const seg = W.seg;
                if (st.worldX > seg.hi || st.worldX < seg.lo) {
                  // 到段尾：接**同一高度**上最近的段（容差 12 世界单位），否则掉头
                  const edgeX = st.worldX > seg.hi ? seg.hi : seg.lo;
                  const dirSign = st.worldX > seg.hi ? 1 : -1;
                  let next = null;
                  let bestD = Infinity;
                  for (const cand of flats) {
                    if (Math.abs(cand.groundY - seg.groundY) > 12) continue;
                    const far = dirSign > 0 ? cand.lo - edgeX : edgeX - cand.hi;
                    if (far < -8 || far > 160) continue;
                    if (far < bestD) { bestD = far; next = cand; }
                  }
                  if (next) { W.seg = next; st.worldX = Math.max(next.lo, Math.min(next.hi, st.worldX)); }
                  else { st.facing = -st.facing; st.worldX = Math.max(seg.lo, Math.min(seg.hi, st.worldX)); }
                }
                if (st.leg <= 0) {
                  st.mode = 'idle';
                  st.idleRemaining = rand(D.idleMinMs, D.idleMaxMs);
                  play(st, anims, 'stand', D.delayScale, sittingRef.current);
                }
                // 世界坐标 → 屏幕。注意渲染时 top = snap.y - anchor.y（按脚底对齐），
                // 所以这里要把「脚底的屏幕 y」换算成**左上角**：直接给 y + cell.h。
                // 之前给的是脚底 y，渲染又减 anchor.y → 位置被锚点尺寸牵动，
                // 换帧/换动作时锚点一变就跳（用户反馈"有抖动"）。
                const p = toScreen(st.worldX, groundYAt(W.seg, st.worldX));
                const sh = anims[st.action] || baseSheet(data);
                const cellK = data.defaults.scale;
                st.x = p.x;
                // 同 groundY 的口径：snap.y = 脚底屏幕y - cell.h*k + anchor.y*k
                st.y = p.y - sh.cell.h * cellK + sh.anchor.y * cellK;
                // 地图地面可能落在视口外（相机看向别处时）→ 夹回可视范围，
                // 否则桌宠会跑到视口外（实测 y=-30 被裁掉，看着像"渲染不行"）。
                {
                  const view2 = viewRef.current || { h: 0 };
                  const ch2 = sh.cell.h * cellK;
                  st.y = Math.min(Math.max(st.y, 8), Math.max(8, view2.h - ch2 - 8));
                  const minX2 = 8, maxX2 = Math.max(8, (view2.w || 0) - sh.cell.w * cellK - 8);
                  st.x = Math.min(Math.max(st.x, minX2), maxX2);
                }
                // 地图地面可能落在视口外（相机上看时），夹回可视范围
                st.y = Math.min(Math.max(st.y, 8), Math.max(8, view.h - sh.cell.h * cellK - 8));
              } else if (st.mode === 'idle') {
                st.idleRemaining -= dt;
                // 坐椅子期间**不启动走动**（也不起飞）：只把 idleRemaining 续期，
                // 让它一直停在原地。起身（清空椅子）后自动恢复原来的行为。
                if (sittingRef.current) st.idleRemaining = 60000;
                else if (st.idleRemaining <= 0) { st.mode = 'walk'; play(st, anims, 'move', D.delayScale); }
              }
              // 地平线跑出视口就夹回来，别让宠物消失
              if (st.y > view.h - 2) st.y = view.h - 2;
              if (st.y < 2) st.y = 2;
              push();
              return;
            }
          }

          if (st.hoverHold && st.mode !== 'drag' && st.mode !== 'react') {
            // 悬浮时保持 stand，不推进走/待机状态机
            push();
            return;
          }

          if (st.mode === 'walk') {
            const speed = D.speed * D.scale;            // WZ px/s → CSS px/s
            const dx = st.facing * speed * (dt / 1000);
            st.x += dx;
            st.leg -= Math.abs(dx);
            const { minX, maxX } = xRange(data, view.w);
            if (st.x <= minX) {
              st.x = minX;
              st.facing = 1;                            // 撞左墙 → 掉头向右
              st.leg = Math.min(maxX - st.x, rand(D.minLeg, D.maxLeg));
            } else if (st.x >= maxX) {
              st.x = maxX;
              st.facing = -1;                           // 撞右墙 → 掉头向左
              st.leg = Math.min(st.x - minX, rand(D.minLeg, D.maxLeg));
            } else if (st.leg <= 0) {
              st.mode = 'idle';
              st.idleRemaining = rand(D.idleMinMs, D.idleMaxMs);
              play(st, anims, 'stand', D.delayScale, sittingRef.current);
            }
          } else if (st.mode === 'fly') {
            // 飞行：**竖直 + 水平漂移**，时间到**就地**落下。
            // 用户口径「fly 起来以后就不要回到原点了」：之前只上下动、落地锚回起飞点，
            // 等于白飞；现在飞的时候也横移，落地就站在落点继续走。
            const { minX, maxX } = xRange(data, view.w);
            const vy = (D.flySpeed ?? D.speed) * D.scale * (dt / 1000) * st.flyDir;
            const vx = (D.flySpeed ?? D.speed) * D.scale * (dt / 1000) * (st.facing || 1);
            st.y += vy;
            st.x = clamp(st.x + vx, minX, maxX);
            if (st.y <= st.flyTop) { st.y = st.flyTop; st.flyDir = 1; }
            if (st.y >= st.flyBottom) { st.y = st.flyBottom; st.flyDir = -1; }
            if (st.x <= minX || st.x >= maxX) st.facing = -(st.facing || 1);
            st.flyRemaining -= dt;
            if (st.flyRemaining <= 0) {
              // 就地停：**不要回退到地板**（用户口径"落地人物会回退到地板上，不太好看"），
              // 保持当前 x 和当前高度，原地待机；下一轮走动再自然贴回地面。
              st.landedY = st.y;
              st.mode = 'idle';
              st.idleRemaining = rand(D.idleMinMs, D.idleMaxMs);
              play(st, anims, 'stand', D.delayScale, sittingRef.current);
            }
          } else if (st.mode === 'idle') {
            st.idleRemaining -= dt;
            if (sittingRef.current) st.idleRemaining = 60000;
            else if (st.idleRemaining <= 0) startWalk(st);
          }
          // 所有模式统一夹一次：y 不能跑出视口
          // （实测：飞行 + 地图模式下会飞到 y=-87，宠物直接消失/抖动）。
          {
            const st2 = stRef.current;
            const d2 = dataRef.current;
            if (st2 && d2) {
              const cellH = ((anims[st2.action] || baseSheet(d2)).cell || {}).h || 0;
              const top = 8;
              const bottom = Math.max(top, (viewRef.current.h || 0) - cellH - 8);
              if (st2.mode !== 'drag') st2.y = Math.min(Math.max(st2.y, top), bottom);
            }
          }
          // 'drag'（跟手，不自动走）/ 'react'（OneShot 播放中，播完回调接管）
          push();
        };

        const loop = (ts) => {
          if (stopped) return;
          const delta = last ? ts - last : FALLBACK_DELTA;
          last = ts;
          try {
            step(delta);
          } catch (err) {
            // 循环里任何异常都会静默打死 rAF（历史上踩过一次），这里必须暴露出来
            if (typeof window !== 'undefined') window.__petError = String(err && err.message || err);
            if (!stopped) raf = window.requestAnimationFrame(loop);
            return;
          }
          raf = window.requestAnimationFrame(loop);
        };
        raf = window.requestAnimationFrame(loop);
        return () => {
          stopped = true;
          window.cancelAnimationFrame(raf);
        };
      }, [data]);

      // ── 指针交互（C# SetupDragEvents 口径）──────────────────────────
      // 鼠标悬浮到宠物上 → 播 stand（用户口径）。移开就接着走。
      const onPetEnter = useCallback(() => {
        const st = stRef.current;
        const d = dataRef.current;
        if (!st || !d) return;
        if (st.mode === 'drag' || st.mode === 'react' || st.mode === 'fly') return;
        st.hoverHold = true;
        // 坐着的时候悬浮不该站起来（否则鼠标停在宠物上就一直在 sit/stand 之间翻）
        play(st, d.anims, 'stand', d.defaults.delayScale, sittingRef.current);
      }, []);
      const onPetLeave = useCallback(() => {
        const st = stRef.current;
        const d = dataRef.current;
        if (!st || !d) return;
        st.hoverHold = false;
        if (st.mode === 'walk') play(st, d.anims, 'move', d.defaults.delayScale);
      }, []);

      const onPointerDown = useCallback((event) => {
        const st = stRef.current;
        if (!st) return;
        if (event.button !== undefined && event.button !== 0) return;
        event.preventDefault();
        try { event.currentTarget.setPointerCapture(event.pointerId); } catch { /* 忽略 */ }
        dragRef.current = {
          id: event.pointerId,
          startX: event.clientX,
          startY: event.clientY,
          grabX: st.x - event.clientX,
          grabY: st.y - event.clientY,
          active: false,
        };
      }, []);

      const onPointerMove = useCallback((event) => {
        const drag = dragRef.current;
        const st = stRef.current;
        const d = dataRef.current;
        if (!drag || !st || !d || drag.id !== event.pointerId) return;
        const dx = event.clientX - drag.startX;
        const dy = event.clientY - drag.startY;
        if (!drag.active) {
          if (Math.abs(dx) + Math.abs(dy) < DRAG_THRESHOLD) return;   // 点击（C# 阈值 4）
          drag.active = true;
          st.mode = 'drag';
          st.pending = null;
          play(st, d.anims, dragAction(d.anims), d.defaults.delayScale);
        }
        const view = viewRef.current;
        const inset = d.defaults.edgeInset;
        st.x = clamp(event.clientX + drag.grabX, inset, Math.max(inset, view.w - inset));
        st.y = clamp(event.clientY + drag.grabY, inset, Math.max(inset, view.h - inset));
      }, []);

      const endDrag = useCallback((event) => {
        const drag = dragRef.current;
        const st = stRef.current;
        const d = dataRef.current;
        if (!drag || !st || !d || drag.id !== event.pointerId) return;
        dragRef.current = null;
        try { event.currentTarget.releasePointerCapture(event.pointerId); } catch { /* 忽略 */ }
        if (drag.active) {
          // 拖完：落点即新位置，站一下接着走（y 不变 → 在新的高度继续来回走）
          st.mode = 'idle';
          st.idleRemaining = rand(500, 1200);
          play(st, d.anims, 'stand', d.defaults.delayScale);
          return;
        }
        // 点击：hit1/hit 一次 OneShot，播完回 stand 再继续走
        const hit = realAction(d.anims, 'hit1') || realAction(d.anims, 'hit');
        if (!hit) {
          st.mode = 'idle';
          st.idleRemaining = rand(d.defaults.idleMinMs, d.defaults.idleMaxMs);
          return;
        }
        st.mode = 'react';
        st.onComplete = () => {
          st.mode = 'idle';
          st.idleRemaining = rand(d.defaults.idleMinMs, d.defaults.idleMaxMs);
        };
        play(st, d.anims, hit, d.defaults.delayScale);
      }, []);

      const onPointerCancel = useCallback((event) => {
        const drag = dragRef.current;
        const st = stRef.current;
        const d = dataRef.current;
        if (!drag || !st || !d || drag.id !== event.pointerId) return;
        dragRef.current = null;
        st.mode = 'idle';
        st.idleRemaining = rand(500, 1200);
        play(st, d.anims, 'stand', d.defaults.delayScale);
      }, []);

      // ── 渲染 ────────────────────────────────────────────────────────
      const layerStyle = {
        position: 'fixed',
        inset: 0,
        pointerEvents: 'none',      // 只让宠物那一块可点，其余穿透（浮层不挡 DSH 交互）
        zIndex: 1,
      };
      // 浮层容器不吃事件；未就绪时连宠物都不渲染
      if (!data || !snap || error) {
        return el('div', { style: layerStyle, 'data-desktop-pet': 'idle' },
          error ? el('div', { style: { display: 'none' } }, error) : null);
      }

      const sheet = data.anims[snap.action] || baseSheet(data);
      // 用户口径：**不放大**，原始像素多大就多大。缩放只允许 ≤1（大地图才可能缩小）。
      const k = Math.min(1, Number(data.defaults.scale) || 1);
      const { cell, anchor } = sheet;
      const flipped = snap.facing >= 0;   // C# FlipX = Facing >= 0（原始帧朝左）

      // **最终防线：保证整只宠物都在视口内**。
      // 地图模式下 snap.y 由世界坐标算出（地面可能落在视口上方/下方），
      // 只夹 st.y 不够 —— 实测出现 top=-75，宠物大半个身子在屏幕外，
      // 看起来就是"位置不对/被裁成两截"。这里以「精灵矩形完整可见」为约束收口。
      const _vw = viewRef.current.w || 0;
      const _vh = viewRef.current.h || 0;
      const _rawLeft = snap.x - anchor.x;
      const _rawTop = snap.y - anchor.y;
      const petLeft = _vw ? Math.min(Math.max(_rawLeft, 2), Math.max(2, _vw - cell.w - 2)) : _rawLeft;
      const petTop = _vh ? Math.min(Math.max(_rawTop, 2), Math.max(2, _vh - cell.h - 2)) : _rawTop;

      const petStyle = {
        position: 'absolute',
        left: `${petLeft}px`,
        top: `${petTop}px`,
        width: `${cell.w}px`,
        height: `${cell.h}px`,
        backgroundImage: `url(${sheet.url})`,
        // 整格步进，取整防止半像素把相邻帧的边缘带进来
        backgroundPosition: `${-Math.round(snap.frame) * cell.w}px 0px`,
        backgroundRepeat: 'no-repeat',
        imageRendering: 'pixelated',
        pointerEvents: 'auto',
        cursor: snap.dragging ? 'grabbing' : 'grab',
        userSelect: 'none',
        WebkitUserSelect: 'none',
        touchAction: 'none',
        // 绕锚点镜像 + 缩放（= MapService.DrawSpriteScreen 的 translate→scale→drawBitmap(−ox,−oy)）
        transformOrigin: `${Math.round(anchor.x)}px ${Math.round(anchor.y)}px`,
        transform: `scale(${flipped ? -k : k}, ${k})`,
        // 只提示 transform（镜像/缩放需要合成层）。
        // 之前还写了 background-position：那会为每只宠物建独立合成层，
        // 在「fixed 定位 + 叠在 backdrop-filter 模糊层之上」时，半像素位移
        // 会在合成层里被重采样 → 走动时持续抖动。
        willChange: 'transform',
      };

      // 换宠 UI 的配色沿用主题 token（浮层里不引 Harness 客户端包）
      const gearStyle = {
        position: 'absolute',
        left: `${clamp(petLeft + cell.w, 8, (viewRef.current.w || 9999) - 30)}px`,
        top: `${clamp(petTop, 8, 9999)}px`,
        width: 22, height: 22, borderRadius: 999, cursor: 'pointer',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontSize: 12, lineHeight: 1, pointerEvents: 'auto',
        border: '1px solid var(--dsw-alias-border-l2, rgba(128,128,128,.4))',
        background: 'var(--dsw-alias-bg-layer-1, rgba(20,20,20,.72))',
        color: 'var(--dsw-alias-label-primary, #eee)',
        opacity: pickerOpen ? 1 : 0.55,
      };
      const popStyle = {
        position: 'fixed', left: 16, bottom: 60, width: 300, maxHeight: 380,
        display: 'flex', flexDirection: 'column', overflow: 'hidden',
        borderRadius: 10, pointerEvents: 'auto', zIndex: 2,
        border: '1px solid var(--dsw-alias-border-l2, rgba(128,128,128,.4))',
        // 注意：DSH 背景打开时，冒险岛插件会注入 `body * { background-color: transparent !important }`
        // 来做磨砂，主题变量在这里会被一起清掉（实测面板变成全透明、字压在列表上没法看）。
        // 所以这里用**实色**，不依赖任何 CSS 变量。
        background: '#22242a',
        color: '#eef1f5',
        font: '12px/1.5 -apple-system, system-ui, sans-serif',
        boxShadow: '0 6px 24px rgba(0,0,0,.35)',
      };
      const miniBtn = {
        padding: '3px 10px', borderRadius: 999, cursor: 'pointer', fontSize: 11.5,
        border: '1px solid rgba(255,255,255,.22)',
        background: 'transparent', color: 'inherit',
      };
      const miniBtnOn = {
        borderColor: 'var(--dsw-alias-brand-primary, #6cf)',
        color: 'var(--dsw-alias-brand-primary, #6cf)',
        fontWeight: 600,
      };
      const quickChip = {
        padding: '3px 9px', borderRadius: 999, fontSize: 11.5, cursor: 'pointer',
        border: '1px solid var(--dsw-alias-border-l2)',
        background: 'var(--dsw-alias-bg-layer-2)',
        color: 'var(--dsw-alias-label-primary)',
      };
      const quickChipOn = {
        borderColor: 'var(--dsw-alias-brand-primary)',
        background: 'color-mix(in srgb, var(--dsw-alias-brand-primary) 18%, transparent)',
      };
      const inputStyle = {
        margin: 8, padding: '6px 8px', borderRadius: 6, fontSize: 12,
        border: '1px solid var(--dsw-alias-border-l2, rgba(128,128,128,.4))',
        background: '#2c2f36',
        color: 'inherit', outline: 'none',
      };
      const rowStyle = {
        padding: '5px 10px', cursor: 'pointer', display: 'flex', gap: 8, alignItems: 'baseline',
      };

      return el('div', { style: layerStyle, 'data-desktop-pet': 'layer' },
        // 换宠面板的加载转圈动画（内联注入一次，key 固定所以不会重复累积）
        el('style', { key: 'petSpin', dangerouslySetInnerHTML: { __html: '@keyframes msWzSpin{to{transform:rotate(360deg)}}' } }),
        // 加载中：显示转圈 + 文案，别让用户看着一片空白（纸娃娃首次合成要好几秒）
        petLoading || !data
          ? el('div', {
              style: {
                position: 'absolute', left: 16, bottom: 16,
                display: 'flex', alignItems: 'center', gap: 6,
                fontSize: 11, padding: '6px 10px',
                background: 'var(--dsw-alias-bg-layer-2)',
                border: '1px solid var(--dsw-alias-border-l2)',
                borderRadius: 10, pointerEvents: 'none', zIndex: 2,
              },
              'data-desktop-pet': 'loading',
            },
              el('span', {
                style: {
                  width: 12, height: 12, flex: '0 0 auto',
                  border: '2px solid var(--dsw-alias-border-l2, rgba(128,128,128,.35))',
                  borderTopColor: 'var(--dsw-alias-brand-primary, #4a8cff)',
                  borderRadius: '50%', animation: 'msWzSpin .7s linear infinite',
                },
              }),
              el('span', null, error ? `桌宠出错：${String(error).slice(0, 60)}` : '桌宠加载中…'))
          : null,
        el('div', {
          style: { ...petStyle, display: petLoading || !data ? 'none' : undefined },
          'data-desktop-pet': 'pet',
        'data-pet-walk': walkState,
        ref: (node) => { if (node) window.__petEl = node; },
          'data-pet-action': sheet.action,
          'data-pet-frame': snap.frame,
          role: 'img',
          'aria-label': `${data.name || data.id}（${sheet.action}）`,
          title: `${data.name || data.id} · ${data.id}　拖动我 / 点我一下`,
          onPointerDown,
          onMouseEnter: onPetEnter,
          onMouseLeave: onPetLeave,
          onPointerMove,
          onPointerUp: endDrag,
          onPointerCancel,
        }),
        // 换宠入口（跟着宠物走）
        el('button', {
          type: 'button', style: gearStyle, title: '换一只宠物',
          'data-desktop-pet': 'gear',
          onClick: (e) => { e.stopPropagation(); setPickerOpen((v) => !v); },
        }, '⚙'),
        pickerOpen
          ? el('div', { style: popStyle, 'data-desktop-pet': 'picker' },
              el('div', { style: { display: 'flex', alignItems: 'center', gap: 8, padding: '8px 10px 0' } },
                el('strong', { style: { fontSize: 12 } }, '换宠物'),
                el('span', { style: { fontSize: 11, opacity: 0.7 } }, `当前 ${data.name || data.id}`),
                el('span', {
                  style: { marginLeft: 'auto', cursor: 'pointer', opacity: 0.7 },
                  onClick: () => setPickerOpen(false),
                }, '✕')),
              el('div', { style: { display: 'flex', gap: 6, padding: '0 10px 6px' } },
                el('button', {
                  type: 'button',
                  style: { ...miniBtn, ...(data.mode === 'chara' ? miniBtnOn : {}) },
                  onClick: () => callApi('petSetChara', {})
                    .then(() => setMobId('chara:' + Date.now()))
                    .catch((e) => setPickErr(String(e.message || e))),
                }, '纸娃娃'),
                el('button', {
                  type: 'button',
                  style: { ...miniBtn, ...(data.mode !== 'chara' ? miniBtnOn : {}) },
                  onClick: () => callApi('petSetMob', {})
                    .then((d) => setMobId(d.mobId))
                    .catch((e) => setPickErr(String(e.message || e))),
                }, '怪物')),
              // ── 快速选择：常用宠物一行 chip，点一下即换（不用搜索、不用滚动）──
              el('div', { style: { padding: '0 10px 6px' } },
                el('div', { style: { fontSize: 11, opacity: 0.6, marginBottom: 4 } }, '快速选择'),
                el('div', {
                  style: { display: 'flex', gap: 4, flexWrap: 'wrap', maxHeight: 92, overflow: 'auto' },
                },
                  // 纸娃娃入口放最前（最常用的两个模式切换）
                  el('button', {
                    type: 'button',
                    style: { ...quickChip, ...(data.mode === 'chara' ? quickChipOn : {}) },
                    disabled: busy,
                    title: '用纸娃娃面板里配置的外观当桌宠',
                    onClick: useChara,
                  }, '纸娃娃'),
                  // 再给一个「回到上次那只怪物」（当前如果是纸娃娃，这就是回到怪物）
                  currentMobId
                    ? el('button', {
                        type: 'button',
                        style: { ...quickChip, ...(data.mode !== 'chara' ? quickChipOn : {}) },
                        disabled: busy,
                        title: `切回怪物 ${currentMobId}`,
                        onClick: () => useMob(currentMobId),
                      }, '上次的怪物')
                    : null,
                  // 经典候选（服务端 presets，最多 17 个）
                  (presetList.length ? presetList : []).map((p) => el('button', {
                    key: p.id,
                    type: 'button',
                    style: {
                      ...quickChip,
                      ...(String(p.id) === String(currentMobId) && data.mode !== 'chara' ? quickChipOn : {}),
                    },
                    disabled: busy,
                    title: `${p.name || '（无名字）'} · ${p.id}`,
                    onClick: () => pickPet(p.id),
                  }, p.name || p.id)))),
              el('input', {
                style: inputStyle, placeholder: '搜中文名或编号：花蘑菇 / 路西德 / 1210102',
                value: query, onChange: (e) => setQuery(e.target.value),
              }),
              pickErr ? el('div', { style: { padding: '0 10px 6px', color: 'var(--dsw-alias-state-error-primary, #f66)' } }, pickErr) : null,
              el('div', { style: { overflow: 'auto', flex: 1, paddingBottom: 6 } },
                pickLoading
                  ? el('div', { style: { padding: '10px', opacity: 0.75, display: 'flex', gap: 8, alignItems: 'center' } },
                      // 纯 CSS 转圈：不引任何图标包，颜色跟主题走
                      el('span', {
                        style: {
                          width: 13, height: 13, flex: '0 0 auto',
                          border: '2px solid var(--dsw-alias-border-l2, rgba(128,128,128,.35))',
                          borderTopColor: 'var(--dsw-alias-brand-primary, #4a8cff)',
                          borderRadius: '50%',
                          animation: 'msWzSpin .7s linear infinite',
                        },
                      }),
                      el('span', null, query.trim() ? '搜索中…' : '读取候选…'))
                  : candidates.length === 0
                  ? el('div', { style: { padding: '4px 10px', opacity: 0.6 } },
                      query.trim() ? '没有匹配的怪物' : '暂无候选')
                  : candidates.map((c) => el('div', {
                      key: c.id,
                      style: { ...rowStyle, background: String(c.id) === String(data.id) ? 'var(--dsw-alias-bg-layer-2, rgba(255,255,255,.08))' : 'transparent' },
                      onClick: () => !busy && pickPet(c.id),
                    },
                      el('span', { style: { opacity: 0.6, fontVariantNumeric: 'tabular-nums', minWidth: 58 } }, c.id),
                      el('span', null, c.name || '—')))),
              busy ? el('div', { style: { padding: '0 10px 8px', opacity: 0.7 } }, '切换中…') : null)
          : null);
    }

    return {
      inject: ['slots'],
      apply(ctx) {
        ctx.slots.inject('shell.overlay', () => ctx.slots.register(
          { name: 'shell.overlay', id: 'desktop-pet', order: 30 },
          DesktopPet,
        ));
      },
    };
    })();

    return {
      // layout 用于启动器的面板切换（ctx.layout.selectPanel）
      inject: ['slots', 'layout'],
      apply(ctx) {
        const layout = ctx.get('layout');
        // 启动就**异步预取**（用户口径：「插件启动的时候就异步加载，而不是点进页面才加载」）——
        // 部位表 / 椅子清单 / 资源包 / 精选地图四份一起并发发出去，存模块级缓存，
        // 组件挂载时先用缓存，进来就是有数据的画面。
        try { startPrefetch(); } catch { /* 预取失败不影响插件加载 */ }
        // 桌面宠物（原 @local/dsh-desktop-pet）：注册它自己的 shell.overlay 浮层。
        // 与「冒险岛地图背景」「启动器」同槽不冲突（id 不同、order 不同）。
        petClient.apply(ctx);
        ctx.slots.inject('main', () => ctx.slots.register({ name: 'main', key: PANEL_ID }, WzPanel));
        ctx.slots.inject('sidebar.panellist', () => ctx.slots.register(
          { name: 'sidebar.panellist', id: PANEL_ID, order: 40, label: () => '冒险岛资源' },
          WzIcon,
        ));
        ctx.slots.inject('shell.overlay', () => ctx.slots.register(
          { name: 'shell.overlay', id: 'maplestory-wz-background', order: -10, label: () => '冒险岛地图背景' },
          BackgroundHost,
        ));
        // 启动器：order 大一点，浮在背景之上但仍在其它 toast 之下
        ctx.slots.inject('shell.overlay', () => ctx.slots.register(
          { name: 'shell.overlay', id: 'maplestory-wz-launcher', order: 5, label: () => '打开冒险岛资源面板' },
          (props) => el(PanelLauncher, { ...props, layout }),
        ));
      },
    };
  },
});
