using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Dispeller.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Dispeller.Services;

/// <summary>
/// Reads the glamour dresser while its agent is available and keeps a lightweight in-memory snapshot.
/// The polling is intentionally throttled: this catches same-slot-count replacements without scanning
/// the 8,000-entry client array every framework frame.
/// </summary>
public sealed class DresserScanner : IDisposable
{
    private const long PollIntervalMs = 750;
    private const uint PrismBoxSlotCount = 800;
    private const uint ItemIdModulo = 1_000_000;
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly object syncRoot = new();

    private List<DresserItem> cachedItems = [];
    private ulong lastFingerprint;
    private bool hasFingerprint;
    private long nextPollAt;
    private bool disposed;

    public event Action<IReadOnlyList<DresserItem>>? Updated;

    public DresserScanner(IFramework framework, IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public IReadOnlyList<DresserItem> GetDresserItems()
    {
        lock (syncRoot)
        {
            return cachedItems.ToArray();
        }
    }

    public bool TryRefresh() => TryCapture(force: true);

    public void ClearCache()
    {
        lock (syncRoot)
        {
            cachedItems = [];
            lastFingerprint = 0;
            hasFingerprint = false;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        if (now < nextPollAt)
            return;

        nextPollAt = now + PollIntervalMs;

        try
        {
            TryCapture(force: false);
        }
        catch
        {
            // Framework polling is deliberately quiet. Manual refresh logs actionable failures.
        }
    }

    private unsafe bool TryCapture(bool force)
    {
        try
        {
            var agent = AgentMiragePrismPrismBox.Instance();
            if (agent == null || !agent->IsAddonReady() || agent->Data == null)
                return false;

            var items = new List<DresserItem>(Math.Max(agent->Data->UsedSlots, 0));
            var fingerprint = FnvOffsetBasis;

            foreach (var item in agent->Data->PrismBoxItems)
            {
                // The client array also contains non-storage display/outfit entries. Only 0-799 are
                // actual Glamour Dresser slots that MirageManager.RestorePrismBoxItem can restore.
                if (item.ItemId == 0 || item.Slot >= PrismBoxSlotCount)
                    continue;

                var stain1 = item.Stains[0];
                var stain2 = item.Stains[1];
                var baseItemId = item.ItemId % ItemIdModulo;

                items.Add(new DresserItem(
                    item.Slot,
                    baseItemId,
                    item.IconId,
                    stain1,
                    stain2));

                // Keep the raw item id in the fingerprint so HQ/modifier changes still invalidate the cache.
                fingerprint = Hash(fingerprint, item.Slot);
                fingerprint = Hash(fingerprint, item.ItemId);
                fingerprint = Hash(fingerprint, item.IconId);
                fingerprint = Hash(fingerprint, stain1);
                fingerprint = Hash(fingerprint, stain2);
            }

            fingerprint = Hash(fingerprint, unchecked((uint)agent->Data->UsedSlots));

            bool changed;
            lock (syncRoot)
            {
                changed = !hasFingerprint || fingerprint != lastFingerprint;
                if (!force && !changed)
                    return true;

                cachedItems = items;
                lastFingerprint = fingerprint;
                hasFingerprint = true;
            }

            if (changed)
            {
                log.Debug($"Dresser cache updated: {items.Count} items.");
                Updated?.Invoke(items);
            }
            else if (force)
            {
                log.Debug($"Dresser refresh completed with no changes: {items.Count} items.");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (force)
                log.Error(ex, "Failed to refresh glamour dresser data.");

            return false;
        }
    }

    private static ulong Hash(ulong hash, uint value)
    {
        hash ^= value;
        return hash * FnvPrime;
    }

    private static ulong Hash(ulong hash, byte value)
    {
        hash ^= value;
        return hash * FnvPrime;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        framework.Update -= OnFrameworkUpdate;
        disposed = true;
    }
}
