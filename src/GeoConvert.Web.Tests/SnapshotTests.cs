// Each test boots the multithreaded WASM runtime in a fresh browser page, which is CPU-heavy; run them
// one at a time so several threaded-runtime boots don't contend and time out under load.
[NotInParallel]
public class SnapshotTests
{
    static WebApplication? app;
    static int port;
    static IPlaywright? playwright;
    static IBrowser? browser;

    [Before(Class)]
    public static async Task OneTimeSetUp()
    {
        port = GetAvailablePort();

        // Use pre-published output from build (see csproj PublishBlazorForTests target)
        var testAssemblyDir = Path.GetDirectoryName(typeof(SnapshotTests).Assembly.Location)!;
        var wwwrootPath = Path.Combine(testAssemblyDir, "..", "blazor-publish", "wwwroot");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();

        app = builder.Build();

        var contentTypeProvider = new FileExtensionContentTypeProvider
        {
            Mappings =
            {
                [".wasm"] = "application/wasm"
            }
        };

        var fileProvider = new PhysicalFileProvider(wwwrootPath);

        // The published app uses the multithreaded WASM runtime, which needs SharedArrayBuffer — only
        // exposed to a cross-origin-isolated page. Set the COOP/COEP headers here so the runtime boots
        // under Playwright (in production the coi.js service worker supplies them on GitHub Pages).
        app.Use((context, next) =>
        {
            context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
            context.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
            return next();
        });

        app.UseDefaultFiles(
            new DefaultFilesOptions
            {
                FileProvider = fileProvider
            });
        app.UseStaticFiles(
            new StaticFileOptions
            {
                FileProvider = fileProvider,
                ContentTypeProvider = contentTypeProvider,
                ServeUnknownFileTypes = true
            });

        app.MapFallbackToFile(
            "index.html",
            new StaticFileOptions
            {
                FileProvider = fileProvider
            });

        await app.StartAsync();

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync();
    }

    [After(Class)]
    public static async Task OneTimeTearDown()
    {
        if (browser != null)
        {
            await browser.CloseAsync();
        }

        playwright?.Dispose();

        if (app != null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Test]
    public async Task HomePage()
    {
        var page = await browser!.NewPageAsync();
        await page.GotoAsync($"http://localhost:{port}/");

        await SettleAsync(page);

        await Verify(page);
    }

    // The multithreaded runtime only boots when the page is cross-origin isolated; assert it is, so a
    // regression in the COOP/COEP setup (headers here, coi.js service worker in production) is caught.
    [Test]
    public async Task PageIsCrossOriginIsolated()
    {
        var page = await browser!.NewPageAsync();
        await page.GotoAsync($"http://localhost:{port}/");
        await page.WaitForSelectorAsync(".file-drop");

        var isolated = await page.EvaluateAsync<bool>("() => self.crossOriginIsolated");

        await Assert.That(isolated).IsTrue();
    }

    // End-to-end on the real threaded runtime: uploading a map runs the read + render off the UI thread
    // (Task.Run) and paints a preview. If threads or the offload path were broken the preview never
    // appears and this times out.
    [Test]
    public async Task UploadingMapRendersPreview()
    {
        var page = await browser!.NewPageAsync();
        await page.GotoAsync($"http://localhost:{port}/");
        await SettleAsync(page);

        await page.SetInputFilesAsync("#map-file", new FilePayload
        {
            Name = "sample.geojson",
            MimeType = "application/geo+json",
            Buffer = Sample.GeoJsonBytes
        });

        var image = await page.WaitForSelectorAsync(".preview img", new()
        {
            Timeout = 30000
        });
        var source = await image!.GetAttributeAsync("src");

        await Assert.That(source).StartsWith("data:image/png");
    }

    // Clicking "View sample world map" fetches the world map bundled as a static web asset (built from
    // MapBundle.World), then reads and renders it off the UI thread — the same pipeline as an upload but
    // sourced from the build-time asset. A generous timeout covers reading/rasterising the whole world.
    [Test]
    public async Task SampleWorldMapRendersPreview()
    {
        var page = await browser!.NewPageAsync();
        await page.GotoAsync($"http://localhost:{port}/");
        await SettleAsync(page);

        await page.ClickAsync(".sample-btn");

        var image = await page.WaitForSelectorAsync(".preview img", new()
        {
            Timeout = 60000
        });
        var source = await image!.GetAttributeAsync("src");

        await Assert.That(source).StartsWith("data:image/png");
    }

    // The sample map's download phase shows an indeterminate progress bar (no determinate fill: the
    // server's Content-Length is the compressed transport size while the bytes the WASM HttpClient
    // yields are the decompressed payload, so a ratio would overshoot 100%). The detail line still
    // ticks up with decompressed bytes received. Throttle the transfer via CDP so the phase spans
    // several seconds, making the label and detail line observable before the preview renders.
    [Test]
    public async Task SampleWorldMapShowsDownloadProgress()
    {
        var page = await browser!.NewPageAsync();
        await page.GotoAsync($"http://localhost:{port}/");
        await SettleAsync(page);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        await cdp.SendAsync("Network.enable");
        await cdp.SendAsync("Network.emulateNetworkConditions",
            new()
            {
                ["offline"] = false,
                ["latency"] = 0,
                ["downloadThroughput"] = 4 * 1024 * 1024,
                ["uploadThroughput"] = 4 * 1024 * 1024,
            });

        await page.ClickAsync(".sample-btn");

        // The download-phase label appears while bytes stream in.
        await page.WaitForFunctionAsync(
            "() => document.querySelector('.progress-label span')?.textContent?.includes('Downloading')",
            null,
            new()
            {
                Timeout = 20000
            });

        // The bar is indeterminate (no honest decoded total is available up front) and the detail line
        // shows a single byte count, not a "X / Y" ratio. Wait for the count to climb into MB so we
        // don't race the very first 64 KB chunk.
        var fillClass = await page.GetAttributeAsync(".progress-fill", "class");
        await Assert.That(fillClass).Contains("progress-fill-indeterminate");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('.progress-count')?.textContent?.includes('MB')",
            null,
            new()
            {
                Timeout = 20000
            });
        var detail = await page.TextContentAsync(".progress-count");
        await Assert.That(detail).Contains("MB");
        await Assert.That(detail).DoesNotContain("/");

        // And the load still completes to a rendered preview afterwards.
        await page.WaitForSelectorAsync(".preview img", new()
        {
            Timeout = 60000
        });
    }

