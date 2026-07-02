using System;
using System.IO;

internal static class AchievementExtensionPaths
{
    public static readonly Guid SuccessStoryPluginFolderId =
        Guid.Parse("cebe6d32-8c46-4459-b993-5a5189d60788");

    public const string SuccessStoryGameDataSubfolder = "SuccessStory";

    public static readonly Guid PlayniteAchievementsPluginId =
        Guid.Parse("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");

    public const string PlayniteAchievementsCacheDbFileName = "achievement_cache.db";

    public const string ImportSourceSuccessStory = "successStory";

    public const string ImportSourcePlayniteAchievements = "playniteAchievements";

    public static string GetSuccessStoryGameFilePath(string extensionsDataPath, Guid playniteGameId)
    {
        return Path.Combine(
            extensionsDataPath,
            SuccessStoryPluginFolderId.ToString(),
            SuccessStoryGameDataSubfolder,
            $"{playniteGameId}.json");
    }

    public static string GetPlayniteAchievementsPluginUserDataPath(string extensionsDataPath)
    {
        return Path.Combine(extensionsDataPath, PlayniteAchievementsPluginId.ToString());
    }

    public static string GetPlayniteAchievementsCacheDbPath(string extensionsDataPath)
    {
        return Path.Combine(
            GetPlayniteAchievementsPluginUserDataPath(extensionsDataPath),
            PlayniteAchievementsCacheDbFileName);
    }
}
