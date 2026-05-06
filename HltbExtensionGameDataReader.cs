using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Playnite.SDK;

/// <summary>Counts HowLongToBeat JSON read outcomes for one games/sync batch (visibility in Playnite logs).</summary>
internal sealed class HltbSyncBatchCounters
{
    public int AttachedToPayload;
    public int SkippedNoExtensionsDataPath;
    public int SkippedFileNotFound;
    public int SkippedJsonItemsEmpty;
    public int SkippedGameHltbDataMissing;
    public int SkippedAllMediansEmptyOrInvalid;
    public int SkippedPathCombineError;
    public int SkippedFileReadError;
    public int SkippedJsonParseError;
    /// <summary>First missing path sample when <see cref="SkippedFileNotFound"/> is non-zero.</summary>
    public string SampleMissingFilePath;

    public void LogBatchSummary(ILogger log, int gamesInPayload, string extensionsDataPath)
    {
        if (gamesInPayload <= 0)
            return;

        var pathPresent = !string.IsNullOrEmpty(extensionsDataPath);
        if (!pathPresent && SkippedNoExtensionsDataPath == gamesInPayload)
        {
            log.Warn(
                "PlayLog HLTB: Playnite ExtensionsDataPath is empty — cannot read HowLongToBeat addon JSON; hltbData omitted for every game.");
        }

        log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "PlayLog HLTB: sync batch — gamesInPayload={0}, hltbAttached={1}, skipped_noExtensionsDataPath={2}, skipped_fileNotFound={3}, skipped_jsonItemsEmpty={4}, skipped_gameHltbDataMissing={5}, skipped_allMediansEmptyOrInvalid={6}, skipped_pathCombineError={7}, skipped_fileReadError={8}, skipped_jsonParseError={9}. extensionsDataPathPresent={10}. sampleMissingFile={11}",
            gamesInPayload,
            AttachedToPayload,
            SkippedNoExtensionsDataPath,
            SkippedFileNotFound,
            SkippedJsonItemsEmpty,
            SkippedGameHltbDataMissing,
            SkippedAllMediansEmptyOrInvalid,
            SkippedPathCombineError,
            SkippedFileReadError,
            SkippedJsonParseError,
            pathPresent,
            SampleMissingFilePath ?? "(n/a)"));

        if (AttachedToPayload == 0 && pathPresent && SkippedFileNotFound > 0 && SkippedJsonParseError == 0 && SkippedFileReadError == 0)
        {
            log.Info(
                "PlayLog HLTB: hint — files are expected under ExtensionsDataPath\\"
                + HltbExtensionPaths.HowLongToBeatPluginFolderId.ToString()
                + "\\" + HltbExtensionPaths.GameDataSubfolder
                + "\\{playniteGameId}.json (HowLongToBeat Playnite extension must have saved data for each game).");
        }
    }
}

internal static class HltbExtensionGameDataReader
{
    private static readonly ILogger logger = LogManager.GetLogger();

    public static GameHltbDataDto TryRead(
        string extensionsDataPath,
        Guid playniteGameId,
        HltbSyncBatchCounters batchCounters)
    {
        if (batchCounters == null)
            throw new ArgumentNullException(nameof(batchCounters));

        if (string.IsNullOrEmpty(extensionsDataPath))
        {
            batchCounters.SkippedNoExtensionsDataPath++;
            return null;
        }

        string filePath;
        try
        {
            filePath = Path.Combine(
                extensionsDataPath,
                HltbExtensionPaths.HowLongToBeatPluginFolderId.ToString(),
                HltbExtensionPaths.GameDataSubfolder,
                $"{playniteGameId}.json");
        }
        catch (Exception ex)
        {
            batchCounters.SkippedPathCombineError++;
            logger.Warn(ex, "PlayLog HLTB: failed to build extension path for game " + playniteGameId);
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
            logger.Warn(ex, "PlayLog HLTB: failed to read extension file: " + filePath);
            return null;
        }

        HltbExtensionGameJson root;
        try
        {
            root = JsonConvert.DeserializeObject<HltbExtensionGameJson>(json);
        }
        catch (Exception ex)
        {
            batchCounters.SkippedJsonParseError++;
            logger.Warn(ex, "PlayLog HLTB: failed to parse extension JSON: " + filePath);
            return null;
        }

        if (root?.Items == null || root.Items.Count == 0)
        {
            batchCounters.SkippedJsonItemsEmpty++;
            logger.Info(
                "PlayLog HLTB: skipped game " + playniteGameId + " — JSON has no Items or Items is empty: " + filePath);
            return null;
        }

        var gameHltbData = root.Items[0]?.GameHltbData;
        if (gameHltbData == null)
        {
            batchCounters.SkippedGameHltbDataMissing++;
            logger.Info(
                "PlayLog HLTB: skipped game " + playniteGameId + " — Items[0].GameHltbData is null: " + filePath);
            return null;
        }

        int? mainStory = SecondsToRoundedMinutes(gameHltbData.MainStoryMedian);
        int? mainExtra = SecondsToRoundedMinutes(gameHltbData.MainExtraMedian);
        int? completionist = SecondsToRoundedMinutes(gameHltbData.CompletionistMedian);

        if (mainStory == null && mainExtra == null && completionist == null)
        {
            batchCounters.SkippedAllMediansEmptyOrInvalid++;
            logger.Info(
                "PlayLog HLTB: skipped game " + playniteGameId
                + " — MainStoryMedian / MainExtraMedian / CompletionistMedian missing or non-positive (seconds): "
                + filePath);
            return null;
        }

        batchCounters.AttachedToPayload++;
        return new GameHltbDataDto
        {
            MainStory = mainStory,
            MainExtra = mainExtra,
            Completionist = completionist
        };
    }

    private static int? SecondsToRoundedMinutes(double? seconds)
    {
        if (!seconds.HasValue || seconds.Value <= 0d)
            return null;
        return Math.Max(0, (int)Math.Round(seconds.Value / 60.0));
    }

    private sealed class HltbExtensionGameJson
    {
        [JsonProperty("Items")]
        public List<HltbExtensionGameItemJson> Items { get; set; }
    }

    private sealed class HltbExtensionGameItemJson
    {
        [JsonProperty("GameHltbData")]
        public HltbExtensionGameHltbDataJson GameHltbData { get; set; }
    }

    private sealed class HltbExtensionGameHltbDataJson
    {
        [JsonProperty("MainStoryMedian")]
        public double? MainStoryMedian { get; set; }

        [JsonProperty("MainExtraMedian")]
        public double? MainExtraMedian { get; set; }

        [JsonProperty("CompletionistMedian")]
        public double? CompletionistMedian { get; set; }
    }
}
