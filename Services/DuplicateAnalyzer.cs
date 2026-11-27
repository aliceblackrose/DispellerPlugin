using System;
using System.Collections.Generic;
using System.Linq;
using Dispeller.Models;

namespace Dispeller.Services;

/// <summary>
/// Converts raw dresser entries into strongly-typed duplicate-model groups and precomputes
/// all recommendation and change-tracking data used by the UI.
/// </summary>
public sealed class DuplicateAnalyzer
{
    private readonly Func<uint, ItemMetadata?> getMetadata;

    public DuplicateAnalyzer(Func<uint, ItemMetadata?> getMetadata)
    {
        this.getMetadata = getMetadata;
    }

    public ScanResult Analyze(
        IReadOnlyList<DresserItem> rawItems,
        DresserSnapshot? previousSnapshot,
        IReadOnlySet<uint> protectedItemIds,
        IReadOnlySet<string> ignoredGroupKeys,
        bool preserveDyedItems)
    {
        var uniqueItems = GetUniqueItems(rawItems);
        var analyzed = CreateAnalyzedItems(uniqueItems, protectedItemIds, preserveDyedItems);

        var groups = analyzed
            .GroupBy(item => (item.Metadata.Slot, item.Metadata.Model))
            .Where(group => group.Count() > 1)
            .Select(group => BuildGroup(group.Key.Slot, group.Key.Model, group.ToList(), ignoredGroupKeys))
            .OrderBy(group => (int)group.Slot)
            .ThenByDescending(group => group.Items.Count)
            .ThenBy(group => group.Model.Primary)
            .ThenBy(group => group.Model.Secondary)
            .ThenBy(group => group.Model.Variant)
            .ThenBy(group => group.Model.Extra)
            .ToList();

        var previousKeys = previousSnapshot == null
            ? new HashSet<(uint Slot, uint ItemId)>()
            : previousSnapshot.Items
                .Select(item => (item.Slot, item.ItemId))
                .ToHashSet();

        var currentKeys = uniqueItems
            .Select(item => (item.Slot, item.ItemId))
            .ToHashSet();

        var currentGroupKeys = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var previousGroupKeys = previousSnapshot == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : BuildDuplicateGroupKeys(previousSnapshot);

        return new ScanResult
        {
            Groups = groups,
            TotalDresserItems = rawItems.Count,
            ValidDresserItems = analyzed.Count,
            DuplicateItemCount = groups.Sum(group => group.Items.Count),
            ArmoireEligibleDuplicateItems = groups.Sum(group => group.Items.Count(item => item.Metadata.CanGoInArmoire)),
            RecommendedRecoverableSlots = groups.Sum(group => group.RemovalCandidateCount),

            AddedSincePrevious = previousSnapshot == null ? 0 : currentKeys.Except(previousKeys).Count(),
            RemovedSincePrevious = previousSnapshot == null ? 0 : previousKeys.Except(currentKeys).Count(),
            NewDuplicateGroups = previousSnapshot == null ? 0 : currentGroupKeys.Except(previousGroupKeys).Count(),
            ResolvedDuplicateGroups = previousSnapshot == null ? 0 : previousGroupKeys.Except(currentGroupKeys).Count(),
            HasPreviousSnapshot = previousSnapshot != null,
        };
    }

    public static string CreateGroupKey(GearSlot slot, ModelSignature model) =>
        $"{(int)slot}:{model}";

    private static List<DresserItem> GetUniqueItems(IEnumerable<DresserItem> items) =>
        items
            .Where(item => item.ItemId != 0)
            .GroupBy(item => (item.Slot, item.ItemId))
            .Select(group => group.First())
            .ToList();

    private List<AnalyzedItem> CreateAnalyzedItems(
        IReadOnlyList<DresserItem> items,
        IReadOnlySet<uint>? protectedItemIds,
        bool preserveDyedItems = false)
    {

        var analyzed = new List<AnalyzedItem>(items.Count);
        foreach (var dresserItem in items)
        {
            var metadata = getMetadata(dresserItem.ItemId);
            if (metadata == null || metadata.Model.IsEmpty)
                continue;

            analyzed.Add(new AnalyzedItem
            {
                DresserItem = dresserItem,
                Metadata = metadata,
                IsProtected = protectedItemIds?.Contains(dresserItem.ItemId) == true,
                IsDyedProtected = preserveDyedItems && (dresserItem.Stain1 != 0 || dresserItem.Stain2 != 0),
            });
        }

        return analyzed;
    }

