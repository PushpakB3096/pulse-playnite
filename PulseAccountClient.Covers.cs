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
    private bool includePlayniteCoversInSync;
    private bool? syncPlayniteCoversCache;
    private bool? hasActivePlayLogPlusCache;
    private DateTime usersMeFeatureCacheAtUtc = DateTime.MinValue;
    private static readonly TimeSpan UsersMeFeatureCacheTtl = TimeSpan.FromMinutes(5);

    public async Task<bool> GetSyncPlayniteCoversAsync(bool forceRefresh = false)
    {
        await EnsureUsersMeFeatureCacheAsync(forceRefresh).ConfigureAwait(false);
        return syncPlayniteCoversCache == true;
    }

    public async Task<bool> GetHasActivePlayLogPlusAsync(bool forceRefresh = false)
    {
        await EnsureUsersMeFeatureCacheAsync(forceRefresh).ConfigureAwait(false);
        return hasActivePlayLogPlusCache == true;
    }

    private async Task EnsureUsersMeFeatureCacheAsync(bool forceRefresh)
    {
        if (!HasBearerToken())
        {
            syncPlayniteCoversCache = false;
            hasActivePlayLogPlusCache = false;
            usersMeFeatureCacheAtUtc = DateTime.UtcNow;
            return;
        }

        if (!forceRefresh
            && syncPlayniteCoversCache.HasValue
            && hasActivePlayLogPlusCache.HasValue
            && DateTime.UtcNow - usersMeFeatureCacheAtUtc < UsersMeFeatureCacheTtl)
        {
            return;
        }

        var req = new HttpRequestMessage(HttpMethod.Get, usersMeEndpoint);
        ApplyBearer(req);

        try
        {
            var resp = await http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger.Info("PlayLog: /users/me failed for account features; treating as false.");
                syncPlayniteCoversCache = false;
                hasActivePlayLogPlusCache = false;
                usersMeFeatureCacheAtUtc = DateTime.UtcNow;
                return;
            }

            var parsed = JsonConvert.DeserializeObject<UsersMeResponse>(body);
            syncPlayniteCoversCache = parsed?.Data?.Features?.SyncPlayniteCovers == true;
            hasActivePlayLogPlusCache = parsed?.Data?.PremiumUserDetails?.PremiumActive == true;
            usersMeFeatureCacheAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: /users/me request failed for account features.");
            syncPlayniteCoversCache = false;
            hasActivePlayLogPlusCache = false;
            usersMeFeatureCacheAtUtc = DateTime.UtcNow;
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

        var metadata = Pulse.PlayniteCoverReader.TryReadForSync(playniteApi, game, coverSyncStateStore);
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

        [JsonProperty("premiumUserDetails")]
        public UsersMePremiumUserDetails PremiumUserDetails { get; set; }
    }

    private sealed class UsersMeFeatures
    {
        [JsonProperty("syncPlayniteCovers")]
        public bool SyncPlayniteCovers { get; set; }
    }

    private sealed class UsersMePremiumUserDetails
    {
        [JsonProperty("premiumActive")]
        public bool PremiumActive { get; set; }
    }

    private sealed class GamesSyncResponse
    {
        [JsonProperty("coversNeedingUpload")]
        public List<string> CoversNeedingUpload { get; set; }
    }
}
