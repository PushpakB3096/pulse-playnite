using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Playnite.SDK;

namespace Pulse
{
    /// <summary>
    /// Persists in-memory active PlayLog sessions so stops can be reconciled after Playnite restarts.
    /// </summary>
    public sealed class ActiveSessionStateStore
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly string statePath;
        private readonly object stateLock = new object();

        public ActiveSessionStateStore(string stateFilePath)
        {
            statePath = stateFilePath ?? throw new ArgumentNullException(nameof(stateFilePath));
        }

        public void SaveActiveSessions(
            IReadOnlyDictionary<Guid, string> sessions,
            DateTime? lastShutdownUtc = null)
        {
            var payload = new ActiveSessionStateFile
            {
                LastShutdownUtc = lastShutdownUtc?.ToUniversalTime().ToString("o"),
                Sessions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            if (sessions != null)
            {
                foreach (var entry in sessions)
                {
                    if (entry.Key == Guid.Empty || string.IsNullOrWhiteSpace(entry.Value))
                    {
                        continue;
                    }

                    payload.Sessions[entry.Key.ToString("D")] = entry.Value;
                }
            }

            lock (stateLock)
            {
                try
                {
                    var directory = Path.GetDirectoryName(statePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    if (payload.Sessions.Count == 0 && string.IsNullOrEmpty(payload.LastShutdownUtc))
                    {
                        if (File.Exists(statePath))
                        {
                            File.Delete(statePath);
                        }

                        return;
                    }

                    var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                    File.WriteAllText(statePath, json);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "PlayLog: failed to save active session state.");
                }
            }
        }

        public ActiveSessionReconciliation TakePendingReconciliation()
        {
            lock (stateLock)
            {
                if (!File.Exists(statePath))
                {
                    return ActiveSessionReconciliation.Empty;
                }

                try
                {
                    var json = File.ReadAllText(statePath);
                    var parsed = JsonConvert.DeserializeObject<ActiveSessionStateFile>(json);
                    File.Delete(statePath);

                    if (parsed?.Sessions == null || parsed.Sessions.Count == 0)
                    {
                        return ActiveSessionReconciliation.Empty;
                    }

                    DateTime? shutdownUtc = null;
                    if (!string.IsNullOrWhiteSpace(parsed.LastShutdownUtc))
                    {
                        if (DateTime.TryParse(
                                parsed.LastShutdownUtc,
                                null,
                                System.Globalization.DateTimeStyles.RoundtripKind,
                                out var parsedShutdown))
                        {
                            shutdownUtc = parsedShutdown.ToUniversalTime();
                        }
                    }

                    var sessions = new List<ActiveSessionReconciliationEntry>();
                    foreach (var entry in parsed.Sessions)
                    {
                        if (!Guid.TryParse(entry.Key, out var gameId))
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(entry.Value))
                        {
                            continue;
                        }

                        sessions.Add(new ActiveSessionReconciliationEntry
                        {
                            GameId = gameId,
                            ClientSessionId = entry.Value
                        });
                    }

                    return new ActiveSessionReconciliation
                    {
                        LastShutdownUtc = shutdownUtc,
                        Sessions = sessions
                    };
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "PlayLog: could not load active session state; starting fresh.");
                    try
                    {
                        File.Delete(statePath);
                    }
                    catch
                    {
                        // ignore cleanup failure
                    }

                    return ActiveSessionReconciliation.Empty;
                }
            }
        }

        private sealed class ActiveSessionStateFile
        {
            [JsonProperty("lastShutdownUtc")]
            public string LastShutdownUtc { get; set; }

            [JsonProperty("sessions")]
            public Dictionary<string, string> Sessions { get; set; }
        }
    }

    public sealed class ActiveSessionReconciliation
    {
        public static readonly ActiveSessionReconciliation Empty = new ActiveSessionReconciliation
        {
            Sessions = new List<ActiveSessionReconciliationEntry>()
        };

        public DateTime? LastShutdownUtc { get; set; }

        public IList<ActiveSessionReconciliationEntry> Sessions { get; set; }
    }

    public sealed class ActiveSessionReconciliationEntry
    {
        public Guid GameId { get; set; }

        public string ClientSessionId { get; set; }
    }
}
