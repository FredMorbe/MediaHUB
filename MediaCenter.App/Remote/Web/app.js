'use strict';

// Mediacenter im Heimnetz: Fernbedienung, Bibliothek und Wiedergabe im Browser. Alle Texte aus der Bibliothek kommen als Text ins Dokument (nie als HTML).

const root = document.getElementById('app');
const state = { tab: 'remote', query: '', timer: null, overlays: 0 };

function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (key === 'class') el.className = value;
    else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
    else if (value === true) el.setAttribute(key, '');
    else if (value !== false && value != null) el.setAttribute(key, value);
  }
  for (const kid of kids.flat()) {
    if (kid == null || kid === false) continue;
    el.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
  }
  return el;
}

async function api(path, body) {
  const options = body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-MC': '1' }, body: JSON.stringify(body) };
  const response = await fetch(path, options);
  if (response.status === 401 && path !== '/api/login') {
    showLogin();
    throw new Error('Bitte anmelden.');
  }
  let data = null;
  try { data = await response.json(); } catch { /* keine JSON-Antwort */ }
  if (!response.ok) throw new Error((data && data.error) || `Fehler ${response.status}`);
  return data;
}

function toast(text, error) {
  const el = h('div', { class: 'toast' + (error ? ' error' : ''), role: 'status' }, text);
  document.body.append(el);
  setTimeout(() => el.remove(), error ? 6000 : 2800);
}

function clock(seconds) {
  seconds = Math.max(0, Math.floor(seconds || 0));
  const hours = Math.floor(seconds / 3600), minutes = Math.floor(seconds % 3600 / 60), rest = seconds % 60;
  const two = (n) => String(n).padStart(2, '0');
  return hours > 0 ? `${hours}:${two(minutes)}:${two(rest)}` : `${minutes}:${two(rest)}`;
}

// --- Anmeldung -------------------------------------------------------------------------------------------

function showLogin() {
  stopPolling();
  document.querySelectorAll('.sheet, .player').forEach((el) => el.remove());
  const error = h('div', { class: 'error', role: 'alert' });
  const pin = h('input', { type: 'password', inputmode: 'numeric', autocomplete: 'one-time-code', maxlength: '8', placeholder: '••••••••', 'aria-label': 'PIN' });
  const submit = async () => {
    error.textContent = '';
    try {
      await api('/api/login', { pin: pin.value.trim() });
      start();
    } catch (e) {
      error.textContent = e.message;
      pin.select();
    }
  };
  pin.addEventListener('keydown', (e) => { if (e.key === 'Enter') submit(); });
  root.replaceChildren(h('div', { class: 'login' },
    h('img', { src: '/icon.png', alt: '' }),
    h('h1', {}, 'Mediacenter'),
    h('div', { class: 'muted' }, 'PIN eingeben. Du findest sie in der App unter Einstellungen → Steuerung → Heimnetz-Server.'),
    pin,
    h('button', { class: 'primary', onclick: submit }, 'Anmelden'),
    error));
  pin.focus();
}

// --- Rahmen ----------------------------------------------------------------------------------------------

const TABS = [['remote', 'Fernbedienung'], ['movie', 'Filme'], ['series', 'Serien'], ['continue', 'Weiter']];

function start() {
  const content = h('div', { id: 'content' });
  const bar = h('nav', { class: 'tabbar', role: 'tablist' }, TABS.map(([id, label]) =>
    h('button', { role: 'tab', 'aria-selected': String(state.tab === id), onclick: () => { state.tab = id; start(); } }, label)));
  root.replaceChildren(h('div', { class: 'shell' }, content), bar);
  stopPolling();
  ({ remote: renderRemote, movie: renderLibrary, series: renderLibrary, continue: renderContinue })[state.tab](content);
}

function stopPolling() {
  if (state.timer) clearInterval(state.timer);
  state.timer = null;
}

// --- Fernbedienung ---------------------------------------------------------------------------------------

