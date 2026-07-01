using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

internal sealed class AchievementSyncBatchCounters
{
    public string ActiveSourceKey;
    public int AttachedToPayload;
    public int SkippedNoExtensionsDataPath;
    public int SkippedNoSourceInstalled;
    public int SkippedFileNotFound;
    public int SkippedJsonItemsEmpty;
    public int SkippedPathCombineError;
    public int SkippedFileReadError;
    public int SkippedJsonParseError;
    public int SkippedDbReadError;
    public int SkippedDbNoGame;
    public bool LoggedPaDbPath;
    public string SampleMissingFilePath;

    public void LogBatchSummary(ILogger log, int gamesInPayload, string extensionsDataPath)
    {
        if (gamesInPayload <= 0)
            return;

        var pathPresent = !string.IsNullOrEmpty(extensionsDataPath);
        if (!pathPresent && SkippedNoExtensionsDataPath == gamesInPayload)
        {
            log.Warn(
                "PlayLog achievements: Playnite ExtensionsDataPath is empty; achievementImport omitted for every game.");
        }

        log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "PlayLog achievements: sync batch — gamesInPayload={0}, attached={1}, source={2}, "
            + "skipped_noExtensionsDataPath={3}, skipped_noSourceInstalled={4}, skipped_fileNotFound={5}, "
            + "skipped_jsonItemsEmpty={6}, skipped_pathCombineError={7}, skipped_fileReadError={8}, "
            + "skipped_jsonParseError={9}, skipped_dbReadError={10}, skipped_dbNoGame={11}. "
            + "extensionsDataPathPresent={12}. sampleMissingFile={13}",
            gamesInPayload,
            AttachedToPayload,
            ActiveSourceKey ?? "(none)",
            SkippedNoExtensionsDataPath,
            SkippedNoSourceInstalled,
            SkippedFileNotFound,
            SkippedJsonItemsEmpty,
            SkippedPathCombineError,
            SkippedFileReadError,
            SkippedJsonParseError,
            SkippedDbReadError,
            SkippedDbNoGame,
            pathPresent,
            SampleMissingFilePath ?? "(n/a)"));

        if (AttachedToPayload == 0
            && pathPresent
            && SkippedNoSourceInstalled == 0
            && SkippedFileNotFound > 0
            && SkippedJsonParseError == 0
            && SkippedFileReadError == 0
            && ActiveSourceKey == AchievementExtensionPaths.ImportSourceSuccessStory)
        {
            log.Info(
                "PlayLog achievements: hint — SuccessStory files are expected under ExtensionsDataPath\\"
                + AchievementExtensionPaths.SuccessStoryPluginFolderId
                + "\\" + AchievementExtensionPaths.SuccessStoryGameDataSubfolder
                + "\\{playniteGameId}.json");
        }
    }
}

internal static class AchievementExtensionGameDataReader
{
    private static readonly ILogger logger = LogManager.GetLogger();

    /// <summary>
    /// Returns true when the given source key's plugin data can be found on disk.
    /// Used by the settings UI to warn the user when their selected source is not installed.
    /// </summary>
    public static bool IsSourceInstalled(string extensionsDataPath, string sourceKey)
    {
        if (string.IsNullOrEmpty(extensionsDataPath) || string.IsNullOrEmpty(sourceKey))
            return false;

        if (sourceKey == AchievementExtensionPaths.ImportSourceSuccessStory)
            return IsSuccessStoryInstalled(extensionsDataPath);

        if (sourceKey == AchievementExtensionPaths.ImportSourcePlayniteAchievements)
            return IsPlayniteAchievementsInstalled(extensionsDataPath);

        return false;
    }

    /// <summary>
    /// Reads achievement import data for a single game using the configured source preference.
    /// Returns null when the source is not installed, the game has no data, or a read error occurs.
    /// </summary>
    public static AchievementImportDto TryRead(
        string extensionsDataPath,
        Guid playniteGameId,
        string sourcePreference,
        AchievementSyncBatchCounters batchCounters)
    {
        if (batchCounters == null)
            throw new ArgumentNullException(nameof(batchCounters));

        if (string.IsNullOrEmpty(extensionsDataPath))
        {
            batchCounters.SkippedNoExtensionsDataPath++;
            return null;
        }

        var source = string.IsNullOrEmpty(sourcePreference)
            ? AchievementExtensionPaths.ImportSourcePlayniteAchievements
            : sourcePreference;

        if (string.IsNullOrEmpty(batchCounters.ActiveSourceKey))
            batchCounters.ActiveSourceKey = source;

        if (source == AchievementExtensionPaths.ImportSourceSuccessStory)
            return TryReadFromSuccessStory(extensionsDataPath, playniteGameId, batchCounters);

        if (source == AchievementExtensionPaths.ImportSourcePlayniteAchievements)
            return TryReadFromPlayniteAchievements(extensionsDataPath, playniteGameId, batchCounters);

        batchCounters.SkippedNoSourceInstalled++;
        return null;
    }

