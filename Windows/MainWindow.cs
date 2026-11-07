using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dispeller.Models;
using Dispeller.Services;

namespace Dispeller.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private readonly List<DresserItem> currentItems = [];
    private ScanResult? result;

    private string searchText = string.Empty;
    private string statusMessage = "Ready.";
    private bool initialLoadAttempted;
    private bool dresserChangedSinceAnalysis;
    private DresserDuplicateRemover.CleanupPreview? pendingAutoCleanPreview;

    private const string AutoCleanPopupId = "Confirm Auto-Clean##Dispeller";

    private static readonly Vector4 DarkPurple = new(0.28f, 0.20f, 0.45f, 1.00f);
    private static readonly Vector4 SoftMagenta = new(0.78f, 0.37f, 0.64f, 1.00f);
    private static readonly Vector4 LightMagenta = new(0.88f, 0.47f, 0.74f, 1.00f);
    private static readonly Vector4 LightPurple = new(0.65f, 0.60f, 0.80f, 1.00f);
    private static readonly Vector4 LightMint = new(0.50f, 0.85f, 0.75f, 1.00f);
    private static readonly Vector4 BrightWhite = Vector4.One;
    private static readonly Vector4 MutedText = new(0.72f, 0.72f, 0.76f, 1.00f);

    public MainWindow(Plugin plugin)
        : base("Dispeller - Glamour Dresser Analyzer##Dispeller2", ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public void NotifyDresserUpdated()
    {
        dresserChangedSinceAnalysis = true;
    }

    public void NotifyCharacterChanged()
    {
        currentItems.Clear();
        result = null;
        initialLoadAttempted = false;
        dresserChangedSinceAnalysis = false;
        pendingAutoCleanPreview = null;
        statusMessage = "Character changed. Loading this character's dresser snapshot...";
    }

    public override void Draw()
    {
        if (!initialLoadAttempted && Plugin.PlayerState.IsLoaded)
        {
            initialLoadAttempted = true;
            LoadBestAvailableData(refreshIfOpen: false);
        }

        DrawHeader();
        DrawToolbar();
        DrawAutoCleanConfirmation();
        DrawStatus();

        if (result != null)
        {
            DrawDashboard(result);
            DrawFilters();
            DrawResults(result);
        }
        else
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "Open your Glamour Dresser once to create a persistent snapshot, then Dispeller can analyze it even after the dresser is closed or after a restart.");
        }
    }

    private void DrawHeader()
    {
        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        drawList.AddRectFilledMultiColor(
            cursor,
            cursor + new Vector2(width, 56),
            ImGui.ColorConvertFloat4ToU32(SoftMagenta),
            ImGui.ColorConvertFloat4ToU32(DarkPurple),
            ImGui.ColorConvertFloat4ToU32(DarkPurple),
            ImGui.ColorConvertFloat4ToU32(SoftMagenta));

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 14);
        ImGui.PushStyleColor(ImGuiCol.Text, BrightWhite);
        ImGui.SetWindowFontScale(1.18f);
        ImGui.TextUnformatted("✨ Dispeller 2.2");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 28);
        ImGui.Spacing();
    }

    private void DrawToolbar()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, SoftMagenta);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, LightMagenta);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, DarkPurple);

        if (ImGui.Button("Scan / Refresh Dresser"))
            LoadBestAvailableData(refreshIfOpen: true);

        ImGui.PopStyleColor(3);

        ImGui.SameLine();

        if (ImGui.Button("Re-analyze"))
            AnalyzeCurrentItems();

        ImGui.SameLine();
        if (plugin.DuplicateRemover.IsRunning)
        {
            ImGui.TextUnformatted(
                $"Auto-Clean: {plugin.DuplicateRemover.RemovedCount}/{plugin.DuplicateRemover.RequestedCount} restored, " +
                $"{plugin.DuplicateRemover.SkippedCount} skipped.");
            ImGui.SameLine();
            if (ImGui.Button("Stop Auto-Clean"))
            {
                plugin.DuplicateRemover.Cancel();
                statusMessage = "Auto-Clean stopped.";
            }
        }
        else if (ImGui.Button("Auto-Clean Duplicates"))
        {
            pendingAutoCleanPreview = plugin.DuplicateRemover.Preview();
            if (pendingAutoCleanPreview is { CandidateCount: > 0 } preview)
            {
                LoadBestAvailableData(refreshIfOpen: false);
                statusMessage = $"Auto-Clean preview: {preview.CandidateCount} eligible dresser items.";
                ImGui.OpenPopup(AutoCleanPopupId);
            }
            else
            {
                statusMessage = pendingAutoCleanPreview == null
                    ? "Auto-Clean requires a loaded, open Glamour Dresser."
                    : "No eligible unprotected duplicates found to restore.";
            }
        }

        if (dresserChangedSinceAnalysis)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, LightMint);
            ImGui.TextUnformatted("Dresser changed - refresh recommended.");
            ImGui.PopStyleColor();
        }
    }

    private void DrawAutoCleanConfirmation()
    {
        if (!ImGui.BeginPopupModal(AutoCleanPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (pendingAutoCleanPreview is { } preview && !plugin.DuplicateRemover.IsRunning)
        {
            ImGui.TextUnformatted($"Restore {preview.CandidateCount} recommended duplicate item(s) to inventory?");
            ImGui.Spacing();
            ImGui.TextWrapped("Auto-Clean restores items from the Glamour Dresser to your inventory. It does not discard them.");
            ImGui.TextWrapped("Keep the dresser open and make sure you have enough inventory space.");
            ImGui.TextWrapped("Protected items and ignored model groups are excluded. Display filters do not limit Auto-Clean.");
            ImGui.Spacing();

            if (ImGui.Button("Start Auto-Clean"))
            {
                var started = plugin.DuplicateRemover.Start(preview);
                statusMessage = started
                    ? $"Auto-Clean started for {preview.CandidateCount} items. Use Stop Auto-Clean to cancel."
                    : "Auto-Clean did not start. Check chat for details, then preview again.";
                pendingAutoCleanPreview = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingAutoCleanPreview = null;
                ImGui.CloseCurrentPopup();
            }
        }
        else
        {
            ImGui.TextWrapped("The preview is no longer valid. Reopen Auto-Clean to review live dresser items.");
            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawStatus()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, MutedText);
        ImGui.TextWrapped(statusMessage);
        ImGui.PopStyleColor();
        ImGui.Separator();
    }

    private void DrawDashboard(ScanResult scan)
    {
        ImGui.Spacing();

        ImGui.TextUnformatted($"Dresser items: {scan.TotalDresserItems}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"   Duplicate groups: {scan.Groups.Count}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"   Duplicate items: {scan.DuplicateItemCount}");

        ImGui.PushStyleColor(ImGuiCol.Text, LightMint);
        ImGui.TextUnformatted($"Recommended recoverable slots: {scan.RecommendedRecoverableSlots}");
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextUnformatted($"   Armoire-eligible duplicates: {scan.ArmoireEligibleDuplicateItems}");

        if (scan.HasPreviousSnapshot)
        {
            ImGui.TextUnformatted(
                $"Since previous snapshot: +{scan.AddedSincePrevious} / -{scan.RemovedSincePrevious} items, " +
                $"+{scan.NewDuplicateGroups} / -{scan.ResolvedDuplicateGroups} duplicate groups");
        }

        if (scan.IsFromSavedSnapshot && scan.SnapshotUpdatedAtUtc.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, LightPurple);
            var localTime = scan.SnapshotUpdatedAtUtc.Value.ToLocalTime();
            var character = string.IsNullOrWhiteSpace(scan.SnapshotCharacterName)
                ? string.Empty
                : $" for {scan.SnapshotCharacterName}";

            ImGui.TextWrapped($"Using saved snapshot{character} from {localTime:g}.");
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.Separator();
    }

    private void DrawFilters()
    {
        ImGui.Spacing();

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Search", ref searchText, 128);

        ImGui.SameLine();
        DrawSlotCombo();

        ImGui.SameLine();
        DrawSortCombo();

        ImGui.SameLine();
        DrawMinimumGroupCombo();

        var armoireOnly = plugin.Configuration.ArmoireOnly;
        if (ImGui.Checkbox("Armoire only", ref armoireOnly))
        {
            plugin.Configuration.ArmoireOnly = armoireOnly;
            plugin.Configuration.Save();
        }

        ImGui.SameLine();

        var hideIgnored = plugin.Configuration.HideIgnoredGroups;
        if (ImGui.Checkbox("Hide ignored", ref hideIgnored))
        {
            plugin.Configuration.HideIgnoredGroups = hideIgnored;
            plugin.Configuration.Save();
        }

        ImGui.SameLine();

        var showModelIds = plugin.Configuration.ShowModelIds;
        if (ImGui.Checkbox("Show model IDs", ref showModelIds))
        {
            plugin.Configuration.ShowModelIds = showModelIds;
            plugin.Configuration.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button("Reset filters"))
        {
            searchText = string.Empty;
            plugin.Configuration.SlotFilter = GearSlot.All;
            plugin.Configuration.ArmoireOnly = false;
            plugin.Configuration.MinimumGroupSize = 2;
            plugin.Configuration.Save();
        }

        if (plugin.Configuration.ProtectedItemIds.Count > 0 || plugin.Configuration.IgnoredGroupKeys.Count > 0)
        {
            ImGui.SameLine();

            if (ImGui.Button($"Protected: {plugin.Configuration.ProtectedItemIds.Count}"))
                ImGui.OpenPopup("protected_actions");

            ImGui.SameLine();

            if (ImGui.Button($"Ignored: {plugin.Configuration.IgnoredGroupKeys.Count}"))
                ImGui.OpenPopup("ignored_actions");

            if (ImGui.BeginPopup("protected_actions"))
            {
                if (ImGui.MenuItem("Clear all protected items"))
                {
                    plugin.Configuration.ProtectedItemIds.Clear();
                    plugin.Configuration.Save();
                    AnalyzeCurrentItems();
                }

                ImGui.EndPopup();
            }

            if (ImGui.BeginPopup("ignored_actions"))
            {
                if (ImGui.MenuItem("Clear all ignored groups"))
                {
                    plugin.Configuration.IgnoredGroupKeys.Clear();
                    plugin.Configuration.Save();
                    AnalyzeCurrentItems();
                }

                ImGui.EndPopup();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
    }

    private void DrawSlotCombo()
    {
        var current = plugin.Configuration.SlotFilter;

        ImGui.SetNextItemWidth(130);
        if (!ImGui.BeginCombo("Slot", current.DisplayName()))
            return;

        foreach (var slot in Enum.GetValues<GearSlot>())
        {
            if (slot == GearSlot.Unknown)
                continue;

            var selected = slot == current;
            if (ImGui.Selectable(slot.DisplayName(), selected))
            {
                plugin.Configuration.SlotFilter = slot;
                plugin.Configuration.Save();
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawSortCombo()
    {
        var current = plugin.Configuration.SortMode;
        var preview = GetSortName(current);

        ImGui.SetNextItemWidth(150);
        if (!ImGui.BeginCombo("Sort", preview))
            return;

        foreach (var mode in Enum.GetValues<ResultSortMode>())
        {
            var selected = mode == current;
            if (ImGui.Selectable(GetSortName(mode), selected))
            {
                plugin.Configuration.SortMode = mode;
                plugin.Configuration.Save();
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawMinimumGroupCombo()
    {
        var current = Math.Clamp(plugin.Configuration.MinimumGroupSize, 2, 5);

        ImGui.SetNextItemWidth(95);
        if (!ImGui.BeginCombo("Min size", $"{current}+"))
            return;

        for (var size = 2; size <= 5; size++)
        {
            var selected = size == current;
            if (ImGui.Selectable($"{size}+", selected))
            {
                plugin.Configuration.MinimumGroupSize = size;
                plugin.Configuration.Save();
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawResults(ScanResult scan)
    {
        var groups = GetVisibleGroups(scan).ToList();

        ImGui.Spacing();
        ImGui.TextUnformatted($"Showing {groups.Count} of {scan.Groups.Count} duplicate groups.");

        if (groups.Count == 0)
        {
            ImGui.TextWrapped("No duplicate-model groups match the current filters.");
            return;
        }

        using var child = ImRaii.Child("Results", Vector2.Zero, false);
        if (!child.Success)
            return;

        foreach (var group in groups)
            DrawGroup(group);
    }

    private IEnumerable<DuplicateGroup> GetVisibleGroups(ScanResult scan)
    {
        IEnumerable<DuplicateGroup> groups = scan.Groups;

        if (plugin.Configuration.HideIgnoredGroups)
            groups = groups.Where(group => !group.IsIgnored);

        if (plugin.Configuration.SlotFilter != GearSlot.All)
            groups = groups.Where(group => group.Slot == plugin.Configuration.SlotFilter);

        if (plugin.Configuration.ArmoireOnly)
            groups = groups.Where(group => group.Items.Any(item => item.Metadata.CanGoInArmoire));

        if (plugin.Configuration.MinimumGroupSize > 2)
            groups = groups.Where(group => group.Items.Count >= plugin.Configuration.MinimumGroupSize);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            groups = groups.Where(group =>
                group.Items.Any(item =>
                    item.Metadata.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    item.Metadata.ItemId.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase)));
        }

        groups = plugin.Configuration.SortMode switch
        {
            ResultSortMode.LargestGroups => groups
                .OrderByDescending(group => group.Items.Count)
                .ThenBy(group => (int)group.Slot)
                .ThenBy(group => group.Model.Primary)
                .ThenBy(group => group.Model.Secondary)
                .ThenBy(group => group.Model.Variant)
                .ThenBy(group => group.Model.Extra),

            ResultSortMode.MostRecoverable => groups
                .OrderByDescending(group => group.RemovalCandidateCount)
                .ThenByDescending(group => group.Items.Count)
                .ThenBy(group => (int)group.Slot),

            ResultSortMode.Name => groups
                .OrderBy(group => group.Items[0].Metadata.Name, StringComparer.OrdinalIgnoreCase),

            _ => groups
                .OrderBy(group => (int)group.Slot)
                .ThenByDescending(group => group.Items.Count)
                .ThenBy(group => group.Model.Primary)
                .ThenBy(group => group.Model.Secondary)
                .ThenBy(group => group.Model.Variant)
                .ThenBy(group => group.Model.Extra),
        };

        return groups;
    }

    private void DrawGroup(DuplicateGroup group)
    {
        using var id = ImRaii.PushId(group.Key);

        var color = GetColorForSlot(group.Slot);
        ImGui.PushStyleColor(ImGuiCol.Header, color);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(color.X, color.Y, color.Z, 0.88f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, color);

        var ignoredMarker = group.IsIgnored ? " [ignored]" : string.Empty;
        var label =
            $"{group.Slot.DisplayName()} • {group.Items.Count} items • {group.RemovalCandidateCount} recommended removal{(group.RemovalCandidateCount == 1 ? string.Empty : "s")}{ignoredMarker}##header";

        var open = ImGui.CollapsingHeader(label);
        ImGui.PopStyleColor(3);

        if (!open)
            return;

        if (plugin.Configuration.ShowModelIds)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, MutedText);
            ImGui.TextUnformatted($"Model: {group.Model}");
            ImGui.PopStyleColor();
        }

        if (group.RemovalCandidateCount > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, LightMint);
            ImGui.TextUnformatted(
                $"Suggestion: keep {group.RecommendedKeeper.Metadata.Name}; consider moving/removing {group.RemovalCandidateCount} duplicate{(group.RemovalCandidateCount == 1 ? string.Empty : "s")}.");
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextWrapped("No safe removal suggestion because the remaining duplicates are protected.");
        }

        foreach (var item in group.Items)
            DrawItem(group, item);

        ImGui.Spacing();
    }

    private void DrawItem(DuplicateGroup group, AnalyzedItem item)
    {
        using var id = ImRaii.PushId($"{item.Metadata.ItemId}-{item.DresserItem.Slot}");

        var icon = GetIcon(item.Metadata.IconId);
        if (icon != null)
        {
            ImGui.Image(icon.Handle, new Vector2(32, 32));
            ImGui.SameLine();
        }
        else
        {
            ImGui.Dummy(new Vector2(32, 32));
            ImGui.SameLine();
        }

        var prefix = item.IsRecommendedKeeper
            ? "★ KEEP"
            : item.IsRemovalCandidate
                ? "↳ candidate"
                : "🔒 protected";

        var textColor = item.IsRecommendedKeeper
            ? LightMint
            : item.IsProtected
                ? LightPurple
                : BrightWhite;

        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        ImGui.TextUnformatted($"{prefix}  {item.Metadata.Name}");
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            DrawRecommendationTooltip(item);

        DrawItemContextMenu(group, item);

        ImGui.SameLine();

        if (item.Metadata.DyeCount > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, MutedText);
            ImGui.TextUnformatted($"  {item.Metadata.DyeCount} dye");
            ImGui.PopStyleColor();
            ImGui.SameLine();
        }

        if (item.Metadata.CanGoInArmoire)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, SoftMagenta);
            ImGui.TextUnformatted("  [Armoire]");
            ImGui.PopStyleColor();
        }
    }

    private void DrawRecommendationTooltip(AnalyzedItem item)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Item ID: {item.Metadata.ItemId}");
        ImGui.TextUnformatted($"Model: {item.Metadata.Model}");
        ImGui.Separator();

        foreach (var reason in item.RecommendationReasons)
            ImGui.TextWrapped($"• {reason}");

        ImGui.EndTooltip();
    }

    private void DrawItemContextMenu(DuplicateGroup group, AnalyzedItem item)
    {
        if (!ImGui.BeginPopupContextItem("item_context"))
            return;

        if (ImGui.MenuItem("Copy item name"))
            ImGui.SetClipboardText(item.Metadata.Name);

        if (ImGui.MenuItem("Copy item ID"))
            ImGui.SetClipboardText(item.Metadata.ItemId.ToString());

        if (ImGui.MenuItem("Copy model ID"))
            ImGui.SetClipboardText(item.Metadata.Model.ToString());

        ImGui.Separator();

        var protectLabel = item.IsProtected ? "Unprotect item" : "Protect item";
        if (ImGui.MenuItem(protectLabel))
        {
            ToggleProtected(item.Metadata.ItemId);
            ImGui.CloseCurrentPopup();
        }

        var ignoreLabel = group.IsIgnored ? "Unignore model group" : "Ignore model group";
        if (ImGui.MenuItem(ignoreLabel))
        {
            ToggleIgnored(group.Key);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void ToggleProtected(uint itemId)
    {
        if (!plugin.Configuration.ProtectedItemIds.Add(itemId))
            plugin.Configuration.ProtectedItemIds.Remove(itemId);

        plugin.Configuration.Save();
        AnalyzeCurrentItems();
    }

    private void ToggleIgnored(string groupKey)
    {
        if (!plugin.Configuration.IgnoredGroupKeys.Add(groupKey))
            plugin.Configuration.IgnoredGroupKeys.Remove(groupKey);

        plugin.Configuration.Save();
        AnalyzeCurrentItems();
    }

    private IDalamudTextureWrap? GetIcon(uint iconId)
    {
        if (iconId == 0)
            return null;

        return Plugin.TextureProvider
            .GetFromGameIcon(new GameIconLookup(iconId))
            .GetWrapOrDefault();
    }

    private void LoadBestAvailableData(bool refreshIfOpen)
    {
        dresserChangedSinceAnalysis = false;

        var refreshed = refreshIfOpen && plugin.DresserScanner.TryRefresh();

        var liveItems = plugin.DresserScanner.GetDresserItems();
        if (liveItems.Count > 0)
        {
            currentItems.Clear();
            currentItems.AddRange(liveItems);

            // Capture is idempotent; this also covers data cached before player state was available.
            plugin.SnapshotService.Capture(currentItems);

            AnalyzeCurrentItems();

            statusMessage = refreshed
                ? $"Refreshed and analyzed {currentItems.Count} live dresser items."
                : $"Analyzed {currentItems.Count} cached dresser items.";

            return;
        }

        var snapshot = plugin.SnapshotService.GetCurrent();
        if (snapshot != null && snapshot.Items.Count > 0)
        {
            currentItems.Clear();
            currentItems.AddRange(snapshot.ToDresserItems());

            AnalyzeCurrentItems();

            if (result != null)
            {
                result.IsFromSavedSnapshot = true;
                result.SnapshotUpdatedAtUtc = snapshot.UpdatedAtUtc;
                result.SnapshotCharacterName = snapshot.CharacterName;
            }

            statusMessage =
                "The Glamour Dresser is not currently available, so Dispeller loaded your saved per-character snapshot.";

            return;
        }

        result = null;
        currentItems.Clear();

        statusMessage = refreshIfOpen
            ? "No dresser data is available. Open your Glamour Dresser, then click Scan / Refresh Dresser."
            : "No saved dresser snapshot exists yet. Open your Glamour Dresser once to create one.";
    }

    private void AnalyzeCurrentItems()
    {
        if (currentItems.Count == 0)
        {
            result = null;
            return;
        }

        result = plugin.DuplicateAnalyzer.Analyze(
            currentItems,
            plugin.SnapshotService.GetPrevious(),
            plugin.Configuration);

        dresserChangedSinceAnalysis = false;
    }

    private static string GetSortName(ResultSortMode mode) => mode switch
    {
        ResultSortMode.LargestGroups => "Largest groups",
        ResultSortMode.MostRecoverable => "Most recoverable",
        ResultSortMode.Name => "Item name",
        _ => "Slot",
    };

    private static Vector4 GetColorForSlot(GearSlot slot)
    {
        if (slot.IsWeapon())
            return LightMint;

        if (slot.IsAccessory())
            return LightPurple;

        return LightMagenta;
    }
}
