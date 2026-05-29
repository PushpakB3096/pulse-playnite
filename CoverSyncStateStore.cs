using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Playnite.SDK;

namespace Pulse
{
    public sealed class CoverSyncStateStore
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly string statePath;
        private readonly object stateLock = new object();
        private Dictionary<string, CoverSyncStateEntry> entries = new Dictionary<string, CoverSyncStateEntry>(StringComparer.OrdinalIgnoreCase);
        private bool isDirty;

        public CoverSyncStateStore(string stateFilePath)
        {
            statePath = stateFilePath ?? throw new ArgumentNullException(nameof(stateFilePath));
            Load();
        }

        public PlayniteCoverMetadata TryGetSyncMetadata(
            string playniteId,
            string coverRef,
            string fullPath)
        {
            if (string.IsNullOrWhiteSpace(playniteId)
                || string.IsNullOrWhiteSpace(coverRef)
                || string.IsNullOrWhiteSpace(fullPath))
            {
                return null;
            }

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(fullPath);
            }
            catch
            {
                return null;
            }

            if (!fileInfo.Exists)
            {
                return null;
            }

            lock (stateLock)
            {
                CoverSyncStateEntry cachedEntry;
                if (entries.TryGetValue(playniteId, out cachedEntry)
                    && cachedEntry != null
                    && string.Equals(cachedEntry.CoverRef, coverRef, StringComparison.Ordinal)
                    && cachedEntry.ByteSize == fileInfo.Length
                    && cachedEntry.LastWriteUtcTicks == fileInfo.LastWriteTimeUtc.Ticks
                    && !string.IsNullOrWhiteSpace(cachedEntry.Hash))
                {
                    return ToFileMetadata(cachedEntry, fullPath);
                }
            }

            return null;
        }

        public PlayniteCoverMetadata ReadHashAndCache(
            string playniteId,
            string coverRef,
            string fullPath,
            byte[] fileBytes,
            string hash,
            string contentType)
        {
            if (string.IsNullOrWhiteSpace(playniteId)
                || string.IsNullOrWhiteSpace(coverRef)
                || string.IsNullOrWhiteSpace(fullPath)
                || fileBytes == null
                || string.IsNullOrWhiteSpace(hash))
            {
                return null;
            }

            long lastWriteUtcTicks = 0;
            try
            {
                lastWriteUtcTicks = new FileInfo(fullPath).LastWriteTimeUtc.Ticks;
            }
            catch
            {
                // Keep 0 when file metadata cannot be read; cache still stores hash for this sync.
            }

            var metadata = new PlayniteCoverMetadata
            {
                SourceKind = "file",
                Hash = hash,
                ByteSize = fileBytes.LongLength,
                ContentType = contentType,
                FilePath = fullPath
            };

            lock (stateLock)
            {
                entries[playniteId] = new CoverSyncStateEntry
                {
                    CoverRef = coverRef,
                    Hash = hash,
                    ByteSize = fileBytes.LongLength,
                    ContentType = contentType,
                    FilePath = fullPath,
                    LastWriteUtcTicks = lastWriteUtcTicks
                };
                isDirty = true;
            }

            return metadata;
        }

        public PlayniteCoverMetadata GetUploadMetadata(string playniteId)
        {
            if (string.IsNullOrWhiteSpace(playniteId))
            {
                return null;
            }

            lock (stateLock)
            {
                CoverSyncStateEntry cachedEntry;
                if (!entries.TryGetValue(playniteId, out cachedEntry) || cachedEntry == null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(cachedEntry.FilePath)
                    || string.IsNullOrWhiteSpace(cachedEntry.Hash))
                {
                    return null;
                }

                return ToFileMetadata(cachedEntry, cachedEntry.FilePath);
            }
        }

        public void SaveIfDirty()
        {
            Dictionary<string, CoverSyncStateEntry> snapshot;
            lock (stateLock)
            {
                if (!isDirty)
                {
                    return;
                }

                snapshot = new Dictionary<string, CoverSyncStateEntry>(entries, StringComparer.OrdinalIgnoreCase);
                isDirty = false;
            }

            try
            {
                var directory = Path.GetDirectoryName(statePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                File.WriteAllText(statePath, json);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: failed to save cover sync state.");
                lock (stateLock)
                {
                    isDirty = true;
                }
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(statePath))
                {
                    return;
                }

                var json = File.ReadAllText(statePath);
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, CoverSyncStateEntry>>(json);
                if (parsed != null)
                {
                    entries = new Dictionary<string, CoverSyncStateEntry>(parsed, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "PlayLog: could not load cover sync state; starting fresh.");
                entries = new Dictionary<string, CoverSyncStateEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static PlayniteCoverMetadata ToFileMetadata(CoverSyncStateEntry entry, string fullPath)
        {
            return new PlayniteCoverMetadata
            {
                SourceKind = "file",
                Hash = entry.Hash,
                ByteSize = entry.ByteSize,
                ContentType = entry.ContentType,
                FilePath = fullPath
            };
        }

        private sealed class CoverSyncStateEntry
        {
            [JsonProperty("coverRef")]
            public string CoverRef { get; set; }

            [JsonProperty("hash")]
            public string Hash { get; set; }

            [JsonProperty("byteSize")]
            public long ByteSize { get; set; }

            [JsonProperty("contentType")]
            public string ContentType { get; set; }

            [JsonProperty("filePath")]
            public string FilePath { get; set; }

            [JsonProperty("lastWriteUtcTicks")]
            public long LastWriteUtcTicks { get; set; }
        }
    }
}