function renderRemote(content) {
  const title = h('h2', {}, 'Es läuft nichts');
  const detail = h('div', { class: 'detail' });
  const fill = h('div', { class: 'fill', style: 'width:0%' });
  const bar = h('div', { class: 'bar' }, fill);
  const position = h('span', {}, '0:00'), duration = h('span', {}, '0:00');
  let length = 0;
  const seek = h('div', { class: 'seek', role: 'slider', 'aria-label': 'Stelle im Video' }, bar);
  seek.addEventListener('pointerdown', (e) => {
    if (length <= 0) return;
    const box = bar.getBoundingClientRect();
    const fraction = Math.min(1, Math.max(0, (e.clientX - box.left) / box.width));
    send({ seek: fraction * length });
  });
  const volume = h('input', { type: 'range', min: '0', max: '100', step: '1', 'aria-label': 'Lautstärke' });
  volume.addEventListener('change', () => send({ volume: Number(volume.value) }));
  const controls = h('div', { class: 'hidden' });

  const act = (label, action, large, cls) => h('button', { class: cls || '', onclick: () => send({ action, large: !!large }) }, label);
  controls.append(
    h('div', { class: 'grid g5' }, act('−60', 'SeekBack', true), act('−10', 'SeekBack'), act('⏯', 'PlayPause', false, 'primary big'), act('+10', 'SeekForward'), act('+60', 'SeekForward', true)),
    h('div', { class: 'section-label' }, 'Ton'),
    h('div', { class: 'grid g2' }, volume, act('Stumm', 'Mute')),
    h('div', { class: 'section-label' }, 'Spuren und Sprünge'),
    h('div', { class: 'grid g3' }, act('Untertitel', 'SubtitleCycle'), act('Tonspur', 'AudioCycle'), act('Intro', 'SkipIntro')),
    h('div', { class: 'grid g3', style: 'margin-top:10px' }, act('◀ Kapitel', 'ChapterPrevious'), act('Kapitel ▶', 'ChapterNext'), act('Nächste Folge', 'NextVideo')),
    h('div', { class: 'grid g2', style: 'margin-top:10px' }, act('Vollbild', 'Fullscreen'), act('Beenden', 'Exit')));

  const nowCard = h('div', { class: 'card now' }, title, detail, seek, h('div', { class: 'times' }, position, duration), controls);

  const press = (label, button, cls, aria) => {
    const el = h('button', { class: cls || '', 'aria-label': aria || label }, label);
    let repeat = null, delay = null;
    const stop = () => { clearTimeout(delay); clearInterval(repeat); delay = repeat = null; };
    el.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      send({ press: button });
      if (button.startsWith('Dpad')) delay = setTimeout(() => { repeat = setInterval(() => send({ press: button }), 130); }, 420);
    });
    for (const type of ['pointerup', 'pointerleave', 'pointercancel']) el.addEventListener(type, stop);
    return el;
  };
  const dpad = h('div', { class: 'card' },
    h('h3', {}, 'App bedienen'),
    h('div', { class: 'muted', style: 'font-size:13px;margin-top:2px' }, 'Wie mit einem Controller: Pfeile bewegen die Auswahl, OK öffnet, Zurück geht eine Seite zurück.'),
    h('div', { class: 'dpad' },
      h('span'), press('▲', 'DpadUp', '', 'Hoch'), h('span'),
      press('◀', 'DpadLeft', '', 'Links'), press('OK', 'A', 'primary'), press('▶', 'DpadRight', '', 'Rechts'),
      h('span'), press('▼', 'DpadDown', '', 'Runter'), h('span')),
    h('div', { class: 'grid g3', style: 'margin-top:12px' }, press('Zurück', 'B'), press('Menü', 'X'), press('Suche', 'Y')),
    h('div', { class: 'grid g3', style: 'margin-top:10px' }, press('◀ Reiter', 'LeftBumper'), press('Start', 'View'), press('Reiter ▶', 'RightBumper')));

  content.append(h('div', { class: 'top' }, h('h1', {}, 'Fernbedienung')), nowCard, dpad);

  async function send(command) {
    try {
      const result = await api('/api/remote', command);
      if (result && result.ok === false) toast('Das geht gerade nicht (läuft ein Video?).', true);
      setTimeout(refresh, 250);
    } catch (e) {
      toast(e.message, true);
    }
  }

  async function refresh() {
    try {
      const now = await api('/api/now');
      length = now.duration || 0;
      title.textContent = now.active ? (now.title || 'Läuft') : 'Es läuft nichts';
      detail.textContent = now.active ? [now.live ? 'Live-TV' : null, now.casting ? 'Läuft auf dem Fernseher' : null, now.paused ? 'Pause' : 'Wiedergabe', now.detail].filter(Boolean).join(' · ') : 'Wähle unter „Filme“ oder „Serien“ etwas aus, oder bediene die App unten.';
      controls.classList.toggle('hidden', !now.active);
      seek.classList.toggle('hidden', !now.active || now.live);
      fill.style.width = length > 0 ? `${Math.min(100, now.position / length * 100)}%` : '0%';
      position.textContent = now.live ? '' : clock(now.position);
      duration.textContent = now.live ? '' : clock(length);
      if (document.activeElement !== volume) volume.value = String(Math.round(now.volume));
    } catch { /* Verbindung kurz weg: der nächste Takt versucht es neu */ }
  }

  refresh();
  state.timer = setInterval(() => { if (document.visibilityState === 'visible' && !state.overlays) refresh(); }, 1500);
}

