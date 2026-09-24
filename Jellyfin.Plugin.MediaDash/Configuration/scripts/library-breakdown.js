// MediaDash Library-breakdown pure helpers. Extracted from configPage.html on
// 2026-09-23 (06-FOLLOWUP-2a). All functions here are pure — they take data +
// palettes + esc/bytes helpers and return HTML strings. `renderLibraryBreakdown`
// stays inline in configPage.html because it uses `view.querySelector` and
// `api()`, which close over the page-level IIFE.
//
// Closures resolved via ns.MediaDash: esc (dom-helpers.js), bytes (dom-helpers.js).
// Palettes are exported so an Overview panel factory (future follow-up) can pass
// them in explicitly if it ever wants to override.
//
// Same 06-D pattern as page-lifetime.js / api-client.js / dom-helpers.js: served
// by PluginAssetController, consumed via <script src>, loaded by
// tools/comprehensive-test/ui-lifecycle-self-tests.js via fs+vm.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    // Local aliases — the alias-at-top-of-IIFE pattern the rest of the codebase uses.
    // Falling back to identity/no-op keeps the self-test fs+vm load working even
    // before dom-helpers has been evaluated by the same page (self-test loads in
    // order; production loads by <script src> order).
    var esc = ns.MediaDash.esc || function (s) { return String(s == null ? '' : s); };
    var bytes = ns.MediaDash.bytes || function (n) { return String(n || 0); };

    // Fixed palette for the labeled resolution buckets — keeps colours stable across libraries
    // so the eye reads "same bucket, different library" at a glance.
    ns.MediaDash.LIB_RES_ORDER = ['4K', '1080p', '720p', 'SD', 'Unknown'];
    ns.MediaDash.LIB_RES_COLORS = {
        '4K':      '#8b5cf6',
        '1080p':   '#3b82f6',
        '720p':    '#10b981',
        'SD':      '#64748b',
        'Unknown': '#71717a'
    };
    // Codecs and containers use a dictionary; unknown keys fall back to a neutral zinc.
    ns.MediaDash.LIB_CODEC_COLORS = {
        h264:    '#ef4444',
        hevc:    '#f59e0b',
        av1:     '#10b981',
        vp9:     '#3b82f6',
        mpeg4:   '#8b5cf6',
        vc1:     '#f97316',
        mpeg2video: '#ec4899',
        unknown: '#71717a'
    };
    ns.MediaDash.LIB_CONT_COLORS = {
        mkv:   '#3b82f6',
        mp4:   '#10b981',
        avi:   '#f59e0b',
        mov:   '#8b5cf6',
        webm:  '#ec4899',
        ts:    '#f97316',
        m2ts:  '#a78bfa',
        other: '#71717a'
    };

    ns.MediaDash.libIconFor = function libIconFor(collectionType) {
        switch (collectionType) {
            case 'movies':     return 'movie';
            case 'tvshows':    return 'live_tv';
            case 'music':      return 'library_music';
            case 'books':      return 'menu_book';
            case 'homevideos': return 'photo_library';
            case 'musicvideos': return 'music_video';
            default:           return 'folder_special';
        }
    };

    ns.MediaDash.libStackBar = function libStackBar(map, orderedKeys, colors) {
        map = map || {};
        var total = 0;
        orderedKeys.forEach(function (k) { total += (map[k] || 0); });
        Object.keys(map).forEach(function (k) {
            if (orderedKeys.indexOf(k) < 0) total += (map[k] || 0);
        });
        if (!total) return '<div class="mdStackBar"></div>';
        var html = '<div class="mdStackBar">';
        orderedKeys.forEach(function (k) {
            var v = map[k] || 0;
            if (v <= 0) return;
            var pct = (v / total * 100).toFixed(2);
            html += '<span class="mdStackSeg" style="width:' + pct + '%;background:' + colors[k] + ';" ' +
                'title="' + esc(k) + ': ' + v + ' (' + Math.round(v / total * 100) + '%)"></span>';
        });
        html += '</div>';
        return html;
    };

    ns.MediaDash.libLegend = function libLegend(entries) {
        // entries: [{ label, value, color }]
        var total = 0;
        entries.forEach(function (e) { total += (e.value || 0); });
        if (!total) return '<div class="mdLegend"><span style="opacity:.6;">no data</span></div>';
        var html = '<div class="mdLegend">';
        entries.forEach(function (e) {
            if (!e.value) return;
            html += '<span class="mdLegendChip" title="' + esc(e.label) + ': ' + e.value + '">' +
                '<span class="mdLegendSwatch" style="background:' + e.color + ';"></span>' +
                esc(e.label) + ' <b>' + e.value + '</b> · ' + Math.round(e.value / total * 100) + '%' +
                '</span>';
        });
        html += '</div>';
        return html;
    };

    ns.MediaDash.libSegmentsFromMap = function libSegmentsFromMap(map, colors) {
        map = map || {};
        return Object.keys(map)
            .map(function (k) {
                return { label: k, value: map[k], color: colors[k] || colors.other || colors.unknown || '#71717a' };
            })
            .filter(function (e) { return e.value > 0; })
            .sort(function (a, b) { return b.value - a.value; });
    };

    ns.MediaDash.donutSvg = function donutSvg(segments, size) {
        var total = segments.reduce(function (a, s) { return a + s.value; }, 0);
        if (total <= 0) return '';
        var r = size / 2 - 8, c = 2 * Math.PI * r, offset = c * 0.25;
        var svg = '<svg width="' + size + '" height="' + size + '" viewBox="0 0 ' + size + ' ' + size + '">';
        segments.forEach(function (s) {
            if (s.value <= 0) return;
            var frac = s.value / total;
            svg += '<circle cx="' + (size / 2) + '" cy="' + (size / 2) + '" r="' + r + '" fill="none"' +
                ' stroke="' + s.color + '" stroke-width="13" stroke-linecap="butt"' +
                ' stroke-dasharray="' + (frac * c - 1.5) + ' ' + (c - frac * c + 1.5) + '"' +
                ' stroke-dashoffset="' + offset + '"></circle>';
            offset -= frac * c;
        });
        svg += '</svg>';
        return svg;
    };

    ns.MediaDash.libCardHtml = function libCardHtml(lib) {
        var LIB_RES_ORDER   = ns.MediaDash.LIB_RES_ORDER;
        var LIB_RES_COLORS  = ns.MediaDash.LIB_RES_COLORS;
        var LIB_CODEC_COLORS = ns.MediaDash.LIB_CODEC_COLORS;
        var LIB_CONT_COLORS  = ns.MediaDash.LIB_CONT_COLORS;
        var libStackBar     = ns.MediaDash.libStackBar;
        var libLegend       = ns.MediaDash.libLegend;
        var libSegmentsFromMap = ns.MediaDash.libSegmentsFromMap;
        var libIconFor      = ns.MediaDash.libIconFor;
        var donutSvg        = ns.MediaDash.donutSvg;

        var resBar = libStackBar(lib.Resolutions, LIB_RES_ORDER, LIB_RES_COLORS);
        var resLegend = libLegend(LIB_RES_ORDER.map(function (k) {
            return { label: k, value: (lib.Resolutions || {})[k] || 0, color: LIB_RES_COLORS[k] };
        }));
        var codecSegs = libSegmentsFromMap(lib.Codecs, LIB_CODEC_COLORS);
        var containerSegs = libSegmentsFromMap(lib.Containers, LIB_CONT_COLORS);
        var codecDonut = donutSvg(codecSegs.map(function (s) { return { value: s.value, color: s.color }; }), 110);

        return '<div class="mdLibCard">' +
            '<div class="mdLibHead">' +
                '<span class="material-icons">' + libIconFor(lib.CollectionType) + '</span>' +
                '<span class="mdLibTitle">' + esc(lib.Name) + '</span>' +
                '<span class="mdLibCount">' + lib.ItemCount + ' items · ' + bytes(lib.TotalBytes) + '</span>' +
            '</div>' +
            '<div class="mdLibSubLabel">Resolution</div>' + resBar + resLegend +
            '<div class="mdLibSubLabel">Codec</div>' +
            '<div class="mdLibDonutRow">' + codecDonut +
                '<div style="flex:1;">' + libLegend(codecSegs) + '</div></div>' +
            '<div class="mdLibSubLabel">Container</div>' + libLegend(containerSegs) +
            '</div>';
    };
})();
