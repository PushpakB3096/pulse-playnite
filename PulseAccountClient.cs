using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;

public partial class PulseAccountClient
{
    private static readonly ILogger logger = LogManager.GetLogger();

    private const string BASE_URL = "https://pulse-server-m2u1.onrender.com";

    private static readonly HttpClient http = new HttpClient();

    private const int GamesSyncBatchSize = 300;
    private const string GamesSyncV2Query = "?syncVersion=2";

    private readonly IPlayniteAPI playniteApi;
    private readonly Func<string> getBearerToken;
    private readonly string _extensionsDataPath;
    private readonly string gamesSyncEndpoint;
    private readonly string gamesSyncEndpointV2;
    private readonly string gamesSyncCompleteEndpointV2;
    private readonly string gamesByPlayniteDeletePrefix;
    private readonly string sessionStartEndpoint;
    private readonly string sessionStopEndpoint;
    private readonly string pairingStartEndpoint;
    private readonly string pairingStatusPrefix;
    private readonly string sessionImportGameActivityEndpoint;

    public PulseAccountClient(IPlayniteAPI api, Func<string> getBearerToken)
    {
        if (api == null)
            throw new ArgumentNullException(nameof(api));
        playniteApi = api;
        this.getBearerToken = getBearerToken ?? throw new ArgumentNullException(nameof(getBearerToken));
        _extensionsDataPath = api.Paths?.ExtensionsDataPath;

        var baseUrlClean = BASE_URL.TrimEnd('/');
        InitializeCoverEndpoints(baseUrlClean);
        gamesSyncEndpoint = baseUrlClean + "/api/games/sync";
        gamesSyncEndpointV2 = gamesSyncEndpoint + GamesSyncV2Query;
        gamesSyncCompleteEndpointV2 = baseUrlClean + "/api/games/sync/complete" + GamesSyncV2Query;
        gamesByPlayniteDeletePrefix = baseUrlClean + "/api/games/by-playnite/";
        sessionStartEndpoint = baseUrlClean + "/api/sessions/start";
        sessionStopEndpoint = baseUrlClean + "/api/sessions/stop";
        pairingStartEndpoint = baseUrlClean + "/api/pairing/start";
        pairingStatusPrefix = baseUrlClean + "/api/pairing/";
        sessionImportGameActivityEndpoint =
            baseUrlClean + "/api/sessions/import/game-activity";
    }

