using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

namespace Pulse
{
    internal sealed class GameActivitySessionImporter
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private static readonly Guid GameActivityPluginId =
            Guid.Parse("afbb1a0d-04a1-4d0c-9afa-c6e42ca855b4");

        private const int ImportBatchSize = 100;

        private readonly IPlayniteAPI api;
        private readonly PulseAccountClient client;
        private readonly Func<string> getBearerToken;

        public GameActivitySessionImporter(
            IPlayniteAPI api,
            PulseAccountClient client,
            Func<string> getBearerToken)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.getBearerToken = getBearerToken ?? throw new ArgumentNullException(nameof(getBearerToken));
        }

        public Task RunAsync()
        {
            return Task.Run(async () =>
            {
                try
                {
                    await RunImportAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "PlayLog: Game Activity historic import failed.");
                }
            });
        }

        private async Task RunImportAsync()
        {
            var token = getBearerToken?.Invoke();
            if (string.IsNullOrWhiteSpace(token))
            {
                logger.Info("PlayLog: skip GA import — not linked");
                return;
            }

            var gaRoot = Path.Combine(
                api.Paths.ExtensionsDataPath,
                GameActivityPluginId.ToString("D"),
                "GameActivity");

            if (!Directory.Exists(gaRoot))
            {
                logger.Info("PlayLog: Game Activity data folder not found at " + gaRoot);
                return;
            }

            string[] jsonFiles;
            try
            {
                jsonFiles = Directory.GetFiles(gaRoot, "*.json");
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "PlayLog: failed to enumerate Game Activity JSON files.");
                return;
            }

            if (jsonFiles.Length == 0)
            {
                logger.Info("PlayLog: Game Activity folder has no JSON files.");
                return;
            }

            var totalInserted = 0;
            var totalSkippedOverlap = 0;
            var totalSkippedDup = 0;
            var totalErrors = 0;
            var pending = new List<GameActivityImportRowDto>(ImportBatchSize);

            async Task FlushPendingAsync()
            {
                if (pending.Count == 0)
                {
                    return;
                }

                var dto = new GameActivityImportBatchDto { Sessions = pending.ToList() };
                pending.Clear();

                var batchResult = await client.PostGameActivityImportBatchAsync(dto).ConfigureAwait(false);
                if (batchResult == null)
                {
                    return;
                }

                totalInserted += batchResult.Inserted;
                totalSkippedOverlap += batchResult.SkippedOverlap;
                totalSkippedDup += batchResult.SkippedDuplicateIdempotency;
                totalErrors += batchResult.Errors;
            }

            foreach (var path in jsonFiles)
            {
                var playniteId = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(playniteId))
                {
                    continue;
                }

                Guid fileGameGuid;
                if (!Guid.TryParse(playniteId, out fileGameGuid))
                {
                    logger.Warn("PlayLog: skip GA file with non-GUID name: " + path);
                    continue;
                }

                GaGameActivitiesFileDto fileDto;
                try
                {
                    var text = File.ReadAllText(path);
                    fileDto = JsonConvert.DeserializeObject<GaGameActivitiesFileDto>(text);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "PlayLog: failed to deserialize GA file: " + path);
                    continue;
                }

                if (fileDto == null)
                {
                    logger.Warn("PlayLog: GA file deserialized to null: " + path);
                    continue;
                }

                if (fileDto.Id.HasValue && fileDto.Id.Value != fileGameGuid)
                {
                    logger.Warn(
                        "PlayLog: GA file Id mismatch with filename; skipping: " + path);
                    continue;
                }

                var items = fileDto.Items;
                if (items == null || items.Count == 0)
                {
                    continue;
                }

                var sorted = items
                    .Where(a => a != null && a.DateSession.HasValue && a.ElapsedSeconds.HasValue)
                    .OrderBy(a => a.DateSession.GetValueOrDefault())
                    .ToList();

                for (var ordinal = 0; ordinal < sorted.Count; ordinal++)
                {
                    var act = sorted[ordinal];
                    var startRaw = act.DateSession.GetValueOrDefault();
                    var elapsed = act.ElapsedSeconds.GetValueOrDefault();

                    var startUtc = ToUtc(startRaw);
                    DateTime endUtc;
                    try
                    {
                        var maxSec = (ulong)Math.Floor(TimeSpan.MaxValue.TotalSeconds);
                        var sec = elapsed > maxSec ? maxSec : elapsed;
                        endUtc = startUtc.AddSeconds((double)sec);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "PlayLog: skip GA row with invalid duration in " + path);
                        continue;
                    }

                    if (endUtc < startUtc)
                    {
                        continue;
                    }

                    var sessionGuid = DeterministicSessionGuid.FromGaActivity(
                        playniteId,
                        startUtc,
                        endUtc,
                        ordinal);

                    pending.Add(new GameActivityImportRowDto
                    {
                        ClientSessionId = sessionGuid.ToString("D"),
                        PlayniteId = playniteId,
                        StartTime = startUtc.ToString("o", CultureInfo.InvariantCulture),
                        EndTime = endUtc.ToString("o", CultureInfo.InvariantCulture)
                    });

                    if (pending.Count >= ImportBatchSize)
                    {
                        await FlushPendingAsync().ConfigureAwait(false);
                    }
                }
            }

            await FlushPendingAsync().ConfigureAwait(false);

            logger.Info(string.Format(
                CultureInfo.InvariantCulture,
                "PlayLog: GA import finished inserted={0} skipped_overlap={1} skipped_duplicate={2} errors={3}",
                totalInserted,
                totalSkippedOverlap,
                totalSkippedDup,
                totalErrors));
        }

        private static DateTime ToUtc(DateTime value)
        {
            switch (value.Kind)
            {
                case DateTimeKind.Utc:
                    return value;
                case DateTimeKind.Local:
                    return value.ToUniversalTime();
                default:
                    return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
            }
        }
    }
}
