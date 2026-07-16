using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;

public partial class PulseAccountClient
{
    private readonly string playniteFilterPresetsSyncEndpoint;

    public async Task SyncFilterPresetsAsync()
    {
        if (!HasBearerToken())
        {
            logger.Info("PlayLog: skip filter preset sync — not linked");
            return;
        }

        var database = playniteApi?.Database;
        if (database == null || !database.IsOpen)
        {
            logger.Info("PlayLog: skip filter preset sync — database not open");
            return;
        }

        List<FilterPreset> sortedPresets;
        try
        {
            sortedPresets = playniteApi.MainView?.GetSortedFilterPresets()
                ?? database.FilterPresets.ToList();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "PlayLog: failed to read sorted filter presets; falling back to collection order");
            sortedPresets = database.FilterPresets.ToList();
        }

        var presetDtos = new List<FilterPresetSyncDto>(sortedPresets.Count);
        for (var sortIndex = 0; sortIndex < sortedPresets.Count; sortIndex++)
        {
            var preset = sortedPresets[sortIndex];
            if (preset == null)
            {
                continue;
            }

            presetDtos.Add(MapFilterPresetToDto(database, preset, sortIndex));
        }

        var payload = new FilterPresetsSyncRequest
        {
            Presets = presetDtos,
            SyncedAt = DateTime.UtcNow.ToString("o")
        };

