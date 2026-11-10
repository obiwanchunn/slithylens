// SlithyLens for iPhone — a web app (add it to the Home Screen).
// Everything happens on the phone: photos are never uploaded anywhere.
'use strict';

const VERSION = 'iPhone v002 (2026-11-10)';

const FULL_QUALITY = 0.92;            // name.jpg, name_crop.jpg, name_lens.jpg
const SMALL_LIMIT = 500000;           // bytes; fits both "500 KB" conventions
const SMALL_Q_MAX = 90, SMALL_Q_MIN = 70;
// iPhone Safari refuses canvases above ~16.7 megapixels, so bigger photos are worked on
// at that size (crops of 24/48 MP photos still come from the full-size original).
const MAX_PIXELS = 16777216;
const MAX_STACK = 5;                  // photos kept for "‹ Back" (each one uses memory)

const HANDLE = 9;                     // drawn handle radius (CSS px)
const GRAB = 26;                      // finger tolerance
const LOUPE_R = 62;

// ------------------------------------------------------------------ elements
const $ = id => document.getElementById(id);
const view = $('view'), ctx = view.getContext('2d');
const els = {
  title: $('title'), back: $('backBtn'), menuBtn: $('menuBtn'), empty: $('empty'),
  status: $('statusText'), save: $('saveBtn'), tools: $('tools'), editBar: $('editBar'),
  open: $('openBtn'), emptyOpen: $('emptyOpenBtn'), jpg: $('jpgBtn'), small: $('smallBtn'),
  crop: $('cropBtn'), lens: $('lensBtn'), cancel: $('cancelBtn'), apply: $('applyBtn'),
  file: $('fileInput'), menu: $('menu'), version: $('versionText'),
  reload: $('reloadBtn'), closeMenu: $('closeMenuBtn'),
};
els.version.textContent = VERSION;

// ------------------------------------------------------------------ state
/**
 * A photo on the stack.
 * { title, base, canvas (working pixels), w, h, preview (≤2048 px canvas),
 *   source (full-res drawable or null), srcW, srcH, bytes, isJpeg,
 *   blob (results only), saved, crop, quad }
 */
const stack = [];
let photo = null;
let mode = 'view';            // 'view' | 'crop' | 'lens'
let busy = false;

let scale = 1, ox = 0, oy = 0, fitMode = true;   // screen = image * scale + (ox, oy), CSS px
let crop = null;                                 // {x, y, w, h} image px
let quad = null;                                 // [TL, TR, BR, BL] {x, y} image px

// ------------------------------------------------------------------ helpers
const sleep = ms => new Promise(r => setTimeout(r, ms));
const frame = () => new Promise(r => requestAnimationFrame(() => setTimeout(r, 0)));
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
const fmtSize = b => b >= 1e6 ? (b / 1e6).toFixed(1) + ' MB' : Math.max(1, Math.round(b / 1000)) + ' KB';
const fmtDims = (w, h) => `${w.toLocaleString()} × ${h.toLocaleString()}`;

function makeCanvas(w, h) {
  const c = document.createElement('canvas');
  c.width = Math.max(1, Math.round(w)); c.height = Math.max(1, Math.round(h));
  return c;
}
function freeCanvas(c) { if (c && c.getContext) { c.width = 1; c.height = 1; } }

/** Good-quality downscale (halving steps, then a final smooth draw). */
function resizeCanvas(src, w, h) {
  let cur = src, cw = src.width, ch = src.height;
  while (cw / 2 >= w && ch / 2 >= h) {
    const next = makeCanvas(Math.round(cw / 2), Math.round(ch / 2));
    const g = next.getContext('2d');
    g.imageSmoothingEnabled = true; g.imageSmoothingQuality = 'high';
    g.drawImage(cur, 0, 0, next.width, next.height);
    if (cur !== src) freeCanvas(cur);
    cur = next; cw = next.width; ch = next.height;
  }
  const out = makeCanvas(w, h);
  const g = out.getContext('2d');
  g.imageSmoothingEnabled = true; g.imageSmoothingQuality = 'high';
  g.drawImage(cur, 0, 0, out.width, out.height);
  if (cur !== src) freeCanvas(cur);
  return out;
}

function encodeJpeg(canvas, quality) {
  return new Promise((resolve, reject) =>
    canvas.toBlob(b => b ? resolve(b) : reject(new Error('Could not encode the JPEG (out of memory?)')),
      'image/jpeg', quality));
}

function splitName(name) {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? { base: name.slice(0, dot), ext: name.slice(dot + 1).toLowerCase() } : { base: name, ext: '' };
}

// ------------------------------------------------------------------ status / UI state
function setStatus(text) { els.status.textContent = text; }

