using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Pulse
{
    public class PulseAccountClient
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // TODO: change this to your actual deployed backend URL
        private const string BASE_URL = "https://pulse-server-m2u1.onrender.com";
        // TODO: set this to your real API key (same as in your Node .env)
        private const string API_KEY = "pulse-api-key";

        private readonly IPlayniteAPI api;
        private readonly string gamesSyncEndpoint;

        public PulseAccountClient(IPlayniteAPI api)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));

            // Ensure no trailing slash
            var baseUrlClean = BASE_URL.TrimEnd('/');
            gamesSyncEndpoint = baseUrlClean + "/api/games/sync";
        }

        public async Task SyncGamesAsync(IEnumerable<Game> games)
        {
            var gameList = games?.ToList() ?? new List<Game>();
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

            using (var httpClient = new HttpClient())
            {
                // Simple API key auth. Match this with your Node middleware.
                httpClient.DefaultRequestHeaders.Add("X-Api-Key", API_KEY);

                HttpResponseMessage resp;
                try
                {
                    resp = await httpClient.PostAsync(gamesSyncEndpoint, content).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Pulse: HTTP request to games/sync failed.");
                    throw;
                }

                var responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    logger.Error($"Pulse: backend responded with {resp.StatusCode}: {responseBody}");
                    throw new Exception($"Pulse backend error: {resp.StatusCode} - {responseBody}");
                }

                logger.Info($"Pulse: successfully synced {gameList.Count} game(s).");
            }
        }

        private PulseGameDto MapGameToDto(Game game)
        {
            return new PulseGameDto
            {
                PlayniteId = game.Id.ToString(),
                Name = game.Name,
                CoverImageUrl = game.CoverImage, // this is the Playnite image key, not a real URL; you can refine later
                Genres = game.Genres?.Select(g => g.Name).ToList() ?? new List<string>(),
                Tags = game.Tags?.Select(t => t.Name).ToList() ?? new List<string>(),
                Platform = game.Platforms?.FirstOrDefault()?.Name ?? "PC",
                Source = game.Source?.Name ?? "Unknown",
                TotalPlaytimeMinutes = (int)(game.Playtime / 60.0),       // Playnite's Playtime is in seconds
                LastPlayedAt = game.LastActivity
            };
        }

        private class GamesSyncRequest
        {
            [JsonProperty("games")]
            public List<PulseGameDto> Games { get; set; }
        }

        private class PulseGameDto
        {
            [JsonProperty("playniteId")]
            public string PlayniteId { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("coverImageUrl")]
            public string CoverImageUrl { get; set; }

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
    }
}
