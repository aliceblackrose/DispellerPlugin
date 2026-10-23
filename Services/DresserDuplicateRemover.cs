using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Dispeller.Models;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Dispeller.Services;

/// <summary>
/// Restores the analyzer's recommended duplicate-model removal candidates from the Glamour Dresser
/// back into the player's inventory, one item at a time.
/// </summary>
public sealed class DresserDuplicateRemover : IDisposable
{
    private const long RemovalIntervalMs = 650;
    private const uint PrismBoxSlotCount = 800;
    private const uint ItemIdModulo = 1_000_000;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly DresserScanner dresserScanner;
    private readonly DuplicateAnalyzer duplicateAnalyzer;
    private readonly SnapshotService snapshotService;
    private readonly Configuration configuration;
    private readonly Queue<RemovalTarget> pending = new();

    private long nextRemovalAt;
    private int requestedCount;
    private int removedCount;
    private int skippedCount;
    private bool disposed;

    public bool IsRunning { get; private set; }

    public DresserDuplicateRemover(
        IFramework framework,
        IPluginLog log,
        IChatGui chat,
        DresserScanner dresserScanner,
        DuplicateAnalyzer duplicateAnalyzer,
        SnapshotService snapshotService,
        Configuration configuration)
    {
        this.framework = framework;
        this.log = log;
        this.chat = chat;
        this.dresserScanner = dresserScanner;
        this.duplicateAnalyzer = duplicateAnalyzer;
        this.snapshotService = snapshotService;
        this.configuration = configuration;

        framework.Update += OnFrameworkUpdate;
    }

    public unsafe bool Start()
    {
        if (IsRunning)
        {
            chat.PrintError("[Dispeller] Duplicate cleanup is already running.");
            return false;
        }

        if (!dresserScanner.TryRefresh())
        {
            chat.PrintError("[Dispeller] Open the Glamour Dresser, then run /dispeller clean again.");
            return false;
        }

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxRequested || !manager->PrismBoxLoaded)
        {
            chat.PrintError("[Dispeller] Glamour Dresser data is not loaded yet. Keep the dresser open and try again.");
            return false;
        }

        var liveItems = dresserScanner.GetDresserItems();
        if (liveItems.Count == 0)
        {
            chat.PrintError("[Dispeller] No live Glamour Dresser items were found.");
            return false;
        }

        var analysis = duplicateAnalyzer.Analyze(
            liveItems,
            snapshotService.GetPrevious(),
            configuration);

        var targets = analysis.Groups
            .Where(group => !group.IsIgnored)
            .SelectMany(group => group.Items)
            .Where(item => item.IsRemovalCandidate)
            .Select(item => new RemovalTarget(item.DresserItem.Slot, item.Metadata.ItemId, item.Metadata.Name))
            .Where(target => target.PrismBoxIndex < PrismBoxSlotCount)
            .DistinctBy(target => target.PrismBoxIndex)
            .ToList();

        if (targets.Count == 0)
        {
            chat.Print("[Dispeller] No unprotected duplicate-model removal candidates were found.");
            return false;
        }

        pending.Clear();
        foreach (var target in targets)
            pending.Enqueue(target);

        requestedCount = targets.Count;
        removedCount = 0;
        skippedCount = 0;
        nextRemovalAt = Environment.TickCount64;
        IsRunning = true;

        chat.Print($"[Dispeller] Removing {requestedCount} recommended duplicate{(requestedCount == 1 ? string.Empty : "s")} from the Glamour Dresser. Use /dispeller stop to cancel.");
        log.Information($"Starting Glamour Dresser duplicate cleanup with {requestedCount} candidates.");
        return true;
    }

    public void Cancel()
    {
        if (!IsRunning)
        {
            chat.Print("[Dispeller] No duplicate cleanup is running.");
            return;
        }

        var remaining = pending.Count;
        pending.Clear();
        IsRunning = false;

        chat.Print($"[Dispeller] Duplicate cleanup stopped. Removed {removedCount}; {remaining} candidate{(remaining == 1 ? string.Empty : "s")} left queued.");
        log.Information($"Glamour Dresser duplicate cleanup cancelled after {removedCount} removals.");
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning)
            return;

        var now = Environment.TickCount64;
        if (now < nextRemovalAt)
            return;

        if (pending.Count == 0)
        {
            Finish();
            return;
        }

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxRequested || !manager->PrismBoxLoaded)
        {
            Abort("Glamour Dresser data became unavailable. Keep the dresser open while cleaning duplicates.");
            return;
        }

        var target = pending.Peek();
        if (target.PrismBoxIndex >= PrismBoxSlotCount)
        {
            pending.Dequeue();
            skippedCount++;
            nextRemovalAt = now + RemovalIntervalMs;
            return;
        }

        var rawItemId = manager->PrismBoxItemIds[(int)target.PrismBoxIndex];
        var currentItemId = rawItemId % ItemIdModulo;

        if (currentItemId != target.ItemId)
        {
            pending.Dequeue();
            skippedCount++;
            log.Warning(
                $"Skipped stale dresser slot {target.PrismBoxIndex}: expected item {target.ItemId}, found {currentItemId}.");
            nextRemovalAt = now + RemovalIntervalMs;
            return;
        }

        if (!manager->RestorePrismBoxItem(target.PrismBoxIndex))
        {
            Abort($"The game rejected restoring {target.Name}. Check inventory space and unique-item restrictions, then try again.");
            return;
        }

        pending.Dequeue();
        removedCount++;
        log.Information($"Restored duplicate '{target.Name}' ({target.ItemId}) from dresser slot {target.PrismBoxIndex}.");
        nextRemovalAt = now + RemovalIntervalMs;
    }

    private void Finish()
    {
        IsRunning = false;

        // The scanner will continue polling, but force a final refresh so the UI/snapshot catches up quickly.
        dresserScanner.TryRefresh();

        var skippedText = skippedCount > 0 ? $" Skipped {skippedCount} stale slot{(skippedCount == 1 ? string.Empty : "s")}." : string.Empty;
        chat.Print($"[Dispeller] Duplicate cleanup finished: removed {removedCount} of {requestedCount}.{skippedText}");
        log.Information($"Glamour Dresser duplicate cleanup finished: removed {removedCount}/{requestedCount}, skipped {skippedCount}.");
    }

    private void Abort(string reason)
    {
        var remaining = pending.Count;
        pending.Clear();
        IsRunning = false;

        chat.PrintError($"[Dispeller] Duplicate cleanup stopped: {reason} Removed {removedCount}; {remaining} candidate{(remaining == 1 ? string.Empty : "s")} not processed.");
        log.Warning($"Glamour Dresser duplicate cleanup aborted: {reason}");
    }

    public void Dispose()
    {
        if (disposed)
            return;

        framework.Update -= OnFrameworkUpdate;
        pending.Clear();
        IsRunning = false;
        disposed = true;
    }

    private sealed record RemovalTarget(uint PrismBoxIndex, uint ItemId, string Name);
}
