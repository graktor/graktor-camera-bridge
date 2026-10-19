using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ZendoriaCameraBridge;

/// <summary>
/// Drives the real Tailscale client (Windows, macOS, or Linux) via its own command-line -- this
/// program does not embed Tailscale's networking code, it automates the same commands a person
/// would type by hand. Tailscale itself (a proper per-OS install + background service) is a
/// genuine prerequisite this program checks for and refuses to fake around, unlike MediaMTX (a
/// plain portable executable this program is comfortable fetching on someone's behalf).
/// </summary>
public class TailscaleManager
{
    // Known install locations per OS -- PATH lookup (below) covers everything else, including
    // every Linux package manager's own default (/usr/bin, /usr/local/bin), which there's no
    // single well-known path for the way Windows/macOS each have one dominant installer.
    private static readonly string[] KnownPaths = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new[]
        {
            @"C:\Program Files\Tailscale\tailscale.exe",
            @"C:\Program Files (x86)\Tailscale\tailscale.exe",
        }
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? new[]
        {
            "/Applications/Tailscale.app/Contents/MacOS/Tailscale",
            "/usr/local/bin/tailscale",
            "/opt/homebrew/bin/tailscale",
        }
        : new[] { "/usr/bin/tailscale", "/usr/local/bin/tailscale" };

    private static readonly string ExeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tailscale.exe" : "tailscale";

    private string? _exePath;
    private bool? _isInstalled;

    /// <summary>
    /// Actually verifies Tailscale can be launched -- not just that a known install path
    /// exists, since it might be on PATH from a non-default install location, and not just
    /// "some string was assigned", which would silently claim success for a PATH lookup that
    /// was never actually resolved.
    /// </summary>
    public bool IsInstalled
    {
        get
        {
            if (_isInstalled.HasValue) return _isInstalled.Value;

            foreach (var p in KnownPaths)
            {
                if (File.Exists(p)) { _exePath = p; return (_isInstalled = true).Value; }
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = ExeName,
                    ArgumentList = { "version" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                process!.WaitForExit(5000);
                _exePath = ExeName;
                return (_isInstalled = process.ExitCode == 0).Value;
            }
            catch
            {
                // Not found on PATH, or couldn't be launched at all -- genuinely not installed.
                return (_isInstalled = false).Value;
            }
        }
    }

    private string FindExe() => _exePath ?? throw new InvalidOperationException("Tailscale is not installed -- check IsInstalled first.");

    private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var exe = FindExe();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
    }

    public record Status(bool Online, string? DnsName, string BackendState);

    public async Task<Status> GetStatusAsync()
    {
        var (_, output) = await RunAsync("status", "--json");
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        var backendState = root.TryGetProperty("BackendState", out var bs) ? bs.GetString() ?? "Unknown" : "Unknown";
        var self = root.TryGetProperty("Self", out var s) ? s : default;
        var dnsName = self.ValueKind == JsonValueKind.Object && self.TryGetProperty("DNSName", out var dn) ? dn.GetString()?.TrimEnd('.') : null;
        var online = self.ValueKind == JsonValueKind.Object && self.TryGetProperty("Online", out var o) && o.GetBoolean();
        return new Status(online, dnsName, backendState);
    }

    /// <summary>Joins Zendoria's central tailnet using a fresh key from the API. No-ops if already connected.</summary>
    public async Task EnsureConnectedAsync(string authKey, string hostname, ILogger log)
    {
        var status = await GetStatusAsync();
        if (status.BackendState == "Running" && status.Online) return;

        log.Info("Connecting to Zendoria's private network via Tailscale...");
        var (exitCode, output) = await RunAsync("up", "--authkey=" + authKey, "--hostname=" + hostname, "--accept-routes=false");
        if (exitCode != 0) throw new InvalidOperationException($"'tailscale up' failed: {output}");
    }

    /// <summary>
    /// Publishes the given local port as a real public HTTPS address. Idempotent -- safe to
    /// call every loop. Returns whether it actually succeeded, since a caller that goes ahead
    /// and reports a playback URL regardless would be handing out an address that doesn't
    /// actually work yet.
    /// </summary>
    public async Task<bool> EnsureFunnelOnAsync(int localPort, ILogger log)
    {
        // Tailscale replaced the old "funnel <port> on/off" form with a serve-style "--bg"
        // flag (see https://tailscale.com/kb/1242/tailscale-serve) -- found the hard way
        // against a real client during the first real-world run of this program.
        var (exitCode, output) = await RunAsync("funnel", "--bg", localPort.ToString());
        if (exitCode != 0)
        {
            log.Warn($"'tailscale funnel --bg {localPort}' did not succeed: {output.Trim()}");
            return false;
        }
        return true;
    }
}