function describe(p) {
  const parts = [fmtDims(p.srcW || p.w, p.srcH || p.h)];   // the name is already in the title bar
  if (p.bytes) parts.push(fmtSize(p.bytes));
  if (p.blob && !p.saved) parts.push('not saved yet');
  if (p.blob && p.saved) parts.push('saved');
  return parts.join('  ·  ');
}

function updateUi() {
  const has = !!photo;
  els.empty.hidden = has;
  els.title.textContent = has ? photo.title : 'SlithyLens';
  els.back.hidden = stack.length < 2 || mode !== 'view';
  els.tools.hidden = mode !== 'view';
  els.editBar.hidden = mode === 'view';
  els.apply.textContent = mode === 'crop' ? 'Save crop' : 'Save lens';
  els.save.hidden = !(has && photo.blob) || mode !== 'view';
  els.save.textContent = has && photo && photo.saved ? 'Save again…' : 'Save…';

  const free = has && !busy && mode === 'view';
  els.open.disabled = busy;
  els.emptyOpen.disabled = busy;
  els.jpg.disabled = !free || !canMakeJpg(photo);
  els.small.disabled = !free || (photo.isJpeg && photo.bytes > 0 && photo.bytes <= SMALL_LIMIT);
  els.crop.disabled = !free;
  els.lens.disabled = !free;
  els.apply.disabled = busy;
  els.cancel.disabled = busy;
  els.save.disabled = busy;
  document.body.classList.toggle('busy', busy);
}

/** JPG would just be the same file again if the photo already is name.jpg. */
function canMakeJpg(p) {
  return !!p && (p.base + '.jpg').toLowerCase() !== p.title.toLowerCase();
}

// ------------------------------------------------------------------ opening
els.open.onclick = els.emptyOpen.onclick = () => { if (!busy) els.file.click(); };
els.file.onchange = async () => {
  const f = els.file.files && els.file.files[0];
  els.file.value = '';
  if (f) await openFile(f);
};

async function openFile(file) {
  busy = true; updateUi();
  setStatus(`Opening ${file.name}…`);
  await frame();
  let url = null;
  try {
    url = URL.createObjectURL(file);
    const img = new Image();
    img.decoding = 'async';
    img.src = url;
    try { await img.decode(); }
    catch { throw new Error(/\.(heic|heif|hif)$/i.test(file.name)
      ? 'this browser can’t read HEIC. On iPhone, use Safari (iOS 17 or later).'
      : 'not an image this browser can read.'); }

    const srcW = img.naturalWidth, srcH = img.naturalHeight;
    const k = Math.min(1, Math.sqrt(MAX_PIXELS / (srcW * srcH)));
    const w = Math.round(srcW * k), h = Math.round(srcH * k);
    const canvas = makeCanvas(w, h);
    const g = canvas.getContext('2d');
    g.imageSmoothingEnabled = true; g.imageSmoothingQuality = 'high';
    g.drawImage(img, 0, 0, w, h);

    const { base, ext } = splitName(file.name || 'photo.jpg');
    // A fresh photo replaces the whole stack.
    while (stack.length) releasePhoto(stack.pop());
    pushPhoto({
      title: file.name || 'photo', base, canvas, w, h,
      source: k < 1 ? img : null, srcW, srcH,
      bytes: file.size, isJpeg: ext === 'jpg' || ext === 'jpeg' || file.type === 'image/jpeg',
      blob: null, saved: false, crop: null, quad: null,
    });
    // keep the object URL alive while the full-size original is in use
    if (k < 1) { photo.url = url; url = null; }
    setStatus(describe(photo) + (k < 1 ? `  ·  working at ${fmtDims(w, h)}` : ''));
  } catch (e) {
    setStatus(`Couldn’t open ${file.name}: ${e.message}`);
  } finally {
    if (url) URL.revokeObjectURL(url);
    busy = false; updateUi();
  }
}

function makePreview(canvas) {
  const longest = Math.max(canvas.width, canvas.height);
  if (longest <= 2048) return null;
  const k = 2048 / longest;
  return resizeCanvas(canvas, Math.round(canvas.width * k), Math.round(canvas.height * k));
}

function pushPhoto(p) {
  p.preview = makePreview(p.canvas);
  stack.push(p);
  while (stack.length > MAX_STACK) releasePhoto(stack.shift());
  photo = p;
  mode = 'view';
  fit();
  updateUi();
}

function releasePhoto(p) {
  freeCanvas(p.canvas); freeCanvas(p.preview);
  if (p.url) URL.revokeObjectURL(p.url);
  p.source = null; p.blob = null;
}