    private void ApplyBearer(HttpRequestMessage req)
    {
        var t = getBearerToken != null ? getBearerToken.Invoke() : null;
        if (string.IsNullOrWhiteSpace(t))
            return;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.Trim());
    }

    private bool HasBearerToken()
    {
        var token = getBearerToken != null ? getBearerToken.Invoke() : null;
        return !string.IsNullOrWhiteSpace(token);
    }

    public async Task PostSessionStartAsync(SessionStartDto dto)
    {
        if (dto == null)
            throw new ArgumentNullException(nameof(dto));

        var json = JsonConvert.SerializeObject(dto);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var req = new HttpRequestMessage(HttpMethod.Post, sessionStartEndpoint);
        req.Content = content;
        ApplyBearer(req);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: HTTP session start failed.");
            throw;
        }

        var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            logger.Error("PlayLog: session start backend responded with " + resp.StatusCode + ": " + responseBody);
            throw new Exception("PlayLog backend error: " + resp.StatusCode);
        }

        logger.Info("PlayLog: session start posted for clientSessionId=" + dto.ClientSessionId);
    }

    public async Task PostSessionStopAsync(SessionStopDto dto)
    {
        if (dto == null)
            throw new ArgumentNullException(nameof(dto));

        var json = JsonConvert.SerializeObject(dto);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var req = new HttpRequestMessage(HttpMethod.Post, sessionStopEndpoint);
        req.Content = content;
        ApplyBearer(req);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: HTTP session stop failed.");
            throw;
        }

        var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            logger.Error("PlayLog: session stop backend responded with " + resp.StatusCode + ": " + responseBody);
            throw new Exception("PlayLog backend error: " + resp.StatusCode);
        }

        logger.Info("PlayLog: session stop posted for clientSessionId=" + dto.ClientSessionId);
    }

    public async Task<GameActivityImportBatchResult> PostGameActivityImportBatchAsync(
        GameActivityImportBatchDto dto)
    {
        if (dto == null || dto.Sessions == null || dto.Sessions.Count == 0)
        {
            logger.Info("PlayLog: GA import batch empty; skipping POST.");
            return new GameActivityImportBatchResult();
        }

        var json = JsonConvert.SerializeObject(dto);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var req = new HttpRequestMessage(HttpMethod.Post, sessionImportGameActivityEndpoint);
        req.Content = content;
        ApplyBearer(req);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "PlayLog: HTTP GA import batch failed.");
            return null;
        }

        var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            logger.Warn(
                "PlayLog: GA import batch responded with "
                    + resp.StatusCode
                    + ": "
                    + responseBody);
            return null;
        }

        try
        {
            var env =
                JsonConvert.DeserializeObject<ApiEnvelope<GameActivityImportResponseData>>(
                    responseBody);
            var counts = env?.Data?.Counts;
            if (counts == null)
            {
                return new GameActivityImportBatchResult();
            }

            return new GameActivityImportBatchResult
            {
                Inserted = counts.Inserted,
                SkippedOverlap = counts.SkippedOverlap,
                SkippedDuplicateIdempotency = counts.SkippedDuplicateIdempotency,
                Errors = counts.Errors
            };
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "PlayLog: failed to parse GA import response.");
            return new GameActivityImportBatchResult();
        }
    }

    public class SessionStartDto
    {
        [JsonProperty("clientSessionId")]
        public string ClientSessionId { get; set; }

        [JsonProperty("playniteId")]
        public string PlayniteId { get; set; }

        [JsonProperty("startTime")]
        public string StartTime { get; set; }
    }

    public class SessionStopDto
    {
        [JsonProperty("clientSessionId")]
        public string ClientSessionId { get; set; }

        [JsonProperty("playniteId")]
        public string PlayniteId { get; set; }

        [JsonProperty("endTime")]
        public string EndTime { get; set; }
    }

    private class GameActivityImportResponseData
    {
        [JsonProperty("counts")]
        public GameActivityImportCountsDto Counts { get; set; }
    }

    private class GameActivityImportCountsDto
    {
        [JsonProperty("inserted")]
        public int Inserted { get; set; }

        [JsonProperty("skipped_overlap")]
        public int SkippedOverlap { get; set; }

        [JsonProperty("skipped_duplicate_idempotency")]
        public int SkippedDuplicateIdempotency { get; set; }

        [JsonProperty("errors")]
        public int Errors { get; set; }
    }

    public async Task DeleteGamesByPlayniteIdsAsync(IEnumerable<string> playniteIds)
    {
        if (!HasBearerToken())
        {
            logger.Info("PlayLog: skip delete by playnite — not linked");
            return;
        }

        var ids = playniteIds != null
            ? playniteIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList()
            : new List<string>();

        if (ids.Count == 0)
        {
            logger.Info("PlayLog: DeleteGamesByPlayniteIdsAsync called with 0 ids.");
            return;
        }

        foreach (var id in ids)
        {
            var url = gamesByPlayniteDeletePrefix + Uri.EscapeDataString(id);
            var req = new HttpRequestMessage(HttpMethod.Delete, url);
            ApplyBearer(req);

            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(req).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: HTTP DELETE by-playnite failed for id=" + id);
                throw;
            }

            var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if ((int)resp.StatusCode == 404)
            {
                logger.Info("PlayLog: delete by-playnite returned 404 (game may already be gone): " + id);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                logger.Error("PlayLog: backend responded with " + resp.StatusCode + ": " + responseBody);
                throw new Exception("PlayLog backend error: " + resp.StatusCode);
            }
        }

        logger.Info("PlayLog: successfully processed " + ids.Count + " delete(s) by playnite id.");
    }

    partial void InitializeCoverEndpoints(string baseUrlClean);

    public void SetIncludePlayniteCoversInSync(bool enabled)
    {
        includePlayniteCoversInSync = enabled;
    }

    public async Task<IReadOnlyList<string>> SyncGamesAsync(IEnumerable<Game> games, bool fullLibrarySync = false)
    {
        var gameList = games != null ? games.ToList() : new List<Game>();
        if (gameList.Count == 0)
        {
            logger.Info("PlayLog: SyncGamesAsync called with 0 games.");
            return Array.Empty<string>();
        }

        if (!HasBearerToken())
        {
            logger.Info("PlayLog: skip library sync — not linked");
            return Array.Empty<string>();
        }

        var hltbBatchCounters = new HltbSyncBatchCounters();
        var totalGames = gameList.Count;
        var batchCount = (totalGames + GamesSyncBatchSize - 1) / GamesSyncBatchSize;
        var syncRunId = Guid.NewGuid().ToString();
        var allCoversNeedingUpload = new List<string>();

        for (var syncBatchIndex = 0; syncBatchIndex < batchCount; syncBatchIndex++)
        {
            var skip = syncBatchIndex * GamesSyncBatchSize;
            var batchSize = Math.Min(GamesSyncBatchSize, totalGames - skip);
            var batchGames = gameList.GetRange(skip, batchSize);
            var payload = new GamesSyncRequest
            {
                Games = batchGames
                    .Select(syncGame => MapGameToDto(syncGame, hltbBatchCounters))
                    .ToList(),
                FullLibrarySync = false,
                SyncRun = new SyncRunDto
                {
                    RunId = syncRunId,
                    BatchIndex = syncBatchIndex,
                    BatchCount = batchCount,
                    TotalGames = totalGames
                }
            };

            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var httpReq = new HttpRequestMessage(HttpMethod.Post, gamesSyncEndpointV2);
            httpReq.Content = content;
            ApplyBearer(httpReq);

            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(httpReq).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: HTTP request to games/sync failed.");
                throw;
            }

            var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                logger.Error("PlayLog: backend responded with " + resp.StatusCode + ": " + responseBody);
                throw new Exception("PlayLog backend error: " + resp.StatusCode);
            }

            allCoversNeedingUpload.AddRange(ParseCoversNeedingUpload(responseBody));
        }

        hltbBatchCounters.LogBatchSummary(logger, gameList.Count, _extensionsDataPath);

        if (fullLibrarySync)
        {
            var playniteIds = gameList.Select(libraryGame => libraryGame.Id.ToString()).Distinct().ToList();
            await PostGamesSyncCompleteAsync(syncRunId, playniteIds).ConfigureAwait(false);
        }

        logger.Info("PlayLog: successfully synced " + gameList.Count + " game(s).");
        return allCoversNeedingUpload
            .Where(playniteId => !string.IsNullOrWhiteSpace(playniteId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task PostGamesSyncCompleteAsync(string syncRunId, IReadOnlyList<string> playniteIds)
    {
        if (string.IsNullOrWhiteSpace(syncRunId))
        {
            throw new ArgumentException("syncRunId is required", nameof(syncRunId));
        }

        var payload = new GamesSyncCompleteRequest
        {
            PlayniteIds = playniteIds != null ? playniteIds.ToList() : new List<string>(),
            PruneMissing = true,
            SyncRun = new GamesSyncCompleteRunDto { RunId = syncRunId }
        };

        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var httpReq = new HttpRequestMessage(HttpMethod.Post, gamesSyncCompleteEndpointV2);
        httpReq.Content = content;
        ApplyBearer(httpReq);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(httpReq).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: HTTP request to games/sync/complete failed.");
            throw;
        }

        var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            logger.Error("PlayLog: sync/complete responded with " + resp.StatusCode + ": " + responseBody);
            throw new Exception("PlayLog backend error: " + resp.StatusCode);
        }
    }

    public async Task<PairingStartResult> StartPairingAsync()
    {
        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, pairingStartEndpoint);
        req.Content = content;

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: HTTP pairing start failed.");
            throw;
        }

        var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            logger.Error("PlayLog: pairing start responded with " + resp.StatusCode + ": " + responseBody);
            throw new Exception("PlayLog backend error: " + resp.StatusCode);
        }

        var env = JsonConvert.DeserializeObject<ApiEnvelope<PairingStartData>>(responseBody);
        if (env?.Data == null || string.IsNullOrWhiteSpace(env.Data.PairingId))
        {
            throw new Exception("PlayLog: invalid pairing start response.");
        }

        return new PairingStartResult
        {
            PairingId = env.Data.PairingId,
            UserCode = env.Data.UserCode
        };
    }

    public async Task<PairingStatusResult> GetPairingStatusAsync(string pairingId)
    {
        if (string.IsNullOrWhiteSpace(pairingId))
            throw new ArgumentException("pairingId required", nameof(pairingId));

        var url = pairingStatusPrefix + Uri.EscapeDataString(pairingId.Trim()) + "/status";
        var req = new HttpRequestMessage(HttpMethod.Get, url);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: HTTP pairing status failed.");
            throw;
        }

        var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            logger.Error("PlayLog: pairing status responded with " + resp.StatusCode + ": " + responseBody);
            throw new Exception("PlayLog backend error: " + resp.StatusCode);
        }

        var env = JsonConvert.DeserializeObject<ApiEnvelope<PairingStatusData>>(responseBody);
        if (env?.Data == null)
        {
            throw new Exception("PlayLog: invalid pairing status response.");
        }

        return new PairingStatusResult
        {
            Status = env.Data.Status,
            PluginToken = env.Data.PluginToken
        };
    }

    private class ApiEnvelope<T>
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("data")]
        public T Data { get; set; }
    }

    private class PairingStartData
    {
        [JsonProperty("pairingId")]
        public string PairingId { get; set; }

        [JsonProperty("userCode")]
        public string UserCode { get; set; }

        [JsonProperty("expiresAt")]
        public string ExpiresAt { get; set; }
    }

    private class PairingStatusData
    {
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("pluginToken")]
        public string PluginToken { get; set; }
    }

    public sealed class PairingStartResult
    {
        public string PairingId { get; set; }
        public string UserCode { get; set; }
    }

    public sealed class PairingStatusResult
    {
        public string Status { get; set; }
        public string PluginToken { get; set; }
    }

}