    private static AchievementImportDto TryReadFromSuccessStory(
        string extensionsDataPath,
        Guid playniteGameId,
        AchievementSyncBatchCounters batchCounters)
    {
        if (!IsSuccessStoryInstalled(extensionsDataPath))
        {
            batchCounters.SkippedNoSourceInstalled++;
            return null;
        }

        string filePath;
        try
        {
            filePath = AchievementExtensionPaths.GetSuccessStoryGameFilePath(extensionsDataPath, playniteGameId);
        }
        catch (Exception ex)
        {
            batchCounters.SkippedPathCombineError++;
            logger.Warn(ex, "PlayLog achievements: failed to build SuccessStory path for game " + playniteGameId);
            return null;
        }

        if (!File.Exists(filePath))
        {
            batchCounters.SkippedFileNotFound++;
            if (batchCounters.SampleMissingFilePath == null)
                batchCounters.SampleMissingFilePath = filePath;
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            batchCounters.SkippedFileReadError++;
            logger.Warn(ex, "PlayLog achievements: failed to read SuccessStory file: " + filePath);
            return null;
        }

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (Exception ex)
        {
            batchCounters.SkippedJsonParseError++;
            logger.Warn(ex, "PlayLog achievements: failed to parse SuccessStory JSON: " + filePath);
            return null;
        }

        var itemsToken = root["Items"];
        if (itemsToken == null || itemsToken.Type != JTokenType.Array || !itemsToken.HasValues)
        {
            batchCounters.SkippedJsonItemsEmpty++;
            return null;
        }

        var trimmedRaw = new JObject
        {
            ["Items"] = itemsToken.DeepClone()
        };

        var dateLastRefresh = root["DateLastRefresh"];
        if (dateLastRefresh != null && dateLastRefresh.Type != JTokenType.Null)
            trimmedRaw["DateLastRefresh"] = dateLastRefresh.DeepClone();

        batchCounters.AttachedToPayload++;
        return new AchievementImportDto
        {
            Source = AchievementExtensionPaths.ImportSourceSuccessStory,
            Raw = trimmedRaw
        };
    }

    private static AchievementImportDto TryReadFromPlayniteAchievements(
        string extensionsDataPath,
        Guid playniteGameId,
        AchievementSyncBatchCounters batchCounters)
    {
        var dbPath = AchievementExtensionPaths.GetPlayniteAchievementsCacheDbPath(extensionsDataPath);

        if (!batchCounters.LoggedPaDbPath)
        {
            batchCounters.LoggedPaDbPath = true;
            logger.Info("PlayLog achievements [PA]: checking DB at path=\"" + dbPath
                + "\" exists=" + File.Exists(dbPath));
        }

        if (!File.Exists(dbPath))
        {
            batchCounters.SkippedNoSourceInstalled++;
            return null;
        }

        try
        {
            var raw = QueryPlayniteAchievementsDb(dbPath, playniteGameId.ToString());
            if (raw == null)
            {
                batchCounters.SkippedDbNoGame++;
                return null;
            }

            batchCounters.AttachedToPayload++;
            return new AchievementImportDto
            {
                Source = AchievementExtensionPaths.ImportSourcePlayniteAchievements,
                Raw = raw
            };
        }
        catch (Exception ex)
        {
            batchCounters.SkippedDbReadError++;
            logger.Warn(ex, "PlayLog achievements: failed to read PlayniteAchievements DB for game " + playniteGameId);
            return null;
        }
    }