els.back.onclick = () => {
  if (busy || stack.length < 2) return;
  releasePhoto(stack.pop());
  photo = stack[stack.length - 1];
  mode = 'view';
  fit(); updateUi();
  setStatus(describe(photo));
};

// ------------------------------------------------------------------ results
async function addResult(canvas, suffix, quality, blob) {
  const parent = photo;
  if (!blob) blob = await encodeJpeg(canvas, quality);
  const title = parent.base + suffix + '.jpg';
  pushPhoto({
    title, base: parent.base + suffix, canvas, w: canvas.width, h: canvas.height,
    source: null, srcW: canvas.width, srcH: canvas.height,
    bytes: blob.size, isJpeg: true, blob, saved: false, crop: null, quad: null,
  });
  setStatus(describe(photo));
}

async function run(label, work) {
  if (busy || !photo) return;
  busy = true; updateUi();
  setStatus(label);
  await frame();
  try { await work(); }
  catch (e) { setStatus('Failed: ' + (e && e.message ? e.message : e)); }
  finally { busy = false; updateUi(); draw(); }
}

/** JPG: full-size copy. */
els.jpg.onclick = () => run('Making JPG…', async () => {
  const p = photo;
  const out = makeCanvas(p.w, p.h);
  out.getContext('2d').drawImage(p.canvas, 0, 0);
  await addResult(out, '', FULL_QUALITY);
});

/** Small: best-looking JPG of 500 KB or less. */
els.small.onclick = () => run('Making small JPG (≤ 500 KB)…', async () => {
  const { canvas, blob } = await makeSmall(photo.canvas);
  await addResult(canvas, '_small', 0, blob);
});

async function makeSmall(src) {
  let k = 1;
  for (let attempt = 0; attempt < 10; attempt++) {
    const c = k < 0.999 ? resizeCanvas(src, Math.round(src.width * k), Math.round(src.height * k))
                        : (() => { const o = makeCanvas(src.width, src.height); o.getContext('2d').drawImage(src, 0, 0); return o; })();
    let best = await encodeJpeg(c, SMALL_Q_MAX / 100);
    if (best.size <= SMALL_LIMIT) return { canvas: c, blob: best };
    const low = await encodeJpeg(c, SMALL_Q_MIN / 100);
    if (low.size > SMALL_LIMIT) {
      k *= Math.sqrt(SMALL_LIMIT / low.size) * 0.97;   // size goes roughly with pixel count
      freeCanvas(c);
      continue;
    }
    best = low;
    let lo = SMALL_Q_MIN, hi = SMALL_Q_MAX;              // lo fits, hi doesn't
    while (hi - lo > 1) {
      const mid = (lo + hi) >> 1;
      const b = await encodeJpeg(c, mid / 100);
      if (b.size <= SMALL_LIMIT) { best = b; lo = mid; } else hi = mid;
    }
    return { canvas: c, blob: best };
  }
  throw new Error('couldn’t get it under 500 KB');
}

/** Crop: from the full-size original when the photo was too big to work on directly. */
async function makeCrop(p, r) {
  if (p.source) {
    const k = p.srcW / p.w;
    let sx = r.x * k, sy = r.y * k, sw = r.w * k, sh = r.h * k;
    let ow = Math.round(sw), oh = Math.round(sh);
    const fit = Math.min(1, Math.sqrt(MAX_PIXELS / (ow * oh)));
    ow = Math.round(ow * fit); oh = Math.round(oh * fit);
    const out = makeCanvas(ow, oh);
    const g = out.getContext('2d');
    g.imageSmoothingEnabled = true; g.imageSmoothingQuality = 'high';
    g.drawImage(p.source, sx, sy, sw, sh, 0, 0, ow, oh);
    return out;
  }
  const out = makeCanvas(r.w, r.h);
  out.getContext('2d').drawImage(p.canvas, r.x, r.y, r.w, r.h, 0, 0, r.w, r.h);
  return out;
}

// ------------------------------------------------------------------ lens (perspective straighten)
/** Maps the unit square onto the quad (Heckbert). Returns f(s, t) → [x, y]. */
function squareToQuad(q) {
  const [x0, y0, x1, y1, x2, y2, x3, y3] = [q[0].x, q[0].y, q[1].x, q[1].y, q[2].x, q[2].y, q[3].x, q[3].y];
  const dx3 = x0 - x1 + x2 - x3, dy3 = y0 - y1 + y2 - y3;
  let a, b, c, d, e, f, g, h;
  if (Math.abs(dx3) < 1e-9 && Math.abs(dy3) < 1e-9) {
    a = x1 - x0; b = x3 - x0; c = x0; d = y1 - y0; e = y3 - y0; f = y0; g = 0; h = 0;
  } else {
    const dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
    const den = dx1 * dy2 - dx2 * dy1;
    g = (dx3 * dy2 - dx2 * dy3) / den;
    h = (dx1 * dy3 - dx3 * dy1) / den;
    a = x1 - x0 + g * x1; b = x3 - x0 + h * x3; c = x0;
    d = y1 - y0 + g * y1; e = y3 - y0 + h * y3; f = y0;
  }
  return { a, b, c, d, e, f, g, h };
}