public sealed class GameActivityImportBatchDto
{
    [JsonProperty("sessions")]
    public List<GameActivityImportRowDto> Sessions { get; set; }
}

public sealed class GameActivityImportRowDto
{
    [JsonProperty("clientSessionId")]
    public string ClientSessionId { get; set; }

    [JsonProperty("playniteId")]
    public string PlayniteId { get; set; }

    [JsonProperty("startTime")]
    public string StartTime { get; set; }

    [JsonProperty("endTime")]
    public string EndTime { get; set; }
}

public sealed class GameActivityImportBatchResult
{
    public int Inserted { get; set; }
    public int SkippedOverlap { get; set; }
    public int SkippedDuplicateIdempotency { get; set; }
    public int Errors { get; set; }
}

public sealed class GameHltbDataDto
{
    [JsonProperty("mainStory", NullValueHandling = NullValueHandling.Ignore)]
    public int? MainStory { get; set; }

    [JsonProperty("mainExtra", NullValueHandling = NullValueHandling.Ignore)]
    public int? MainExtra { get; set; }

    [JsonProperty("completionist", NullValueHandling = NullValueHandling.Ignore)]
    public int? Completionist { get; set; }
}

partial class PulseAccountClient
{
    private PulseGameDto MapGameToDto(Game game, HltbSyncBatchCounters hltbBatchCounters)
    {
        var releaseDate = game.ReleaseDate;

        var dto = new PulseGameDto
        {
            PlayniteId = game.Id.ToString(),
            Name = game.Name,
            SortingName = game.SortingName,
            GameId = game.GameId,
            PluginId = game.PluginId != Guid.Empty ? game.PluginId.ToString() : null,
            Version = game.Version,

            Description = game.Description,

            Notes = game.Notes,
            Manual = game.Manual,

            Favorite = game.Favorite,
            Hidden = game.Hidden,
            IsInstalled = game.IsInstalled,
            IsRunning = game.IsRunning,
            IsLaunching = game.IsLaunching,
            IsInstalling = game.IsInstalling,
            IsUninstalling = game.IsUninstalling,
            IncludeLibraryPluginAction = game.IncludeLibraryPluginAction,
            IsCustomGame = game.IsCustomGame,

            Added = game.Added,
            Modified = game.Modified,
            LastPlayedAt = game.LastActivity,
            ReleaseDate = releaseDate.HasValue && releaseDate.Value.Year > 0
                ? new ReleaseDateDto
                {
                    Year = releaseDate.Value.Year,
                    Month = releaseDate.Value.Month,
                    Day = releaseDate.Value.Day,
                }
                : null,

            TotalPlaytimeSeconds = game.Playtime,
            TotalPlaytimeMinutes = (int)Math.Round(game.Playtime / 60.0),
            PlayCount = game.PlayCount,

            InstallDirectory = game.InstallDirectory,

            UserScore = game.UserScore,
            CommunityScore = game.CommunityScore,
            CriticScore = game.CriticScore,

            Genres = game.Genres?.Select(g => g.Name).ToList() ?? new List<string>(),
            Tags = game.Tags?.Select(t => t.Name).ToList() ?? new List<string>(),
            Platform = game.Platforms != null && game.Platforms.Any() ? game.Platforms.First().Name : "PC",
            Platforms = game.Platforms?.Select(p => p.Name).ToList() ?? new List<string>(),
            Developers = game.Developers?.Select(d => d.Name).ToList() ?? new List<string>(),
            Publishers = game.Publishers?.Select(p => p.Name).ToList() ?? new List<string>(),
            Categories = game.Categories?.Select(c => c.Name).ToList() ?? new List<string>(),
            Regions = game.Regions?.Select(r => r.Name).ToList() ?? new List<string>(),
            Series = game.Series?.Select(s => s.Name).ToList() ?? new List<string>(),
            AgeRatings = game.AgeRatings?.Select(a => a.Name).ToList() ?? new List<string>(),
            Features = game.Features?.Select(f => f.Name).ToList() ?? new List<string>(),

            Source = game.Source?.Name ?? "Unknown",
            SourceId = game.SourceId != Guid.Empty ? game.SourceId.ToString() : null,
            CompletionStatus = game.CompletionStatus?.Name,
            CompletionStatusId = game.CompletionStatusId != Guid.Empty ? game.CompletionStatusId.ToString() : null,

            GenreIds = game.GenreIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),
            TagIds = game.TagIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),
            PlatformIds = game.PlatformIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),
            DeveloperIds = game.DeveloperIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),
            PublisherIds = game.PublisherIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),
            CategoryIds = game.CategoryIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),
            RegionIds = game.RegionIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),
            SeriesIds = game.SeriesIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),
            AgeRatingIds = game.AgeRatingIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),
            FeatureIds = game.FeatureIds?.Select(id => id.ToString()).ToList() ?? new List<string>(),

            Links = game.Links?.Select(l => new LinkDto { Name = l.Name, Url = l.Url }).ToList() ?? new List<LinkDto>(),
            Roms = game.Roms?.Select(r => new GameRomDto { Name = r.Name, Path = r.Path }).ToList() ?? new List<GameRomDto>(),
            GameActions = game.GameActions?.Select(a => new GameActionDto
            {
                Name = a.Name,
                Path = a.Path,
                Arguments = a.Arguments,
                WorkingDir = a.WorkingDir,
                Type = (int)a.Type,
                IsPlayAction = a.IsPlayAction,
                EmulatorId = a.EmulatorId != Guid.Empty ? a.EmulatorId.ToString() : null
            }).ToList() ?? new List<GameActionDto>(),

            PreScript = game.PreScript,
            PostScript = game.PostScript,
            GameStartedScript = game.GameStartedScript,
            UseGlobalPreScript = game.UseGlobalPreScript,
            UseGlobalPostScript = game.UseGlobalPostScript,
            UseGlobalGameStartedScript = game.UseGlobalGameStartedScript
        };

        var hltbData = HltbExtensionGameDataReader.TryRead(
            _extensionsDataPath,
            game.Id,
            hltbBatchCounters);
        if (hltbData != null)
            dto.HltbData = hltbData;

        AttachPlayniteCoverMetadata(dto, game);

        return dto;
    }

    private class SyncRunDto
    {
        [JsonProperty("runId")]
        public string RunId { get; set; }

        [JsonProperty("batchIndex")]
        public int BatchIndex { get; set; }

        [JsonProperty("batchCount")]
        public int BatchCount { get; set; }

        [JsonProperty("totalGames")]
        public int TotalGames { get; set; }
    }

    private class GamesSyncRequest
    {
        [JsonProperty("games")]
        public List<PulseGameDto> Games { get; set; }

        [JsonProperty("fullLibrarySync")]
        public bool FullLibrarySync { get; set; }

        [JsonProperty("syncRun")]
        public SyncRunDto SyncRun { get; set; }
    }

    private class GamesSyncCompleteRunDto
    {
        [JsonProperty("runId")]
        public string RunId { get; set; }
    }

    private class GamesSyncCompleteRequest
    {
        [JsonProperty("playniteIds")]
        public List<string> PlayniteIds { get; set; }

        [JsonProperty("pruneMissing")]
        public bool PruneMissing { get; set; }

        [JsonProperty("syncRun")]
        public GamesSyncCompleteRunDto SyncRun { get; set; }
    }

    private class ReleaseDateDto
    {
        [JsonProperty("year")]
        public int Year { get; set; }

        [JsonProperty("month")]
        public int? Month { get; set; }

        [JsonProperty("day")]
        public int? Day { get; set; }

        [JsonProperty("date")]
        public DateTime? Date { get; set; }
    }

    private class LinkDto
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; }
    }

    private class GameRomDto
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }
    }

    private class GameActionDto
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("arguments")]
        public string Arguments { get; set; }

        [JsonProperty("workingDir")]
        public string WorkingDir { get; set; }

        [JsonProperty("type")]
        public int Type { get; set; }

        [JsonProperty("isPlayAction")]
        public bool IsPlayAction { get; set; }

        [JsonProperty("emulatorId")]
        public string EmulatorId { get; set; }

        [JsonProperty("emulatorProfileId")]
        public string EmulatorProfileId { get; set; }
    }

    private class PulseGameDto
    {
        [JsonProperty("playniteId")]
        public string PlayniteId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("sortingName")]
        public string SortingName { get; set; }

        [JsonProperty("gameId")]
        public string GameId { get; set; }

        [JsonProperty("pluginId")]
        public string PluginId { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("notes")]
        public string Notes { get; set; }

        [JsonProperty("manual")]
        public string Manual { get; set; }

        [JsonProperty("favorite")]
        public bool Favorite { get; set; }

        [JsonProperty("hidden")]
        public bool Hidden { get; set; }

        [JsonProperty("isInstalled")]
        public bool IsInstalled { get; set; }

        [JsonProperty("isRunning")]
        public bool IsRunning { get; set; }

        [JsonProperty("isLaunching")]
        public bool IsLaunching { get; set; }

        [JsonProperty("isInstalling")]
        public bool IsInstalling { get; set; }

        [JsonProperty("isUninstalling")]
        public bool IsUninstalling { get; set; }

        [JsonProperty("overrideInstallState")]
        public bool OverrideInstallState { get; set; }

        [JsonProperty("includeLibraryPluginAction")]
        public bool IncludeLibraryPluginAction { get; set; }

        [JsonProperty("enableSystemHdr")]
        public bool EnableSystemHdr { get; set; }

        [JsonProperty("isCustomGame")]
        public bool IsCustomGame { get; set; }

        [JsonProperty("added")]
        public DateTime? Added { get; set; }

        [JsonProperty("modified")]
        public DateTime? Modified { get; set; }

        [JsonProperty("lastPlayedAt")]
        public DateTime? LastPlayedAt { get; set; }

        [JsonProperty("releaseDate")]
        public ReleaseDateDto ReleaseDate { get; set; }

        [JsonProperty("lastSizeScanDate")]
        public DateTime? LastSizeScanDate { get; set; }

        [JsonProperty("totalPlaytimeSeconds")]
        public ulong TotalPlaytimeSeconds { get; set; }

        [JsonProperty("totalPlaytimeMinutes")]
        public int TotalPlaytimeMinutes { get; set; }

        [JsonProperty("playCount")]
        public ulong PlayCount { get; set; }

        [JsonProperty("installDirectory")]
        public string InstallDirectory { get; set; }

        [JsonProperty("installSizeBytes")]
        public ulong? InstallSizeBytes { get; set; }

        [JsonProperty("userScore")]
        public int? UserScore { get; set; }

        [JsonProperty("communityScore")]
        public int? CommunityScore { get; set; }

        [JsonProperty("criticScore")]
        public int? CriticScore { get; set; }

        [JsonProperty("genres")]
        public List<string> Genres { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; }

        [JsonProperty("platform")]
        public string Platform { get; set; }

        [JsonProperty("platforms")]
        public List<string> Platforms { get; set; }

        [JsonProperty("developers")]
        public List<string> Developers { get; set; }

        [JsonProperty("publishers")]
        public List<string> Publishers { get; set; }

        [JsonProperty("categories")]
        public List<string> Categories { get; set; }

        [JsonProperty("regions")]
        public List<string> Regions { get; set; }

        [JsonProperty("series")]
        public List<string> Series { get; set; }

        [JsonProperty("ageRatings")]
        public List<string> AgeRatings { get; set; }

        [JsonProperty("features")]
        public List<string> Features { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("sourceId")]
        public string SourceId { get; set; }

        [JsonProperty("completionStatus")]
        public string CompletionStatus { get; set; }

        [JsonProperty("completionStatusId")]
        public string CompletionStatusId { get; set; }

        [JsonProperty("genreIds")]
        public List<string> GenreIds { get; set; }

        [JsonProperty("tagIds")]
        public List<string> TagIds { get; set; }

        [JsonProperty("platformIds")]
        public List<string> PlatformIds { get; set; }

        [JsonProperty("developerIds")]
        public List<string> DeveloperIds { get; set; }

        [JsonProperty("publisherIds")]
        public List<string> PublisherIds { get; set; }

        [JsonProperty("categoryIds")]
        public List<string> CategoryIds { get; set; }

        [JsonProperty("regionIds")]
        public List<string> RegionIds { get; set; }

        [JsonProperty("seriesIds")]
        public List<string> SeriesIds { get; set; }

        [JsonProperty("ageRatingIds")]
        public List<string> AgeRatingIds { get; set; }

        [JsonProperty("featureIds")]
        public List<string> FeatureIds { get; set; }

        [JsonProperty("links")]
        public List<LinkDto> Links { get; set; }

        [JsonProperty("roms")]
        public List<GameRomDto> Roms { get; set; }

        [JsonProperty("gameActions")]
        public List<GameActionDto> GameActions { get; set; }

        [JsonProperty("preScript")]
        public string PreScript { get; set; }

        [JsonProperty("postScript")]
        public string PostScript { get; set; }

        [JsonProperty("gameStartedScript")]
        public string GameStartedScript { get; set; }

        [JsonProperty("useGlobalPreScript")]
        public bool UseGlobalPreScript { get; set; }

        [JsonProperty("useGlobalPostScript")]
        public bool UseGlobalPostScript { get; set; }

        [JsonProperty("useGlobalGameStartedScript")]
        public bool UseGlobalGameStartedScript { get; set; }

        [JsonProperty("hltbData", NullValueHandling = NullValueHandling.Ignore)]
        public GameHltbDataDto HltbData { get; set; }

        [JsonProperty("playniteCover", NullValueHandling = NullValueHandling.Ignore)]
        public PlayniteCoverSyncDto PlayniteCover { get; set; }
    }

    public sealed class PlayniteCoverSyncDto
    {
        [JsonProperty("hash", NullValueHandling = NullValueHandling.Ignore)]
        public string Hash { get; set; }

        [JsonProperty("byteSize", NullValueHandling = NullValueHandling.Ignore)]
        public long? ByteSize { get; set; }

        [JsonProperty("contentType", NullValueHandling = NullValueHandling.Ignore)]
        public string ContentType { get; set; }

        [JsonProperty("sourceKind")]
        public string SourceKind { get; set; }

        [JsonProperty("url", NullValueHandling = NullValueHandling.Ignore)]
        public string Url { get; set; }
    }
}
