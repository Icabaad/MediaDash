// Owns the active page generation, timers and detachable listeners.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    // Page lifetime: hide/reopen invalidates in-flight responses (via `api`) and disposes
    // owned cleanups. Wired to pageshow/pagehide by the consumer.
    ns.MediaDash.createPageLifetime = function createPageLifetime() {
        var active = false, generation = 0, cleanups = [];
        var timers = new Map();
        var life = {
            start: function () { if (active) life.stop(); active = true; generation++; return generation; },
            current: function () { return generation; },
            accepts: function (token) { return active && token === generation; },
            own: function (cleanup) { cleanups.push(cleanup); },
            timeout: function (callback, delay) {
                if (!active) return null;
                var token = generation;
                var id = setTimeout(function () {
                    timers.delete(id);
                    if (life.accepts(token)) callback();
                }, delay);
                timers.set(id, clearTimeout);
                return id;
            },
            interval: function (callback, delay) {
                if (!active) return null;
                var token = generation;
                var id = setInterval(function () { if (life.accepts(token)) callback(); }, delay);
                timers.set(id, clearInterval);
                return id;
            },
            clearTimer: function (id) {
                var clear = timers.get(id);
                if (clear) clear(id);
                timers.delete(id);
            },
            stop: function () {
                active = false; generation++;
                timers.forEach(function (clear, id) { clear(id); });
                timers.clear();
                var pending = cleanups; cleanups = [];
                pending.forEach(function (fn) { try { fn(); } catch (e) { /* keep disposing */ } });
            }
        };
        return life;
    };
})();
