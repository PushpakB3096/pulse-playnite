using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

internal static class PulseGameInfoPushApplier
{
    internal sealed class ApplyResult
    {
        public bool AllApplied { get; set; }
        public bool Changed { get; set; }
    }

    internal static ApplyResult Apply(
        IGameDatabase database,
        Game game,
        IDictionary<string, object> gameInfo)
    {
        var result = new ApplyResult { AllApplied = true, Changed = false };
        if (database == null || game == null || gameInfo == null || gameInfo.Count == 0)
        {
            return result;
        }

        if (gameInfo.ContainsKey("playtimeSeconds"))
        {
            if (!TryApplyPlaytimeSeconds(game, gameInfo["playtimeSeconds"], out var changed))
            {
                result.AllApplied = false;
            }
            else if (changed)
            {
                result.Changed = true;
            }
        }

        if (gameInfo.ContainsKey("playCount"))
        {
            if (!TryApplyPlayCount(game, gameInfo["playCount"], out var changed))
            {
                result.AllApplied = false;
            }
            else if (changed)
            {
                result.Changed = true;
            }
        }

        if (gameInfo.ContainsKey("lastPlayedAt"))
        {
            if (!TryApplyLastPlayedAt(game, gameInfo["lastPlayedAt"], out var changed))
            {
                result.AllApplied = false;
            }
            else if (changed)
            {
                result.Changed = true;
            }
        }

        if (gameInfo.ContainsKey("releaseDate"))
        {
            if (!TryApplyReleaseDate(game, gameInfo["releaseDate"], out var changed))
            {
                result.AllApplied = false;
            }
            else if (changed)
            {
                result.Changed = true;
            }
        }

        if (gameInfo.ContainsKey("version"))
        {
            if (!TryApplyVersion(game, gameInfo["version"], out var changed))
            {
                result.AllApplied = false;
            }
            else if (changed)
            {
                result.Changed = true;
            }
        }

        if (gameInfo.ContainsKey("userScore"))
        {
            if (!TryApplyScore(
                    gameInfo["userScore"],
                    game.UserScore,
                    value => game.UserScore = value,
                    out var changed))
            {
                result.AllApplied = false;
            }
            else if (changed)
            {
                result.Changed = true;
            }
        }

        if (gameInfo.ContainsKey("communityScore"))
        {
            if (!TryApplyScore(
                    gameInfo["communityScore"],
                    game.CommunityScore,
                    value => game.CommunityScore = value,
                    out var changed))
            {
                result.AllApplied = false;
            }
            else if (changed)
            {
                result.Changed = true;
            }
        }

        if (gameInfo.ContainsKey("criticScore"))
        {
            if (!TryApplyScore(
                    gameInfo["criticScore"],
                    game.CriticScore,
                    value => game.CriticScore = value,
                    out var changed))
            {
                result.AllApplied = false;
            }
            else if (changed)
            {
                result.Changed = true;
            }
        }

        ApplyMetadataList(database, gameInfo, "developers", result, names =>
        {
            var nextIds = ResolveMetadataIds(database.Companies, names);
            return ApplyGuidList(game.DeveloperIds, nextIds, value => game.DeveloperIds = value);
        });
        ApplyMetadataList(database, gameInfo, "publishers", result, names =>
        {
            var nextIds = ResolveMetadataIds(database.Companies, names);
            return ApplyGuidList(game.PublisherIds, nextIds, value => game.PublisherIds = value);
        });
        ApplyMetadataList(database, gameInfo, "series", result, names =>
        {
            var nextIds = ResolveMetadataIds(database.Series, names);
            return ApplyGuidList(game.SeriesIds, nextIds, value => game.SeriesIds = value);
        });
        ApplyMetadataList(database, gameInfo, "regions", result, names =>
        {
            var nextIds = ResolveMetadataIds(database.Regions, names);
            return ApplyGuidList(game.RegionIds, nextIds, value => game.RegionIds = value);
        });
        ApplyMetadataList(database, gameInfo, "ageRatings", result, names =>
        {
            var nextIds = ResolveMetadataIds(database.AgeRatings, names);
            return ApplyGuidList(game.AgeRatingIds, nextIds, value => game.AgeRatingIds = value);
        });
        ApplyMetadataList(database, gameInfo, "platforms", result, names =>
        {
            var nextIds = ResolveMetadataIds(database.Platforms, names);
            return ApplyGuidList(game.PlatformIds, nextIds, value => game.PlatformIds = value);
        });

        return result;
    }

