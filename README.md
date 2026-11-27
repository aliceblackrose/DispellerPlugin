# Dispeller

Dispeller is a Dalamud plugin for Final Fantasy XIV that analyzes the Glamour Dresser for items that share a gear model. It suggests which duplicates can be restored to inventory while keeping at least one recommended piece per model group.

## Auto-Clean

1. Open the **Glamour Dresser** in game, then open Dispeller with `/dispeller`.
2. Review the duplicate groups. Right-click an item to **Protect item** or **Ignore model group** before cleaning.
3. Click **Auto-Clean Duplicates**, review the live eligible-item count and the confirmation, and click **Start Auto-Clean**.
4. Keep the dresser open and leave inventory space available. Watch the restored/skipped count in the toolbar or click **Stop Auto-Clean** to cancel.

Auto-Clean **restores** recommended duplicate items from the dresser to inventory; it does not delete, discard, desynthesize, or automatically move items to the Armoire. It uses fresh live dresser data, skips protected items, ignored groups and (by default) items with stored dyes. It checks the exact selection when starting, revalidates each candidate and its keeper before restoring, and waits for the previous slot to become empty before sending another request. If a restore cannot be confirmed, the queue stops. You can disable dye protection in the main window; do so only after reviewing the dyed items. Search and display filters do not change the cleanup selection. If the selection changes between preview and confirmation, no cleanup starts until you review it again.

The dashboard updates as the live dresser changes, and recoverable-slot totals exclude ignored groups. An empty loaded dresser is a valid snapshot.

Existing commands remain available: `/dispeller clean` starts cleaning immediately, and `/dispeller stop` cancels a running cleanup.

## Build

The project uses .NET 10 and the Dalamud .NET SDK. Pull requests are built by the GitHub Actions workflow in `.github/workflows/pr-build.yml`.

## Regression checks

Run `dotnet run --project tests/Dispeller.Analysis.Tests.csproj --configuration Release` to exercise the recommendation engine without a running game or Dalamud installation. The tests cover keeper selection, stored dyes, ignored groups, protected items, empty scans and snapshot differences. In-game validation is still required for native restore behavior.