    private DuplicateGroup BuildGroup(
        GearSlot slot,
        ModelSignature model,
        List<AnalyzedItem> items,
        IReadOnlySet<string> ignoredGroupKeys)
    {
        var key = CreateGroupKey(slot, model);
        var isIgnored = ignoredGroupKeys.Contains(key);
        foreach (var item in items)
            item.KeepScore = CalculateKeepScore(item);

        var keeper = items
            .OrderByDescending(item => item.KeepScore)
            .ThenBy(item => item.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Metadata.ItemId)
            .ThenBy(item => item.DresserItem.Slot)
            .First();

        var maximumDyeCount = items.Max(item => item.Metadata.DyeCount);
        var anyArmoireEligible = items.Any(item => item.Metadata.CanGoInArmoire);

        foreach (var item in items)
        {
            item.IsRecommendedKeeper = ReferenceEquals(item, keeper);
            item.IsRemovalCandidate = !isIgnored && !item.IsRecommendedKeeper && !item.IsEffectivelyProtected;

            if (item.IsProtected)
                item.RecommendationReasons.Add("Protected by you.");
            if (item.IsDyedProtected)
                item.RecommendationReasons.Add("Protected because a stored dye is present.");
            if (isIgnored)
                item.RecommendationReasons.Add("This model group is ignored.");

            if (item.IsRecommendedKeeper)
            {
                if (item.Metadata.DyeCount == maximumDyeCount && maximumDyeCount > 0)
                    item.RecommendationReasons.Add($"Has {maximumDyeCount} dye channel{(maximumDyeCount == 1 ? string.Empty : "s")}.");

                if (!item.Metadata.CanGoInArmoire && anyArmoireEligible)
                    item.RecommendationReasons.Add("Keeps an Armoire-eligible duplicate out of the dresser.");

                if (item.RecommendationReasons.Count == 0)
                    item.RecommendationReasons.Add("Best default keeper after tie-breaking.");
            }
            else
            {
                if (item.Metadata.CanGoInArmoire)
                    item.RecommendationReasons.Add("Can be moved to the Armoire instead of occupying a dresser slot.");

                if (item.Metadata.DyeCount < keeper.Metadata.DyeCount)
                    item.RecommendationReasons.Add("Has fewer dye channels than the recommended keeper.");

                if (item.IsEffectivelyProtected)
                    item.RecommendationReasons.Add("Not suggested for restoration because it is protected.");
                else if (item.RecommendationReasons.Count == 0)
                    item.RecommendationReasons.Add("Shares the same model and is not protected.");
            }
        }

        items = items
            .OrderByDescending(item => item.IsRecommendedKeeper)
            .ThenByDescending(item => item.IsEffectivelyProtected)
            .ThenByDescending(item => item.KeepScore)
            .ThenBy(item => item.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DuplicateGroup
        {
            Slot = slot,
            Model = model,
            Key = key,
            Items = items,
            IsIgnored = isIgnored,
        };
    }

    private static int CalculateKeepScore(AnalyzedItem item)
    {
        var score = 0;

        if (item.IsEffectivelyProtected)
            score += 10_000;

        // Dye flexibility is the strongest objective difference between identical models.
        score += item.Metadata.DyeCount * 100;

        // An Armoire-eligible piece is often better moved out of the dresser than kept as the
        // dresser copy, so non-Armoire pieces get a modest keeper preference.
        if (!item.Metadata.CanGoInArmoire)
            score += 25;

        return score;
    }

    private HashSet<string> BuildDuplicateGroupKeys(DresserSnapshot snapshot)
    {
        var analyzed = CreateAnalyzedItems(GetUniqueItems(snapshot.ToDresserItems()), protectedItemIds: null);

        return analyzed
            .GroupBy(item => (item.Metadata.Slot, item.Metadata.Model))
            .Where(group => group.Count() > 1)
            .Select(group => CreateGroupKey(group.Key.Slot, group.Key.Model))
            .ToHashSet(StringComparer.Ordinal);
    }
}
