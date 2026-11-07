# Dispeller

Dispeller is a Dalamud plugin for Final Fantasy XIV that analyzes the Glamour Dresser for items that share a gear model. It suggests which duplicates can be restored to inventory while keeping at least one recommended piece per model group.

## Auto-Clean

1. Open the **Glamour Dresser** in game, then open Dispeller with `/dispeller`.
2. Review the duplicate groups. Right-click an item to **Protect item** or **Ignore model group** before cleaning.
3. Click **Auto-Clean Duplicates**, review the live eligible-item count and the confirmation, and click **Start Auto-Clean**.
4. Keep the dresser open and leave inventory space available. Watch the restored/skipped count in the toolbar or click **Stop Auto-Clean** to cancel.

Auto-Clean **restores** recommended duplicate items from the dresser to inventory; it does not delete, discard, desynthesize, or automatically move items to the Armoire. It uses fresh live dresser data, skips protected items and ignored groups, and checks the selected dresser slots again when starting. Search and display filters do not change the cleanup selection. If the selection changes between preview and confirmation, no cleanup starts until you review it again.

Existing commands remain available: `/dispeller clean` starts cleaning immediately, and `/dispeller stop` cancels a running cleanup.

## Build

The project uses .NET 10 and the Dalamud .NET SDK. Pull requests are built by the GitHub Actions workflow in `.github/workflows/pr-build.yml`.
