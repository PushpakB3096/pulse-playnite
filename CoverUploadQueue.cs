using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

namespace Pulse
{
    public sealed class CoverUploadQueue
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly string queuePath;
        private readonly PulseAccountClient client;
        private readonly Func<string> getBearerToken;
        private readonly object fileLock = new object();
        private volatile bool isDraining;

        public CoverUploadQueue(string queueFilePath, PulseAccountClient accountClient, Func<string> getBearerToken)
        {
            queuePath = queueFilePath ?? throw new ArgumentNullException(nameof(queueFilePath));
            client = accountClient ?? throw new ArgumentNullException(nameof(accountClient));
            this.getBearerToken = getBearerToken ?? throw new ArgumentNullException(nameof(getBearerToken));
        }

        public bool IsDraining => isDraining;

        public int PendingCount
        {
            get
            {
                lock (fileLock)
                {
                    if (!File.Exists(queuePath))
                    {
                        return 0;
                    }

                    return File.ReadAllLines(queuePath)
                        .Count(line => !string.IsNullOrWhiteSpace(line));
                }
            }
        }

        public void Enqueue(PlayniteCoverMetadata metadata, string playniteId)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(playniteId))
            {
                return;
            }

            if (!string.Equals(metadata.SourceKind, "file", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(metadata.FilePath) || string.IsNullOrWhiteSpace(metadata.Hash))
            {
                return;
            }

            var line = JsonConvert.SerializeObject(new CoverUploadQueueLine
            {
                PlayniteId = playniteId,
                CoverPath = metadata.FilePath,
                Hash = metadata.Hash,
                ContentType = metadata.ContentType
            });
            AppendLine(line);
        }

        public void EnqueueMany(IEnumerable<KeyValuePair<string, PlayniteCoverMetadata>> items)
        {
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                Enqueue(item.Value, item.Key);
            }
        }

        private void AppendLine(string line)
        {
            lock (fileLock)
            {
                var dir = Path.GetDirectoryName(queuePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(queuePath, line + Environment.NewLine);
            }
        }

        public void TryDrainAll()
        {
            var bearerToken = getBearerToken != null ? getBearerToken.Invoke() : null;
            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                logger.Info("PlayLog: skip cover upload queue — not linked");
                return;
            }

            isDraining = true;
            try
            {
                lock (fileLock)
                {
                    if (!File.Exists(queuePath))
                    {
                        return;
                    }

                    var lines = File.ReadAllLines(queuePath)
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .ToList();

                    if (lines.Count == 0)
                    {
                        return;
                    }

                    var processedCount = 0;
                    for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                    {
                        try
                        {
                            ProcessLine(lines[lineIndex]).GetAwaiter().GetResult();
                            processedCount++;
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "PlayLog: cover upload queue line failed; keeping remaining lines for retry.");
                            break;
                        }
                    }

                    if (processedCount == 0)
                    {
                        return;
                    }

                    var remaining = lines.Skip(processedCount).ToList();
                    if (remaining.Count == 0)
                    {
                        File.Delete(queuePath);
                    }
                    else
                    {
                        File.WriteAllLines(queuePath, remaining);
                    }
                }
            }
            finally
            {
                isDraining = false;
            }
        }

        private async Task ProcessLine(string line)
        {
            var payload = JsonConvert.DeserializeObject<CoverUploadQueueLine>(line);
            if (payload == null
                || string.IsNullOrWhiteSpace(payload.PlayniteId)
                || string.IsNullOrWhiteSpace(payload.CoverPath)
                || string.IsNullOrWhiteSpace(payload.Hash))
            {
                return;
            }

            if (!File.Exists(payload.CoverPath))
            {
                logger.Warn("PlayLog: cover upload skipped — file missing for playniteId=" + payload.PlayniteId);
                return;
            }

            var bytes = File.ReadAllBytes(payload.CoverPath);
            await client.UploadPlayniteCoverAsync(
                payload.PlayniteId,
                payload.Hash,
                bytes,
                payload.ContentType).ConfigureAwait(false);
        }

        private sealed class CoverUploadQueueLine
        {
            [JsonProperty("playniteId")]
            public string PlayniteId { get; set; }

            [JsonProperty("coverPath")]
            public string CoverPath { get; set; }

            [JsonProperty("hash")]
            public string Hash { get; set; }

            [JsonProperty("contentType")]
            public string ContentType { get; set; }
        }
    }
}
