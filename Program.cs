using ZendoriaCameraBridge;

ILogger log = new FileLogger();
Console.WriteLine("Zendoria Camera Bridge");
Console.WriteLine("=======================");

var config = BridgeConfig.Load();
if (config is null || !config.IsPaired)
{
    config = await PairInteractivelyAsync(log);
}

var api = new ApiClient(config);
var mediaMtx = new MediaMtxManager();
var tailscale = new TailscaleManager();

if (!tailscale.IsInstalled)
{
    log.Error("Tailscale isn't installed on this PC. Install it from https://tailscale.com/download, then run this program again.");
    log.Error("(This program uses Tailscale to give your cameras a real, secure web address without opening anything on your router.)");
    Environment.Exit(1);
    return;
}

await mediaMtx.EnsureInstalledAsync(log);

var lastReportedUrls = new Dictionary<int, string>();
var hostname = "zendoria-cam-" + Environment.MachineName.ToLowerInvariant();

Console.CancelKeyPress += (_, e) =>
{
    log.Info("Shutting down...");
    mediaMtx.Stop();
};

log.Info("Running. Press Ctrl+C to stop.");

while (true)
{
    try
    {
        await api.HeartbeatAsync();

        var cameras = await api.GetCamerasAsync();
        mediaMtx.EnsureRunning(cameras, log);

        if (cameras.Count > 0)
        {
            await EnsurePublicAccessAsync(cameras, log);
        }
    }
    catch (AgentUnauthorizedException)
    {
        // Almost certainly: this agent was removed from the Cameras page and a fresh pairing
        // code issued -- the old credentials on disk are just dead, not a transient failure,
        // so retrying them forever would never recover on its own. Re-pairing itself is
        // wrapped separately: it needs a real interactive console (asks for a fresh code),
        // and a failed attempt there -- no console attached, nothing typed, a bad code --
        // must not crash the whole agent. Worst case it just asks again next round.
        log.Warn("This device's pairing is no longer valid (it may have been removed from the Cameras page). Re-pairing now.");
        try
        {
            config = await PairInteractivelyAsync(log);
            api = new ApiClient(config);
        }
        catch (Exception ex)
        {
            log.Error($"Could not re-pair: {ex.Message}. Will ask again shortly -- run this program from a normal console window if it isn't already.");
        }
    }
    catch (Exception ex)
    {
        log.Error($"This round failed, will retry shortly: {ex.Message}");
    }

    await Task.Delay(TimeSpan.FromSeconds(60));
}

async Task<BridgeConfig> PairInteractivelyAsync(ILogger logger)
{
    var fresh = new BridgeConfig();
    Console.Write($"Zendoria API address [{fresh.ApiBaseUrl}]: ");
    var url = Console.ReadLine();
    if (!string.IsNullOrWhiteSpace(url)) fresh.ApiBaseUrl = url.Trim().TrimEnd('/');

    Console.Write("Pairing code (from the Cameras page in Zendoria): ");
    var code = Console.ReadLine()?.Trim() ?? "";

    var pairingClient = new ApiClient(fresh);
    var (agentId, agentKey) = await pairingClient.PairAsync(code);
    fresh.AgentId = agentId;
    fresh.AgentKey = agentKey;
    fresh.Save();
    logger.Info("Paired successfully.");
    return fresh;
}

async Task EnsurePublicAccessAsync(List<CameraForBridge> cameras, ILogger logger)
{
    var status = await tailscale.GetStatusAsync();
    if (!(status.BackendState == "Running" && status.Online))
    {
        var keyResult = await api.GetTailscaleAuthKeyAsync();
        if (keyResult.AuthKey is null)
        {
            logger.Warn(keyResult.Message ?? "Remote access isn't available yet.");
            return;
        }
        await tailscale.EnsureConnectedAsync(keyResult.AuthKey, hostname, logger);
        status = await tailscale.GetStatusAsync();
    }

    var funnelOk = await tailscale.EnsureFunnelOnAsync(mediaMtx.LocalHlsPort, logger);
    if (!funnelOk || status.DnsName is null) return;

    foreach (var camera in cameras)
    {
        var url = $"https://{status.DnsName}/{MediaMtxManager.PathNameFor(camera.CameraId)}/index.m3u8";
        if (lastReportedUrls.TryGetValue(camera.CameraId, out var existing) && existing == url) continue;

        await api.ReportPlaybackUrlAsync(camera.CameraId, url);
        lastReportedUrls[camera.CameraId] = url;
        logger.Info($"'{camera.Name}' is now live at {url}");
    }
}
