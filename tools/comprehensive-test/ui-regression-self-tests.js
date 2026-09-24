'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');
const config = path.resolve(__dirname, '../../Jellyfin.Plugin.MediaDash/Configuration');
const html = fs.readFileSync(path.join(config, 'configPage.html'), 'utf8');
function source(name) {
    const file = path.join(config, 'scripts', name);
    return fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : html;
}
function element() {
    const handlers = new Map();
    return {
        handlers,
        addEventListener(name, fn) { if (!handlers.has(name)) handlers.set(name, new Set()); handlers.get(name).add(fn); },
        removeEventListener(name, fn) { if (handlers.has(name)) handlers.get(name).delete(fn); },
        fire(name, ev) { for (const fn of handlers.get(name) || []) fn(ev); }
    };
}
function environment() {
    const system = element(), drives = element(), timers = new Map();
    let menus = 0, timerId = 0;
    const ctx = {
        view: { querySelector: s => s === '#mdSystem' ? system : drives },
        showSysPerfCtxMenu() { menus++; }, showSmartCtxMenu() { menus++; },
        setTimeout(fn) { timers.set(++timerId, fn); return timerId; },
        clearTimeout(id) { timers.delete(id); }
    };
    vm.createContext(ctx);
    vm.runInContext(source('page-lifetime.js'), ctx);
    ctx.lifetime = ctx.MediaDash.createPageLifetime();
    ctx.ownGlobal = (target, name, fn, options) => {
        target.addEventListener(name, fn, options);
        ctx.lifetime.own(() => target.removeEventListener(name, fn, options));
    };
    ctx.deps = ctx;
    return { ctx, system, drives, timers, menus: () => menus };
}
const event = { target: { closest: () => ({}) }, preventDefault() {}, clientX: 1, clientY: 1, touches: [{ clientX: 1, clientY: 1 }] };
function bindMenus(ctx) {
    const code = source('tabs/overview-tab.js');
    const start = code.indexOf("var sysEl = ");
    const end = code.indexOf('// Global dismissal', start);
    assert(start >= 0 && end > start, 'context-menu wiring source found');
    vm.runInContext(code.slice(start, end), ctx);
}
test('base-path deployment does not request assets from origin root', () => {
    assert.equal([...html.matchAll(/(?:src|href)="(\/MediaDash\/Asset\/[^\"]+)"/g)].length, 0);
});
test('context-menu callbacks do not multiply on reopen', () => {
    const env = environment();
    for (let i = 0; i < 3; i++) { env.ctx.lifetime.start(); bindMenus(env.ctx); env.ctx.lifetime.stop(); }
    env.ctx.lifetime.start(); bindMenus(env.ctx);
    env.system.fire('contextmenu', event); env.drives.fire('contextmenu', event);
    assert.equal(env.menus(), 2);
});
test('pagehide cancels pending long presses', () => {
    const env = environment(); env.ctx.lifetime.start(); bindMenus(env.ctx);
    env.system.fire('touchstart', event); env.drives.fire('touchstart', event);
    env.ctx.lifetime.stop();
    assert.equal(env.timers.size, 0);
});
test('translation finishing after pagehide cannot initialize a hidden view', async () => {
    const env = environment(); let onShow, finish, initialized = 0;
    Object.assign(env.ctx, {
        document: { querySelector: () => ({ addEventListener: (_name, fn) => { onShow = fn; } }) },
        ApiClient: { getUrl: x => x }, populateLanguageDatalist() {}, populateGenreDatalist() {},
        settingsTab: { populateLanguageDatalist() {}, populateGenreDatalist() {} },
        loadI18n: () => new Promise(resolve => { finish = resolve; }),
        STR: { tagline: 'test' }, initAfterI18n() { initialized++; }
    });
    const code = source('page-controller.js');
    const start = code.indexOf("document.querySelector('#MediaDashConfigPage').addEventListener('pageshow'");
    const end = code.indexOf('function initAfterI18n()', start);
    assert(start >= 0 && end > start);
    vm.runInContext(code.slice(start, end), env.ctx);
    onShow.call({ querySelector: () => ({}) }); env.ctx.lifetime.stop(); finish();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(initialized, 0);
});