function quadIsValid(q) {
  let sign = 0;
  for (let i = 0; i < 4; i++) {
    const p0 = q[i], p1 = q[(i + 1) % 4], p2 = q[(i + 2) % 4];
    const cross = (p1.x - p0.x) * (p2.y - p1.y) - (p1.y - p0.y) * (p2.x - p1.x);
    if (Math.abs(cross) < 1e-6) return false;
    const s = Math.sign(cross);
    if (!sign) sign = s; else if (s !== sign) return false;
  }
  return true;
}

async function makeLens(p, q) {
  const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
  let W = Math.max(dist(q[0], q[1]), dist(q[3], q[2]));
  let H = Math.max(dist(q[0], q[3]), dist(q[1], q[2]));
  const fit = Math.min(1, Math.sqrt(MAX_PIXELS / (W * H)));
  W = Math.max(1, Math.round(W * fit)); H = Math.max(1, Math.round(H * fit));

  // Only read the part of the photo the shape covers.
  const bx0 = clamp(Math.floor(Math.min(...q.map(v => v.x))) - 2, 0, p.w - 1);
  const by0 = clamp(Math.floor(Math.min(...q.map(v => v.y))) - 2, 0, p.h - 1);
  const bx1 = clamp(Math.ceil(Math.max(...q.map(v => v.x))) + 2, bx0 + 1, p.w);
  const by1 = clamp(Math.ceil(Math.max(...q.map(v => v.y))) + 2, by0 + 1, p.h);
  const bw = bx1 - bx0, bh = by1 - by0;
  const src = p.canvas.getContext('2d').getImageData(bx0, by0, bw, bh).data;

  const out = makeCanvas(W, H);
  const og = out.getContext('2d');
  const img = og.createImageData(W, H);
  const dst = img.data;
  const m = squareToQuad(q);
  const maxX = bw - 1, maxY = bh - 1;

  for (let j = 0; j < H; j++) {
    const t = (j + 0.5) / H;
    for (let i = 0; i < W; i++) {
      const s = (i + 0.5) / W;
      const den = m.g * s + m.h * t + 1;
      // source position, pixel centres at +0.5, relative to the box
      let x = (m.a * s + m.b * t + m.c) / den - 0.5 - bx0;
      let y = (m.d * s + m.e * t + m.f) / den - 0.5 - by0;
      if (x < 0) x = 0; else if (x > maxX) x = maxX;
      if (y < 0) y = 0; else if (y > maxY) y = maxY;
      const xi = x | 0, yi = y | 0;
      const fx = x - xi, fy = y - yi;
      const xn = xi < maxX ? 1 : 0, yn = yi < maxY ? bw : 0;
      const i00 = (yi * bw + xi) * 4, i10 = i00 + xn * 4, i01 = i00 + yn * 4, i11 = i01 + xn * 4;
      const w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
      const o = (j * W + i) * 4;
      dst[o]     = src[i00]     * w00 + src[i10]     * w10 + src[i01]     * w01 + src[i11]     * w11;
      dst[o + 1] = src[i00 + 1] * w00 + src[i10 + 1] * w10 + src[i01 + 1] * w01 + src[i11 + 1] * w11;
      dst[o + 2] = src[i00 + 2] * w00 + src[i10 + 2] * w10 + src[i01 + 2] * w01 + src[i11 + 2] * w11;
      dst[o + 3] = 255;
    }
    if ((j & 255) === 255) await sleep(0);   // let the screen breathe on big images
  }
  og.putImageData(img, 0, 0);
  return out;
}

// ------------------------------------------------------------------ crop / lens modes
els.crop.onclick = () => {
  if (!photo || busy) return;
  const p = photo;
  crop = p.crop ? { ...p.crop } : { x: p.w * 0.05, y: p.h * 0.05, w: p.w * 0.9, h: p.h * 0.9 };
  mode = 'crop'; updateUi(); draw();
  setStatus('Drag the edges or corners, then Save crop.');
};

