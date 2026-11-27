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
    private const long ConfirmationTimeoutMs = 5000;
    private const uint PrismBoxSlotCount = 800;
    private const uint ItemIdModulo = 1_000_000;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly DresserScanner dresserScanner;
    private readonly DuplicateAnalyzer duplicateAnalyzer;
    private readonly Configuration configuration;
    private readonly Queue<RemovalTarget> pending = new();

    private long nextRemovalAt;
    private long confirmationDeadline;
    private RemovalTarget? inFlight;
    private int requestedCount;
    private int removedCount;
    private int skippedCount;
    private bool disposed;

    public bool IsRunning { get; private set; }
    public int RequestedCount => requestedCount;
    public int RemovedCount => removedCount;
    public int SkippedCount => skippedCount;

    /// <summary>
    /// Exact live slot/item selection the user has confirmed. The signature prevents
    /// a changed dresser from silently cleaning a different set of items.
    /// </summary>
    public sealed record CleanupPreview(int CandidateCount, string CandidateSignature);

    public DresserDuplicateRemover(
        IFramework framework,
        IPluginLog log,
        IChatGui chat,
        DresserScanner dresserScanner,
        DuplicateAnalyzer duplicateAnalyzer,
        Configuration configuration)
    {
        this.framework = framework;
        this.log = log;
        this.chat = chat;
        this.dresserScanner = dresserScanner;
        this.duplicateAnalyzer = duplicateAnalyzer;
        this.configuration = configuration;

        framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// Gets an up-to-date cleanup preview without restoring any items.
    /// Saved snapshots are never used for automatic removal.
    /// </summary>
    public CleanupPreview? Preview()
    {
        if (disposed || IsRunning)
        {
            chat.PrintError("[Dispeller] Duplicate cleanup is already running.");
            return null;
        }

        return TryGetTargets(out var targets)
            ? new CleanupPreview(targets.Count, GetCandidateSignature(targets))
            : null;
    }

    public bool Start(CleanupPreview? expectedPreview = null)
    {
        if (disposed || IsRunning)
        {
            chat.PrintError("[Dispeller] Duplicate cleanup is already running.");
            return false;
        }

        if (!TryGetTargets(out var targets))
            return false;

        // Re-scan immediately before executing a confirmed UI preview.
        // Do not restore a different set of items if the dresser changed.
        if (expectedPreview != null &&
            (targets.Count != expectedPreview.CandidateCount ||
             GetCandidateSignature(targets) != expectedPreview.CandidateSignature))
        {
            chat.PrintError("[Dispeller] Dresser contents or cleanup recommendations changed. Click Auto-Clean again to review the updated selection.");
            return false;
        }

        if (targets.Count == 0)
        {
            chat.Print("[Dispeller] No unprotected duplicate-model removal candidates were found.");
            return false;
        }

        pending.Clear();
        inFlight = null;
        foreach (var target in targets)
            pending.Enqueue(target);

        requestedCount = targets.Count;
        removedCount = 0;
        skippedCount = 0;
        nextRemovalAt = Environment.TickCount64;
        IsRunning = true;

        chat.Print($"[Dispeller] Restoring {requestedCount} recommended duplicate{(requestedCount == 1 ? string.Empty : "s")} to inventory. Use /dispeller stop or the Stop Auto-Clean button to cancel.");
        log.Information($"Starting Glamour Dresser duplicate cleanup with {requestedCount} candidates.");
        return true;
    }

    private unsafe bool TryGetTargets(out List<RemovalTarget> targets)
    {
        targets = [];

        if (!dresserScanner.TryRefresh())
        {
            chat.PrintError("[Dispeller] Open the Glamour Dresser before starting Auto-Clean.");
            return false;
        }

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxRequested || !manager->PrismBoxLoaded)
        {
            chat.PrintError("[Dispeller] Glamour Dresser data is not loaded yet. Keep the dresser open and try again.");
            return false;
        }

        var liveItems = dresserScanner.GetDresserItems();
        var analysis = duplicateAnalyzer.Analyze(
            liveItems,
            null,
            configuration.ProtectedItemIds,
            configuration.IgnoredGroupKeys,
            configuration.PreserveDyedItems);

        targets = analysis.Groups
            .Where(group => !group.IsIgnored)
            .SelectMany(group => group.Items
                .Where(item => item.IsRemovalCandidate)
                .Select(item => new RemovalTarget(
                    item.DresserItem.Slot, item.Metadata.ItemId, item.Metadata.Name,
                    item.DresserItem.Stain1, item.DresserItem.Stain2,
                    group.RecommendedKeeper.DresserItem.Slot, group.RecommendedKeeper.Metadata.ItemId)))
            .Where(target => target.PrismBoxIndex < PrismBoxSlotCount &&
                target.KeeperIndex < PrismBoxSlotCount && target.PrismBoxIndex != target.KeeperIndex)
            .DistinctBy(target => target.PrismBoxIndex)
            .ToList();

        return true;
    }

    private static string GetCandidateSignature(IEnumerable<RemovalTarget> targets) =>
        string.Join(";", targets
            .OrderBy(target => target.PrismBoxIndex)
            .Select(target => $"{target.PrismBoxIndex}:{target.ItemId}:{target.Stain1}:{target.Stain2}:{target.KeeperIndex}:{target.KeeperItemId}"));

    public void Cancel()
    {
        if (!IsRunning)
        {
            chat.Print("[Dispeller] No duplicate cleanup is running.");
            return;
        }

        var remaining = pending.Count;
        var awaitingConfirmation = inFlight != null;
        pending.Clear();
        inFlight = null;
        IsRunning = false;

        if (awaitingConfirmation)
            chat.Print("[Dispeller] The last restore request was already sent and may still complete.");
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

        // The native call accepting a request is not proof that the item was restored.
        // Never send another restore until the previous slot is observed empty.
        if (inFlight is { } sent)
        {
            var observed = manager->PrismBoxItemIds[(int)sent.PrismBoxIndex];
            if (observed == 0)
            {
                pending.Dequeue();
                inFlight = null;
                removedCount++;
                log.Information($"Confirmed restored slot {sent.PrismBoxIndex} ({sent.ItemId}).");
                nextRemovalAt = now + RemovalIntervalMs;
                return;
            }

            if (observed % ItemIdModulo != sent.ItemId || now >= confirmationDeadline)
            {
                Abort("The last restore could not be confirmed. No further restore requests were sent.");
                return;
            }

            nextRemovalAt = now + RemovalIntervalMs;
            return;
        }

        var target = pending.Peek();

        // Re-evaluate the current recommendations, including changed protection rules,
        // dye state, ignored groups and the existence of the original keeper.
        if (!TryGetTargets(out var liveTargets))
        {
            Abort("Could not refresh live recommendations.");
            return;
        }

        if (!liveTargets.Contains(target) ||
            manager->PrismBoxItemIds[(int)target.PrismBoxIndex] % ItemIdModulo != target.ItemId ||
            manager->PrismBoxItemIds[(int)target.KeeperIndex] % ItemIdModulo != target.KeeperItemId)
        {
            pending.Dequeue();
            skippedCount++;
            log.Warning($"Skipped dresser slot {target.PrismBoxIndex}: its live recommendation or keeper changed.");
            nextRemovalAt = now + RemovalIntervalMs;
            return;
        }

        if (!manager->RestorePrismBoxItem(target.PrismBoxIndex))
        {
            Abort($"The game rejected restoring {target.Name}. Check inventory space and unique-item restrictions, then try again.");
            return;
        }

        inFlight = target;
        confirmationDeadline = now + ConfirmationTimeoutMs;
        nextRemovalAt = now + RemovalIntervalMs;
    }

    private void Finish()
    {
        IsRunning = false;

        // The scanner will continue polling, but force a final refresh so the UI/snapshot catches up quickly.
        dresserScanner.TryRefresh();

        var skippedText = skippedCount > 0 ? $" Skipped {skippedCount} changed slot{(skippedCount == 1 ? string.Empty : "s")}." : string.Empty;
        chat.Print($"[Dispeller] Duplicate cleanup finished: removed {removedCount} of {requestedCount}.{skippedText}");
        log.Information($"Glamour Dresser duplicate cleanup finished: removed {removedCount}/{requestedCount}, skipped {skippedCount}.");
    }

    private void Abort(string reason)
    {
        var remaining = pending.Count;
        var awaitingConfirmation = inFlight != null;
        pending.Clear();
        inFlight = null;
        IsRunning = false;

        if (awaitingConfirmation)
            chat.Print("[Dispeller] The last restore request may still complete; check the dresser before restarting.");
        chat.PrintError($"[Dispeller] Duplicate cleanup stopped: {reason} Removed {removedCount}; {remaining} candidate{(remaining == 1 ? string.Empty : "s")} not processed.");
        log.Warning($"Glamour Dresser duplicate cleanup aborted: {reason}");
    }

    public void Dispose()
    {
        if (disposed)
            return;

        framework.Update -= OnFrameworkUpdate;
        pending.Clear();
        inFlight = null;
        IsRunning = false;
        disposed = true;
    }

    private sealed record RemovalTarget(
        uint PrismBoxIndex, uint ItemId, string Name, byte Stain1, byte Stain2,
        uint KeeperIndex, uint KeeperItemId);
}