test('bootstrap uses server base path, orders scripts and mounts only after assets load', async () => {
    const loaded = []; let mounted = 0;
    const appendChild = el => { loaded.push(el.src || el.href); queueMicrotask(() => el.onload()); };
    const root = { appendChild };
    const ctx = { window: { MediaDash: { mountPage: actual => { assert.equal(actual, root); mounted++; } } },
        document: { createElement: () => ({ remove() {} }), head: { appendChild } } };
    vm.runInNewContext(source('bootstrap.js'), ctx);
    const pending = ctx.window.MediaDash.bootstrap(root, { getUrl: p => 'https://example.invalid/jellyfin/' + p });
    assert.equal(mounted, 0);
    await pending;
    assert.equal(mounted, 1);
    assert(loaded.every(url => url.startsWith('https://example.invalid/jellyfin/MediaDash/Asset/')));
    const scripts = loaded.filter(url => url.endsWith('.js')).map(url => url.split('/').pop());
    assert.equal(scripts[0], 'page-lifetime.js');
    assert.equal(scripts.at(-1), 'page-controller.js');
    for (const url of loaded) {
        const name = url.split('/').pop();
        const exists = fs.existsSync(path.join(config, name.endsWith('.css') ? 'styles' : 'scripts', name)) ||
            fs.existsSync(path.join(config, 'scripts/tabs', name));
        assert(exists, name + ' exists as an actual file');
    }
});

test('missing bootstrap asset rejects without mounting a partial interface', async () => {
    let mounted = false;
    const appendChild = el => queueMicrotask(() => el.onerror());
    const ctx = { window: { MediaDash: { mountPage() { mounted = true; } } },
        document: { createElement: () => ({ remove() {} }), head: { appendChild } } };
    vm.runInNewContext(source('bootstrap.js'), ctx);
    await assert.rejects(ctx.window.MediaDash.bootstrap({ appendChild }, { getUrl: p => '/' + p }), /Could not load MediaDash interface/);
    assert.equal(mounted, false);
});

test('Files ignores an older directory response arriving after newer navigation', async () => {
    const pending = [];
    const elements = new Map();
    const root = { querySelector(selector) {
        if (!elements.has(selector)) elements.set(selector, { style: {}, parentElement: { style: {} }, setAttribute() {} });
        return elements.get(selector);
    } };
    const ctx = { window: { MediaDash: {} }, Set };
    vm.runInNewContext(source('tabs/files-tab.js'), ctx);
    const files = ctx.window.MediaDash.createFilesTab({ view: root, api: () => new Promise(resolve => pending.push(resolve)) });
    files.loadFiles('/older'); files.loadFiles('/newer');
    pending[1]({ Path: '/newer', Entries: [] }); await Promise.resolve();
    pending[0]({ Path: '/older', Entries: [] }); await Promise.resolve();
    assert.equal(files.currentFilePath, '/newer');
    assert.equal(root.querySelector('#mdFileCrumbsText').textContent, '/newer');
});

test('API suppresses stale failures and configuration responses after hide', async () => {
    const ctx = { window: {} }; vm.createContext(ctx);
    vm.runInContext(source('page-lifetime.js'), ctx); vm.runInContext(source('api-client.js'), ctx);
    const life = ctx.window.MediaDash.createPageLifetime(); life.start();
    let reject, resolveConfig, callbacks = 0;
    const api = ctx.window.MediaDash.createApi(life, {
        getUrl: p => p, getJSON: () => new Promise((_resolve, fail) => { reject = fail; }),
        getPluginConfiguration: () => new Promise(resolve => { resolveConfig = resolve; })
    });
    api('Status').catch(() => callbacks++);
    life.stop(); reject(new Error('old failure'));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(callbacks, 0, 'old failure cannot render into a reopened page');
    life.start(); api.configuration('plugin').then(() => callbacks++);
    life.stop(); resolveConfig({ DryRun: true });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(callbacks, 0);
});

test('settings global listeners detach on hide and reattach once per visit', () => {
    const ctx = { window: element(), document: element() }; ctx.window.MediaDash = {};
    vm.createContext(ctx); vm.runInContext(source('page-lifetime.js'), ctx);
    vm.runInContext(source('tabs/settings-tab.js'), ctx);
    const lifetime = ctx.window.MediaDash.createPageLifetime();
    const settings = ctx.window.MediaDash.createSettingsTab({ lifetime,
        ownGlobal(target, name, handler) { target.addEventListener(name, handler); lifetime.own(() => target.removeEventListener(name, handler)); }
    });
    for (let i = 0; i < 3; i++) {
        lifetime.start(); settings.activateSettings();
        assert.equal(ctx.window.handlers.get('beforeunload').size, 1);
        assert.equal(ctx.document.handlers.get('click').size, 1);
        lifetime.stop();
        assert.equal(ctx.window.handlers.get('beforeunload').size, 0);
        assert.equal(ctx.document.handlers.get('click').size, 0);
    }
});

test('owned timers cannot fire after hide or survive duplicate start', () => {
    const timers = new Map(); let id = 0, fired = 0;
    const ctx = { window: {}, setTimeout(fn) { timers.set(++id, fn); return id; }, clearTimeout(key) { timers.delete(key); } };
    vm.runInNewContext(source('page-lifetime.js'), ctx);
    const life = ctx.window.MediaDash.createPageLifetime(); life.start();
    life.timeout(() => fired++, 100); life.stop();
    assert.equal(timers.size, 0); assert.equal(fired, 0);
    life.start(); life.timeout(() => fired++, 100); life.start();
    assert.equal(timers.size, 0);
});