els.lens.onclick = () => {
  if (!photo || busy) return;
  const p = photo;
  const mx = p.w * 0.08, my = p.h * 0.08;
  quad = p.quad ? p.quad.map(v => ({ ...v }))
                : [{ x: mx, y: my }, { x: p.w - mx, y: my }, { x: p.w - mx, y: p.h - my }, { x: mx, y: p.h - my }];
  mode = 'lens'; updateUi(); draw();
  setStatus('Drag the four corners onto the page’s corners, then Save lens.');
};

function endEdit() {
  if (mode === 'crop') photo.crop = { ...crop };
  if (mode === 'lens') photo.quad = quad.map(v => ({ ...v }));
  mode = 'view'; updateUi(); draw();
}

els.cancel.onclick = () => { if (!busy) { endEdit(); setStatus(describe(photo)); } };

els.apply.onclick = () => {
  if (busy || !photo) return;
  if (mode === 'crop') {
    const p = photo;
    const x = clamp(Math.round(crop.x), 0, p.w - 1), y = clamp(Math.round(crop.y), 0, p.h - 1);
    const r = { x, y, w: clamp(Math.round(crop.x + crop.w), x + 1, p.w) - x, h: clamp(Math.round(crop.y + crop.h), y + 1, p.h) - y };
    endEdit();
    run('Cropping…', async () => addResult(await makeCrop(p, r), '_crop', FULL_QUALITY));
  } else if (mode === 'lens') {
    if (!quadIsValid(quad)) { setStatus('The corners are crossed. Drag them back into a four-sided shape.'); return; }
    const p = photo, q = quad.map(v => ({ ...v }));
    endEdit();
    run('Straightening…', async () => addResult(await makeLens(p, q), '_lens', FULL_QUALITY));
  }
};

// ------------------------------------------------------------------ saving (share sheet)
els.save.onclick = async () => {
  if (!photo || !photo.blob) return;
  const p = photo;
  const file = new File([p.blob], p.title, { type: 'image/jpeg' });
  try {
    if (navigator.canShare && navigator.canShare({ files: [file] })) {
      await navigator.share({ files: [file] });
    } else {
      const a = document.createElement('a');
      a.href = URL.createObjectURL(file); a.download = p.title;
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 30000);
    }
    p.saved = true;
    setStatus(describe(p));
  } catch (e) {
    if (e && e.name === 'AbortError') setStatus(describe(p));      // closed the share sheet
    else setStatus('Couldn’t save: ' + (e && e.message ? e.message : e));
  }
  updateUi();
};

// ------------------------------------------------------------------ menu
els.menuBtn.onclick = () => els.menu.showModal();
els.closeMenu.onclick = () => els.menu.close();
els.reload.onclick = async () => {
  try { const r = await navigator.serviceWorker?.getRegistration(); await r?.update(); } catch { }
  location.reload();
};

// ------------------------------------------------------------------ view: fit, zoom, draw
let cssW = 0, cssH = 0, dpr = 1;

function resize() {
  const r = view.getBoundingClientRect();
  dpr = window.devicePixelRatio || 1;
  const pw = cssW, ph = cssH;
  cssW = r.width; cssH = r.height;
  view.width = Math.round(cssW * dpr); view.height = Math.round(cssH * dpr);
  if (fitMode) fit();
  else { ox += (cssW - pw) / 2; oy += (cssH - ph) / 2; keepOnScreen(); draw(); }
}
new ResizeObserver(resize).observe(view);

function fitScale() {
  if (!photo) return 1;
  return Math.min((cssW - 16) / photo.w, (cssH - 16) / photo.h);
}

function fit() {
  fitMode = true;
  if (photo && cssW > 0) {
    scale = fitScale();
    ox = (cssW - photo.w * scale) / 2; oy = (cssH - photo.h * scale) / 2;
  }
  draw();
}

function zoomAt(sx, sy, newScale) {
  if (!photo) return;
  const min = Math.min(fitScale(), 1 / dpr) * 0.5, max = 16 / dpr;
  newScale = clamp(newScale, min, Math.max(min, max));
  const ix = (sx - ox) / scale, iy = (sy - oy) / scale;
  scale = newScale; ox = sx - ix * scale; oy = sy - iy * scale;
  fitMode = false; keepOnScreen(); draw();
}

function keepOnScreen() {
  if (!photo) return;
  const m = 60, lim = (v, lo, hi) => lo > hi ? (lo + hi) / 2 : clamp(v, lo, hi);
  ox = lim(ox, m - photo.w * scale, cssW - m);
  oy = lim(oy, m - photo.h * scale, cssH - m);
}

const toScreen = (x, y) => ({ x: x * scale + ox, y: y * scale + oy });
const toImage = (x, y) => ({ x: (x - ox) / scale, y: (y - oy) / scale });

