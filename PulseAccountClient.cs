using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;

public class PulseAccountClient
{
    private static readonly ILogger logger = LogManager.GetLogger();

    private const string BASE_URL = "https://pulse-server-m2u1.onrender.com";
    private const string API_KEY = "pulse-api-key";

    private static readonly HttpClient http = new HttpClient();

    private readonly string gamesSyncEndpoint;

    public PulseAccountClient(IPlayniteAPI api)
    {
        if (api == null)
            throw new ArgumentNullException(nameof(api));

        var baseUrlClean = BASE_URL.TrimEnd('/');
        gamesSyncEndpoint = baseUrlClean + "/api/games/sync";
    }

    public async Task SyncGamesAsync(IEnumerable<Game> games)
    {
        var gameList = games != null ? games.ToList() : new List<Game>();
        if (gameList.Count == 0)
        {
            logger.Info("Pulse: SyncGamesAsync called with 0 games.");
            return;
        }

        var payload = new GamesSyncRequest
        {
            Games = gameList.Select(MapGameToDto).ToList()
        };

        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var req = new HttpRequestMessage(HttpMethod.Post, gamesSyncEndpoint);
        req.Content = content;
        req.Headers.Add("X-Api-Key", API_KEY);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Pulse: HTTP request to games/sync failed.");
            throw;
        }

        var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            logger.Error("Pulse: backend responded with " + resp.StatusCode + ": " + responseBody);
            throw new Exception("Pulse backend error: " + resp.StatusCode);
        }

        logger.Info("Pulse: successfully synced " + gameList.Count + " game(s).");
    }

    private PulseGameDto MapGameToDto(Game game)
    {
        var rd = game.ReleaseDate;

        return new PulseGameDto
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
            ReleaseDate = rd.HasValue && rd.Value.Year > 0
                ? new ReleaseDateDto
                {
                    Year = rd.Value.Year,
                    Month = rd.Value.Month,
                    Day = rd.Value.Day,
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
    }

    private class GamesSyncRequest
    {
        [JsonProperty("games")]
        public List<PulseGameDto> Games { get; set; }
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
    }
}
