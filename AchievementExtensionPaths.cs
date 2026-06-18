using System;
using System.IO;

internal static class AchievementExtensionPaths
{
    public static readonly Guid SuccessStoryPluginFolderId =
        Guid.Parse("cebe6d32-8c46-4459-b993-5a5189d60788");

    public const string SuccessStoryGameDataSubfolder = "SuccessStory";

    public const string PlayniteAchievementsAddonFolderName = "PlayniteAchievements";

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
        return Path.Combine(extensionsDataPath, PlayniteAchievementsAddonFolderName);
    }

    public static string GetPlayniteAchievementsCacheDbPath(string extensionsDataPath)
    {
        return Path.Combine(
            GetPlayniteAchievementsPluginUserDataPath(extensionsDataPath),
            PlayniteAchievementsCacheDbFileName);
    }
}
