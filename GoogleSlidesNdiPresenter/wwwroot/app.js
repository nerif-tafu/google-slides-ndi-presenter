// Google Slides NDI Presenter — app UI (desktop window and web remote).
(() => {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);
  const app = $('#app');

  // The desktop window passes a secret in the URL fragment; it unlocks settings and restart.
  const token = (() => {
    const m = location.hash.match(/token=([A-F0-9]+)/i);
    if (m) {
      sessionStorage.setItem('appToken', m[1]);
      history.replaceState(null, '', location.pathname);
    }
    return sessionStorage.getItem('appToken');
  })();
  const isDesktop = !!token;

  let state = null;
  let info = null;

  // ------------------------------------------------------------------ API

  async function api(path, { method = 'POST', body, privileged = false } = {}) {
    const headers = {};
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (privileged && token) headers['X-App-Token'] = token;
    const res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    let data = null;
    try { data = await res.json(); } catch { /* empty body */ }
    if (!res.ok || (data && data.error)) throw new Error((data && data.error) || `Request failed (${res.status})`);
    if (data && 'slideCount' in data) render(data);
    return data;
  }
  const act = path => api(path).catch(err => toast(err.message, 'error'));

  // ------------------------------------------------------------------ Live state (Server-Sent Events)

  let connTimer = null;
  function connect() {
    const es = new EventSource('/api/events');
    es.onmessage = e => {
      clearTimeout(connTimer);
      $('#connBanner').hidden = true;
      render(JSON.parse(e.data));
    };
    es.onerror = () => {
      clearTimeout(connTimer);
      connTimer = setTimeout(() => { $('#connBanner').hidden = false; }, 1500);
    };
  }

  async function loadInfo() {
    try {
      info = await (await fetch('/api/info')).json();
    } catch { info = null; }
    renderRemote();
  }

  // ------------------------------------------------------------------ Helpers

  const QUALITY = { hd2160: '4K', hd1440: '1440p', hd1080: '1080p', hd720: '720p', large: '480p', medium: '360p', small: '240p', tiny: '144p' };
  const qualityLabel = q => QUALITY[q] || '';

  function hashHue(s) {
    let h = 0;
    for (const c of s) h = (h * 31 + c.charCodeAt(0)) >>> 0;
    return h % 360;
  }
  function artStyle(title) {
    const h = hashHue(title || '?');
    return `background: linear-gradient(135deg, hsl(${h} 70% 58%), hsl(${(h + 45) % 360} 65% 38%))`;
  }
  function initials(title) {
    const words = (title || '?').trim().split(/\s+/).filter(Boolean);
    return ((words[0] || '?')[0] + (words[1] ? words[1][0] : '')).toUpperCase();
  }
  function timeAgo(iso) {
    const s = (Date.now() - new Date(iso).getTime()) / 1000;
    if (s < 60) return 'just now';
    if (s < 3600) return `${Math.floor(s / 60)} min ago`;
    if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
    const d = Math.floor(s / 86400);
    return d === 1 ? 'yesterday' : d < 7 ? `${d} days ago` : new Date(iso).toLocaleDateString();
  }
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
  const thumbUrl = (n, w) => `/api/slides/${n}.jpg?w=${w}&id=${state ? state.presentationId : ''}&v=${state ? state.version : 0}`;

  function parseSlidesUrl(raw) {
    const s = (raw || '').trim();
    if (!s) return { empty: true };
    if (/\/presentation\/d\/e\//.test(s))
      return { error: '“Publish to web” links can’t be used — copy the link from the Share button instead.' };
    const m = s.match(/\/presentation\/d\/([a-zA-Z0-9_-]{20,})/);
    if (m) return { id: m[1], url: s };
    if (/^[a-zA-Z0-9_-]{25,}$/.test(s)) return { id: s, url: s };
    if (/docs\.google\.com\/(document|spreadsheets|forms)/.test(s))
      return { error: 'That’s a Google Docs/Sheets link — paste a Google Slides link.' };
    return { error: 'That doesn’t look like a Google Slides link.' };
  }

  // ------------------------------------------------------------------ Toasts

  function toast(message, kind = 'info', action) {
    const el = document.createElement('div');
    el.className = `toast is-${kind}`;
    const icon = kind === 'error' ? 'i-alert' : kind === 'ok' ? 'i-check' : 'i-alert';
    el.innerHTML = `<svg class="ic"><use href="#${icon}"/></svg><span>${esc(message)}</span>`;
    if (action) {
      const b = document.createElement('button');
      b.className = 'btn btn-secondary';
      b.textContent = action.label;
      b.onclick = () => { action.run(); dismiss(); };
      el.append(b);
    }
    $('#toasts').append(el);
    const dismiss = () => { el.classList.add('is-leaving'); setTimeout(() => el.remove(), 200); };
    setTimeout(dismiss, kind === 'error' || action ? 8000 : 3500); // give time to use an action (e.g. Reopen)
  }

  // ------------------------------------------------------------------ Rendering

  let lastVersion = -1, lastThumbCount = -1, lastSlide = -1, lastRecentKey = '';

  function render(s) {
    const prev = state;
    state = s;

    // Loads started elsewhere (Companion, a phone, the HTTP API, or reopening the last deck at
    // startup) get the same overlay as ones started from this window.
    if (s.loading && !switching) setSwitching({ id: s.loadingId, title: s.loadingTitle, refresh: s.loadingRefresh, remote: true });
    else if (!s.loading && switching && switching.remote) setSwitching(null);

    const hasDeck = !!s.presentationId || s.slideCount > 0;
    app.dataset.view = hasDeck ? 'stage' : 'empty';

    // Load buttons
    for (const id of ['#refreshBtn', '#closeBtn', '#emptySubmit', '#openSubmit']) {
      const b = $(id);
      b.disabled = s.loading;
    }
    $('#refreshBtn').classList.toggle('is-busy', s.loading);
    renderOutOfDate(s);

    // Header
    const title = s.title || (s.loading ? 'Loading…' : 'Untitled presentation');
    $('#deckTitle').textContent = title;
    document.title = hasDeck ? `${title} · Slides Presenter` : 'Google Slides NDI Presenter';
    const updated = (s.status.match(/at (\d\d:\d\d)/) || [])[1];
    $('#deckMeta').textContent = s.slideCount
      ? `${plural(s.slideCount, 'slide')}${updated ? ` · Updated ${updated}` : ''}`
      : s.loading ? 'Downloading slides and notes…' : '';
    if (s.presentationId) $('#openInSlides').href = `https://docs.google.com/presentation/d/${s.presentationId}/edit`;
    const warn = $('#liveWarn');
    warn.hidden = !s.liveError;
    warn.title = s.liveError ? `${s.liveError} Animations and video won't play.` : '';

    // Outputs
    renderOutput($('#outSlides'), s.slidesNdiName, s.slidesReceivers, s.ndiError);
    renderOutput($('#outNotes'), s.notesNdiName, s.notesReceivers, s.ndiError);

    // Recents
    const recentKey = JSON.stringify([s.recent, s.presentationId]);
    if (recentKey !== lastRecentKey) { lastRecentKey = recentKey; renderRecents(); }

    // Filmstrip & progress
    if (s.version !== lastVersion || s.thumbnailCount !== lastThumbCount) {
      lastVersion = s.version; lastThumbCount = s.thumbnailCount;
      buildFilmstrip();
    }
    if (s.slide !== lastSlide || !prev || prev.version !== s.version) {
      lastSlide = s.slide;
      updateCurrentSlide();
    }

    // Slide toolbar (in live mode Next/Previous also step through animation builds, so they stay enabled)
    $('#posNow').textContent = s.slideCount ? s.slide : '–';
    $('#posTotal').textContent = s.slideCount || '–';
    $('#prevBtn').disabled = !s.slideCount || (!s.live && s.slide <= 1);
    $('#nextBtn').disabled = !s.slideCount || (!s.live && s.slide >= s.slideCount);
    renderVideo(s.video);

    // Notes toolbar
    const size = Math.round(s.notesAutoFit ? s.notesEffectiveFontSize : s.notesFontSize);
    $('#sizeVal').textContent = size;
    $('#sizeDown').disabled = size <= 12;
    $('#sizeUp').disabled = size >= 300;
    $('#autoBtn').setAttribute('aria-checked', String(s.notesAutoFit));
    $('#scrollUpBtn').disabled = s.notesScroll <= 0;
    $('#scrollDownBtn').disabled = s.notesScroll >= s.notesMaxScroll - 0.5;
  }

  // Shown once each time the open deck goes out of date, so it doesn't nag after every edit.
  let outOfDateToasted = '';

  function renderOutOfDate(s) {
    const btn = $('#refreshBtn');
    const stale = s.outOfDate && !s.loading;
    btn.classList.toggle('has-update', stale);
    btn.title = stale ? 'Updated in Google Slides — refresh to load the changes (R)' : 'Reload the presentation (R)';
    const key = stale ? `${s.presentationId}@${s.loadedRevision}` : '';
    if (key && key !== outOfDateToasted) {
      toast('This presentation has been updated in Google Slides.', 'info', { label: 'Refresh', run: refresh });
    }
    if (!s.loading) outOfDateToasted = key;
  }

  function renderOutput(el, name, receivers, error) {
    $('[data-name]', el).textContent = name || 'Unavailable';
    const status = $('[data-status]', el);
    if (error || !name) {
      el.dataset.state = 'error';
      status.textContent = 'NDI not available';
      el.title = error || '';
    } else {
      const via = state && state.ndiDiscovery ? `Registered with discovery server ${state.ndiDiscovery}.` : '';
      if (receivers > 0) {
        el.dataset.state = 'live';
        status.textContent = `${plural(receivers, 'receiver')} connected`;
        el.title = via;
      } else {
        el.dataset.state = 'idle';
        status.textContent = 'Waiting for a receiver';
        el.title = `The source is on the network; nothing is watching it yet. ${via}`.trim();
      }
    }
  }


  const clock = s => { s = Math.max(0, Math.floor(s || 0)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };

  // The play/rewind controls on the slide monitor work for both video and audio clips.
  function renderVideo(video) {
    const btn = $('#videoBtn');
    $('#videoControls').hidden = !video;
    if (!video) return;
    const noun = video.kind === 'audio' ? 'audio' : 'video';
    const playing = video.state === 'playing' || video.state === 'buffering';
    btn.classList.toggle('is-playing', playing);
    $('#videoIcon').setAttribute('href', playing ? '#i-pause' : '#i-play');
    $('#videoBtnText').textContent = `${playing ? 'Pause' : video.state === 'ended' ? 'Replay' : 'Play'} ${noun}`;
    const parts = [];
    if (video.state === 'buffering') parts.push('Buffering…');
    else if (video.duration > 0) parts.push(`${clock(video.position)} / ${clock(video.duration)}`);
    const q = qualityLabel(video.quality) || qualityLabel(video.best);
    if (noun === 'video' && q && video.state !== 'buffering') parts.push(q);
    $('#videoMeta').textContent = parts.join(' · ');
    btn.title = `${playing ? 'Pause' : 'Play'} the ${noun} on this slide (P)` +
      (noun === 'video' && video.best ? ` — best quality available: ${qualityLabel(video.best) || video.best}` : '');
  }

  // ------------------------------------------------------------------ Audio and video list

  const START_LABELS = { automatic: 'Automatically', 'on click': 'On click', manual: 'Manually' };
  function showMediaList() {
    if (anyDialogOpen() || !state) return;
    const media = state.media || [];
    const list = $('#mediaList');
    if (!media.length) {
      list.innerHTML = '<p class="media-empty">There’s no audio or video in this presentation.</p>';
    } else {
      list.innerHTML = `<table class="media-table">
        <thead><tr><th>Slide</th><th>Media</th><th>Starts</th><th>Sound</th></tr></thead>
        <tbody>${media.map(m => `
          <tr class="${m.slide === state.slide ? 'is-current' : ''}">
            <td class="media-slide">${m.slide}</td>
            <td><div class="media-what"><svg class="ic"><use href="#i-${m.kind === 'audio' ? 'audio' : 'video'}"/></svg>
              <span>${m.kind === 'audio' ? 'Audio' : 'Video'}<small title="${esc(m.name || '')}">${esc(m.name || '')}</small></span></div></td>
            <td><span class="pill ${m.start === 'automatic' ? 'pill-auto' : ''}">${START_LABELS[m.start] || esc(m.start)}</span></td>
            <td>${m.muted ? '<span class="pill pill-muted">Muted</span>' : '<span class="pill">On</span>'}</td>
          </tr>`).join('')}</tbody></table>`;
    }
    $('#mediaDialog').showModal();
  }
  $('#mediaInfoBtn').addEventListener('click', showMediaList);

  function recentItem(r, { removable }) {
    const li = document.createElement('li');
    const current = state && r.id === state.presentationId && !switching;
    const opening = switching && switching.id === r.id;
    li.className = 'recent' + (current ? ' is-current' : '') + (opening ? ' is-opening' : '');
    li.tabIndex = 0;
    li.title = r.url;
    li.innerHTML = `
      <div class="recent-art" style="${artStyle(r.title)}">${esc(initials(r.title))}</div>
      <div class="recent-text"><strong>${esc(r.title)}</strong><span>${opening ? (switching.refresh ? 'Refreshing…' : 'Opening…') : `${plural(r.slideCount, 'slide')} · ${timeAgo(r.lastOpened)}`}</span></div>
      ${opening ? '<span class="spinner spinner-sm" aria-label="Loading"></span>'
        : removable && !current ? '<button class="recent-remove" type="button" aria-label="Remove from recent" title="Remove from recent"><svg class="ic"><use href="#i-x"/></svg></button>' : ''}`;
    const open = () => {
      if (switching) return;
      if (!current) openPresentation(r.url, r.title);
      closeDialog('#openDialog'); closeMenu();
    };
    li.addEventListener('click', e => {
      if (e.target.closest('.recent-remove')) {
        e.stopPropagation();
        fetch(`/api/recent/${encodeURIComponent(r.id)}`, { method: 'DELETE' }).then(res => res.json()).then(render).catch(() => {});
        return;
      }
      open();
    });
    li.addEventListener('keydown', e => { if (e.key === 'Enter') open(); });
    return li;
  }

  function renderRecents() {
    const recent = state.recent || [];
    const side = $('#recentList');
    side.replaceChildren(...recent.map(r => recentItem(r, { removable: true })));
    $('#recentEmpty').hidden = recent.length > 0;

    const grid = $('#emptyRecent');
    grid.replaceChildren(...recent.slice(0, 6).map(r => {
      const b = document.createElement('button');
      b.className = 'card';
      b.type = 'button';
      b.title = r.url;
      b.innerHTML = `<div class="recent-art" style="${artStyle(r.title)}">${esc(initials(r.title))}</div>
        <strong>${esc(r.title)}</strong><span>${plural(r.slideCount, 'slide')} · ${timeAgo(r.lastOpened)}</span>`;
      b.onclick = () => openPresentation(r.url, r.title);
      return b;
    }));
    $('#emptyRecentWrap').hidden = recent.length === 0;
  }

  function renderRemote() {
    const box = $('#remoteUrls');
    const section = $('#remoteSection');
    section.hidden = !isDesktop; // a phone using the remote doesn't need to be told about itself
    if (!info) { box.innerHTML = ''; return; }
    if (info.warning) {
      box.innerHTML = `<p class="remote-off">${esc(info.warning)}</p>`;
    } else if (!info.remoteEnabled) {
      box.innerHTML = '<p class="remote-off">Off. Turn on “Allow other devices” in Settings.</p>';
    } else if (!info.remoteUrls.length) {
      box.innerHTML = '<p class="remote-off">Not connected to a network.</p>';
    } else {
      box.replaceChildren(...info.remoteUrls.map(u => {
        const row = document.createElement('div');
        row.className = 'remote-url';
        row.innerHTML = `<code>${esc(u.replace(/^http:\/\//, '').replace(/\/$/, ''))}</code>
          <button class="btn btn-ghost btn-icon" type="button" title="Copy link" aria-label="Copy link"><svg class="ic ic-sm"><use href="#i-copy"/></svg></button>`;
        $('button', row).onclick = async () => {
          try { await navigator.clipboard.writeText(u); toast('Link copied — open it on your phone or tablet.', 'ok'); }
          catch { toast('Couldn’t copy the link.', 'error'); }
        };
        return row;
      }));
    }
    $('#settingsBtn').hidden = !isDesktop;
  }

  // ------------------------------------------------------------------ Filmstrip & progress

  // The mouse wheel scrolls the filmstrip sideways (it never changes slide). At either end the
  // wheel falls through to scrolling the page as usual.
  $('#filmstrip').addEventListener('wheel', e => {
    const strip = e.currentTarget;
    if (e.ctrlKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return; // trackpad sideways swipes already work
    const delta = e.deltaY * (e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? strip.clientWidth : 1);
    const max = strip.scrollWidth - strip.clientWidth;
    if (max <= 0 || (delta < 0 && strip.scrollLeft <= 0) || (delta > 0 && strip.scrollLeft >= max - 1)) return;
    e.preventDefault();
    strip.scrollBy({ left: delta, behavior: 'instant' });
  }, { passive: false });

  let jumpFilmstrip = true;
  function buildFilmstrip() {
    const strip = $('#filmstrip');
    const n = state.thumbnailCount;
    $('#slideCountLabel').textContent = n ? String(n) : '';
    const items = [];
    for (let i = 1; i <= n; i++) {
      const b = document.createElement('button');
      b.className = 'thumb';
      b.type = 'button';
      b.dataset.n = i;
      b.setAttribute('role', 'option');
      b.innerHTML = `<div class="thumb-img"><img loading="lazy" alt="Slide ${i}" src="${thumbUrl(i, 640)}"></div><span class="thumb-label">${i}</span>`;
      const img = $('img', b);
      img.onload = () => img.classList.add('is-loaded');
      b.onclick = () => act(`/api/goto/${i}`);
      items.push(b);
    }
    strip.replaceChildren(...items);
    // A new deck starts at the beginning; the first positioning jumps instead of animating.
    strip.scrollTo({ left: 0, behavior: 'instant' });
    jumpFilmstrip = true;
  }

  function updateCurrentSlide() {
    const cur = state.slide;
    for (const t of $('#filmstrip').children) {
      const n = +t.dataset.n;
      t.classList.toggle('is-current', n === cur);
      t.classList.toggle('is-next', n === cur + 1);
      t.setAttribute('aria-selected', String(n === cur));
    }
    const current = $(`#filmstrip [data-n="${cur}"]`);
    if (current) {
      // Centre the current slide in the strip (without scrolling the page itself).
      const strip = $('#filmstrip');
      const r = current.getBoundingClientRect(), sr = strip.getBoundingClientRect();
      const left = strip.scrollLeft + (r.left - sr.left) - (strip.clientWidth - r.width) / 2;
      strip.scrollTo({ left: Math.max(0, left), behavior: jumpFilmstrip ? 'instant' : 'smooth' });
    }
    jumpFilmstrip = false;
  }

  // ------------------------------------------------------------------ Monitors (MJPEG streams)

  let streamWidth = 0;
  function startStreams() {
    if (document.hidden) return stopStreams();
    const screen = $('.screen');
    const w = Math.min(1920, Math.max(480, Math.ceil(((screen ? screen.clientWidth : 800) * devicePixelRatio) / 160) * 160));
    if (w === streamWidth && $('#slidesStream').src) return;
    streamWidth = w;
    $('#slidesStream').src = `/api/stream/slide.mjpg?w=${w}`;
    $('#notesStream').src = `/api/stream/notes.mjpg?w=${w}`;
  }
  function stopStreams() {
    streamWidth = 0;
    $('#slidesStream').removeAttribute('src');
    $('#notesStream').removeAttribute('src');
  }
  document.addEventListener('visibilitychange', startStreams);
  let resizeTimer;
  window.addEventListener('resize', () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(startStreams, 400); });

  // ------------------------------------------------------------------ Opening presentations

  // The presentation being switched to (blurs the main panel and marks its recent item).
  let switching = null;
  function setSwitching(value) {
    switching = value;
    app.classList.toggle('is-switching', !!value);
    if (value) {
      const verb = value.refresh ? 'Refreshing' : 'Opening';
      $('#switchTitle').textContent = value.title ? `${verb} “${value.title}”` : `${verb} presentation…`;
      $('#switchSub').textContent = value.refresh
        ? 'Downloading the latest slides and speaker notes'
        : 'Downloading slides and speaker notes';
    }
    if (state) renderRecents();
  }

  async function openPresentation(url, title) {
    const parsed = parseSlidesUrl(url);
    if (parsed.error || parsed.empty) { toast(parsed.error || 'Paste a Google Slides link first.', 'error'); return false; }
    if (switching) return false;
    // Reloading the deck that's already open is a refresh, not a switch.
    const isSwitch = !state || parsed.id !== state.presentationId;
    if (isSwitch) {
      const known = state && (state.recent || []).find(r => r.id === parsed.id);
      setSwitching({ id: parsed.id, title: title || (known && known.title) });
    }
    try {
      const s = await api('/api/load', { body: { url: parsed.url } });
      toast(`Opened “${s.title || 'presentation'}” · ${plural(s.slideCount, 'slide')}`, 'ok');
      requestAnimationFrame(startStreams);
      return true;
    } catch (err) {
      toast(err.message, 'error');
      return false;
    } finally {
      if (isSwitch) setSwitching(null);
    }
  }

  function wireUrlField(input, field, hint, defaultHint) {
    const update = () => {
      const p = parseSlidesUrl(input.value);
      field.classList.toggle('is-invalid', !!p.error);
      field.classList.toggle('is-valid', !!p.id);
      hint.classList.toggle('is-error', !!p.error);
      hint.innerHTML = p.error ? esc(p.error) : defaultHint;
      return p;
    };
    input.addEventListener('input', update);
    return update;
  }

  const DEFAULT_HINT = 'The presentation must be shared as <b>Anyone with the link can view</b>.';
  const validateEmpty = wireUrlField($('#emptyUrl'), $('#emptyField'), $('#emptyHint'), DEFAULT_HINT);
  const validateOpen = wireUrlField($('#openUrl'), $('#openField'), $('#openHint'), DEFAULT_HINT);

  $('#emptyForm').addEventListener('submit', async e => {
    e.preventDefault();
    if (validateEmpty().id) await openPresentation($('#emptyUrl').value);
    else $('#emptyUrl').focus();
  });

  async function showOpenDialog() {
    if (anyDialogOpen()) return;
    const dlg = $('#openDialog');
    $('#openUrl').value = '';
    validateOpen();
    $('#clipSuggest').hidden = true;
    dlg.showModal();
    $('#openUrl').focus();
    try {
      const text = (await navigator.clipboard.readText()).trim();
      const p = parseSlidesUrl(text);
      if (p.id && (!state || p.id !== state.presentationId)) {
        $('#clipText').textContent = text;
        $('#clipSuggest').hidden = false;
        $('#clipSuggest').onclick = () => { $('#openUrl').value = text; validateOpen(); submitOpen(); };
      }
    } catch { /* clipboard not available */ }
  }

  async function submitOpen() {
    const p = validateOpen();
    if (!p.id) { $('#openUrl').focus(); return; }
    closeDialog('#openDialog');
    await openPresentation($('#openUrl').value);
  }
  $('#openForm').addEventListener('submit', e => {
    e.preventDefault();
    submitOpen();
  });
  $('#openBtn').addEventListener('click', () => { closeMenu(); showOpenDialog(); });

  async function refresh() {
    if (!state || !state.url || state.loading || switching) return;
    setSwitching({ id: state.presentationId, title: state.title, refresh: true });
    try {
      await api('/api/refresh');
      toast('Presentation refreshed', 'ok');
    } catch (err) {
      toast(err.message, 'error');
    } finally {
      setSwitching(null);
    }
  }
  $('#refreshBtn').addEventListener('click', refresh);

  async function closePresentation() {
    if (!state || !state.presentationId || switching) return;
    const reopen = { url: state.url, title: state.title };
    try {
      await api('/api/close');
      toast(`Closed “${reopen.title || 'presentation'}”. The outputs are showing the holding screen.`, 'info',
        { label: 'Reopen', run: () => openPresentation(reopen.url, reopen.title) });
    } catch (err) { toast(err.message, 'error'); }
  }
  $('#closeBtn').addEventListener('click', closePresentation);

  // ------------------------------------------------------------------ Transport & notes

  $('#prevBtn').addEventListener('click', () => act('/api/previous'));
  $('#nextBtn').addEventListener('click', () => act('/api/next'));
  $('#videoBtn').addEventListener('click', () => act('/api/video/toggle'));
  $('#videoRestartBtn').addEventListener('click', () => act('/api/video/restart'));
  $('#scrollUpBtn').addEventListener('click', () => act('/api/notes/scroll/up'));
  $('#scrollDownBtn').addEventListener('click', () => act('/api/notes/scroll/down'));
  $('#autoBtn').addEventListener('click', () => act('/api/notes/autofit/toggle'));

  $('#sizeUp').addEventListener('click', () => act('/api/notes/increase'));
  $('#sizeDown').addEventListener('click', () => act('/api/notes/decrease'));

  // ------------------------------------------------------------------ Settings

  async function showSettings() {
    if (!isDesktop || anyDialogOpen()) return;
    const form = $('#settingsForm');
    $('#settingsError').textContent = '';
    try {
      const s = await (await fetch('/api/settings')).json();
      form.slidesNdiName.value = s.slidesNdiName;
      form.notesNdiName.value = s.notesNdiName;
      form.ndiDiscoveryServer.value = s.ndiDiscoveryServer || '';
      const res = `${s.width}x${s.height}`;
      if (![...form.resolution.options].some(o => o.value === res)) form.resolution.add(new Option(`${s.width} × ${s.height}`, res));
      form.resolution.value = res;
      if (![...form.frameRate.options].some(o => o.value === String(s.frameRate))) form.frameRate.add(new Option(`${s.frameRate} fps`, s.frameRate));
      form.frameRate.value = String(s.frameRate);
      form.remoteAccess.checked = s.remoteAccess;
      form.apiPort.value = s.apiPort;
      await fillAudio(form, s);
      await fillGoogle();
    } catch { toast('Couldn’t load settings.', 'error'); return; }
    $('#settingsDialog').showModal();
    form.slidesNdiName.focus();
    form.slidesNdiName.select();
  }
  $('#settingsBtn').addEventListener('click', () => { closeMenu(); showSettings(); });
  $('#emptySettingsBtn').addEventListener('click', showSettings);
  $('#emptySettingsBtn').hidden = !isDesktop;
  $('#emptyShortcutsBtn').addEventListener('click', () => { if (!anyDialogOpen()) $('#shortcutsDialog').showModal(); });

  // Audio settings apply live (no restart), so levels can be set while a video plays.
  async function fillAudio(form, s) {
    const { devices, error } = await (await fetch('/api/audio/devices')).json();
    const def = devices.find(d => d.isDefault);
    const options = (includeDefault) => [
      ...(includeDefault ? [new Option(`System default${def ? ` (${def.name})` : ''}`, 'default')] : []),
      ...devices.map(d => new Option(d.name, d.id)),
      new Option('Off', 'none'),
    ];
    form.audioOutputDevice.replaceChildren(...options(true));
    form.audioMonitorDevice.replaceChildren(...options(true));
    const pick = (select, value) => {
      if (![...select.options].some(o => o.value === value)) select.add(new Option('Unavailable device', value));
      select.value = value;
    };
    pick(form.audioOutputDevice, s.audioOutputDevice);
    pick(form.audioMonitorDevice, s.audioMonitorDevice);
    form.audioOutputVolume.value = s.audioOutputVolume;
    form.audioMonitorVolume.value = s.audioMonitorVolume;
    updateAudioLabels(form);
    $('#audioError').textContent = error || '';
  }
  function updateAudioLabels(form) {
    $('#outVolLabel').textContent = `${form.audioOutputVolume.value}%`;
    $('#monVolLabel').textContent = `${form.audioMonitorVolume.value}%`;
    form.audioOutputVolume.disabled = form.audioOutputDevice.value === 'none';
    form.audioMonitorVolume.disabled = form.audioMonitorDevice.value === 'none';
  }
  let audioTimer;
  function saveAudio() {
    const form = $('#settingsForm');
    updateAudioLabels(form);
    clearTimeout(audioTimer);
    audioTimer = setTimeout(async () => {
      try {
        const r = await api('/api/audio', { method: 'PUT', privileged: true, body: {
          outputDevice: form.audioOutputDevice.value, outputVolume: Number(form.audioOutputVolume.value),
          monitorDevice: form.audioMonitorDevice.value, monitorVolume: Number(form.audioMonitorVolume.value),
        } });
        $('#audioError').textContent = (r && r.error) || '';
      } catch (err) { $('#audioError').textContent = err.message; }
    }, 80);
  }
  for (const name of ['audioOutputDevice', 'audioMonitorDevice', 'audioOutputVolume', 'audioMonitorVolume'])
    $('#settingsForm')[name].addEventListener('input', saveAudio);

  // The Google account (desktop only) signs in through its own window and applies straight away.
  let google = { signedIn: false, email: null };
  function renderGoogle() {
    const row = $('#googleGroup .account-row');
    row.classList.toggle('is-signed-in', google.signedIn);
    $('#googleName').textContent = google.signedIn ? (google.email || 'Signed in') : 'Not signed in';
    $('#googleHint').hidden = google.signedIn;
    $('#googleAvatar').textContent = google.signedIn && google.email ? google.email[0] : 'G';
    $('#googleBtn').textContent = google.signedIn ? 'Sign out' : 'Sign in';
  }
  async function fillGoogle() {
    $('#googleError').textContent = '';
    try { google = await api('/api/google', { method: 'GET', privileged: true }); }
    catch { google = { signedIn: false, email: null }; }
    renderGoogle();
  }
  $('#googleBtn').addEventListener('click', async () => {
    const btn = $('#googleBtn');
    const signingIn = !google.signedIn;
    btn.disabled = true;
    $('#googleError').textContent = '';
    if (signingIn) btn.textContent = 'Waiting for sign-in…';
    try {
      google = await api(signingIn ? '/api/google/signin' : '/api/google/signout', { privileged: true });
      toast(signingIn ? `Signed in${google.email ? ` as ${google.email}` : ''}.` : 'Signed out of Google.', 'ok');
    } catch (err) {
      $('#googleError').textContent = err.message;
    } finally {
      btn.disabled = false;
      renderGoogle();
    }
  });

  // ------------------------------------------------------------------ Audio meter

  (() => {
    const meter = $('#meter'), bars = [$('#meterL'), $('#meterR')];
    let hideTimer;
    const toScale = v => {
      if (v <= 0.0001) return 0;
      const db = 20 * Math.log10(v);           // -60 dB .. 0 dB → 0 .. 1
      return Math.max(0, Math.min(1, (db + 60) / 60));
    };
    const es = new EventSource('/api/meter');
    es.onmessage = e => {
      const { l, r } = JSON.parse(e.data);
      [l, r].forEach((v, i) => { bars[i].style.clipPath = `inset(${(1 - toScale(v)) * 100}% 0 0 0)`; });
      if (l > 0.003 || r > 0.003) {
        meter.classList.add('is-active');
        clearTimeout(hideTimer);
        hideTimer = setTimeout(() => meter.classList.remove('is-active'), 1500);
      }
    };
  })();

  $('#settingsForm').addEventListener('submit', async e => {
    e.preventDefault();
    const f = e.currentTarget;
    const [width, height] = f.resolution.value.split('x').map(Number);
    const body = {
      slidesNdiName: f.slidesNdiName.value, notesNdiName: f.notesNdiName.value, ndiDiscoveryServer: f.ndiDiscoveryServer.value,
      width, height, frameRate: Number(f.frameRate.value),
      remoteAccess: f.remoteAccess.checked, apiPort: Number(f.apiPort.value),
    };
    const save = $('#settingsSave');
    save.disabled = true;
    try {
      await api('/api/settings', { method: 'PUT', body, privileged: true });
      closeDialog('#settingsDialog');
      toast('Settings saved. Restarting…', 'ok');
      await api('/api/app/restart', { privileged: true }).catch(() => {});
    } catch (err) {
      $('#settingsError').textContent = err.message;
    } finally {
      save.disabled = false;
    }
  });

  // ------------------------------------------------------------------ Dialogs & menu

  const anyDialogOpen = () => !!document.querySelector('dialog[open]');
  function closeDialog(sel) { const d = $(sel); if (d.open) d.close(); }
  $('#shortcutsBtn').addEventListener('click', () => { closeMenu(); if (!anyDialogOpen()) $('#shortcutsDialog').showModal(); });
  for (const d of document.querySelectorAll('dialog')) {
    // A click on the backdrop closes the dialog, but only if the press also started there:
    // dragging out of a text field to select text must not close it.
    let pressedOnBackdrop = false;
    d.addEventListener('pointerdown', e => { pressedOnBackdrop = e.target === d; });
    d.addEventListener('click', e => {
      if (e.target.closest('[data-close]') || (e.target === d && pressedOnBackdrop)) d.close();
      pressedOnBackdrop = false;
    });
  }

  const closeMenu = () => app.classList.remove('menu-open');
  $('#menuBtn').addEventListener('click', () => app.classList.toggle('menu-open'));
  app.addEventListener('click', e => { if (e.target === app) closeMenu(); });

  // ------------------------------------------------------------------ Keyboard

  let gotoBuffer = '', gotoTimer;
  function showGoto() {
    $('#gotoNum').textContent = gotoBuffer;
    $('#gotoHud').hidden = !gotoBuffer;
    clearTimeout(gotoTimer);
    if (gotoBuffer) gotoTimer = setTimeout(() => { gotoBuffer = ''; showGoto(); }, 2500);
  }

  document.addEventListener('keydown', e => {
    if (anyDialogOpen()) return;
    const typing = e.target.closest('input:not([type="range"]), select, textarea');
    const mod = e.ctrlKey || e.metaKey;

    if (mod && (e.key === 'o' || e.key === 'l')) { e.preventDefault(); showOpenDialog(); return; }
    if (mod && e.key === ',') { e.preventDefault(); showSettings(); return; }
    if (mod && (e.key === 'w' || e.key === 'W')) { e.preventDefault(); closePresentation(); return; }
    if (typing || mod || e.altKey) return;
    if (e.target.type === 'range' && ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'].includes(e.key)) return;

    const handled = () => e.preventDefault();
    if (/^[0-9]$/.test(e.key) && state && state.slideCount) {
      gotoBuffer = (gotoBuffer + e.key).slice(0, 3);
      showGoto(); handled(); return;
    }
    if (e.key === 'Enter' && gotoBuffer) {
      const n = parseInt(gotoBuffer, 10);
      gotoBuffer = ''; showGoto();
      if (n >= 1 && n <= state.slideCount) act(`/api/goto/${n}`);
      else toast(`There's no slide ${n}.`, 'error');
      handled(); return;
    }
    if (e.key === 'Escape' && gotoBuffer) { gotoBuffer = ''; showGoto(); return; }

    switch (e.key) {
      case 'ArrowRight': case ' ': case 'PageDown': case 'n': case 'N':
        act('/api/next'); handled(); break;
      case 'ArrowLeft': case 'PageUp': case 'b': case 'B':
        act('/api/previous'); handled(); break;
      case 'Home': if (state && state.slideCount) act('/api/goto/1'); handled(); break;
      case 'End': if (state && state.slideCount) act(`/api/goto/${state.slideCount}`); handled(); break;
      case 'p': case 'P': case 'k': case 'K':
        if (state && state.video) act(e.shiftKey ? '/api/video/restart' : '/api/video/toggle'); handled(); break;
      case 'ArrowUp': act('/api/notes/scroll/up'); handled(); break;
      case 'ArrowDown': act('/api/notes/scroll/down'); handled(); break;
      case '+': case '=': act('/api/notes/increase'); handled(); break;
      case '-': case '_': act('/api/notes/decrease'); handled(); break;
      case 'a': case 'A': act('/api/notes/autofit/toggle'); handled(); break;
      case 'r': case 'R': refresh(); handled(); break;
      case '?': $('#shortcutsDialog').showModal(); handled(); break;
    }
  });

  // ------------------------------------------------------------------ Start

  connect();
  loadInfo();
  startStreams();
  new MutationObserver(() => { if (app.dataset.view === 'stage') startStreams(); })
    .observe(app, { attributes: true, attributeFilter: ['data-view'] });
})();
