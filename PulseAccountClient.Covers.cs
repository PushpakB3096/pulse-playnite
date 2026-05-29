using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;

public partial class PulseAccountClient
{
    private readonly string usersMeEndpoint;
    private readonly string playniteCoverUploadEndpoint;

    private bool includePlayniteCoversInSync;
    private bool? syncPlayniteCoversCache;
    private DateTime syncPlayniteCoversCacheAtUtc = DateTime.MinValue;
    private static readonly TimeSpan SyncPlayniteCoversCacheTtl = TimeSpan.FromMinutes(5);

    partial void InitializeCoverEndpoints(string baseUrlClean)
    {
        usersMeEndpoint = baseUrlClean + "/api/users/me";
        playniteCoverUploadEndpoint = baseUrlClean + "/api/games/covers/playnite";
    }

    public async Task<bool> GetSyncPlayniteCoversAsync(bool forceRefresh = false)
    {
        if (!HasBearerToken())
        {
            return false;
        }

        if (!forceRefresh
            && syncPlayniteCoversCache.HasValue
            && DateTime.UtcNow - syncPlayniteCoversCacheAtUtc < SyncPlayniteCoversCacheTtl)
        {
            return syncPlayniteCoversCache.Value;
        }

        var req = new HttpRequestMessage(HttpMethod.Get, usersMeEndpoint);
        ApplyBearer(req);

        try
        {
            var resp = await http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger.Info("PlayLog: /users/me failed for cover flag; treating as false.");
                syncPlayniteCoversCache = false;
                syncPlayniteCoversCacheAtUtc = DateTime.UtcNow;
                return false;
            }

            var parsed = JsonConvert.DeserializeObject<UsersMeResponse>(body);
            var enabled = parsed?.Data?.Features?.SyncPlayniteCovers == true;
            syncPlayniteCoversCache = enabled;
            syncPlayniteCoversCacheAtUtc = DateTime.UtcNow;
            return enabled;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: /users/me request failed for cover flag.");
            syncPlayniteCoversCache = false;
            syncPlayniteCoversCacheAtUtc = DateTime.UtcNow;
            return false;
        }
    }

    public async Task UploadPlayniteCoverAsync(
        string playniteId,
        string hash,
        byte[] fileBytes,
        string contentType)
    {
        if (string.IsNullOrWhiteSpace(playniteId))
        {
            throw new ArgumentException("playniteId is required", nameof(playniteId));
        }

        if (fileBytes == null || fileBytes.Length == 0)
        {
            throw new ArgumentException("file bytes are required", nameof(fileBytes));
        }

        if (!HasBearerToken())
        {
            throw new InvalidOperationException("PlayLog is not linked");
        }

        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent(playniteId), "playniteId");
            form.Add(new StringContent(hash ?? string.Empty), "hash");

            var fileContent = new ByteArrayContent(fileBytes);
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            }

            form.Add(fileContent, "file", "cover.bin");

            var req = new HttpRequestMessage(HttpMethod.Post, playniteCoverUploadEndpoint)
            {
                Content = form
            };
            ApplyBearer(req);

            var resp = await http.SendAsync(req).ConfigureAwait(false);
            var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger.Error("PlayLog: cover upload failed " + resp.StatusCode + ": " + responseBody);
                throw new Exception("PlayLog cover upload error: " + resp.StatusCode);
            }
        }
    }

    private void AttachPlayniteCoverMetadata(PulseGameDto dto, Game game)
    {
        if (!includePlayniteCoversInSync || dto == null || game == null)
        {
            return;
        }

        var metadata = Pulse.PlayniteCoverReader.TryRead(playniteApi, game);
        if (metadata == null)
        {
            return;
        }

        if (string.Equals(metadata.SourceKind, "url", StringComparison.OrdinalIgnoreCase))
        {
            dto.PlayniteCover = new PlayniteCoverSyncDto
            {
                SourceKind = "url",
                Url = metadata.Url
            };
            return;
        }

        dto.PlayniteCover = new PlayniteCoverSyncDto
        {
            SourceKind = "file",
            Hash = metadata.Hash,
            ByteSize = metadata.ByteSize,
            ContentType = metadata.ContentType
        };
    }

    private List<string> ParseCoversNeedingUpload(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new List<string>();
        }

        try
        {
            var parsed = JsonConvert.DeserializeObject<GamesSyncResponse>(responseBody);
            return parsed?.CoversNeedingUpload ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private sealed class UsersMeResponse
    {
        [JsonProperty("data")]
        public UsersMeData Data { get; set; }
    }

    private sealed class UsersMeData
    {
        [JsonProperty("features")]
        public UsersMeFeatures Features { get; set; }
    }

    private sealed class UsersMeFeatures
    {
        [JsonProperty("syncPlayniteCovers")]
        public bool SyncPlayniteCovers { get; set; }
    }

    private sealed class GamesSyncResponse
    {
        [JsonProperty("coversNeedingUpload")]
        public List<string> CoversNeedingUpload { get; set; }
    }
}