let drawQueued = false;
function draw() {
  if (drawQueued) return;
  drawQueued = true;
  requestAnimationFrame(() => { drawQueued = false; render(); });
}

function drawPhoto(g, p, s, x, y) {
  // use the small preview when zoomed out: smoother and faster
  const usePreview = p.preview && s * dpr < p.preview.width / p.w;
  g.imageSmoothingEnabled = true; g.imageSmoothingQuality = 'high';
  g.drawImage(usePreview ? p.preview : p.canvas, x, y, p.w * s, p.h * s);
}

function render() {
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = '#1e1e1e';
  ctx.fillRect(0, 0, cssW, cssH);
  if (!photo) return;
  drawPhoto(ctx, photo, scale, ox, oy);
  if (mode === 'crop') drawCrop();
  else if (mode === 'lens') drawLens();
  if (drag && drag.kind === 'handle') drawLoupe();
}

function shadeOutside(pathFn) {
  const tl = toScreen(0, 0), br = toScreen(photo.w, photo.h);
  ctx.save();
  ctx.beginPath();
  ctx.rect(tl.x, tl.y, br.x - tl.x, br.y - tl.y);
  pathFn();
  ctx.fillStyle = 'rgba(0,0,0,.6)';
  ctx.fill('evenodd');
  ctx.restore();
}

function strokeTwice(pathFn) {
  ctx.lineJoin = 'round';
  ctx.beginPath(); pathFn(); ctx.strokeStyle = 'rgba(0,0,0,.7)'; ctx.lineWidth = 4; ctx.stroke();
  ctx.beginPath(); pathFn(); ctx.strokeStyle = '#3d9bff'; ctx.lineWidth = 2; ctx.stroke();
}

function cropHandles() {
  const r = crop, cx = r.x + r.w / 2, cy = r.y + r.h / 2;
  return [
    { x: r.x, y: r.y }, { x: cx, y: r.y }, { x: r.x + r.w, y: r.y }, { x: r.x + r.w, y: cy },
    { x: r.x + r.w, y: r.y + r.h }, { x: cx, y: r.y + r.h }, { x: r.x, y: r.y + r.h }, { x: r.x, y: cy },
  ];
}

function drawCrop() {
  const a = toScreen(crop.x, crop.y), b = toScreen(crop.x + crop.w, crop.y + crop.h);
  const rectPath = () => ctx.rect(a.x, a.y, b.x - a.x, b.y - a.y);
  shadeOutside(rectPath);
  ctx.strokeStyle = 'rgba(255,255,255,.45)'; ctx.lineWidth = 1;
  for (let i = 1; i <= 2; i++) {
    const x = a.x + (b.x - a.x) * i / 3, y = a.y + (b.y - a.y) * i / 3;
    ctx.beginPath(); ctx.moveTo(x, a.y); ctx.lineTo(x, b.y); ctx.moveTo(a.x, y); ctx.lineTo(b.x, y); ctx.stroke();
  }
  strokeTwice(rectPath);
  cropHandles().forEach((h, i) => {
    const p = toScreen(h.x, h.y), s = i % 2 === 0 ? HANDLE : HANDLE * 0.75;
    ctx.fillStyle = '#fff'; ctx.strokeStyle = '#3d9bff'; ctx.lineWidth = 2;
    ctx.fillRect(p.x - s, p.y - s, 2 * s, 2 * s); ctx.strokeRect(p.x - s, p.y - s, 2 * s, 2 * s);
  });
  label(`${Math.round(crop.w)} × ${Math.round(crop.h)}`, a.x + 8, a.y + 8);
}

