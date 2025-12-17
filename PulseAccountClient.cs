using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

    private readonly IPlayniteAPI api;
    private readonly string gamesSyncEndpoint;
    private readonly string coversUploadEndpoint;

    public PulseAccountClient(IPlayniteAPI api)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));

        var baseUrlClean = BASE_URL.TrimEnd('/');
        gamesSyncEndpoint = baseUrlClean + "/api/games/sync";
        coversUploadEndpoint = baseUrlClean + "/api/assets/covers";
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

        // Optional: upload missing covers if backend asks
        try
        {
            var syncResponse = JsonConvert.DeserializeObject<GamesSyncResponse>(responseBody);
            if (syncResponse != null && syncResponse.MissingCovers != null)
            {
                foreach (var missing in syncResponse.MissingCovers)
                {
                    Guid gameId;
                    if (!Guid.TryParse(missing.PlayniteId, out gameId))
                        continue;

                    var game = gameList.FirstOrDefault(g => g.Id == gameId);
                    if (game == null)
                        continue;

                    var cover = GetCoverInfo(game);
                    if (cover == null)
                        continue;

                    if (!string.IsNullOrEmpty(missing.Sha1) &&
                        !string.Equals(missing.Sha1, cover.Sha1, StringComparison.OrdinalIgnoreCase))
                        continue;

                    await UploadCoverAsync(gameId, cover).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Pulse: failed processing missing covers.");
        }

        logger.Info("Pulse: successfully synced " + gameList.Count + " game(s).");
    }

    private PulseGameDto MapGameToDto(Game game)
    {
        var cover = GetCoverInfo(game);

        return new PulseGameDto
        {
            PlayniteId = game.Id.ToString(),
            Name = game.Name,
            CoverImageDbPath = game.CoverImage,

            CoverSha1 = cover != null ? cover.Sha1 : null,
            CoverFileName = cover != null ? cover.FileName : null,
            CoverExtension = cover != null ? cover.Extension : null,
            CoverSizeBytes = cover != null ? (long?)cover.SizeBytes : null,

            Genres = game.Genres != null ? game.Genres.Select(g => g.Name).ToList() : new List<string>(),
            Tags = game.Tags != null ? game.Tags.Select(t => t.Name).ToList() : new List<string>(),
            Platform = game.Platforms != null && game.Platforms.Any() ? game.Platforms.First().Name : "PC",
            Source = game.Source != null ? game.Source.Name : "Unknown",
            TotalPlaytimeMinutes = (int)Math.Round(game.Playtime / 60.0),
            LastPlayedAt = game.LastActivity
        };
    }

    private CoverInfo GetCoverInfo(Game game)
    {
        if (game == null || string.IsNullOrWhiteSpace(game.CoverImage))
            return null;

        string fullPath;
        try
        {
            fullPath = api.Database.GetFullFilePath(game.CoverImage);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Pulse: failed resolving cover path for " + game.Name);
            return null;
        }

        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return null;

        var fi = new FileInfo(fullPath);

        return new CoverInfo
        {
            DbPath = game.CoverImage,
            FullPath = fullPath,
            FileName = fi.Name,
            Extension = fi.Extension != null ? fi.Extension.ToLowerInvariant() : "",
            SizeBytes = fi.Length,
            Sha1 = ComputeSha1Hex(fullPath)
        };
    }

    private static string ComputeSha1Hex(string path)
    {
        using (var sha1 = SHA1.Create())
        using (var stream = File.OpenRead(path))
        {
            var hash = sha1.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    private async Task UploadCoverAsync(Guid gameId, CoverInfo cover)
    {
        try
        {
            using (var form = new MultipartFormDataContent())
            {
                form.Add(new StringContent(gameId.ToString()), "playniteId");
                form.Add(new StringContent(cover.Sha1), "sha1");

                using (var fs = File.OpenRead(cover.FullPath))
                {
                    var fileContent = new StreamContent(fs);

                    if (cover.Extension == ".png")
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    else if (cover.Extension == ".webp")
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
                    else if (cover.Extension == ".bmp")
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/bmp");
                    else
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

                    form.Add(fileContent, "file", cover.FileName);

                    var req = new HttpRequestMessage(HttpMethod.Post, coversUploadEndpoint);
                    req.Content = form;
                    req.Headers.Add("X-Api-Key", API_KEY);

                    var resp = await http.SendAsync(req).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        logger.Warn("Pulse: cover upload failed for " + gameId + " - " + body);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Pulse: cover upload exception for " + gameId);
        }
    }

    private class GamesSyncRequest
    {
        [JsonProperty("games")]
        public List<PulseGameDto> Games { get; set; }
    }

    private class GamesSyncResponse
    {
        [JsonProperty("missingCovers")]
        public List<MissingCoverDto> MissingCovers { get; set; }
    }

    private class MissingCoverDto
    {
        [JsonProperty("playniteId")]
        public string PlayniteId { get; set; }

        [JsonProperty("sha1")]
        public string Sha1 { get; set; }
    }

    private class PulseGameDto
    {
        [JsonProperty("playniteId")]
        public string PlayniteId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("coverImageDbPath")]
        public string CoverImageDbPath { get; set; }

        [JsonProperty("coverSha1")]
        public string CoverSha1 { get; set; }

        [JsonProperty("coverFileName")]
        public string CoverFileName { get; set; }

        [JsonProperty("coverExtension")]
        public string CoverExtension { get; set; }

        [JsonProperty("coverSizeBytes")]
        public long? CoverSizeBytes { get; set; }

        [JsonProperty("genres")]
        public List<string> Genres { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; }

        [JsonProperty("platform")]
        public string Platform { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("totalPlaytimeMinutes")]
        public int TotalPlaytimeMinutes { get; set; }

        [JsonProperty("lastPlayedAt")]
        public DateTime? LastPlayedAt { get; set; }
    }

    private class CoverInfo
    {
        public string DbPath { get; set; }
        public string FullPath { get; set; }
        public string FileName { get; set; }
        public string Extension { get; set; }
        public long SizeBytes { get; set; }
        public string Sha1 { get; set; }
    }
}