    // The Download button runs the actual write/render conversion (off the UI thread) and then hands the
    // bytes to the browser. Confirm clicking it converts and triggers a file download.
    [Test]
    public async Task DownloadingConvertsAndSavesFile()
    {
        var page = await browser!.NewPageAsync();
        await page.GotoAsync($"http://localhost:{port}/");
        await SettleAsync(page);

        await page.SetInputFilesAsync("#map-file", new FilePayload
        {
            Name = "sample.geojson",
            MimeType = "application/geo+json",
            Buffer = Sample.GeoJsonBytes
        });
        await page.WaitForSelectorAsync(".preview img", new()
        {
            Timeout = 30000
        });

        // Default target format is KML; clicking Download converts then saves.
        var download = await page.RunAndWaitForDownloadAsync(
            () => page.ClickAsync(".convert-btn"),
            new()
            {
                Timeout = 30000
            });

        await Assert.That(download.SuggestedFilename).EndsWith(".kml");
    }

    [Test]
    public async Task HomePageMobile()
    {
        var page = await browser!.NewPageAsync();
        await page.SetViewportSizeAsync(375, 667); // iPhone SE size

        await page.GotoAsync($"http://localhost:{port}/");

        await SettleAsync(page);

        await Verify(page);
    }

    [Test]
    public async Task HomePageDarkMode()
    {
        var page = await browser!.NewPageAsync();

        await page.GotoAsync($"http://localhost:{port}/");

        // Set dark theme in localStorage before Blazor initializes
        await page.EvaluateAsync("() => localStorage.setItem('selectedTheme', 'Dark')");

        // Reload to apply theme
        await page.ReloadAsync();

        await SettleAsync(page);

        await Verify(page);
    }

    [Test]
    public async Task HomePageDarkModeMobile()
    {
        var page = await browser!.NewPageAsync();
        await page.SetViewportSizeAsync(375, 667); // iPhone SE size

        await page.GotoAsync($"http://localhost:{port}/");

        // Set dark theme in localStorage before Blazor initializes
        await page.EvaluateAsync("() => localStorage.setItem('selectedTheme', 'Dark')");

        // Reload to apply theme
        await page.ReloadAsync();

        await SettleAsync(page);

        await Verify(page);
    }

    // Waits for the app to be fully settled before a snapshot: the upload UI present, every asset loaded
    // (the multithreaded runtime fetches its pthread worker after first paint), and web fonts rendered —
    // so the captured screenshot is the deterministic settled page rather than a mid-boot frame.
    static async Task SettleAsync(IPage page)
    {
        await page.WaitForSelectorAsync(".file-drop");
        // NetworkIdle is brittle here: the multithreaded WASM runtime keeps fetching pthread workers
        // after first paint, so the network never goes quiet on the default 30s budget during a cold
        // first boot (the heaviest, run-first test pays the full asset download). Give it generous
        // headroom so a slow runner doesn't time out before the runtime settles — it does settle once
        // the workers are warmed, as every later test in this class confirms.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new()
        {
            Timeout = 120000
        });
        // The theme toggle's label is driven by MainLayout.OnInitializedAsync (an async preference
        // load), so until that completes it shows the default-theme label and only then flips to match
        // the active theme. The slower multithreaded boot widens that window, so wait for the label to
        // agree with data-theme — otherwise a dark-theme screenshot can catch the pre-flip "Dark" label.
        await page.WaitForFunctionAsync(
            "() => { const dark = document.documentElement.getAttribute('data-theme') === 'dark';" +
            " const b = document.querySelector('.theme-toggle-btn');" +
            " return b && (dark ? b.textContent.includes('Light') : b.textContent.includes('Dark')); }");
        await page.EvaluateAsync("() => document.fonts.ready");
    }

    static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
