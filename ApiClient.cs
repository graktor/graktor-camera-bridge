using System.Net;
using System.Net.Http.Json;

namespace ZendoriaCameraBridge;

public record CameraForBridge(int CameraId, string Name, string IpAddress, string? Username, string? Password, int RtspChannel);
public record TailscaleAuthKeyResult(string? AuthKey, string? Message);
record PairResponse(int CameraBridgeAgentId, string AgentKey);

/// <summary>
/// This agent's own credentials were rejected -- e.g. it was removed from the Cameras page
/// and a new pairing code issued, or the key on disk just doesn't match anything server-side
/// any more. Distinct from a transient network/server failure so Program.cs can react by
/// re-pairing instead of just retrying the same dead credentials forever.
/// </summary>
public class AgentUnauthorizedException : Exception
{
    public AgentUnauthorizedException() : base("This agent's credentials were not accepted -- it may have been removed from the Cameras page.") { }
}

/// <summary>Talks to the api/camera-bridge/* endpoints on the Zendoria API -- see CameraBridgeController.cs in the main app.</summary>
public class ApiClient
{
    private readonly HttpClient _http;
    private readonly BridgeConfig _config;

    public ApiClient(BridgeConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    private void AddAgentHeaders(HttpRequestMessage req)
    {
        req.Headers.Add("X-Agent-Id", _config.AgentId.ToString());
        req.Headers.Add("X-Agent-Key", _config.AgentKey);
    }

    private static void EnsureAgentCallSucceeded(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new AgentUnauthorizedException();
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Trades a short-lived pairing code (from the Cameras page) for this agent's own long-lived key.</summary>
    public async Task<(int AgentId, string AgentKey)> PairAsync(string pairingCode)
    {
        var response = await _http.PostAsJsonAsync("/api/camera-bridge/pair", new { pairingCode });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Pairing failed ({(int)response.StatusCode}): {body}");
        }
        var result = await response.Content.ReadFromJsonAsync<PairResponse>() ?? throw new InvalidOperationException("Pairing succeeded but returned no key.");
        return (result.CameraBridgeAgentId, result.AgentKey);
    }

    public async Task HeartbeatAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/camera-bridge/heartbeat");
        AddAgentHeaders(req);
        var response = await _http.SendAsync(req);
        EnsureAgentCallSucceeded(response);
    }

    public async Task<List<CameraForBridge>> GetCamerasAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/camera-bridge/cameras");
        AddAgentHeaders(req);
        var response = await _http.SendAsync(req);
        EnsureAgentCallSucceeded(response);
        return await response.Content.ReadFromJsonAsync<List<CameraForBridge>>() ?? new();
    }

    public async Task ReportPlaybackUrlAsync(int cameraId, string playbackUrl)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/camera-bridge/cameras/{cameraId}/playback-url");
        AddAgentHeaders(req);
        req.Content = JsonContent.Create(new { playbackUrl });
        var response = await _http.SendAsync(req);
        EnsureAgentCallSucceeded(response);
    }

    public async Task<TailscaleAuthKeyResult> GetTailscaleAuthKeyAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/camera-bridge/tailscale-authkey");
        AddAgentHeaders(req);
        var response = await _http.SendAsync(req);
        EnsureAgentCallSucceeded(response);
        return await response.Content.ReadFromJsonAsync<TailscaleAuthKeyResult>() ?? new(null, "No response from server.");
    }
}