function drawLens() {
  const pts = quad.map(v => toScreen(v.x, v.y));
  const quadPath = () => { ctx.moveTo(pts[0].x, pts[0].y); for (let i = 1; i < 4; i++) ctx.lineTo(pts[i].x, pts[i].y); ctx.closePath(); };
  shadeOutside(quadPath);
  const ok = quadIsValid(quad);
  ctx.beginPath(); quadPath(); ctx.strokeStyle = 'rgba(0,0,0,.7)'; ctx.lineWidth = 4; ctx.stroke();
  ctx.beginPath(); quadPath(); ctx.strokeStyle = ok ? '#3d9bff' : '#ff5a36'; ctx.lineWidth = 2; ctx.stroke();
  pts.forEach(p => {
    ctx.beginPath(); ctx.arc(p.x, p.y, HANDLE + 3, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(255,255,255,.9)'; ctx.fill();
    ctx.strokeStyle = '#3d9bff'; ctx.lineWidth = 2.5; ctx.stroke();
  });
  if (!ok) label('Corners are crossed', pts[0].x + 14, pts[0].y + 14);
}

function label(text, x, y) {
  ctx.font = '13px -apple-system, "Segoe UI", sans-serif';
  const w = ctx.measureText(text).width + 14;
  ctx.fillStyle = 'rgba(32,32,32,.8)';
  ctx.beginPath(); ctx.roundRect ? ctx.roundRect(x, y, w, 24, 5) : ctx.rect(x, y, w, 24); ctx.fill();
  ctx.fillStyle = '#fff'; ctx.textBaseline = 'middle'; ctx.fillText(text, x + 7, y + 12);
}

/** Magnifier above the finger, so you can see the corner you're placing. */
function drawLoupe() {
  const target = handlePoint(drag.handle);
  const at = toScreen(target.x, target.y);
  const gap = LOUPE_R + 46;
  let cx = at.x, cy = at.y - gap;
  if (cy - LOUPE_R < 4) cy = at.y + gap;                          // no room above: go below
  cx = clamp(cx, LOUPE_R + 4, cssW - LOUPE_R - 4);
  const z = Math.min(Math.max(scale * 4, 2 / dpr), 16 / dpr);

  ctx.save();
  ctx.beginPath(); ctx.arc(cx, cy, LOUPE_R, 0, Math.PI * 2); ctx.clip();
  ctx.fillStyle = '#1e1e1e'; ctx.fillRect(cx - LOUPE_R, cy - LOUPE_R, 2 * LOUPE_R, 2 * LOUPE_R);
  drawPhoto(ctx, photo, z, cx - target.x * z, cy - target.y * z);
  // the edges near the corner, so you can line them up
  ctx.strokeStyle = '#3d9bff'; ctx.lineWidth = 1.5; ctx.beginPath();
  const zp = (x, y) => ({ x: cx + (x - target.x) * z, y: cy + (y - target.y) * z });
  if (mode === 'lens') {
    const p = quad.map(v => zp(v.x, v.y));
    ctx.moveTo(p[0].x, p[0].y); for (let i = 1; i < 4; i++) ctx.lineTo(p[i].x, p[i].y); ctx.closePath();
  } else {
    const a = zp(crop.x, crop.y), b = zp(crop.x + crop.w, crop.y + crop.h);
    ctx.rect(a.x, a.y, b.x - a.x, b.y - a.y);
  }
  ctx.stroke();
  ctx.strokeStyle = 'rgba(255,70,70,.9)'; ctx.lineWidth = 1; ctx.beginPath();
  ctx.moveTo(cx - LOUPE_R, cy); ctx.lineTo(cx + LOUPE_R, cy); ctx.moveTo(cx, cy - LOUPE_R); ctx.lineTo(cx, cy + LOUPE_R);
  ctx.stroke();
  ctx.restore();
  ctx.beginPath(); ctx.arc(cx, cy, LOUPE_R, 0, Math.PI * 2);
  ctx.strokeStyle = 'rgba(0,0,0,.6)'; ctx.lineWidth = 5; ctx.stroke();
  ctx.strokeStyle = '#fff'; ctx.lineWidth = 2.5; ctx.stroke();
}

// ------------------------------------------------------------------ touch: pan, pinch, handles
const pointers = new Map();
let drag = null;             // {kind:'pan'|'handle'|'pinch', ...}
let lastTap = { t: 0, x: 0, y: 0 };

function localPoint(e) {
  const r = view.getBoundingClientRect();
  return { x: e.clientX - r.left, y: e.clientY - r.top };
}

function handlePoint(h) { return mode === 'lens' ? quad[h] : cropHandles()[h]; }

function hitHandle(p) {
  if (mode === 'lens') {
    let best = -1, bestD = GRAB;
    quad.forEach((v, i) => { const s = toScreen(v.x, v.y), d = Math.hypot(s.x - p.x, s.y - p.y); if (d <= bestD) { best = i; bestD = d; } });
    return best;
  }
  if (mode === 'crop') {
    const hs = cropHandles().map(v => toScreen(v.x, v.y));
    let best = -1, bestD = GRAB;
    hs.forEach((s, i) => {
      const d = Math.hypot(s.x - p.x, s.y - p.y) - (i % 2 === 0 ? 4 : 0);   // corners win ties
      if (d <= bestD) { best = i; bestD = d; }
    });
    if (best >= 0) return best;
    const a = toScreen(crop.x, crop.y), b = toScreen(crop.x + crop.w, crop.y + crop.h), t = 16;
    const inX = p.x > a.x && p.x < b.x, inY = p.y > a.y && p.y < b.y;
    if (inX && Math.abs(p.y - a.y) <= t) return 1;
    if (inY && Math.abs(p.x - b.x) <= t) return 3;
    if (inX && Math.abs(p.y - b.y) <= t) return 5;
    if (inY && Math.abs(p.x - a.x) <= t) return 7;
  }
  return -1;
}

function moveHandle(img) {
  const p = photo;
  img = { x: clamp(img.x, 0, p.w), y: clamp(img.y, 0, p.h) };
  if (mode === 'lens') { quad[drag.handle] = img; return; }
  const h = drag.handle, min = 8;
  let l = crop.x, t = crop.y, r = crop.x + crop.w, b = crop.y + crop.h;
  if (h === 0 || h === 6 || h === 7) l = Math.min(img.x, r - min);
  if (h === 2 || h === 3 || h === 4) r = Math.max(img.x, l + min);
  if (h === 0 || h === 1 || h === 2) t = Math.min(img.y, b - min);
  if (h === 4 || h === 5 || h === 6) b = Math.max(img.y, t + min);
  crop = { x: l, y: t, w: r - l, h: b - t };
}

function startPan(p) { drag = { kind: 'pan', sx: p.x, sy: p.y, ox, oy }; }

function startPinch() {
  const [a, b] = [...pointers.values()];
  const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
  drag = { kind: 'pinch', d0: Math.hypot(a.x - b.x, a.y - b.y) || 1, s0: scale,
           anchor: toImage(mid.x, mid.y) };
}

view.addEventListener('pointerdown', e => {
  if (!photo || busy) return;
  try { view.setPointerCapture(e.pointerId); } catch { /* fine without it */ }
  const p = localPoint(e);
  pointers.set(e.pointerId, p);
  if (pointers.size === 2) { startPinch(); draw(); return; }
  if (pointers.size > 2) return;

  const h = hitHandle(p);
  if (h >= 0) {
    const hp = handlePoint(h), s = toScreen(hp.x, hp.y);
    drag = { kind: 'handle', handle: h, dx: s.x - p.x, dy: s.y - p.y };
    draw();
    return;
  }
  // double-tap: fit ↔ 100%
  const now = performance.now();
  if (now - lastTap.t < 320 && Math.hypot(p.x - lastTap.x, p.y - lastTap.y) < 30) {
    lastTap.t = 0;
    if (fitMode) zoomAt(p.x, p.y, 1 / dpr); else fit();
    drag = null;
    return;
  }
  lastTap = { t: now, x: p.x, y: p.y };
  startPan(p);
});

view.addEventListener('pointermove', e => {
  if (!pointers.has(e.pointerId)) return;
  const p = localPoint(e);
  pointers.set(e.pointerId, p);
  if (!drag) return;
  if (drag.kind === 'pinch' && pointers.size >= 2) {
    const [a, b] = [...pointers.values()];
    const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    const d = Math.hypot(a.x - b.x, a.y - b.y);
    const min = Math.min(fitScale(), 1 / dpr) * 0.5, max = 16 / dpr;
    scale = clamp(drag.s0 * d / drag.d0, min, Math.max(min, max));
    ox = mid.x - drag.anchor.x * scale; oy = mid.y - drag.anchor.y * scale;
    fitMode = false; keepOnScreen(); draw();
  } else if (drag.kind === 'pan') {
    ox = drag.ox + (p.x - drag.sx); oy = drag.oy + (p.y - drag.sy);
    if (Math.abs(p.x - drag.sx) + Math.abs(p.y - drag.sy) > 3) fitMode = false;
    keepOnScreen(); draw();
  } else if (drag.kind === 'handle') {
    moveHandle(toImage(p.x + drag.dx, p.y + drag.dy));
    draw();
  }
});

function pointerEnd(e) {
  if (!pointers.has(e.pointerId)) return;
  pointers.delete(e.pointerId);
  if (pointers.size === 1 && drag && drag.kind === 'pinch') startPan([...pointers.values()][0]);
  else if (pointers.size === 0) drag = null;
  draw();
}
view.addEventListener('pointerup', pointerEnd);
view.addEventListener('pointercancel', pointerEnd);

// mouse wheel / trackpad (handy when testing on a computer)
view.addEventListener('wheel', e => {
  if (!photo) return;
  e.preventDefault();
  const p = localPoint(e);
  zoomAt(p.x, p.y, scale * Math.pow(1.2, -e.deltaY / 120));
}, { passive: false });

// stop iOS from zooming/scrolling the page itself
document.addEventListener('gesturestart', e => e.preventDefault());
document.addEventListener('dblclick', e => e.preventDefault());

// ------------------------------------------------------------------ offline support
if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost')) {
  navigator.serviceWorker.register('sw.js', { updateViaCache: 'none' }).catch(() => { /* still works online */ });
}

updateUi();
setStatus('Open a photo to get started.');
