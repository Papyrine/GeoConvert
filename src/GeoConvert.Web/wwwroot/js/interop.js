window.statePreference = {
    get: function (key) {
        return localStorage.getItem(key);
    },
    set: function (key, value) {
        localStorage.setItem(key, value);
    },
    remove: function (key) {
        localStorage.removeItem(key);
    }
};

window.fileDownload = {
    downloadBlob: function (filename, contentType, base64Content) {
        const byteCharacters = atob(base64Content);
        const byteNumbers = new Array(byteCharacters.length);
        for (let i = 0; i < byteCharacters.length; i++) {
            byteNumbers[i] = byteCharacters.charCodeAt(i);
        }
        const byteArray = new Uint8Array(byteNumbers);
        const blob = new Blob([byteArray], { type: contentType });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(url);
    }
};

window.appInfo = {
    userAgent: function () {
        return navigator.userAgent;
    },
    // Totals the app's boot download. Waits for the load event (and web fonts) so every framework/asset
    // request has finished first. On GitHub Pages the coi.js service worker re-serves every response to add
    // the cross-origin-isolation headers, and a service-worker-synthesised response reports its body sizes
    // as 0 in Resource Timing (a known spec gap) — so when a SW controls the page, ask it for the byte
    // totals it tallied while serving the load. Without a controlling SW (a host that sets the headers
    // itself, or local dev) Resource Timing is accurate, so fall back to summing it: encodedBodySize is the
    // compressed bytes over the wire, decodedBodySize the uncompressed bytes.
    downloadSize: async function () {
        if (document.readyState !== 'complete') {
            await new Promise(resolve => window.addEventListener('load', resolve, { once: true }));
        }
        try {
            await document.fonts.ready;
        } catch {
        }

        const controller = navigator.serviceWorker?.controller;
        if (controller) {
            const totals = await new Promise(resolve => {
                const channel = new MessageChannel();
                const timeout = setTimeout(() => resolve(null), 1000);
                channel.port1.onmessage = event => {
                    clearTimeout(timeout);
                    resolve(event.data);
                };
                controller.postMessage({ type: 'downloadSize' }, [channel.port2]);
            });
            if (totals) {
                return { zipped: totals.zipped, unzipped: totals.unzipped };
            }
        }

        let zipped = 0;
        let unzipped = 0;
        const add = entry => {
            zipped += entry.encodedBodySize || 0;
            unzipped += entry.decodedBodySize || 0;
        };
        performance.getEntriesByType('navigation').forEach(add);
        performance.getEntriesByType('resource').forEach(add);
        return { zipped, unzipped };
    },
    // Approximate RAM the app occupies. Blazor's managed heap lives in WebAssembly linear memory, so the
    // WASM buffer size is the real footprint; fall back to Chromium's JS heap when the runtime handle isn't
    // exposed, and 0 when neither is available (so the caller can hide the figure).
    ramBytes: function () {
        try {
            const buffer = globalThis.getDotnetRuntime?.(0)?.Module?.HEAP8?.buffer;
            if (buffer) {
                return buffer.byteLength;
            }
        } catch {
        }
        return performance.memory?.usedJSHeapSize ?? 0;
    }
};

window.themeManager = {
    applyTheme: function (themeName) {
        document.documentElement.setAttribute('data-theme', themeName.toLowerCase());
    },
    initializeTheme: function () {
        const savedTheme = localStorage.getItem('selectedTheme');
        if (savedTheme) {
            document.documentElement.setAttribute('data-theme', savedTheme.toLowerCase());
        }
    }
};