    /// <summary>
    /// Opens the PA SQLite DB in read-only mode and queries achievement data for the given Playnite game ID.
    /// Returns a JObject matching the PA raw payload shape, or null if the game has no data in the DB.
    /// </summary>
    private static JObject QueryPlayniteAchievementsDb(string dbPath, string playniteGameIdStr)
    {
        var connectionString = "Data Source=" + dbPath + ";Version=3;Read Only=True;";

        using (var connection = new SQLiteConnection(connectionString))
        {
            connection.Open();

            // Look up the game row using the Playnite game GUID.
            long gameRowId;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Id FROM Games WHERE PlayniteGameId = @gid LIMIT 1";
                cmd.Parameters.AddWithValue("@gid", playniteGameIdStr);
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return null;
                gameRowId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }

            // Find the current user's progress row for this game.
            long progressRowId;
            long achievementsUnlocked = 0;
            long totalAchievements = 0;
            string lastUpdatedUtc = null;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT ugp.Id, ugp.AchievementsUnlocked, ugp.TotalAchievements, ugp.LastUpdatedUtc "
                    + "FROM UserGameProgress ugp "
                    + "JOIN Users u ON u.Id = ugp.UserId "
                    + "WHERE ugp.GameId = @gid AND u.IsCurrentUser = 1 "
                    + "LIMIT 1";
                cmd.Parameters.AddWithValue("@gid", gameRowId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        logger.Info("PlayLog achievements [PA]: game found (gameRowId=" + gameRowId
                            + ", playniteId=" + playniteGameIdStr
                            + ") but no UserGameProgress row with IsCurrentUser=1 — PA may not have scanned this game yet or user row missing.");
                        return null;
                    }
                    progressRowId = reader.GetInt64(0);
                    achievementsUnlocked = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                    totalAchievements = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                    lastUpdatedUtc = reader.IsDBNull(3) ? null : reader.GetString(3);
                }
            }

            // Fetch all per-achievement rows for this user+game.
            var items = new JArray();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT d.ApiName, d.DisplayName, d.Description, d.Hidden, d.GlobalPercentUnlocked, "
                    + "       ua.Unlocked, ua.UnlockTimeUtc "
                    + "FROM UserAchievements ua "
                    + "JOIN AchievementDefinitions d ON d.Id = ua.AchievementDefinitionId "
                    + "WHERE ua.UserGameProgressId = @pid";
                cmd.Parameters.AddWithValue("@pid", progressRowId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var item = new JObject();

                        item["apiName"] = reader.IsDBNull(0) ? null : reader.GetString(0);
                        item["displayName"] = reader.IsDBNull(1) ? null : reader.GetString(1);

                        if (!reader.IsDBNull(2))
                            item["description"] = reader.GetString(2);

                        item["hidden"] = !reader.IsDBNull(3) && reader.GetInt64(3) != 0;

                        if (!reader.IsDBNull(4))
                            item["globalPercentUnlocked"] = reader.GetDouble(4);

                        var unlocked = !reader.IsDBNull(5) && reader.GetInt64(5) != 0;
                        item["unlocked"] = unlocked;

                        if (unlocked && !reader.IsDBNull(6))
                            item["unlockTimeUtc"] = reader.GetString(6);
                        else
                            item["unlockTimeUtc"] = null;

                        items.Add(item);
                    }
                }
            }

            if (items.Count == 0)
                return null;

            var raw = new JObject
            {
                ["items"] = items,
                ["achievementsUnlocked"] = achievementsUnlocked,
                ["totalAchievements"] = totalAchievements
            };

            if (!string.IsNullOrEmpty(lastUpdatedUtc))
                raw["lastUpdatedUtc"] = lastUpdatedUtc;

            return raw;
        }
    }

    private static bool IsSuccessStoryInstalled(string extensionsDataPath)
    {
        try
        {
            var folderPath = Path.Combine(
                extensionsDataPath,
                AchievementExtensionPaths.SuccessStoryPluginFolderId.ToString(),
                AchievementExtensionPaths.SuccessStoryGameDataSubfolder);
            return Directory.Exists(folderPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPlayniteAchievementsInstalled(string extensionsDataPath)
    {
        try
        {
            return File.Exists(AchievementExtensionPaths.GetPlayniteAchievementsCacheDbPath(extensionsDataPath));
        }
        catch
        {
            return false;
        }
    }
}

public sealed class AchievementImportDto
{
    [JsonProperty("source")]
    public string Source { get; set; }

    [JsonProperty("raw")]
    public object Raw { get; set; }
}
