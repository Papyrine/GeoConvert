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
    // Totals the app's boot download from the browser's Resource Timing data: encodedBodySize is the
    // compressed bytes received over the wire, decodedBodySize the uncompressed bytes. Waits for the load
    // event (and web fonts) so every framework/asset request is in the timeline before summing. All assets
    // are same-origin, so the sizes are reported rather than zeroed for cross-origin opacity.
    downloadSize: async function () {
        if (document.readyState !== 'complete') {
            await new Promise(resolve => window.addEventListener('load', resolve, { once: true }));
        }
        try {
            await document.fonts.ready;
        } catch {
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
