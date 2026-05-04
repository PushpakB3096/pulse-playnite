using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Playnite.SDK;

internal static class HltbExtensionGameDataReader
{
    private static readonly ILogger logger = LogManager.GetLogger();

    public static GameHltbDataDto TryRead(string extensionsDataPath, Guid playniteGameId)
    {
        if (string.IsNullOrEmpty(extensionsDataPath))
            return null;

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
            logger.Warn(ex, "PlayLog: failed to build HLTB extension path for game " + playniteGameId);
            return null;
        }

        if (!File.Exists(filePath))
            return null;

        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "PlayLog: failed to read HLTB extension file: " + filePath);
            return null;
        }

        HltbExtensionGameJson root;
        try
        {
            root = JsonConvert.DeserializeObject<HltbExtensionGameJson>(json);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "PlayLog: failed to parse HLTB extension JSON: " + filePath);
            return null;
        }

        if (root?.Items == null || root.Items.Count == 0)
            return null;

        var gameHltbData = root.Items[0]?.GameHltbData;
        if (gameHltbData == null)
            return null;

        int? mainStory = SecondsToRoundedMinutes(gameHltbData.MainStoryMedian);
        int? mainExtra = SecondsToRoundedMinutes(gameHltbData.MainExtraMedian);
        int? completionist = SecondsToRoundedMinutes(gameHltbData.CompletionistMedian);

        if (mainStory == null && mainExtra == null && completionist == null)
            return null;

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
