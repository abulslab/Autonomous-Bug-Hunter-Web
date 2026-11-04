using System.Diagnostics;

namespace BugHunter.Api.Hunter;

// Dev convenience: when the browser worker is local and not running, the API starts it (installing its packages and
// Chromium on first use) and stops it on shutdown. Production runs the worker as its own service and leaves this off.
public class BrowserWorkerLauncher(BrowserWorkerClient worker, HunterOptions opts, IHostEnvironment env, ILogger<BrowserWorkerLauncher> log) : IHostedService
{
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly object _gate = new();
    Process? _proc;
    Task? _starting;

    public bool Enabled => opts.AutoStartWorker == true && Uri.TryCreate(opts.WorkerUrl, UriKind.Absolute, out var u) && u.IsLoopback;
    public string? State { get; private set; }

    public Task StartAsync(CancellationToken ct)
    {
        if (Enabled) Kick();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        return Task.CompletedTask;
    }

    // Starts the worker in the background if needed; callers that cannot wait just poll the health check.
    public Task Kick()
    {
        lock (_gate) return _starting is { IsCompleted: false } ? _starting : _starting = Task.Run(() => EnsureAsync(CancellationToken.None));
    }

    public async Task<bool> EnsureRunningAsync(TimeSpan wait, CancellationToken ct)
    {
        if ((await worker.HealthAsync(ct)).Ok) return true;
        if (!Enabled) return false;
        await Task.WhenAny(Kick(), Task.Delay(wait, ct));
        return (await worker.HealthAsync(ct)).Ok;
    }

    async Task EnsureAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var dir = FindWorkerDir();
            var health = await worker.HealthBuildAsync(ct);
            if (health.Ok)
            {
                // A worker left running from older code (the API was killed, so it never stopped it) is replaced.
                var build = dir is null ? null : ((long)Math.Round((File.GetLastWriteTimeUtc(Path.Combine(dir, "server.mjs")) - DateTime.UnixEpoch).TotalMilliseconds)).ToString();
                if (build is null || health.Build is null || health.Build == build || health.Sessions > 0) { State = null; return; }
                log.LogInformation("Browser worker is running older code; restarting it");
                await worker.ShutdownAsync(ct);
                for (var i = 0; i < 20 && (await worker.HealthAsync(ct)).Ok; i++) await Task.Delay(250, ct);
            }
            if (dir is null) { State = "Could not find src/browser-worker next to the API; start it manually."; return; }
            if (!Directory.Exists(Path.Combine(dir, "node_modules", "playwright")))
            {
                State = "Installing the browser worker (first run, this can take a minute)…";
                if (await RunAsync(dir, "npm", "install --no-audit --no-fund", ct) != 0) { State = "npm install failed in src/browser-worker; see the API log."; return; }
                if (await RunAsync(dir, "npx", "playwright install chromium", ct) != 0) { State = "Installing Chromium for Playwright failed; see the API log."; return; }
            }
            State = "Starting the browser worker…";
            var port = new Uri(opts.WorkerUrl).Port.ToString();
            var psi = new ProcessStartInfo("node", "server.mjs") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.Environment["PORT"] = port;
            if (!string.IsNullOrEmpty(opts.WorkerToken)) psi.Environment["WORKER_TOKEN"] = opts.WorkerToken;
            _proc = Start(psi);
            for (var i = 0; i < 40; i++)
            {
                if ((await worker.HealthAsync(ct)).Ok) { State = null; log.LogInformation("Browser worker started on port {Port}", port); return; }
                if (_proc.HasExited) break;
                await Task.Delay(500, ct);
            }
            State = "The browser worker did not start (is Node.js 20+ installed and on PATH?); see the API log.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            State = $"Could not start the browser worker: {e.Message}";
            log.LogWarning(e, "Browser worker auto-start failed");
        }
        finally { _lock.Release(); }
    }

    string? FindWorkerDir()
    {
        if (opts.WorkerDir is { Length: > 0 } configured) return File.Exists(Path.Combine(configured, "server.mjs")) ? configured : null;
        for (var d = new DirectoryInfo(env.ContentRootPath); d is not null; d = d.Parent)
            foreach (var candidate in new[] { Path.Combine(d.FullName, "src", "browser-worker"), Path.Combine(d.FullName, "browser-worker") })
                if (File.Exists(Path.Combine(candidate, "server.mjs"))) return candidate;
        return null;
    }

    Process Start(ProcessStartInfo psi)
    {
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log.LogInformation("[browser-worker] {Line}", e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log.LogWarning("[browser-worker] {Line}", e.Data); };
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    // npm and npx are .cmd shims on Windows, so they go through the shell there.
    async Task<int> RunAsync(string dir, string tool, string args, CancellationToken ct)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c {tool} {args}")
            : new ProcessStartInfo(tool, args);
        psi.WorkingDirectory = dir; psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        using var p = Start(psi);
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}
