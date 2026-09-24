// Server-aware requests scoped to the current page generation.
// Stale successes and failures never render; active HTTP errors propagate unchanged.
(function () {
    'use strict';
    var ns = (typeof window !== 'undefined') ? window : globalThis;
    ns.MediaDash = ns.MediaDash || {};

    ns.MediaDash.createApi = function createApi(lifetime, apiClient) {
        function guard(promise) {
            var token = lifetime.current();
            return promise.then(function (r) {
                if (!lifetime.accepts(token)) return new Promise(function () {});
                return r;
            }, function (error) {
                if (!lifetime.accepts(token)) return new Promise(function () {});
                throw error;
            });
        }
        function api(path) { return guard(apiClient.getJSON(apiClient.getUrl('MediaDash/' + path))); }
        api.configuration = function (id) { return guard(apiClient.getPluginConfiguration(id)); };
        api.ajax = function (options) { return guard(apiClient.ajax(options)); };
        return api;
    };
})();