test('bulk deletion keeps its approved source paths when navigation changes', async () => {
    const requests = [], pending = [];
    const api = () => new Promise(() => {});
    api.ajax = options => { requests.push(JSON.parse(options.data)); return new Promise(resolve => pending.push(resolve)); };
    const ctx = { window: { MediaDash: {}, confirm: () => true, alert() {} }, ApiClient: { getUrl: p => p } };
    // Expose private operations only in the test VM, without adding production API surface.
    vm.runInNewContext(source('tabs/files-tab.js').replace('get currentFilePath()',
        'bulkDelete: bulkDelete, fileSelection: fileSelection, get currentFilePath()'), ctx);
    const files = ctx.window.MediaDash.createFilesTab({ api, view: { querySelector: () => ({ style: {} }) } });
    files.currentFilePath = 'C:\\A'; files.fileSelection.add('one.mkv'); files.fileSelection.add('two.mkv');
    files.bulkDelete(); files.currentFilePath = 'C:\\B'; pending[0]();
    await Promise.resolve();
    assert.deepEqual(requests.map(r => r.Path), ['C:\\A\\one.mkv', 'C:\\A\\two.mkv']);
});

test('settings save stays blocked until both library controls finish hydrating', async () => {
    const code = source('tabs/settings-tab.js');
    const start = code.indexOf('function loadSettings()');
    const end = code.indexOf('// Renders one checkbox', start);
    const pending = [];
    const ctx = { mdConfigLoaded: false, mdSettingsDirty: false,
        deps: { loadEnv: () => Promise.resolve({}), applySubtitleProviderHint() {},
            view: { querySelector: () => null }, api: { configuration: () => Promise.resolve({}) }, lifetime: { timeout() {} } },
        populateLanguageDatalist() {}, populateGenreDatalist() {}, applyConfigToForm() {},
        syncSliderLabels() {}, initTaskCardsAfterLoad() {}, markSettingsClean() {},
        loadLibraries: () => new Promise(resolve => pending.push(resolve)),
        loadStaleExcludedLibraries: () => new Promise(resolve => pending.push(resolve)) };
    vm.createContext(ctx); vm.runInContext(code.slice(start, end), ctx);
    ctx.loadSettings(); await new Promise(resolve => setImmediate(resolve));
    assert.equal(ctx.mdConfigLoaded, false);
    pending[0](); await new Promise(resolve => setImmediate(resolve));
    assert.equal(ctx.mdConfigLoaded, false);
    pending[1](); await new Promise(resolve => setImmediate(resolve));
    assert.equal(ctx.mdConfigLoaded, true);
});

test('bulk move captures selection before the picker and source before the batch', async () => {
    const requests = [], pending = [];
    const api = () => new Promise(() => {});
    api.ajax = options => { requests.push(JSON.parse(options.data)); return new Promise(resolve => pending.push(resolve)); };
    const ctx = { window: { MediaDash: {}, alert() {} }, document: { body: { style: {} } }, ApiClient: { getUrl: p => p } };
    vm.runInNewContext(source('tabs/files-tab.js').replace('get currentFilePath()',
        'bulkMove: bulkMove, picker: picker, fileSelection: fileSelection, get currentFilePath()'), ctx);
    const files = ctx.window.MediaDash.createFilesTab({ api, view: { querySelector: () => ({ style: {} }) } });
    files.currentFilePath = 'C:\\A'; files.fileSelection.add('one.mkv'); files.fileSelection.add('two.mkv');
    files.bulkMove(); files.fileSelection.clear(); files.currentFilePath = 'C:\\B';
    files.picker.onPick('C:\\target'); pending[0](); await Promise.resolve();
    assert.deepEqual(requests.map(r => r.From), ['C:\\A\\one.mkv', 'C:\\A\\two.mkv']);
});

test('uploads retain the selected destination throughout the batch', () => {
    const requests = [];
    const ctx = { window: { MediaDash: {} }, ApiClient: { getUrl: p => p, accessToken: () => 'test-only' },
        XMLHttpRequest: function () { this.upload = {}; this.status = 200; this.open = (_method, url) => { this.url = url; };
            this.setRequestHeader = () => {}; this.send = () => requests.push(this); } };
    vm.runInNewContext(source('tabs/files-tab.js').replace('get currentFilePath()', 'uploadFiles: uploadFiles, get currentFilePath()'), ctx);
    const files = ctx.window.MediaDash.createFilesTab({ view: { querySelector: () => ({}) } });
    files.currentFilePath = 'C:\\A'; files.uploadFiles([{ name: 'one.mkv' }, { name: 'two.mkv' }]);
    files.currentFilePath = 'C:\\B'; requests[0].onload();
    assert.equal(requests.length, 2);
    assert(requests.every(r => new URL(r.url, 'http://test').searchParams.get('path') === 'C:\\A'));
});
