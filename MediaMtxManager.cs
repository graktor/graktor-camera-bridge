using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;

namespace ZendoriaCameraBridge;

/// <summary>
/// Downloads (once), configures, and supervises a local MediaMTX process -- the actual
/// RTSP-to-HLS engine. This program deliberately does not reimplement any part of that: it
/// only ever generates MediaMTX's config file and starts/restarts the process, the same way
/// a person setting this up by hand would, just automatically. MediaMTX serves plain HTTP on
/// the shop LAN; Tailscale Funnel (see TailscaleManager) is what turns that into a public
/// https:// address, terminating TLS itself -- MediaMTX itself never needs a certificate.
/// </summary>
public class MediaMtxManager
{
    private const int HlsPort = 8888;

    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "mediamtx");
    private string ExePath => Path.Combine(_dir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mediamtx.exe" : "mediamtx");
    private string ConfigPath => Path.Combine(_dir, "mediamtx.yml");

    /// <summary>
    /// The OS/arch slice of MediaMTX's own release asset filenames, e.g.
    /// "mediamtx_v1.21.1_linux_arm64.tar.gz" -- Windows ships as .zip, everything else as
    /// .tar.gz, which is why EnsureInstalledAsync branches on the extension below.
    /// </summary>
    private static string ReleaseAssetSuffix
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                _ => "amd64",
            };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"windows_{arch}.zip";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"darwin_{arch}.tar.gz";
            return $"linux_{arch}.tar.gz";
        }
    }

    private Process? _process;
    private string _lastConfigWritten = "";

    public int LocalHlsPort => HlsPort;

    /// <summary>The stable MediaMTX path name for one camera -- based on its id, not its (renameable) name.</summary>
    public static string PathNameFor(int cameraId) => $"cam{cameraId}";

    /// <summary>
    /// Standard Dahua CGI/RTSP convention -- see the conversation that led to this program.
    /// Channel is per-camera (Camera.RtspChannel in Zendoria) since a real camera's actual
    /// feed isn't always on channel 1 -- found the hard way on the very first real camera
    /// this program was tested against. A future version could let the whole path template
    /// be overridden per camera for non-Dahua brands.
    /// </summary>
    public static string RtspUrlFor(CameraForBridge camera)
    {
        var auth = string.IsNullOrEmpty(camera.Username) ? "" : $"{Uri.EscapeDataString(camera.Username)}:{Uri.EscapeDataString(camera.Password ?? "")}@";
        return $"rtsp://{auth}{camera.IpAddress}:554/cam/realmonitor?channel={camera.RtspChannel}&subtype=1";
    }

    public async Task EnsureInstalledAsync(ILogger log)
    {
        if (File.Exists(ExePath)) return;

        Directory.CreateDirectory(_dir);
        log.Info("MediaMTX not found -- downloading it once from its official GitHub releases...");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ZendoriaCameraBridge");

        var release = await http.GetFromJsonAsync<GitHubRelease>("https://api.github.com/repos/bluenviron/mediamtx/releases/latest")
            ?? throw new InvalidOperationException("Could not look up the latest MediaMTX release.");
        var suffix = ReleaseAssetSuffix;
        var asset = release.assets.FirstOrDefault(a => a.name.Contains(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Could not find a '{suffix}' build in the latest MediaMTX release.");

        var archivePath = Path.Combine(_dir, suffix.EndsWith(".tar.gz") ? "mediamtx.tar.gz" : "mediamtx.zip");
        await using (var stream = await http.GetStreamAsync(asset.browser_download_url))
        await using (var file = File.Create(archivePath))
        {
            await stream.CopyToAsync(file);
        }

        if (archivePath.EndsWith(".tar.gz"))
        {
            await using var fileStream = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, _dir, overwriteFiles: true);
        }
        else
        {
            ZipFile.ExtractToDirectory(archivePath, _dir, overwriteFiles: true);
        }
        File.Delete(archivePath);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // tar entries' execute bit isn't always honored depending on how it was packed --
            // explicitly setting it is cheap insurance against "Permission denied" on first run.
            try { File.SetUnixFileMode(ExePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
            catch { /* best effort -- if this fails, the actual process start below will surface a clearer error */ }
        }

        log.Info($"MediaMTX {release.tag_name} installed.");
    }

    /// <summary>Rewrites the config for the current camera list and (re)starts MediaMTX only if something actually changed.</summary>
    public void EnsureRunning(List<CameraForBridge> cameras, ILogger log)
    {
        var yaml = BuildConfig(cameras);
        var configChanged = yaml != _lastConfigWritten;
        if (configChanged)
        {
            File.WriteAllText(ConfigPath, yaml);
            _lastConfigWritten = yaml;
        }

        var needsStart = _process is null || _process.HasExited;
        if (!needsStart && !configChanged) return;

        if (_process is { HasExited: false })
        {
            log.Info("Camera list changed -- restarting MediaMTX.");
            try { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } catch { /* best effort */ }
        }

        // Redirecting stdout/stderr without ever reading them would eventually block MediaMTX
        // once its pipe buffer fills (and hid every diagnostic -- a real camera's actual RTSP
        // connection error, e.g. wrong credentials vs. refused vs. timeout, only ever shows up
        // in MediaMTX's own log output, not this agent's).
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "mediamtx.yml",
                WorkingDirectory = _dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log.Info($"[mediamtx] {e.Data}"); };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log.Warn($"[mediamtx] {e.Data}"); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        log.Info($"MediaMTX started (PID {_process.Id}) serving {cameras.Count} camera(s) on http://127.0.0.1:{HlsPort}.");
    }

    private static string BuildConfig(List<CameraForBridge> cameras)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("hlsAddress: :" + HlsPort);
        // Only HLS is actually used (Funnel proxies that one port) -- every other server
        // MediaMTX would otherwise start (RTSP, RTMP, WebRTC, SRT, MoQ) is pure unused
        // overhead here, and each one is a port that might already be taken by something
        // else on whatever PC this gets installed on. Found the hard way: a second PC had
        // something else already bound to MediaMTX's default RTP port (UDP 8000).
        sb.AppendLine("rtsp: no");
        sb.AppendLine("rtmp: no");
        sb.AppendLine("webrtc: no");
        sb.AppendLine("srt: no");
        sb.AppendLine("paths:");
        if (cameras.Count == 0)
        {
            sb.AppendLine("  # no cameras configured for this branch yet");
        }
        foreach (var c in cameras)
        {
            sb.AppendLine($"  {PathNameFor(c.CameraId)}:");
            sb.AppendLine($"    source: {RtspUrlFor(c)}");
            sb.AppendLine("    sourceOnDemand: no");
        }
        return sb.ToString();
    }

    public void Stop()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    private record GitHubRelease(string tag_name, List<GitHubAsset> assets);
    private record GitHubAsset(string name, string browser_download_url);
}