// --- Bibliothek ------------------------------------------------------------------------------------------

function posterTile(item, onOpen) {
  const art = h('div', { class: 'art' }, item.poster ? null : (item.name || '?').trim().charAt(0).toUpperCase());
  if (item.poster) art.style.backgroundImage = `url(/api/poster/${item.id})`;
  if (item.watched === 'Watched') art.append(h('span', { class: 'badge seen' }, '✓'));
  return h('button', { class: 'poster' + (item.online === false ? ' offline' : ''), onclick: onOpen, 'aria-label': item.name },
    art, h('div', { class: 'name' }, item.name), h('div', { class: 'sub' }, item.year || ''));
}

function renderLibrary(content) {
  const kind = state.tab;
  const grid = h('div', { class: 'posters' });
  const search = h('input', { class: 'search', type: 'search', placeholder: kind === 'movie' ? 'Filme suchen' : 'Serien suchen', value: state.query, 'aria-label': 'Suchen' });
  let wait = null;
  search.addEventListener('input', () => { clearTimeout(wait); wait = setTimeout(() => { state.query = search.value; load(); }, 250); });
  content.append(h('div', { class: 'top' }, h('h1', {}, kind === 'movie' ? 'Filme' : 'Serien')), search, grid);

  async function load() {
    try {
      const items = await api(`/api/library?kind=${kind}&q=${encodeURIComponent(state.query)}`);
      grid.replaceChildren(...(items.length ? items.map((item) => posterTile(item, () => openDetail(item.id))) : [h('div', { class: 'empty' }, 'Nichts gefunden.')]));
    } catch (e) {
      toast(e.message, true);
    }
  }
  load();
}

async function renderContinue(content) {
  const list = h('div', { class: 'posters' });
  content.append(h('div', { class: 'top' }, h('h1', {}, 'Weiterschauen')), list);
  try {
    const items = await api('/api/continue');
    list.replaceChildren(...(items.length ? items.map((c) => {
      const tile = posterTile({ id: c.titleId, name: c.name, poster: c.poster, year: c.season != null ? `S${String(c.season).padStart(2, '0')}E${String(c.episode).padStart(2, '0')} · ${clock(c.position)}` : clock(c.position) }, () => openDetail(c.titleId));
      return tile;
    }) : [h('div', { class: 'empty' }, 'Nichts angefangen.')]));
  } catch (e) {
    toast(e.message, true);
  }
}

// --- Detail ----------------------------------------------------------------------------------------------

async function openDetail(id) {
  let title;
  try {
    title = await api(`/api/title/${id}`);
  } catch (e) {
    toast(e.message, true);
    return;
  }
  state.overlays++;
  const sheet = h('div', { class: 'sheet', role: 'dialog', 'aria-label': title.name });
  const close = () => { sheet.remove(); state.overlays--; };

  const playOnPc = async (video) => {
    try {
      await api('/api/play', { videoId: video.id, resume: true });
      toast('Läuft auf dem PC.');
      close();
      state.tab = 'remote';
      start();
    } catch (e) {
      toast(e.message, true);
    }
  };

  const cover = h('div', { class: 'cover' });
  if (title.poster) cover.style.backgroundImage = `url(/api/poster/${title.id})`;
  const facts = [title.meta, title.rating ? `★ ${title.rating.toFixed(1)}` : null, (title.genres || []).join(', ')].filter(Boolean);
  const inner = h('div', { class: 'inner' },
    h('button', { onclick: close }, '‹ Zurück'),
    h('div', { class: 'head' }, cover, h('div', {}, h('h2', {}, title.name), h('div', { class: 'muted', style: 'margin-top:4px' }, facts.join(' · ')))),
    title.plot ? h('div', { class: 'plot' }, title.plot) : null,
    title.cast ? h('div', { class: 'muted', style: 'font-size:14px' }, `Besetzung: ${title.cast}`) : null);

  if (title.kind === 'movie') {
    const video = title.videos[0];
    if (video) {
      const resume = video.position > 30 && !video.watched ? ` (ab ${clock(video.position)})` : '';
      inner.append(h('div', { class: 'grid g2', style: 'margin-top:16px' },
        h('button', { class: 'primary', disabled: !video.online, onclick: () => playOnPc(video) }, 'Auf dem PC' + resume),
        h('button', { disabled: !video.online || !!video.disc, onclick: () => openPlayer(video, title.name) }, 'Im Handy')));
      if (!video.online) inner.append(h('div', { class: 'muted', style: 'margin-top:8px' }, 'Nicht erreichbar (Laufwerk abgesteckt?).'));
      else if (video.disc) inner.append(h('div', { class: 'muted', style: 'margin-top:8px' }, `${video.disc}: im Handy nicht abspielbar, auf dem PC schon.`));
    }
  } else {
    let season = null;
    for (const video of title.videos) {
      if (video.season !== season) {
        season = video.season;
        inner.append(h('div', { class: 'section-label' }, season == null ? 'Weitere' : `Staffel ${season}`));
      }
      const label = video.episode != null ? `${video.episode}. ${video.name || 'Folge ' + video.episode}` : (video.name || 'Folge');
      const sub = video.position > 30 && !video.watched ? `ab ${clock(video.position)}` : (video.disc || (video.online ? '' : 'nicht erreichbar'));
      inner.append(h('div', { class: 'ep' + (video.watched ? ' seen' : '') },
        h('div', { class: 't' }, h('b', {}, label), sub ? h('span', {}, sub) : null),
        h('button', { class: 'primary', disabled: !video.online, onclick: () => playOnPc(video), 'aria-label': `${label} auf dem PC` }, 'PC'),
        h('button', { disabled: !video.online || !!video.disc, onclick: () => openPlayer(video, `${title.name} – ${label}`), 'aria-label': `${label} im Handy` }, 'Handy')));
    }
  }
  sheet.append(inner);
  document.body.append(sheet);
}

