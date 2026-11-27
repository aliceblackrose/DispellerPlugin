using Dispeller.Models;
using Dispeller.Services;

var model = new ModelSignature(1, 0, 0, 0);
var metadata = new Dictionary<uint, ItemMetadata>
{
    [10] = new(10, "One dye", 0, GearSlot.Body, model, 1, false),
    [20] = new(20, "Two dyes", 0, GearSlot.Body, model, 2, false),
    [30] = new(30, "Armoire", 0, GearSlot.Body, model, 2, true),
    [40] = new(40, "Head", 0, GearSlot.Head, model, 2, false),
};
var analyzer = new DuplicateAnalyzer(id => metadata.GetValueOrDefault(id));
DresserItem[] items = [new(0, 10, 0, 0, 0), new(1, 20, 0, 0, 0), new(2, 30, 0, 0, 0), new(3, 40, 0, 0, 0)];
var noProtection = new HashSet<uint>();
var noIgnored = new HashSet<string>();
ScanResult Analyze(IReadOnlyList<DresserItem> source, HashSet<uint>? protectedIds = null,
    HashSet<string>? ignored = null, bool preserveDyes = true, DresserSnapshot? previous = null) =>
    analyzer.Analyze(source, previous, protectedIds ?? noProtection, ignored ?? noIgnored, preserveDyes);
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
var baseline = Analyze(items);
Check(baseline.Groups.Count == 1, "Different equipment slots must not merge.");
Check(baseline.Groups[0].RecommendedKeeper.Metadata.ItemId == 20, "Prefer dye flexibility and non-Armoire keeper.");
Check(baseline.RecommendedRecoverableSlots == 2, "Three matching items yield two candidates.");
var ignored = Analyze(items, ignored: [baseline.Groups[0].Key]);
Check(ignored.RecommendedRecoverableSlots == 0 && ignored.Groups[0].Items.All(i => !i.IsRemovalCandidate),
    "Ignored groups must contribute no cleanup candidates.");
var protectedResult = Analyze(items, protectedIds: [10, 30]);
Check(protectedResult.Groups[0].RecommendedKeeper.IsProtected && protectedResult.RecommendedRecoverableSlots == 1,
    "All manually protected items must survive.");
DresserItem[] dyed = [items[0] with { Stain2 = 5 }, items[1] with { Stain1 = 6 }, items[2]];
var dyeResult = Analyze(dyed);
Check(dyeResult.RecommendedRecoverableSlots == 1 && dyeResult.Groups[0].Items.Where(i => i.IsDyedProtected).All(i => !i.IsRemovalCandidate),
    "Either stored dye protects its item.");
Check(Analyze(dyed, preserveDyes: false).RecommendedRecoverableSlots == 2, "Dye protection can be disabled explicitly.");
var empty = Analyze([]);
Check(empty.TotalDresserItems == 0 && empty.Groups.Count == 0, "Empty scans are valid.");
Check(Analyze([items[0], items[0]]).Groups.Count == 0, "Repeated raw entries must not create duplicate groups.");
var previous = new DresserSnapshot { Items = [new() { Slot = 0, ItemId = 10 }, new() { Slot = 1, ItemId = 20 }] };
var changed = Analyze([items[0], items[3]], previous: previous);
Check(changed.HasPreviousSnapshot && changed.AddedSincePrevious == 1 && changed.RemovedSincePrevious == 1 &&
    changed.ResolvedDuplicateGroups == 1, "Snapshot differences must reflect resolved groups.");
Console.WriteLine("All Dispeller analysis regression checks passed.");
