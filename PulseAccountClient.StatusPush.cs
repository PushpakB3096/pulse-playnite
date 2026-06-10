using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

public partial class PulseAccountClient
{
    private readonly string playniteStatusPendingEndpoint;
    private readonly string playniteStatusPendingAckEndpoint;

    public async Task<IReadOnlyList<PlayniteStatusPendingDto>> GetPlayniteStatusPendingAsync()
    {
        if (!HasBearerToken())
        {
            return Array.Empty<PlayniteStatusPendingDto>();
        }

        var req = new HttpRequestMessage(HttpMethod.Get, playniteStatusPendingEndpoint);
        ApplyBearer(req);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: GET playnite-status-pending failed.");
            throw;
        }

        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            logger.Error("PlayLog: playnite-status-pending " + resp.StatusCode + ": " + body);
            throw new Exception("PlayLog backend error: " + resp.StatusCode);
        }

        var parsed = JsonConvert.DeserializeObject<PlayniteStatusPendingResponse>(body);
        if (parsed?.Data == null || parsed.Data.Count == 0)
        {
            return Array.Empty<PlayniteStatusPendingDto>();
        }

        return parsed.Data;
    }

    public async Task AckPlayniteStatusPendingAsync(IEnumerable<string> playniteIds)
    {
        if (!HasBearerToken())
        {
            return;
        }

        var idList = playniteIds != null
            ? new List<string>(playniteIds)
            : new List<string>();
        if (idList.Count == 0)
        {
            return;
        }

        var payload = new PlayniteStatusPendingAckRequest { PlayniteIds = idList };
        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, playniteStatusPendingAckEndpoint);
        req.Content = content;
        ApplyBearer(req);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: POST playnite-status-pending/ack failed.");
            throw;
        }

        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            logger.Error("PlayLog: playnite-status-pending/ack " + resp.StatusCode + ": " + body);
            throw new Exception("PlayLog backend error: " + resp.StatusCode);
        }
    }

    private sealed class PlayniteStatusPendingResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("data")]
        public List<PlayniteStatusPendingDto> Data { get; set; }
    }

    private sealed class PlayniteStatusPendingAckRequest
    {
        [JsonProperty("playniteIds")]
        public List<string> PlayniteIds { get; set; }
    }

    public sealed class PlayniteStatusPendingDto
    {
        [JsonProperty("playniteId")]
        public string PlayniteId { get; set; }

        [JsonProperty("targetStatus")]
        public string TargetStatus { get; set; }

        [JsonProperty("targetCompletionStatusName")]
        public string TargetCompletionStatusName { get; set; }

        [JsonProperty("targetCompletionStatusId")]
        public string TargetCompletionStatusId { get; set; }

        [JsonProperty("requestedAt")]
        public string RequestedAt { get; set; }

        [JsonProperty("favorite")]
        public bool? Favorite { get; set; }
    }
}