        var json = JsonConvert.SerializeObject(payload);
        using (var request = new HttpRequestMessage(HttpMethod.Post, playniteFilterPresetsSyncEndpoint))
        {
            ApplyBearer(request);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException(
                    "PlayLog filter preset sync failed: " + (int)response.StatusCode + " " + body);
            }
        }

        logger.Info("PlayLog: successfully synced " + presetDtos.Count + " filter preset(s).");
    }

    private static FilterPresetSyncDto MapFilterPresetToDto(
        IGameDatabase database,
        FilterPreset preset,
        int sortIndex)
    {
        var settings = preset.Settings ?? new FilterPresetSettings();
        var resolvedSettings = BuildResolvedFilterSettings(database, settings, out var unsupportedFields);

        return new FilterPresetSyncDto
        {
            PlaynitePresetId = preset.Id.ToString(),
            Name = preset.Name,
            SortIndex = sortIndex,
            Settings = resolvedSettings,
            PlayniteRaw = settings,
            UnsupportedFields = unsupportedFields,
            ViewOptions = new FilterPresetViewOptionsDto
            {
                SortingOrder = preset.SortingOrder?.ToString(),
                SortingOrderDirection = preset.SortingOrderDirection?.ToString(),
                GroupingOrder = preset.GroupingOrder?.ToString(),
                ShowInFullscreenQuickSelection = preset.ShowInFullscreeQuickSelection
            }
        };
    }

    private static ResolvedFilterPresetSettingsDto BuildResolvedFilterSettings(
        IGameDatabase database,
        FilterPresetSettings settings,
        out List<string> unsupportedFields)
    {
        unsupportedFields = new List<string>();
        var resolved = new ResolvedFilterPresetSettingsDto
        {
            FilterLogic = settings.UseAndFilteringStyle ? "and" : "or"
        };

        if (settings.Favorite)
        {
            resolved.Favorite = true;
        }

        if (settings.Hidden)
        {
            resolved.HiddenOnly = true;
        }

        if (settings.IsInstalled)
        {
            resolved.Installation = new List<string> { "installed" };
        }
        else if (settings.IsUnInstalled)
        {
            resolved.Installation = new List<string> { "not_installed" };
        }

        resolved.CompletionStatuses = ResolveIdItemNames(
            database.CompletionStatuses,
            settings.CompletionStatuses);
        resolved.Platforms = ResolveIdItemNames(database.Platforms, settings.Platform);
        resolved.Sources = ResolveIdItemNames(database.Sources, settings.Source);
        resolved.Genres = ResolveIdItemNames(database.Genres, settings.Genre);
        resolved.Tags = ResolveIdItemNames(database.Tags, settings.Tag);
        resolved.Features = ResolveIdItemNames(database.Features, settings.Feature);
        resolved.Developers = ResolveIdItemNames(database.Companies, settings.Developer);
        resolved.Publishers = ResolveIdItemNames(database.Companies, settings.Publisher);
        resolved.Series = ResolveIdItemNames(database.Series, settings.Series);
        resolved.Regions = ResolveIdItemNames(database.Regions, settings.Region);
        resolved.AgeRatings = ResolveIdItemNames(database.AgeRatings, settings.AgeRating);

        if (!string.IsNullOrWhiteSpace(settings.Version))
        {
            resolved.Version = settings.Version.Trim();
        }

        var releaseYear = ParseReleaseYear(settings.ReleaseYear);
        if (releaseYear.HasValue)
        {
            resolved.ReleaseYear = releaseYear.Value;
        }

        resolved.Playtime = ResolveEnumInts(settings.PlayTime);
        resolved.LastActivity = ResolveEnumInts(settings.LastActivity);
        resolved.RecentActivity = ResolveEnumInts(settings.RecentActivity);
        resolved.Added = ResolveEnumInts(settings.Added);
        resolved.Modified = ResolveEnumInts(settings.Modified);
        resolved.UserScore = ResolveEnumInts(settings.UserScore);
        resolved.CommunityScore = ResolveEnumInts(settings.CommunityScore);
        resolved.CriticScore = ResolveEnumInts(settings.CriticScore);

        if (HasIdItemFilter(settings.Category))
        {
            unsupportedFields.Add("category");
        }

        if (HasIdItemFilter(settings.Library))
        {
            unsupportedFields.Add("library");
        }

        if (!string.IsNullOrWhiteSpace(settings.Name))
        {
            unsupportedFields.Add("name");
        }

        if (HasEnumFilter(settings.InstallSize))
        {
            unsupportedFields.Add("installSize");
        }

        return resolved;
    }

    private static List<string> ResolveIdItemNames<T>(
        IItemCollection<T> collection,
        IdItemFilterItemProperties properties)
        where T : DatabaseObject
    {
        if (properties?.Ids == null || properties.Ids.Count == 0 || collection == null)
        {
            return new List<string>();
        }

        var names = new List<string>();
        foreach (var itemId in properties.Ids)
        {
            var item = collection.Get(itemId);
            if (item != null && !string.IsNullOrWhiteSpace(item.Name))
            {
                names.Add(item.Name);
            }
        }

        return names;
    }

    private static List<int> ResolveEnumInts(EnumFilterItemProperties properties)
    {
        if (properties?.Values == null || properties.Values.Count == 0)
        {
            return new List<int>();
        }

        return properties.Values.ToList();
    }

    private static int? ParseReleaseYear(StringFilterItemProperties properties)
    {
        var rawValue = properties?.Values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (int.TryParse(rawValue.Trim(), out var year))
        {
            return year;
        }

        return null;
    }

    private static bool HasIdItemFilter(IdItemFilterItemProperties properties)
    {
        return (properties?.Ids != null && properties.Ids.Count > 0)
            || !string.IsNullOrWhiteSpace(properties?.Text);
    }

    private static bool HasEnumFilter(EnumFilterItemProperties properties)
    {
        return properties?.Values != null && properties.Values.Count > 0;
    }

    private sealed class FilterPresetsSyncRequest
    {
        [JsonProperty("presets")]
        public List<FilterPresetSyncDto> Presets { get; set; }

        [JsonProperty("syncedAt")]
        public string SyncedAt { get; set; }
    }

    private sealed class FilterPresetSyncDto
    {
        [JsonProperty("playnitePresetId")]
        public string PlaynitePresetId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("sortIndex")]
        public int SortIndex { get; set; }

        [JsonProperty("settings")]
        public ResolvedFilterPresetSettingsDto Settings { get; set; }

        [JsonProperty("playniteRaw")]
        public FilterPresetSettings PlayniteRaw { get; set; }

        [JsonProperty("unsupportedFields")]
        public List<string> UnsupportedFields { get; set; }

        [JsonProperty("viewOptions")]
        public FilterPresetViewOptionsDto ViewOptions { get; set; }
    }

    private sealed class ResolvedFilterPresetSettingsDto
    {
        [JsonProperty("filterLogic")]
        public string FilterLogic { get; set; }

        [JsonProperty("favorite")]
        public bool? Favorite { get; set; }

        [JsonProperty("hiddenOnly")]
        public bool? HiddenOnly { get; set; }

        [JsonProperty("completionStatuses")]
        public List<string> CompletionStatuses { get; set; }

        [JsonProperty("platforms")]
        public List<string> Platforms { get; set; }

        [JsonProperty("sources")]
        public List<string> Sources { get; set; }

        [JsonProperty("genres")]
        public List<string> Genres { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; }

        [JsonProperty("features")]
        public List<string> Features { get; set; }

        [JsonProperty("developers")]
        public List<string> Developers { get; set; }

        [JsonProperty("publishers")]
        public List<string> Publishers { get; set; }

        [JsonProperty("series")]
        public List<string> Series { get; set; }

        [JsonProperty("regions")]
        public List<string> Regions { get; set; }

        [JsonProperty("ageRatings")]
        public List<string> AgeRatings { get; set; }

        [JsonProperty("installation")]
        public List<string> Installation { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("releaseYear")]
        public int? ReleaseYear { get; set; }

        [JsonProperty("playtime")]
        public List<int> Playtime { get; set; }

        [JsonProperty("lastActivity")]
        public List<int> LastActivity { get; set; }

        [JsonProperty("recentActivity")]
        public List<int> RecentActivity { get; set; }

        [JsonProperty("added")]
        public List<int> Added { get; set; }

        [JsonProperty("modified")]
        public List<int> Modified { get; set; }

        [JsonProperty("userScore")]
        public List<int> UserScore { get; set; }

        [JsonProperty("communityScore")]
        public List<int> CommunityScore { get; set; }

        [JsonProperty("criticScore")]
        public List<int> CriticScore { get; set; }
    }

    private sealed class FilterPresetViewOptionsDto
    {
        [JsonProperty("sortingOrder")]
        public string SortingOrder { get; set; }

        [JsonProperty("sortingOrderDirection")]
        public string SortingOrderDirection { get; set; }

        [JsonProperty("groupingOrder")]
        public string GroupingOrder { get; set; }

        [JsonProperty("showInFullscreenQuickSelection")]
        public bool ShowInFullscreenQuickSelection { get; set; }
    }
}
