/* The Ohman panel, running in the page. Not a video and not a picture: the same four pages, the same controls,
   the same layout numbers as src/Ui.xaml, driven by simulated hardware so every click does what it would do on a
   real machine. The firmware is the only thing missing.

   Kept deliberately dependency-free and in one file so it stays easy to check against the app it is imitating. */
(function () {
  'use strict';

  var HOST = document.getElementById('demo');
  if (!HOST) return;

  // ---------- the app's own numbers ----------
  var RAIL = 62, W_NARROW = 398, W_KBD = 638, W_SET = 640, H_SET = 640;
  var MODES = ['Eco', 'Balanced', 'Performance'];
  var MODE_COLOR = ['#2FBF8F', '#3F8CFF', '#E2572C'];
  var MODE_SUB = ['Quiet, cool, longer on battery', 'What the machine ships with', 'Everything the chassis will take'];
  var CURVE_TEMPS = [30, 40, 50, 60, 70, 80, 90];
  var CEIL = 57;                                  // the fan table's top level on the reference machine

  // zone ids are HP's: 0 right, 1 middle, 2 left, 3 WASD
  var ZR = 0, ZM = 1, ZL = 2, ZW = 3;
  var DEFAULT_COLORS = ['#2E6BFF', '#33A5E6', '#5FCBE0', '#F2ECE6'];

  var S = {
    page: 'home',
    mode: 1,
    fan: 'auto',                                  // auto | max | curve | manual
    power: 0, gpu: 3,
    f1: 30, f2: 30, linked: true, manualLink: true,
    floor: 0, ramp: 5, maxCool: true, stopAfter: 30,
    curve: [18, 20, 24, 30, 38, 48, 57],
    light: 1, fx: 0, level: 100, speed: 3,        // light: 0 off, 1 ours, 2 Windows
    colors: DEFAULT_COLORS.slice(),
    gran: 'Zone', sel: [], hover: [],
    hz: 120, gfx: 0,
    sw: { takeover: true, hotkeys: true, ecoBat: false, sync: true, guard: true, lowHz: false, tray: true, start: true, update: true },
    cpu: 55, gpuT: 46, rpm1: 3300, rpm2: 3100, load: 12, watts: 22
  };

  function accent() { return MODE_COLOR[S.mode]; }
  function clamp(v, a, b) { return v < a ? a : v > b ? b : v; }
  function pct(level) { return Math.round(level * 100 / CEIL) + '%'; }
  function rpm(level) { return level <= 0 ? '0 rpm' : (level * 100) + ' rpm'; }

  // ---------- tiny DOM helper ----------
  function el(tag, cls, txt) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (txt != null) n.textContent = txt;
    return n;
  }
  function svgNode(tag, attrs) {
    var n = document.createElementNS('http://www.w3.org/2000/svg', tag);
    for (var k in attrs) if (attrs.hasOwnProperty(k)) n.setAttribute(k, attrs[k]);
    return n;
  }
  function on(node, ev, fn) { node.addEventListener(ev, fn); return node; }

  // ---------- shell ----------
  var stage = el('div', 'demo-stage');
  var scaler = el('div', 'demo-scaler');
  var app = el('div', 'ohman');
  scaler.appendChild(app);
  stage.appendChild(scaler);
  HOST.appendChild(stage);

  var rail = el('div', 'oh-rail');
  var mark = el('div', 'oh-mark');
  var nav = el('div', 'oh-nav');
  var navPill = el('div', 'oh-pill');
  var bottom = el('div', 'oh-bottom');
  rail.appendChild(navPill); rail.appendChild(mark); rail.appendChild(nav); rail.appendChild(bottom);
  var host = el('div', 'oh-host');
  app.appendChild(rail); app.appendChild(host);

  var ICONS = {
    home: '<circle cx="9" cy="9" r="6.6" fill="none" stroke="currentColor" stroke-width="1.6"/><circle cx="9" cy="9" r="2.4" fill="currentColor"/>',
    fans: '<path d="M2 6.4c1.6-1.9 3.2-1.9 4.8 0s3.2 1.9 4.8 0 3.2-1.9 4.4 0M2 11.6c1.6-1.9 3.2-1.9 4.8 0s3.2 1.9 4.8 0 3.2-1.9 4.4 0" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>',
    keyboard: '<rect x="1.4" y="4.2" width="15.2" height="9.6" rx="2" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M4.2 7.2h.9M7 7.2h.9M9.8 7.2h.9M12.6 7.2h1.2M4.2 9.9h.9M7 9.9h.9M9.8 9.9h.9M12.6 9.9h1.2M6.2 12.2h5.6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/>',
    settings: '<path d="M9 11.4a2.4 2.4 0 100-4.8 2.4 2.4 0 000 4.8z" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M14.6 11a1.3 1.3 0 00.26 1.43l.05.05a1.55 1.55 0 11-2.2 2.2l-.04-.05a1.3 1.3 0 00-1.43-.26 1.3 1.3 0 00-.79 1.19v.13a1.55 1.55 0 01-3.1 0v-.07a1.3 1.3 0 00-.85-1.19 1.3 1.3 0 00-1.43.26l-.05.05a1.55 1.55 0 11-2.2-2.2l.05-.04a1.3 1.3 0 00.26-1.43 1.3 1.3 0 00-1.19-.79H1.8a1.55 1.55 0 010-3.1h.07a1.3 1.3 0 001.19-.85 1.3 1.3 0 00-.26-1.43l-.05-.05a1.55 1.55 0 112.2-2.2l.04.05a1.3 1.3 0 001.43.26h.07a1.3 1.3 0 00.79-1.19V1.8a1.55 1.55 0 013.1 0v.07a1.3 1.3 0 00.79 1.19 1.3 1.3 0 001.43-.26l.05-.05a1.55 1.55 0 112.2 2.2l-.05.04a1.3 1.3 0 00-.26 1.43v.07a1.3 1.3 0 001.19.79h.13a1.55 1.55 0 010 3.1h-.07a1.3 1.3 0 00-1.19.79z" fill="none" stroke="currentColor" stroke-width="1.4"/>'
  };

  var PAGES = ['home', 'fans', 'keyboard', 'settings'];
  var navBtns = {};
  function addNav(parent, key, tip) {
    var b = el('div', 'oh-navbtn');
    b.title = tip;
    b.innerHTML = '<svg viewBox="0 0 18 18">' + ICONS[key] + '</svg>';
    on(b, 'click', function () { go(key); });
    parent.appendChild(b);
    navBtns[key] = b;
  }
  addNav(nav, 'home', 'Home'); addNav(nav, 'fans', 'Fans'); addNav(nav, 'keyboard', 'Keyboard');
  addNav(bottom, 'settings', 'Settings');
  on(mark, 'click', function () { go('home'); });
  mark.style.cursor = 'pointer';
  mark.title = 'Home';

  var pages = {};
  PAGES.forEach(function (k) { pages[k] = el('div', 'oh-page'); host.appendChild(pages[k]); });

  // ---------- reusable controls ----------
  function Seg(items, cls, pick) {
    var wrap = el('div', 'oh-seg' + (cls ? ' ' + cls : ''));
    var thumb = el('div', 'oh-thumb');
    wrap.appendChild(thumb);
    var btns = items.map(function (it, i) {
      var b = el('button', null, typeof it === 'string' ? it : it.label);
      if (typeof it !== 'string' && it.disabled) { b.disabled = true; b.title = it.why || ''; }
      on(b, 'click', function () { if (!b.disabled) pick(i); });
      wrap.appendChild(b);
      return b;
    });
    wrap.select = function (i) {
      btns.forEach(function (b, j) { b.classList.toggle('on', j === i); });
      var b = btns[i];
      if (!b) return;
      thumb.style.background = accent();
      // measured after layout so the first paint lands in the right place
      requestAnimationFrame(function () {
        thumb.style.width = b.offsetWidth + 'px';
        thumb.style.transform = 'translateX(' + b.offsetLeft + 'px)';
      });
    };
    wrap.setLabel = function (i, s) { btns[i].textContent = s; };
    wrap.setEnabled = function (i, ok, why) { btns[i].disabled = !ok; btns[i].title = ok ? '' : (why || ''); };
    return wrap;
  }

  function Links(items, pick) {
    var wrap = el('div', 'oh-links');
    var ul = el('div', 'oh-ul');
    var spans = items.map(function (t, i) {
      var s = el('span', null, t);
      on(s, 'click', function () { if (!s.classList.contains('off')) pick(i); });
      wrap.appendChild(s);
      return s;
    });
    wrap.appendChild(ul);
    wrap.select = function (i) {
      spans.forEach(function (s, j) { s.classList.toggle('on', j === i); });
      var s = spans[i];
      if (!s) { ul.style.width = 0; return; }
      ul.style.background = accent();
      requestAnimationFrame(function () {
        ul.style.width = s.offsetWidth + 'px';
        ul.style.transform = 'translateX(' + s.offsetLeft + 'px)';
      });
    };
    wrap.setLabel = function (i, t) { spans[i].textContent = t; };
    wrap.setEnabled = function (i, ok, why) { spans[i].classList.toggle('off', !ok); spans[i].title = ok ? '' : (why || ''); };
    return wrap;
  }

  function Slider(min, max, step, get, set) {
    var s = el('div', 'oh-slider');
    var track = el('div', 'oh-track'), fill = el('div', 'oh-fill'), knob = el('div', 'oh-knob');
    s.appendChild(track); s.appendChild(fill); s.appendChild(knob);
    function place() {
      var t = (get() - min) / (max - min);
      fill.style.width = (t * 100) + '%';
      knob.style.left = (t * 100) + '%';
      fill.style.background = accent(); knob.style.background = accent();
    }
    function fromEvent(e) {
      var r = s.getBoundingClientRect();
      var x = ((e.touches ? e.touches[0].clientX : e.clientX) - r.left) / r.width;
      var v = min + clamp(x, 0, 1) * (max - min);
      set(Math.round(v / step) * step);
      place();
    }
    var dragging = false;
    on(s, 'pointerdown', function (e) { dragging = true; s.setPointerCapture(e.pointerId); fromEvent(e); });
    on(s, 'pointermove', function (e) { if (dragging) fromEvent(e); });
    on(s, 'pointerup', function () { dragging = false; });
    on(s, 'pointercancel', function () { dragging = false; });
    s.sync = place;
    return s;
  }

  function Switch(get, set, big) {
    var s = el('div', 'oh-switch' + (big ? ' lg' : ''));
    s.appendChild(el('i'));
    on(s, 'click', function () { set(!get()); render(); });
    s.sync = function () { s.classList.toggle('on', !!get()); s.style.background = get() ? accent() : ''; };
    return s;
  }

  function Row(label, sub, right) {
    var r = el('div', 'oh-row');
    var l = el('div');
    l.appendChild(el('div', 'oh-label', label));
    if (sub) l.appendChild(el('div', 'oh-sublabel', sub));
    r.appendChild(l);
    if (right) r.appendChild(right);
    r.setSub = function (t) { if (l.children[1]) l.children[1].textContent = t; };
    return r;
  }

  // ================================================================= HOME
  var home = pages.home;
  var hTitle = el('div', 'oh-title'), hStatus = el('div', 'oh-status');
  var head = el('div', 'oh-head'); head.appendChild(hTitle); head.appendChild(hStatus);
  home.appendChild(head);

  function bigBox() {
    var b = el('div', 'oh-big');
    var n = el('div', 'oh-bignum');
    var u = el('span', 'oh-bigunit');
    n.appendChild(u);
    var s = el('div', 'oh-bigsub');
    b.appendChild(n); b.appendChild(s);
    b.set = function (value, unit, sub) { n.firstChild.nodeValue = value; u.textContent = unit; s.textContent = sub; };
    n.insertBefore(document.createTextNode(''), u);
    return b;
  }
  var bCpu = bigBox(), bGpu = bigBox(), bF1 = bigBox(), bF2 = bigBox();
  var row1 = el('div', 'oh-bigrow'); row1.appendChild(bCpu); row1.appendChild(bGpu);
  var row2 = el('div', 'oh-bigrow'); row2.appendChild(bF1); row2.appendChild(bF2);
  home.appendChild(row1); home.appendChild(el('div', 'oh-rule')); home.appendChild(row2);

  var modeSeg = Seg(MODES, 'big', function (i) { S.mode = i; render(); });
  modeSeg.style.margin = '22px 0 4px';
  home.appendChild(modeSeg);

  var fanLinks = Links(['Auto', 'Max', 'Manual'], function (i) {
    if (i === 0) S.fan = 'auto';
    else if (i === 1) S.fan = 'max';
    else { if (S.fan !== 'curve' && S.fan !== 'manual') S.fan = 'manual'; go('fans'); }
    render();
  });
  var fansRow = Row('Fans', null, fanLinks);
  home.appendChild(fansRow);

  var powerVal = el('div', 'oh-val');
  var powerSlider = Slider(0, 15, 1, function () { return S.power; }, function (v) { S.power = v; render(); });
  var powerRight = el('div'); powerRight.style.cssText = 'display:flex;align-items:center;gap:14px;flex:1;max-width:260px';
  powerRight.appendChild(powerSlider); powerRight.appendChild(powerVal);
  home.appendChild(Row('Power gain', null, powerRight));

  var lightRow = Row('Lighting', 'Static · 4 zones', null);
  var miniWrap = el('div');
  miniWrap.style.cssText = 'display:flex;align-items:center;gap:14px';
  var mini = el('div', 'oh-kb noclick');
  mini.style.cssText = 'gap:2px';
  var arrow = el('div', 'oh-link', '→');
  miniWrap.appendChild(mini); miniWrap.appendChild(arrow);
  lightRow.appendChild(miniWrap);
  lightRow.style.cursor = 'pointer';
  on(lightRow, 'click', function () { go('keyboard'); });
  home.appendChild(lightRow);

  var hFoot = el('div', 'oh-foot');
  var hFootL = el('div'), hFootR = el('div');
  hFootL.innerHTML = '<span class="oh-dot"></span><span></span>';
  hFoot.appendChild(hFootL); hFoot.appendChild(hFootR);
  home.appendChild(hFoot);

  // ================================================================= FANS
  var fans = pages.fans;
  var fTitle = el('div', 'oh-title', 'Fans'), fStatus = el('div', 'oh-status');
  var fHead = el('div', 'oh-head'); fHead.appendChild(fTitle); fHead.appendChild(fStatus);
  fans.appendChild(fHead);

  var FANS = ['auto', 'max', 'curve', 'manual'];
  var fanSeg = Seg(['Auto', 'Max', 'Curve', 'Manual'], 'big', function (i) { S.fan = FANS[i]; render(); });
  fanSeg.style.marginBottom = '18px';
  fans.appendChild(fanSeg);

  // --- curve block
  var curveBlock = el('div');
  var curveHead = el('div', 'oh-head');
  var curveTitle = el('div', 'oh-label'), curveHint = el('div', 'oh-status');
  curveHead.appendChild(curveTitle); curveHead.appendChild(curveHint);
  curveHead.style.marginBottom = '10px';
  curveBlock.appendChild(curveHead);

  var PLOT_W = 350, PLOT_H = 196, PAD_L = 32, PAD_B = 26, PAD_T = 14;
  var svg = svgNode('svg', { viewBox: '0 0 ' + (PLOT_W + PAD_L) + ' ' + (PLOT_H + PAD_B + PAD_T), width: PLOT_W + PAD_L, height: PLOT_H + PAD_B + PAD_T });
  var curveHolder = el('div', 'oh-curve');
  curveHolder.appendChild(svg);
  curveBlock.appendChild(curveHolder);
  fans.appendChild(curveBlock);

  function px(i) { return PAD_L + (i / (CURVE_TEMPS.length - 1)) * PLOT_W; }
  function py(level) { return PAD_T + PLOT_H - (level / CEIL) * PLOT_H; }

  var gridG = svgNode('g'), areaPath = svgNode('path', { 'fill-opacity': '.14' }), linePath = svgNode('path', { fill: 'none', 'stroke-width': '2', 'stroke-linejoin': 'round', 'stroke-linecap': 'round' });
  var dotsG = svgNode('g'), axisG = svgNode('g');
  svg.appendChild(gridG); svg.appendChild(areaPath); svg.appendChild(linePath); svg.appendChild(axisG); svg.appendChild(dotsG);

  [0, 25, 50, 75, 100].forEach(function (p) {
    var y = PAD_T + PLOT_H - (p / 100) * PLOT_H;
    gridG.appendChild(svgNode('line', { x1: PAD_L, y1: y, x2: PAD_L + PLOT_W, y2: y, stroke: '#2C2825', 'stroke-width': 1 }));
    var t = svgNode('text', { x: PAD_L - 8, y: y + 3.5, 'text-anchor': 'end', class: 'oh-axis' });
    t.textContent = p; axisG.appendChild(t);
  });
  CURVE_TEMPS.forEach(function (temp, i) {
    if (i) gridG.appendChild(svgNode('line', { x1: px(i), y1: PAD_T, x2: px(i), y2: PAD_T + PLOT_H, stroke: '#241F1C', 'stroke-width': 1 }));
    var t = svgNode('text', { x: px(i), y: PAD_T + PLOT_H + 17, 'text-anchor': 'middle', class: 'oh-axis' });
    t.textContent = temp + '°'; axisG.appendChild(t);
  });

  var dots = CURVE_TEMPS.map(function (_, i) {
    var c = svgNode('circle', { r: 5, class: 'oh-pointdot', 'stroke-width': 2, stroke: '#161311' });
    dotsG.appendChild(c);
    dragPoint(c, i);
    return c;
  });

  function dragPoint(node, i) {
    var dragging = false, startY = 0, startLevels = null;
    function apply(e, shift) {
      var r = svg.getBoundingClientRect();
      var scale = r.height / (PLOT_H + PAD_B + PAD_T);
      var y = (e.clientY - r.top) / scale;
      var level = Math.round(clamp((PAD_T + PLOT_H - y) / PLOT_H, 0, 1) * CEIL);
      if (shift && startLevels) {
        var delta = level - startLevels[i];
        S.curve = startLevels.map(function (v) { return clamp(v + delta, 0, CEIL); });
      } else {
        S.curve[i] = level;
        for (var j = i + 1; j < S.curve.length; j++) if (S.curve[j] < S.curve[i]) S.curve[j] = S.curve[i];
        for (var k = i - 1; k >= 0; k--) if (S.curve[k] > S.curve[i]) S.curve[k] = S.curve[i];
      }
      render();
    }
    on(node, 'pointerdown', function (e) {
      if (S.fan !== 'curve') return;
      dragging = true; startY = e.clientY; startLevels = S.curve.slice();
      node.setPointerCapture(e.pointerId); e.preventDefault();
    });
    on(node, 'pointermove', function (e) { if (dragging) apply(e, e.shiftKey); });
    on(node, 'pointerup', function () { dragging = false; });
    on(node, 'pointercancel', function () { dragging = false; });
  }

  function drawCurve() {
    var levels = S.fan === 'curve' ? S.curve : [16, 18, 22, 28, 36, 46, 55];
    var d = '', area = '';
    levels.forEach(function (lv, i) {
      d += (i ? 'L' : 'M') + px(i) + ' ' + py(lv) + ' ';
    });
    area = d + 'L' + px(levels.length - 1) + ' ' + (PAD_T + PLOT_H) + ' L' + px(0) + ' ' + (PAD_T + PLOT_H) + ' Z';
    linePath.setAttribute('d', d); linePath.setAttribute('stroke', accent());
    areaPath.setAttribute('d', area); areaPath.setAttribute('fill', accent());
    dots.forEach(function (c, i) {
      c.setAttribute('cx', px(i)); c.setAttribute('cy', py(levels[i]));
      c.setAttribute('fill', accent());
      c.style.display = S.fan === 'curve' ? '' : 'none';
    });
    curveHolder.classList.toggle('ro', S.fan !== 'curve');
  }

  // --- max block
  var maxBlock = el('div', 'oh-bigrow');
  maxBlock.style.cssText = 'display:grid;grid-template-columns:1fr 1fr;gap:26px 24px';
  var mF1 = bigBox(), mF2 = bigBox(), mT = bigBox(), mM = bigBox();
  [mF1, mF2, mT, mM].forEach(function (b) { maxBlock.appendChild(b); });
  fans.appendChild(maxBlock);

  // --- manual block
  var manualBlock = el('div');
  var manRow = el('div', 'oh-bigrow');
  var manP1 = bigBox(), manP2 = bigBox();
  manRow.appendChild(manP1); manRow.appendChild(manP2);
  manualBlock.appendChild(manRow);
  var man1Val = el('div', 'oh-val'), man2Val = el('div', 'oh-val');
  var man1 = Slider(0, CEIL, 1, function () { return S.f1; }, function (v) { S.f1 = v; if (S.manualLink) S.f2 = v; render(); });
  var man2 = Slider(0, CEIL, 1, function () { return S.f2; }, function (v) { S.f2 = v; if (S.manualLink) S.f1 = v; render(); });
  function sliderRow(label, sl, val) {
    var r = el('div', 'oh-row');
    r.appendChild(el('div', 'oh-label', label));
    var right = el('div'); right.style.cssText = 'display:flex;align-items:center;gap:14px;flex:1;margin-left:24px';
    right.appendChild(sl); right.appendChild(val);
    r.appendChild(right);
    return r;
  }
  manualBlock.appendChild(sliderRow('CPU fan', man1, man1Val));
  manualBlock.appendChild(sliderRow('GPU fan', man2, man2Val));
  var manLinkSw = Switch(function () { return S.manualLink; }, function (v) { S.manualLink = v; if (v) S.f2 = S.f1; }, true);
  manualBlock.appendChild(Row('Move both together', null, manLinkSw));
  var safety = el('div', 'oh-sublabel', '●  For your safety, Ohman forces max fan above 90°');
  safety.style.cssText = 'color:#F3821D;margin-top:14px;font-size:12.5px';
  manualBlock.appendChild(safety);
  fans.appendChild(manualBlock);

  // --- per-mode options
  var optsAuto = el('div');
  var ecoSw = Switch(function () { return S.sw.ecoCool; }, function (v) { S.sw.ecoCool = v; }, true);
  optsAuto.appendChild(Row('Quieter policy for Eco', null, ecoSw));
  var editAsCurve = el('div', 'oh-link', 'Edit as curve');
  on(editAsCurve, 'click', function () { S.fan = 'curve'; render(); });

  var optsCurve = el('div');
  var linkSw = Switch(function () { return S.linked; }, function (v) { S.linked = v; }, true);
  optsCurve.appendChild(Row('Link GPU fan to CPU curve', null, linkSw));
  var floorVal = el('div', 'oh-val');
  var floorSl = Slider(0, 40, 1, function () { return S.floor; }, function (v) { S.floor = v; render(); });
  var fr = el('div'); fr.style.cssText = 'display:flex;align-items:center;gap:14px;flex:1;margin-left:24px';
  fr.appendChild(floorSl); fr.appendChild(floorVal);
  optsCurve.appendChild(sliderRowNode('Floor', fr));
  var rampVal = el('div', 'oh-val');
  var rampSl = Slider(1, 10, 1, function () { return S.ramp; }, function (v) { S.ramp = v; render(); });
  var rr = el('div'); rr.style.cssText = 'display:flex;align-items:center;gap:14px;flex:1;margin-left:24px';
  rr.appendChild(rampSl); rr.appendChild(rampVal);
  optsCurve.appendChild(sliderRowNode('Ramp delay', rr));
  function sliderRowNode(label, right) {
    var r = el('div', 'oh-row');
    r.appendChild(el('div', 'oh-label', label));
    r.appendChild(right);
    return r;
  }

  var optsMax = el('div');
  var coolSw = Switch(function () { return S.maxCool; }, function (v) { S.maxCool = v; }, true);
  optsMax.appendChild(Row('Back to Auto when cool', null, coolSw));
  var stopSeg = Seg(['15', '30', '60', 'Never'], 'row', function (i) { S.stopAfter = [15, 30, 60, 0][i]; render(); });
  optsMax.appendChild(Row('Stop after', null, stopSeg));

  fans.appendChild(optsAuto); fans.appendChild(optsCurve); fans.appendChild(optsMax);

  var fFoot = el('div', 'oh-foot');
  var fFootL = el('div'), fFootR = el('div');
  fFoot.appendChild(fFootL); fFoot.appendChild(fFootR);
  fans.appendChild(fFoot);

  // ================================================================= KEYBOARD
  var kbd = pages.keyboard;
  var kTitle = el('div', 'oh-title', 'Keyboard'), kStatus = el('div', 'oh-status');
  var kHead = el('div', 'oh-head'); kHead.appendChild(kTitle); kHead.appendChild(kStatus);
  kbd.appendChild(kHead);

  var LIGHT_NAMES = ['Off', 'Static', 'Breathe', 'Cycle', 'Wave', 'Windows'];
  var kbdModes = Links(LIGHT_NAMES, function (i) {
    if (i === 5) { S.light = 2; }
    else if (i === 0) { S.light = 0; }
    else { S.light = 1; S.fx = i - 1; }
    render();
  });
  kbdModes.style.margin = '0 0 22px';
  kbd.appendChild(kbdModes);

  var selRow = el('div', 'oh-row');
  selRow.style.cssText = 'padding:0;border:0;margin-bottom:22px';
  var selLeft = el('div'); selLeft.style.cssText = 'display:flex;align-items:center;gap:10px';
  var selLbl = el('div', 'oh-label', 'Select'); selLbl.style.color = 'var(--seg)'; selLbl.style.fontSize = '13px';
  var granSeg = Seg([
    { label: 'Key', disabled: true, why: 'This keyboard lights in zones, not per key' },
    { label: 'Row', disabled: true, why: 'This keyboard lights in zones, not per key' },
    'Zone', 'All'
  ], 'row', function (i) { S.gran = ['Key', 'Row', 'Zone', 'All'][i]; S.sel = []; render(); });
  selLeft.appendChild(selLbl); selLeft.appendChild(granSeg);
  var selHint = el('div', 'oh-status', 'Click the map to select');
  selRow.appendChild(selLeft); selRow.appendChild(selHint);
  kbd.appendChild(selRow);

  var board = el('div', 'oh-kb');
  kbd.appendChild(board);

  // the layout from src/Keyboard.cs, in key units
  var ROWS = [
    ['Esc', 'F1', 'F2', 'F3', 'F4', 'F5', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11', 'F12', 'Del'],
    ['`', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', '⌫:2.2'],
    ['Tab:1.5', 'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P', '[', ']', '\\:1.7'],
    ['Caps:1.8', 'A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', ';', "'", 'Enter:2.4'],
    ['Shift:2.3', 'Z', 'X', 'C', 'V', 'B', 'N', 'M', ',', '.', '/', 'Shift:2.9'],
    ['Ctrl', 'Fn', 'Win', 'Alt', ' :6.4', 'Alt', 'Ctrl', '◀', '▲▼', '▶']
  ];
  var KEYS = [], MINI = [];

  /* One pass builds both boards: the page's map, which is clickable and labelled, and the glyph on the Home row,
     which is the same layout at 9px a key. Zone assignment is the app's: WASD is its own zone, then left, middle
     and right by where the key's centre falls. */
  function buildBoard(container, small) {
    ROWS.forEach(function (row, ri) {
      var r = el('div', 'oh-kbrow');
      if (small) r.style.gap = '2px';
      var x = 0;
      row.forEach(function (spec) {
        var label = spec, w = 1, c = spec.lastIndexOf(':');
        if (c > 0) { label = spec.slice(0, c); w = parseFloat(spec.slice(c + 1)); }
        label = label.trim();
        var mid = x + w / 2;
        var wasd = label.length === 1 && 'WASD'.indexOf(label) >= 0;
        var zone = wasd ? ZW : mid < 4.6 ? ZL : mid < 9.2 ? ZM : ZR;
        var k = el('div', 'oh-key');
        k.style.flex = w + ' 0 auto';
        if (small) { k.style.height = '9px'; k.style.borderRadius = '2px'; MINI.push({ zone: zone, node: k }); }
        else {
          k.textContent = label;
          var rec = { label: label, x: x, w: w, row: ri, zone: zone, node: k, index: KEYS.length };
          KEYS.push(rec);
          on(k, 'click', function () { selectGroup(rec); });
          on(k, 'mouseenter', function () { S.hover = group(rec); paintBoard(); });
          on(k, 'mouseleave', function () { S.hover = []; paintBoard(); });
        }
        r.appendChild(k);
        x += w;
      });
      container.appendChild(r);
    });
  }
  buildBoard(board, false);
  buildBoard(mini, true);

  function group(rec) {
    if (S.gran === 'Key') return [rec.index];
    if (S.gran === 'Row') return KEYS.filter(function (k) { return k.row === rec.row; }).map(function (k) { return k.index; });
    if (S.gran === 'All') return KEYS.map(function (k) { return k.index; });
    return KEYS.filter(function (k) { return k.zone === rec.zone; }).map(function (k) { return k.index; });
  }
  function selectGroup(rec) { S.sel = group(rec); render(); }
  function selectedZones() {
    var z = {};
    S.sel.forEach(function (i) { z[KEYS[i].zone] = 1; });
    return Object.keys(z).map(Number);
  }

  // --- editor
  var editor = el('div', 'oh-editor');
  var cols = el('div', 'oh-cols');
  var leftCol = el('div', 'oh-leftcol');
  var hexRow = el('div'); hexRow.style.cssText = 'display:flex;align-items:center;gap:9px;height:34px';
  var hexBar = el('div', 'oh-hexbar');
  var chip = el('div', 'oh-chip');
  var hexHash = el('span'); hexHash.textContent = '#';
  hexHash.style.cssText = 'font-family:var(--code);font-size:13.5px;color:#7E7976;padding-left:10px';
  var hexIn = el('input', 'oh-hexin');
  hexIn.maxLength = 6; hexIn.spellcheck = false;
  hexBar.appendChild(chip); hexBar.appendChild(hexHash); hexBar.appendChild(hexIn);
  hexRow.appendChild(el('div', 'oh-icon', '◐')); hexRow.appendChild(hexBar);
  hexBar.style.flex = '1';
  leftCol.appendChild(hexRow);

  var levelRow = el('div'); levelRow.style.cssText = 'display:flex;align-items:center;gap:9px;height:26px;margin-top:6px';
  var levelVal = el('div', 'oh-val'); levelVal.style.minWidth = '34px'; levelVal.style.fontSize = '11.5px';
  var levelSl = Slider(5, 100, 5, function () { return S.level; }, function (v) { S.level = v; render(); });
  levelRow.appendChild(el('div', 'oh-icon', '☀')); levelRow.appendChild(levelSl); levelRow.appendChild(levelVal);
  leftCol.appendChild(levelRow);

  var strips = el('div', 'oh-strips');
  var hueStrip = el('div', 'oh-strip'), shadeStrip = el('div', 'oh-strip');
  strips.appendChild(hueStrip); strips.appendChild(shadeStrip);
  cols.appendChild(leftCol); cols.appendChild(strips);
  editor.appendChild(cols);

  var effRow = el('div', 'oh-effrow');
  var speedVal = el('div', 'oh-val'), lvl2Val = el('div', 'oh-val');
  var speedSl = Slider(1, 5, 1, function () { return S.speed; }, function (v) { S.speed = v; render(); });
  var lvl2Sl = Slider(5, 100, 5, function () { return S.level; }, function (v) { S.level = v; render(); });
  var effA = el('div'); effA.appendChild(el('div', 'oh-label', 'Speed')); effA.appendChild(speedSl); effA.appendChild(speedVal);
  var effB = el('div'); effB.appendChild(el('div', 'oh-label', 'Brightness')); effB.appendChild(lvl2Sl); effB.appendChild(lvl2Val);
  effRow.appendChild(effA); effRow.appendChild(effB);
  editor.appendChild(effRow);

  var kbdInfo = el('div', 'oh-sublabel');
  kbdInfo.style.cssText = 'font-size:13px;line-height:1.6;margin-top:2px';
  editor.appendChild(kbdInfo);
  kbd.appendChild(editor);

  // hue + shade strips
  function hsl2hex(h, s, l) {
    var a = s * Math.min(l, 1 - l);
    function f(n) {
      var k = (n + h / 30) % 12;
      var c = l - a * Math.max(-1, Math.min(Math.min(k - 3, 9 - k), 1));
      return Math.round(255 * c).toString(16).padStart(2, '0');
    }
    return '#' + f(0) + f(8) + f(4);
  }
  function hueOf(hex) {
    var n = parseInt(hex.slice(1), 16), r = (n >> 16) / 255, g = ((n >> 8) & 255) / 255, b = (n & 255) / 255;
    var mx = Math.max(r, g, b), mn = Math.min(r, g, b), d = mx - mn;
    if (d < 1e-6) return 0;
    var h = mx === r ? ((g - b) / d) % 6 : mx === g ? (b - r) / d + 2 : (r - g) / d + 4;
    return (h * 60 + 360) % 360;
  }
  for (var i = 0; i < 36; i++) (function (i) {
    var c = hsl2hex(i * 10, 0.85, 0.55);
    var cell = el('i'); cell.style.background = c;
    on(cell, 'click', function () { paint(c); });
    hueStrip.appendChild(cell);
  })(i);
  var shadeCells = [];
  for (var j = 0; j < 30; j++) {
    var cell = el('i');
    shadeCells.push(cell);
    shadeStrip.appendChild(cell);
    (function (cell) { on(cell, 'click', function () { paint(cell.dataset.c); }); })(cell);
  }
  function drawShades() {
    var cur = currentColor();
    var h = hueOf(cur);
    shadeCells.forEach(function (cell, i) {
      var t = i / 29, c = hsl2hex(h, 0.28 + t * 0.6, 0.95 - t * 0.76);
      cell.dataset.c = c; cell.style.background = c;
    });
  }
  function currentColor() {
    var z = selectedZones();
    if (!z.length) return S.colors[ZL];
    var first = S.colors[z[0]];
    for (var i = 1; i < z.length; i++) if (S.colors[z[i]] !== first) return first;
    return first;
  }
  function paint(hex) {
    var z = selectedZones();
    if (!z.length) z = [ZR, ZM, ZL, ZW];
    z.forEach(function (i) { S.colors[i] = hex; });
    S.light = 1; if (S.fx > 1) S.fx = 0;
    render();
  }
  on(hexIn, 'input', function () {
    var v = hexIn.value.trim().replace(/^#/, '');
    if (/^[0-9a-fA-F]{6}$/.test(v)) paint('#' + v);
  });

  // ================================================================= SETTINGS
  var settings = pages.settings;
  var sHead = el('div', 'oh-head');
  sHead.appendChild(el('div', 'oh-title', 'Settings'));
  sHead.appendChild(el('div', 'oh-status', 'board 8C58'));
  settings.appendChild(sHead);
  var scroll = el('div', 'oh-scroll');
  settings.appendChild(scroll);

  scroll.appendChild(el('div', 'oh-sec', 'Omen key'));
  var keySeg = Seg(['Cycle', 'Panel', 'Max fan', 'Run', 'Off'], 'row', function (i) { S.keyAct = i; render(); });
  S.keyAct = 1;
  var keyRow = el('div', 'oh-row');
  keyRow.style.cssText = 'display:block;padding-bottom:10px';
  keyRow.appendChild(el('div', 'oh-label', 'Opens'));
  keySeg.style.marginTop = '10px';
  keyRow.appendChild(keySeg);
  scroll.appendChild(keyRow);

  var swTakeover = Switch(function () { return S.sw.takeover; }, function (v) { S.sw.takeover = v; });
  scroll.appendChild(Row('Take over the OMEN key', 'Stops the vendor key handler and its logon task', swTakeover));
  var swHot = Switch(function () { return S.sw.hotkeys; }, function (v) { S.sw.hotkeys = v; });
  scroll.appendChild(Row('Hotkeys', 'Shift+F11 cycles · Ctrl+Alt+E/B/P modes · Ctrl+Alt+M max fan', swHot));

  scroll.appendChild(el('div', 'oh-sec', 'Power'));
  var gpuSeg = Seg(['Base', 'Boost', 'Max', 'Auto'], 'row', function (i) { S.gpu = i; render(); });
  var gpuRow = Row('GPU power', 'Follows the mode', gpuSeg);
  scroll.appendChild(gpuRow);
  var swEcoBat = Switch(function () { return S.sw.ecoBat; }, function (v) { S.sw.ecoBat = v; });
  scroll.appendChild(Row('Eco on battery', 'Restores your mode when plugged in', swEcoBat));
  var swSync = Switch(function () { return S.sw.sync; }, function (v) { S.sw.sync = v; });
  scroll.appendChild(Row('Sync Windows power mode', 'Efficiency · Balanced · Best performance', swSync));
  var swGuard = Switch(function () { return S.sw.guard; }, function (v) { S.sw.guard = v; });
  scroll.appendChild(Row('Thermal guard', 'Forces max fan above 90° CPU or 56° chassis, and when the fans read stalled', swGuard));

  scroll.appendChild(el('div', 'oh-sec', 'Display'));
  var gfxSeg = Seg(['Hybrid', 'iGPU only'], 'row', function (i) { S.gfx = i; render(); });
  scroll.appendChild(Row('Graphics', 'Takes effect after a restart', gfxSeg));
  var hzSeg = Seg(['48', '60', '120 Hz'], 'row', function (i) { S.hz = [48, 60, 120][i]; render(); });
  scroll.appendChild(Row('Refresh rate', 'Built-in display', hzSeg));
  var swLowHz = Switch(function () { return S.sw.lowHz; }, function (v) { S.sw.lowHz = v; });
  scroll.appendChild(Row('Lowest refresh rate on battery', 'Back to your choice when plugged in', swLowHz));

  scroll.appendChild(el('div', 'oh-sec', 'App'));
  var swTray = Switch(function () { return S.sw.tray; }, function (v) { S.sw.tray = v; });
  scroll.appendChild(Row('Temperature in the tray', 'CPU temperature drawn on the tray icon', swTray));
  var swStart = Switch(function () { return S.sw.start; }, function (v) { S.sw.start = v; });
  scroll.appendChild(Row('Start with Windows', 'Scheduled task, no prompt at logon', swStart));
  var swUpd = Switch(function () { return S.sw.update; }, function (v) { S.sw.update = v; });
  var updRight = el('div'); updRight.style.cssText = 'display:flex;align-items:center;gap:18px';
  updRight.appendChild(el('div', 'oh-link', 'Check now')); updRight.appendChild(swUpd);
  scroll.appendChild(Row('Check for updates', '2.0 · checked 2 hours ago', updRight));

  var sFoot = el('div', 'oh-foot');
  sFoot.style.justifyContent = 'flex-start';
  sFoot.style.gap = '26px';
  ['Diagnostics', 'Log'].forEach(function (t) { var n = el('div', null, t); n.style.cursor = 'pointer'; sFoot.appendChild(n); });
  var exitLink = el('div', 'oh-link', 'Exit Ohman');
  sFoot.appendChild(exitLink);
  settings.appendChild(sFoot);

  // ================================================================= navigation + morph
  function go(page) { S.page = page; render(); }

  function pageWidth() { return RAIL + (S.page === 'keyboard' ? W_KBD : S.page === 'settings' ? W_SET : W_NARROW); }

  function layout() {
    PAGES.forEach(function (k) {
      pages[k].classList.toggle('on', k === S.page);
      navBtns[k].classList.toggle('on', k === S.page);
    });
    var idx = PAGES.indexOf(S.page);
    navPill.style.opacity = 1;
    navPill.style.transform = 'translateY(' + (S.page === 'settings'
      ? (app.offsetHeight - 62) : (49 + 4 + idx * 46)) + 'px)';

    var w = pageWidth();
    app.style.width = w + 'px';
    // height: measure the visible page's natural height, the way the app measures its own
    var page = pages[S.page];
    var h;
    if (S.page === 'settings') h = H_SET;
    else {
      var prev = page.style.position;
      page.style.position = 'static';
      h = page.scrollHeight;
      page.style.position = prev;
    }
    app.style.height = Math.ceil(h) + 'px';
  }

  // ================================================================= render
  function render() {
    var acc = accent();
    app.style.setProperty('--acc', acc);
    app.style.setProperty('--acc-dim', acc + '2e');
    mark.style.setProperty('--mark-hi', shade(acc, -0.30));
    mark.style.setProperty('--mark-lo', shade(acc, 0.26));

    // home
    hTitle.textContent = MODES[S.mode];
    hStatus.textContent = (S.gpu === 3 ? ['GPU base', 'GPU boost', 'GPU max'][S.mode] : ['GPU base', 'GPU boost', 'GPU max'][S.gpu]) + ' · chassis 39°';
    bCpu.set(Math.round(S.cpu), '°C', 'CPU · ' + Math.round(S.load) + '% · 1.8 GHz · ' + Math.round(S.watts) + ' W');
    bGpu.set(Math.round(S.gpuT), '°C', 'GPU · 0% · 11 W');
    bF1.set(S.rpm1, ' rpm', 'CPU fan');
    bF2.set(S.rpm2, ' rpm', 'GPU fan');
    modeSeg.select(S.mode);
    fanLinks.setLabel(2, S.fan === 'curve' ? 'Curve' : 'Manual');
    fanLinks.select(S.fan === 'auto' ? 0 : S.fan === 'max' ? 1 : 2);
    powerVal.textContent = '+' + S.power + ' W';
    powerSlider.sync();
    lightRow.setSub(S.light === 2 ? 'Windows Dynamic Lighting' : S.light === 0 ? 'Off'
      : ['Static', 'Breathe', 'Cycle', 'Wave'][S.fx] + ' · 4 zones');
    hFootL.lastChild.textContent = 'Transcend 14 · 2 fans';
    hFootR.textContent = 'AC · 100%';

    // fans
    fanSeg.select(FANS.indexOf(S.fan));
    fStatus.textContent = S.rpm1 + ' / ' + S.rpm2 + ' rpm · ' + Math.round(Math.max(S.rpm1, S.rpm2) / (CEIL * 100) * 100) + '%';
    curveBlock.style.display = (S.fan === 'auto' || S.fan === 'curve') ? '' : 'none';
    maxBlock.style.display = S.fan === 'max' ? 'grid' : 'none';
    manualBlock.style.display = S.fan === 'manual' ? '' : 'none';
    optsAuto.style.display = S.fan === 'auto' ? '' : 'none';
    optsCurve.style.display = S.fan === 'curve' ? '' : 'none';
    optsMax.style.display = S.fan === 'max' ? '' : 'none';
    curveTitle.textContent = S.fan === 'auto' ? "This model's curve" : 'CPU curve';
    curveHint.textContent = S.fan === 'auto' ? 'read-only' : 'drag a point · shift-drag moves all';
    drawCurve();
    mF1.set(S.rpm1, '', 'CPU fan · ' + pct(Math.round(S.rpm1 / 100)));
    mF2.set(S.rpm2, '', 'GPU fan · ' + pct(Math.round(S.rpm2 / 100)));
    mT.set(Math.round(S.cpu), '°', 'CPU · steady');
    mM.set(S.stopAfter || 0, ' min', S.stopAfter ? 'Until it stops' : 'Running at max');
    manP1.set(pct(S.f1).replace('%', ''), '%', rpm(S.f1) + ' · CPU fan');
    manP2.set(pct(S.f2).replace('%', ''), '%', rpm(S.f2) + ' · GPU fan');
    man1Val.textContent = pct(S.f1); man2Val.textContent = pct(S.f2);
    man1.sync(); man2.sync();
    floorVal.textContent = S.floor ? pct(S.floor) : 'off';
    rampVal.textContent = S.ramp + ' s';
    floorSl.sync(); rampSl.sync();
    stopSeg.select([15, 30, 60, 0].indexOf(S.stopAfter));
    [linkSw, coolSw, manLinkSw, ecoSw].forEach(function (s) { s.sync(); });
    fFootL.textContent = 'Applied to ' + MODES[S.mode];
    fFootR.textContent = '';
    fFootR.appendChild(S.fan === 'auto' ? editAsCurve : S.fan === 'curve' ? resetCurve : tempsNode());

    // keyboard
    var lit = S.light === 1, pick = lit && S.fx <= 1, effect = lit && S.fx !== 0;
    kbdModes.select(S.light === 2 ? 5 : S.light === 0 ? 0 : 1 + S.fx);
    selRow.style.display = pick ? '' : 'none';
    cols.style.display = pick ? '' : 'none';
    effRow.style.display = effect ? 'flex' : 'none';
    effRow.style.marginTop = pick ? '18px' : '0';
    levelRow.style.display = pick && !effect ? '' : 'none';
    editor.style.display = 'block';
    kbdInfo.style.display = lit ? 'none' : 'block';
    kbdInfo.textContent = S.light === 0
      ? 'The backlight is off. Pick a mode to turn it back on, or press the keyboard backlight key.'
      : 'Windows Dynamic Lighting has the keyboard. Its colours and effects come from Windows settings.';
    kStatus.textContent = S.light === 2 ? 'windows lighting' : S.light === 0 ? 'backlight off'
      : S.fx >= 2 ? 'firmware effect · colour fixed' : '4 zones · ' + ['static', 'breathe'][S.fx];
    granSeg.select(['Key', 'Row', 'Zone', 'All'].indexOf(S.gran));
    selHint.textContent = S.sel.length ? selectionText() : 'Click the map to select';
    var cur = currentColor();
    chip.style.background = cur;
    if (document.activeElement !== hexIn) hexIn.value = cur.slice(1).toUpperCase();
    levelVal.textContent = S.level + '%';
    lvl2Val.textContent = S.level + '%';
    speedVal.textContent = S.speed;
    levelSl.sync(); lvl2Sl.sync(); speedSl.sync();
    drawShades();
    paintBoard();

    // settings
    keySeg.select(S.keyAct);
    gpuSeg.select(S.gpu);
    gpuRow.setSub(S.gpu === 3 ? 'Follows the mode' : ['Base TGP', 'PPAB', 'Custom TGP + PPAB'][S.gpu]);
    gfxSeg.select(S.gfx);
    hzSeg.select([48, 60, 120].indexOf(S.hz));
    [swTakeover, swHot, swEcoBat, swSync, swGuard, swLowHz, swTray, swStart, swUpd].forEach(function (s) { s.sync(); });

    layout();
  }

  var resetCurve = el('div', 'oh-link', 'Reset curve');
  on(resetCurve, 'click', function () { S.curve = [18, 20, 24, 30, 38, 48, 57]; render(); });
  function tempsNode() {
    var n = el('div', null, 'CPU ' + Math.round(S.cpu) + '° · GPU ' + Math.round(S.gpuT) + '°');
    return n;
  }
  function selectionText() {
    if (S.gran === 'All' || S.sel.length === KEYS.length) return 'every key';
    if (S.gran === 'Key') return KEYS[S.sel[0]].label.toLowerCase() + ' key';
    if (S.gran === 'Row') return 'one row';
    var names = selectedZones().map(function (z) { return ['right', 'middle', 'left', 'wasd'][z]; });
    return names.join(' + ');
  }

  function shade(hex, t) {
    var n = parseInt(hex.slice(1), 16), r = n >> 16, g = (n >> 8) & 255, b = n & 255;
    var to = t > 0 ? 255 : 0, k = Math.abs(t);
    function m(c) { return Math.round(c + (to - c) * k); }
    return '#' + [m(r), m(g), m(b)].map(function (v) { return v.toString(16).padStart(2, '0'); }).join('');
  }

  var wavePhase = 0;
  function paintBoard() {
    var on_ = S.light !== 0;
    var dim = S.level / 100;
    var sel = {}; S.sel.forEach(function (i) { sel[i] = 1; });
    var hov = {}; S.hover.forEach(function (i) { hov[i] = 1; });
    var anyHover = S.hover.length > 0;
    KEYS.forEach(function (k) {
      var c;
      if (!on_) c = '#1F1C1A';
      else if (S.light === 2) c = '#2B2826';
      else if (S.fx === 2) c = hsl2hex((wavePhase * 40) % 360, 0.85, 0.55);
      else if (S.fx === 3) c = hsl2hex((wavePhase * 40 + k.x * 22) % 360, 0.85, 0.55);
      else c = S.colors[k.zone];
      var l = S.fx === 1 && on_ && S.light === 1 ? (0.35 + 0.65 * (0.5 + 0.5 * Math.sin(wavePhase))) : 1;
      k.node.style.background = on_ && S.light === 1 ? shade(c, -(1 - dim * l) * 0.86) : c;
      k.node.style.color = on_ && S.light === 1 ? contrast(c) : '#6E6965';
      k.node.style.opacity = anyHover && !hov[k.index] ? 0.45 : 1;
      k.node.style.boxShadow = sel[k.index] ? 'inset 0 0 0 2px #FFF' : 'none';
    });
    MINI.forEach(function (m) {
      var c = !on_ ? '#2B2826' : S.light === 2 ? '#2B2826' : S.colors[m.zone];
      m.node.style.background = c;
    });
  }
  function contrast(hex) {
    var n = parseInt(hex.slice(1), 16);
    var lum = 0.2126 * (n >> 16) + 0.7152 * ((n >> 8) & 255) + 0.0722 * (n & 255);
    return lum > 150 ? '#1A1614' : '#F4F1EF';
  }

  // ---------- simulated hardware ----------
  var t0 = performance.now();
  function tick(now) {
    wavePhase = (now - t0) / 1000 * (S.speed * 0.5 + 0.4);
    if (S.light === 1 && S.fx > 0) paintBoard();
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);

  setInterval(function () {
    var pull = [46, 55, 68][S.mode] + S.power * 0.5;
    S.cpu += (pull - S.cpu) * 0.18 + (Math.random() - 0.5) * 1.6;
    S.gpuT += (pull - 9 - S.gpuT) * 0.15 + (Math.random() - 0.5) * 1.1;
    S.load += (([8, 14, 26][S.mode]) - S.load) * 0.2 + (Math.random() - 0.5) * 5;
    S.watts += (([12, 22, 44][S.mode]) - S.watts) * 0.2 + (Math.random() - 0.5) * 3;
    S.load = clamp(S.load, 1, 100); S.watts = clamp(S.watts, 4, 90);
    var want;
    if (S.fan === 'max') want = CEIL;
    else if (S.fan === 'manual') want = Math.max(S.f1, S.f2);
    else {
      var lv = S.fan === 'curve' ? S.curve : [16, 18, 22, 28, 36, 46, 55];
      want = curveAt(lv, S.cpu);
      if (S.fan === 'curve' && S.floor) want = Math.max(want, S.floor);
    }
    var target1 = want * 100, target2 = (S.fan === 'manual' ? S.f2 : want) * 100;
    S.rpm1 += Math.round((target1 - S.rpm1) * 0.34 / 1) ; S.rpm1 = Math.round(S.rpm1 / 100) * 100;
    S.rpm2 += Math.round((target2 - S.rpm2) * 0.34 / 1) ; S.rpm2 = Math.round(S.rpm2 / 100) * 100;
    render();
  }, 1400);

  function curveAt(levels, temp) {
    if (temp <= CURVE_TEMPS[0]) return levels[0];
    for (var i = 1; i < CURVE_TEMPS.length; i++) {
      if (temp <= CURVE_TEMPS[i]) {
        var t = (temp - CURVE_TEMPS[i - 1]) / (CURVE_TEMPS[i] - CURVE_TEMPS[i - 1]);
        return Math.round(levels[i - 1] + (levels[i] - levels[i - 1]) * t);
      }
    }
    return levels[levels.length - 1];
  }

  // ---------- fit to the column ----------
  function fit() {
    var avail = HOST.clientWidth;
    var w = pageWidth();
    var k = Math.min(1, avail / w);
    scaler.style.transform = k < 1 ? 'scale(' + k + ')' : '';
    scaler.style.height = k < 1 ? (app.offsetHeight * k) + 'px' : '';
    scaler.style.width = w + 'px';
  }
  window.addEventListener('resize', fit);
  var ro = window.ResizeObserver ? new ResizeObserver(fit) : null;
  if (ro) ro.observe(app);

  render();
  requestAnimationFrame(function () { render(); fit(); });
})();
