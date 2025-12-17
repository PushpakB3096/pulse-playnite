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

namespace Pulse
{
    public class PulseAccountClient
{
    private static readonly ILogger logger = LogManager.GetLogger();

    private const string BASE_URL = "https://pulse-server-m2u1.onrender.com";
    private const string API_KEY = "pulse-api-key";

    // Reuse a single HttpClient. Don't create/dispose per request.
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

    public async Task SyncGamesAsync(IEnumerable<Game> games, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var gameList = games?.ToList() ?? new List<Game>();
        if (gameList.Count == 0)
        {
            logger.Info("Pulse: SyncGamesAsync called with 0 games.");
            return;
        }

        // Build payload
        var payload = new GamesSyncRequest
        {
            Games = gameList.Select(MapGameToDto).ToList()
        };

        var json = JsonConvert.SerializeObject(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var req = new HttpRequestMessage(HttpMethod.Post, gamesSyncEndpoint)
        {
            Content = content
        };
        req.Headers.Add("X-Api-Key", API_KEY);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Pulse: HTTP request to /api/games/sync failed.");
            throw;
        }

        var responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            logger.Error($"Pulse: backend responded with {(int)resp.StatusCode} {resp.StatusCode}: {responseBody}");
            throw new Exception($"Pulse backend error: {resp.StatusCode} - {responseBody}");
        }

        // Optional flow: backend can respond with which covers it needs; upload only those.
        // Expected response example:
        // { "missingCovers": [ { "playniteId": "<guid>", "sha1": "<hash>" }, ... ] }
        try
        {
            var syncResp = JsonConvert.DeserializeObject<GamesSyncResponse>(responseBody);
            if (syncResp?.MissingCovers != null && syncResp.MissingCovers.Count > 0)
            {
                foreach (var miss in syncResp.MissingCovers)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!Guid.TryParse(miss.PlayniteId, out var gameId))
                        continue;

                    var game = gameList.FirstOrDefault(g => g.Id == gameId);
                    if (game == null)
                        continue;

                    var cover = GetCoverInfo(game);
                    if (cover == null)
                        continue;

                    // Safety: only upload if hash matches what server requested
                    if (!string.IsNullOrWhiteSpace(miss.Sha1) &&
                        !string.Equals(miss.Sha1, cover.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    await UploadCoverAsync(gameId, cover, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // Don't fail sync just because response parsing/upload failed
            logger.Warn(ex, "Pulse: failed to parse sync response and/or upload missing covers.");
        }

        logger.Info($"Pulse: successfully synced {gameList.Count} game(s).");
    }

    private PulseGameDto MapGameToDto(Game game)
    {
        var cover = GetCoverInfo(game);

        return new PulseGameDto
        {
            PlayniteId = game.Id.ToString(),
            Name = game.Name ?? "",
            CoverImageDbPath = game.CoverImage, // Playnite DB path (not a URL)

            // Optional cover metadata to help backend decide whether it needs upload
            CoverSha1 = cover?.Sha1,
            CoverFileName = cover?.FileName,
            CoverExtension = cover?.Extension,
            CoverSizeBytes = cover?.SizeBytes,

            Genres = game.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
            Tags = game.Tags?.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
            Platform = game.Platforms?.FirstOrDefault()?.Name ?? "PC",
            Source = game.Source?.Name ?? "Unknown",
            TotalPlaytimeMinutes = (int)Math.Round(game.Playtime / 60.0), // Playnite Playtime is seconds
            LastPlayedAt = game.LastActivity
        };
    }

    private CoverInfo? GetCoverInfo(Game game)
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
            logger.Warn(ex, $"Pulse: failed resolving cover path for {game?.Name}");
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
            Extension = (fi.Extension ?? "").ToLowerInvariant(),
            SizeBytes = fi.Length,
            Sha1 = ComputeSha1Hex(fullPath)
        };
    }

    private static string ComputeSha1Hex(string path)
    {
        using var sha1 = SHA1.Create();
        using var stream = File.OpenRead(path);
        var hash = sha1.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private async Task UploadCoverAsync(Guid gameId, CoverInfo cover, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();

            form.Add(new StringContent(gameId.ToString()), "playniteId");
            form.Add(new StringContent(cover.Sha1), "sha1");

            using var fs = File.OpenRead(cover.FullPath);
            using var fileContent = new StreamContent(fs);

            fileContent.Headers.ContentType = cover.Extension switch
            {
                ".png" => new MediaTypeHeaderValue("image/png"),
                ".webp" => new MediaTypeHeaderValue("image/webp"),
                ".bmp" => new MediaTypeHeaderValue("image/bmp"),
                _ => new MediaTypeHeaderValue("image/jpeg")
            };

            form.Add(fileContent, "file", cover.FileName);

            using var req = new HttpRequestMessage(HttpMethod.Post, coversUploadEndpoint)
            {
                Content = form
            };
            req.Headers.Add("X-Api-Key", API_KEY);

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                logger.Warn($"Pulse: cover upload failed for {gameId} ({(int)resp.StatusCode}) body: {body}");
            }
        }
        catch (Exception ex)
        {
            logger.Warn(ex, $"Pulse: cover upload exception for {gameId}");
        }
    }

    private sealed class GamesSyncRequest
    {
        [JsonProperty("games")]
        public List<PulseGameDto> Games { get; set; } = new();
    }

    private sealed class GamesSyncResponse
    {
        [JsonProperty("missingCovers")]
        public List<MissingCoverDto> MissingCovers { get; set; } = new();
    }

    private sealed class MissingCoverDto
    {
        [JsonProperty("playniteId")]
        public string PlayniteId { get; set; } = "";

        [JsonProperty("sha1")]
        public string Sha1 { get; set; } = "";
    }

    private sealed class PulseGameDto
    {
        [JsonProperty("playniteId")]
        public string PlayniteId { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        // IMPORTANT: this is NOT a URL, it's the Playnite DB path (e.g. "0ef9...\\de45....jpg")
        [JsonProperty("coverImageDbPath")]
        public string CoverImageDbPath { get; set; } = "";

        // Cover metadata (optional but recommended)
        [JsonProperty("coverSha1")]
        public string? CoverSha1 { get; set; }

        [JsonProperty("coverFileName")]
        public string? CoverFileName { get; set; }

        [JsonProperty("coverExtension")]
        public string? CoverExtension { get; set; }

        [JsonProperty("coverSizeBytes")]
        public long? CoverSizeBytes { get; set; }

        [JsonProperty("genres")]
        public List<string> Genres { get; set; } = new();

        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonProperty("platform")]
        public string Platform { get; set; } = "PC";

        [JsonProperty("source")]
        public string Source { get; set; } = "Unknown";

        [JsonProperty("totalPlaytimeMinutes")]
        public int TotalPlaytimeMinutes { get; set; }

        [JsonProperty("lastPlayedAt")]
        public DateTime? LastPlayedAt { get; set; }
    }

    private sealed class CoverInfo
    {
        public string DbPath { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Extension { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha1 { get; set; } = "";
    }
}
}
