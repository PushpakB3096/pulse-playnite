using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

namespace Pulse
{
    /// <summary>
    /// Durable JSON-lines queue for session start/stop POSTs. Lines are removed only after a successful HTTP response.
    /// </summary>
    public sealed class SessionSyncQueue
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly string queuePath;
        private readonly PulseAccountClient client;
        private readonly Func<string> getBearerToken;
        private readonly object fileLock = new object();

        public SessionSyncQueue(string queueFilePath, PulseAccountClient accountClient, Func<string> getBearerToken)
        {
            queuePath = queueFilePath ?? throw new ArgumentNullException(nameof(queueFilePath));
            client = accountClient ?? throw new ArgumentNullException(nameof(accountClient));
            this.getBearerToken = getBearerToken ?? throw new ArgumentNullException(nameof(getBearerToken));
        }

        public void EnqueueStart(string clientSessionId, string playniteId, DateTime startTimeUtc)
        {
            var line = JsonConvert.SerializeObject(new SessionQueueLine
            {
                Kind = "start",
                ClientSessionId = clientSessionId,
                PlayniteId = playniteId,
                StartTime = startTimeUtc.ToUniversalTime().ToString("o")
            });
            AppendLine(line);
        }

        public void EnqueueStop(string clientSessionId, string playniteId, DateTime endTimeUtc)
        {
            var line = JsonConvert.SerializeObject(new SessionQueueLine
            {
                Kind = "stop",
                ClientSessionId = clientSessionId,
                PlayniteId = playniteId,
                EndTime = endTimeUtc.ToUniversalTime().ToString("o")
            });
            AppendLine(line);
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

        /// <summary>
        /// Sends queued items in order. Stops at the first failure; failed and following lines remain in the file.
        /// </summary>
        public void TryDrainAll()
        {
            var bearerToken = getBearerToken != null ? getBearerToken.Invoke() : null;
            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                logger.Info("PlayLog: skip session sync — not linked");
                return;
            }

            lock (fileLock)
            {
                if (!File.Exists(queuePath))
                {
                    return;
                }

                var lines = File.ReadAllLines(queuePath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (lines.Count == 0)
                {
                    return;
                }

                var processedCount = 0;
                for (var i = 0; i < lines.Count; i++)
                {
                    try
                    {
                        ProcessLine(lines[i]).GetAwaiter().GetResult();
                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "PlayLog: session queue line failed; keeping remaining lines for retry.");
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
                    File.WriteAllText(
                        queuePath,
                        string.Join(Environment.NewLine, remaining) + Environment.NewLine);
                }
            }
        }

        private async Task ProcessLine(string line)
        {
            var row = JsonConvert.DeserializeObject<SessionQueueLine>(line);
            if (row == null || string.IsNullOrWhiteSpace(row.Kind))
            {
                logger.Warn("PlayLog: skipping malformed session queue line.");
                return;
            }

            if (row.Kind == "start")
            {
                await client.PostSessionStartAsync(new PulseAccountClient.SessionStartDto
                {
                    ClientSessionId = row.ClientSessionId,
                    PlayniteId = row.PlayniteId,
                    StartTime = row.StartTime
                }).ConfigureAwait(false);
                return;
            }

            if (row.Kind == "stop")
            {
                await client.PostSessionStopAsync(new PulseAccountClient.SessionStopDto
                {
                    ClientSessionId = row.ClientSessionId,
                    PlayniteId = row.PlayniteId,
                    EndTime = row.EndTime
                }).ConfigureAwait(false);
                return;
            }

            logger.Warn("PlayLog: unknown session queue kind: " + row.Kind);
        }

        private sealed class SessionQueueLine
        {
            [JsonProperty("kind")]
            public string Kind { get; set; }

            [JsonProperty("clientSessionId")]
            public string ClientSessionId { get; set; }

            [JsonProperty("playniteId")]
            public string PlayniteId { get; set; }

            [JsonProperty("startTime")]
            public string StartTime { get; set; }

            [JsonProperty("endTime")]
            public string EndTime { get; set; }
        }
    }
}