    private static void ApplyMetadataList(
        IGameDatabase database,
        IDictionary<string, object> gameInfo,
        string key,
        ApplyResult result,
        Func<IList<string>, bool> applyNames)
    {
        if (!gameInfo.ContainsKey(key))
        {
            return;
        }

        var names = ParseStringList(gameInfo[key]);
        if (names == null)
        {
            result.AllApplied = false;
            return;
        }

        if (applyNames(names))
        {
            result.Changed = true;
        }
    }

    private static bool ApplyGuidList(
        List<Guid> currentIds,
        List<Guid> nextIds,
        Action<List<Guid>> setIds)
    {
        var current = currentIds ?? new List<Guid>();
        if (GuidListsEqual(current, nextIds))
        {
            return false;
        }

        setIds(nextIds);
        return true;
    }

    private static bool TryApplyPlaytimeSeconds(Game game, object rawValue, out bool changed)
    {
        changed = false;
        if (!TryConvertToNonNegativeWholeNumber(rawValue, out var seconds))
        {
            return false;
        }

        var targetPlaytime = (ulong)seconds;
        if (game.Playtime == targetPlaytime)
        {
            return true;
        }

        game.Playtime = targetPlaytime;
        changed = true;
        return true;
    }

    private static bool TryApplyPlayCount(Game game, object rawValue, out bool changed)
    {
        changed = false;
        if (!TryConvertToNonNegativeWholeNumber(rawValue, out var count))
        {
            return false;
        }

        var targetPlayCount = (ulong)count;
        if (game.PlayCount == targetPlayCount)
        {
            return true;
        }

        game.PlayCount = targetPlayCount;
        changed = true;
        return true;
    }