// --- Player im Browser -----------------------------------------------------------------------------------

async function openPlayer(video, name) {
  let info;
  try {
    info = await api(`/api/streaminfo/${video.id}`);
  } catch (e) {
    toast(e.message, true);
    return;
  }
  state.overlays++;
  const total = video.duration || 0;
  const resume = video.position > 30 && !video.watched && (!total || video.position < total - 30) ? video.position : 0;
  let base = 0; // Beim umgewandelten Strom beginnt das Bild bei „base“; der Browser kennt nur die Zeit seit dem Start des Stroms.

  const el = h('video', { playsinline: true, autoplay: true });
  const status = h('div', { class: 'status' }, info.description);
  const position = h('span', {}, '0:00');
  const fill = h('div', { class: 'fill', style: 'width:0%' });
  const seekBar = h('div', { class: 'seek', role: 'slider', 'aria-label': 'Stelle im Video' }, h('div', { class: 'bar' }, fill));
  const now = () => base + (el.currentTime || 0);

  function load(from) {
    base = from;
    el.src = `/api/stream/${video.id}?start=${Math.floor(from)}`;
    el.play().catch(() => { status.textContent = 'Zum Abspielen auf ▶ tippen.'; });
  }

  if (info.direct) {
    el.controls = true;
    el.src = `/api/stream/${video.id}`;
    el.addEventListener('loadedmetadata', () => { if (resume > 0) el.currentTime = resume; }, { once: true });
  } else {
    load(resume);
    seekBar.addEventListener('pointerdown', (e) => {
      if (total <= 0) return;
      const box = seekBar.firstChild.getBoundingClientRect();
      load(Math.min(1, Math.max(0, (e.clientX - box.left) / box.width)) * total);
    });
  }
  el.addEventListener('error', () => { status.textContent = 'Das Video lässt sich nicht abspielen.'; });
  el.addEventListener('timeupdate', () => {
    position.textContent = `${clock(now())}${total ? ' / ' + clock(total) : ''}`;
    if (total) fill.style.width = `${Math.min(100, now() / total * 100)}%`;
  });
  el.addEventListener('ended', () => { report(true); });

  const jump = (delta) => {
    if (info.direct) el.currentTime = Math.max(0, el.currentTime + delta);
    else load(Math.max(0, Math.min(total || Infinity, now() + delta)));
  };
  const report = (watched) => api('/api/progress', { videoId: video.id, position: now(), watched: !!watched }).catch(() => {});
  const ticker = setInterval(() => { if (!el.paused) report(false); }, 15000);

  const player = h('div', { class: 'player' }, el,
    h('div', { class: 'bar' },
      h('div', { class: 'muted', style: 'font-size:14px' }, name),
      info.direct ? null : seekBar,
      h('div', { class: 'times' }, position, h('span')),
      h('div', { class: 'row' },
        h('button', { onclick: () => jump(-10) }, '−10'),
        h('button', { class: 'primary', onclick: () => (el.paused ? el.play() : el.pause()) }, '⏯'),
        h('button', { onclick: () => jump(10) }, '+10'),
        h('button', { onclick: close }, 'Schließen')),
      status));
  function close() {
    clearInterval(ticker);
    report(false);
    el.pause();
    el.removeAttribute('src');
    el.load();
    player.remove();
    state.overlays--;
  }
  document.body.append(player);
}

// --- Start -----------------------------------------------------------------------------------------------

(async () => {
  try {
    await api('/api/now');
    start();
  } catch {
    showLogin();
  }
})();
