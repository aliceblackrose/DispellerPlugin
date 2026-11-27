using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dispeller.Models;

namespace Dispeller;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public HashSet<uint> ProtectedItemIds { get; set; } = [];
    public HashSet<string> IgnoredGroupKeys { get; set; } = [];

    public GearSlot SlotFilter { get; set; } = GearSlot.All;
    public bool ArmoireOnly { get; set; }
    public bool HideIgnoredGroups { get; set; } = true;
    public bool ShowModelIds { get; set; }
    public bool PreserveDyedItems { get; set; } = true;
    public int MinimumGroupSize { get; set; } = 2;
    public ResultSortMode SortMode { get; set; } = ResultSortMode.Slot;

    public void Normalize()
    {
        ProtectedItemIds ??= [];
        IgnoredGroupKeys ??= [];
        MinimumGroupSize = Math.Clamp(MinimumGroupSize, 2, 5);
        if (!Enum.IsDefined(SlotFilter)) SlotFilter = GearSlot.All;
        if (!Enum.IsDefined(SortMode)) SortMode = ResultSortMode.Slot;
    }

    public void Save() =>
        Plugin.PluginInterface.SavePluginConfig(this);
}

public enum ResultSortMode
{
    Slot = 0,
    LargestGroups = 1,
    MostRecoverable = 2,
    Name = 3,
}
