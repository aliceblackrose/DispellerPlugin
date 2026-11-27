using System;
using System.Collections.Generic;

namespace Dispeller.Models;

public sealed class AnalyzedItem
{
    public required DresserItem DresserItem { get; init; }
    public required ItemMetadata Metadata { get; init; }

    public bool IsProtected { get; set; }
    public bool IsDyedProtected { get; set; }
    public bool IsEffectivelyProtected => IsProtected || IsDyedProtected;
    public bool IsRecommendedKeeper { get; set; }
    public bool IsRemovalCandidate { get; set; }
    public int KeepScore { get; set; }

    public List<string> RecommendationReasons { get; } = [];
}

public sealed class DuplicateGroup
{
    public required GearSlot Slot { get; init; }
    public required ModelSignature Model { get; init; }
    public required string Key { get; init; }
    public required List<AnalyzedItem> Items { get; init; }

    public bool IsIgnored { get; set; }

    public AnalyzedItem RecommendedKeeper =>
        Items.Find(item => item.IsRecommendedKeeper) ?? Items[0];

    public int RemovalCandidateCount =>
        IsIgnored ? 0 : System.Linq.Enumerable.Count(Items, item => item.IsRemovalCandidate);
}

public sealed class ScanResult
{
    public List<DuplicateGroup> Groups { get; init; } = [];

    public int TotalDresserItems { get; init; }
    public int ValidDresserItems { get; init; }
    public int DuplicateItemCount { get; init; }
    public int ArmoireEligibleDuplicateItems { get; init; }
    public int RecommendedRecoverableSlots { get; init; }

    public int AddedSincePrevious { get; init; }
    public int RemovedSincePrevious { get; init; }
    public int NewDuplicateGroups { get; init; }
    public int ResolvedDuplicateGroups { get; init; }

    public bool HasPreviousSnapshot { get; init; }
    public bool IsFromSavedSnapshot { get; set; }
    public DateTimeOffset? SnapshotUpdatedAtUtc { get; set; }
    public string SnapshotCharacterName { get; set; } = string.Empty;
}
