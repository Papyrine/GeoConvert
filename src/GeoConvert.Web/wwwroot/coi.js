// Cross-origin-isolation shim.
//
// The multithreaded WebAssembly runtime (WasmEnableThreads) needs SharedArrayBuffer, which browsers
// only expose to a "cross-origin isolated" page — one served with these two response headers:
//   Cross-Origin-Opener-Policy:   same-origin
//   Cross-Origin-Embedder-Policy: require-corp
// GitHub Pages (where this app is hosted) can't set response headers, so a service worker re-serves
// every response with them. Installing that worker takes a reload to take effect, so the page-side code
// below publishes `window.crossOriginIsolation` for index.html to wait on before starting the runtime.
// When the app is already isolated (e.g. a dev/test server that sets the headers itself) it resolves
// straight away. This is the well-known coi-serviceworker technique (Guido Zuidhof, MIT), trimmed to
// what this app needs.

if (typeof window === 'undefined') {
    // ---- Service worker context ----

    // Running byte totals for the current page load: the compressed transport size (Content-Length) and
    // the decompressed size (counted as each body streams through below). The page reads these via
    // postMessage for the footer's download figure — it can't take them from its own Resource Timing,
    // because a service-worker-synthesised response reports body sizes as 0 (a known Resource Timing spec
    // gap). Zeroed at each navigation so the totals cover the latest load, not an accumulation over reloads.
    let bytesZipped = 0;
    let bytesUnzipped = 0;

    self.addEventListener('install', () => self.skipWaiting());
    self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
    self.addEventListener('message', event => {
        if (event.data?.type === 'downloadSize') {
            event.ports[0]?.postMessage({ zipped: bytesZipped, unzipped: bytesUnzipped });
        }
    });
    self.addEventListener('fetch', event => {
        const request = event.request;
        // A cross-origin "only-if-cached" request can't be re-issued, so leave it untouched.
        if (request.cache === 'only-if-cached' && request.mode !== 'same-origin') {
            return;
        }

        // A navigation is the first request of a page load; reset the totals so they cover just this load.
        if (request.mode === 'navigate') {
            bytesZipped = 0;
            bytesUnzipped = 0;
        }

        event.respondWith(
            fetch(request)
                .then(response => {
                    // Opaque (no-cors) responses have an unreadable body/headers; pass them through.
                    if (response.status === 0) {
                        return response;
                    }

                    const headers = new Headers(response.headers);
                    headers.set('Cross-Origin-Embedder-Policy', 'require-corp');
                    headers.set('Cross-Origin-Opener-Policy', 'same-origin');

                    // 204/304/redirects carry no body to re-stream or measure.
                    if (!response.body) {
                        return new Response(response.body, {
                            status: response.status,
                            statusText: response.statusText,
                            headers
                        });
                    }

                    // Content-Length is the compressed transport size; the fetched body is already
                    // decompressed, so counting it as it streams to the page gives the uncompressed size.
                    // The TransformStream is a passthrough — it tallies bytes without buffering them.
                    bytesZipped += Number(response.headers.get('content-length')) || 0;
                    const counter = new TransformStream({
                        transform(chunk, controller) {
                            bytesUnzipped += chunk.byteLength;
                            controller.enqueue(chunk);
                        }
                    });
                    return new Response(response.body.pipeThrough(counter), {
                        status: response.status,
                        statusText: response.statusText,
                        headers
                    });
                })
                // Re-throw rather than returning undefined: respondWith(undefined) is invalid and
                // aborts the request unrecoverably (the confusing "non-Response value 'undefined'"
                // error). A rejected promise surfaces as an ordinary, retryable network failure instead.
                .catch(error => {
                    console.error(error);
                    throw error;
                }));
    });
} else {
    // ---- Page context ----
    //
    // Resolves true once the page is cross-origin isolated, and false when isolation can't be reached —
    // so the caller can say so plainly instead of letting the runtime abort on a missing SharedArrayBuffer.
    // It stays pending while a reload is on its way, because the page is about to be replaced and nothing
    // should boot into it. index.html gates Blazor.start() on this.
    window.crossOriginIsolation = (() => {
        const swUrl = document.currentScript.src;

        // Already isolated: the host sets the headers itself (the test harness does). No worker needed,
        // and the reload budget below can start fresh.
        if (window.crossOriginIsolated) {
            sessionStorage.removeItem('coiReloads');
            return Promise.resolve(true);
        }

        // No service worker to install, so the headers can never arrive.
        if (!window.isSecureContext || !navigator.serviceWorker) {
            return Promise.resolve(false);
        }

        // Only a reload can put the worker's headers on the document, so cap how many we spend: if the
        // page comes back still un-isolated, reloading again would loop forever.
        const reloads = Number(sessionStorage.getItem('coiReloads')) || 0;
        if (reloads >= 2) {
            return Promise.resolve(false);
        }

        // A registration that never activates would otherwise leave the page on its loading spinner.
        const withTimeout = promise => Promise.race([
            promise,
            new Promise(resolve => setTimeout(() => resolve(null), 10_000))
        ]);

        return navigator.serviceWorker
            .register(swUrl)
            // Wait for `ready`, which resolves once a worker is *active*. The tempting signal, the
            // registration's `updatefound`, fires when the worker merely starts *installing* — reloading
            // that early races activation, and a navigation that finds no active worker isn't intercepted,
            // so the page comes back without the headers and boots un-isolated.
            .then(() => withTimeout(navigator.serviceWorker.ready))
            .then(registration => {
                if (registration === null) {
                    return false;
                }

                // An active worker in scope controls the *next* navigation on its own (clients.claim()
                // only matters for pages already loaded), so the reloaded page arrives with COOP/COEP.
                sessionStorage.setItem('coiReloads', String(reloads + 1));
                window.location.reload();

                // Deliberately never settles: this page is being torn down.
                return new Promise(() => { });
            })
            .catch(error => {
                console.error(error);
                return false;
            });
    })();
}