    private static bool TryApplyLastPlayedAt(Game game, object rawValue, out bool changed)
    {
        changed = false;
        var rawText = rawValue?.ToString()?.Trim();
        if (string.IsNullOrEmpty(rawText))
        {
            return false;
        }

        if (!DateTime.TryParse(
                rawText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }

        if (game.LastActivity == parsed)
        {
            return true;
        }

        game.LastActivity = parsed;
        changed = true;
        return true;
    }

    private static bool TryApplyReleaseDate(Game game, object rawValue, out bool changed)
    {
        changed = false;
        if (!TryParseReleaseDate(rawValue, out var parsedReleaseDate))
        {
            return false;
        }

        if (game.ReleaseDate == parsedReleaseDate)
        {
            return true;
        }

        game.ReleaseDate = parsedReleaseDate;
        changed = true;
        return true;
    }

    private static bool TryApplyVersion(Game game, object rawValue, out bool changed)
    {
        changed = false;
        var version = rawValue?.ToString()?.Trim();
        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        if (string.Equals(game.Version, version, StringComparison.Ordinal))
        {
            return true;
        }

        game.Version = version;
        changed = true;
        return true;
    }

    private static bool TryApplyScore(
        object rawValue,
        int? currentScore,
        Action<int?> assignScore,
        out bool changed)
    {
        changed = false;
        if (rawValue == null)
        {
            if (currentScore == null)
            {
                return true;
            }

            assignScore(null);
            changed = true;
            return true;
        }

        if (!TryConvertToNonNegativeWholeNumber(rawValue, out var score) || score > 100)
        {
            return false;
        }

        var nextScore = (int)score;
        if (currentScore == nextScore)
        {
            return true;
        }

        assignScore(nextScore);
        changed = true;
        return true;
    }

    private static List<Guid> ResolveMetadataIds<T>(
        IItemCollection<T> collection,
        IList<string> names)
        where T : DatabaseObject, new()
    {
        var ids = new List<Guid>();
        if (collection == null || names == null)
        {
            return ids;
        }

        foreach (var rawName in names)
        {
            var trimmedName = rawName?.Trim();
            if (string.IsNullOrEmpty(trimmedName))
            {
                continue;
            }

            var existing = collection.FirstOrDefault(item =>
                item != null &&
                string.Equals(item.Name, trimmedName, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new T { Name = trimmedName };
                collection.Add(existing);
            }

            ids.Add(existing.Id);
        }

        return ids;
    }

    private static bool TryParseReleaseDate(object rawValue, out ReleaseDate? releaseDate)
    {
        releaseDate = null;
        if (rawValue == null)
        {
            return false;
        }

        if (rawValue is ReleaseDate typedReleaseDate)
        {
            releaseDate = typedReleaseDate;
            return true;
        }

        if (TryParseReleaseDateFromToken(rawValue as JObject, out releaseDate))
        {
            return true;
        }

        if (rawValue is IDictionary<string, object> dictionary &&
            TryParseReleaseDateParts(
                dictionary.TryGetValue("year", out var yearRaw) ? yearRaw : null,
                dictionary.TryGetValue("month", out var monthRaw) ? monthRaw : null,
                dictionary.TryGetValue("day", out var dayRaw) ? dayRaw : null,
                out releaseDate))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseReleaseDateFromToken(JObject token, out ReleaseDate? releaseDate)
    {
        releaseDate = null;
        if (token == null)
        {
            return false;
        }

        return TryParseReleaseDateParts(
            token["year"]?.ToObject<object>(),
            token["month"]?.ToObject<object>(),
            token["day"]?.ToObject<object>(),
            out releaseDate);
    }

    private static bool TryParseReleaseDateParts(
        object yearRaw,
        object monthRaw,
        object dayRaw,
        out ReleaseDate? releaseDate)
    {
        releaseDate = null;
        if (!TryConvertToNonNegativeWholeNumber(yearRaw, out var year) || year <= 0)
        {
            return false;
        }

        int? month = null;
        if (monthRaw != null)
        {
            if (!TryConvertToNonNegativeWholeNumber(monthRaw, out var monthNumber) ||
                monthNumber < 1 ||
                monthNumber > 12)
            {
                return false;
            }

            month = (int)monthNumber;
        }

        int? day = null;
        if (dayRaw != null)
        {
            if (!TryConvertToNonNegativeWholeNumber(dayRaw, out var dayNumber) ||
                dayNumber < 1 ||
                dayNumber > 31)
            {
                return false;
            }

            day = (int)dayNumber;
        }

        if (day.HasValue && !month.HasValue)
        {
            return false;
        }

        if (month.HasValue && day.HasValue)
        {
            releaseDate = new ReleaseDate((int)year, month.Value, day.Value);
        }
        else if (month.HasValue)
        {
            releaseDate = new ReleaseDate((int)year, month.Value);
        }
        else
        {
            releaseDate = new ReleaseDate((int)year);
        }

        return true;
    }

    private static IList<string> ParseStringList(object rawValue)
    {
        if (rawValue is IList<string> stringList)
        {
            return stringList;
        }

        if (rawValue is JArray jsonArray)
        {
            return jsonArray
                .Select(token => token?.ToString()?.Trim())
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();
        }

        if (rawValue is IEnumerable<object> objectList)
        {
            return objectList
                .Select(item => item?.ToString()?.Trim())
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();
        }

        return null;
    }

    private static bool GuidListsEqual(IList<Guid> left, IList<Guid> right)
    {
        if (left == null && right == null)
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryConvertToNonNegativeWholeNumber(object rawValue, out long wholeNumber)
    {
        wholeNumber = 0;
        if (rawValue == null)
        {
            return false;
        }

        try
        {
            wholeNumber = Convert.ToInt64(rawValue, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return false;
        }

        return wholeNumber >= 0;
    }
}
