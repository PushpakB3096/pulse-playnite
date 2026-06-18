using System;
using System.Collections.Generic;
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
            "PlayLog achievements: sync batch — gamesInPayload={0}, attached={1}, source={2}, skipped_noExtensionsDataPath={3}, skipped_noSourceInstalled={4}, skipped_fileNotFound={5}, skipped_jsonItemsEmpty={6}, skipped_pathCombineError={7}, skipped_fileReadError={8}, skipped_jsonParseError={9}. extensionsDataPathPresent={10}. sampleMissingFile={11}",
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

    public static void EnsureActiveSource(string extensionsDataPath, AchievementSyncBatchCounters batchCounters)
    {
        if (batchCounters == null)
            throw new ArgumentNullException(nameof(batchCounters));

        if (!string.IsNullOrEmpty(batchCounters.ActiveSourceKey))
            return;

        if (string.IsNullOrEmpty(extensionsDataPath))
        {
            batchCounters.ActiveSourceKey = null;
            return;
        }

        if (IsPlayniteAchievementsInstalled(extensionsDataPath))
        {
            batchCounters.ActiveSourceKey = AchievementExtensionPaths.ImportSourcePlayniteAchievements;
            return;
        }

        if (IsSuccessStoryInstalled(extensionsDataPath))
        {
            batchCounters.ActiveSourceKey = AchievementExtensionPaths.ImportSourceSuccessStory;
            return;
        }

        batchCounters.ActiveSourceKey = null;
    }

    public static AchievementImportDto TryRead(
        string extensionsDataPath,
        Guid playniteGameId,
        AchievementSyncBatchCounters batchCounters)
    {
        if (batchCounters == null)
            throw new ArgumentNullException(nameof(batchCounters));

        EnsureActiveSource(extensionsDataPath, batchCounters);

        if (string.IsNullOrEmpty(batchCounters.ActiveSourceKey))
        {
            if (string.IsNullOrEmpty(extensionsDataPath))
                batchCounters.SkippedNoExtensionsDataPath++;
            else
                batchCounters.SkippedNoSourceInstalled++;
            return null;
        }

        if (batchCounters.ActiveSourceKey == AchievementExtensionPaths.ImportSourcePlayniteAchievements)
            return null;

        if (string.IsNullOrEmpty(extensionsDataPath))
        {
            batchCounters.SkippedNoExtensionsDataPath++;
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
