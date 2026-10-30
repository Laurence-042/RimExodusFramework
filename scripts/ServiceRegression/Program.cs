using RimExodus.Maps;
using Verse;

int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
void Reject(Action action, string message)
{
    bool rejected = false;
    try { action(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, message);
}
foreach (int percent in Enumerable.Range(0, 101))
    foreach (int phase in new[] { -999, -1, 0, 1, 37, 99, 2000000000 })
        foreach (int bucket in new[] { 1, 250, 2000 })
        {
            int actual = Enumerable.Range(1, 100).Count(n => MapRateSchedule.Allows(n * bucket / bucket + phase, percent));
            Check(actual == percent, $"Rate {percent}, phase {phase}, bucket {bucket}: {actual}");
        }
Check(!MapRateSchedule.Allows(int.MinValue, 0) && MapRateSchedule.Allows(int.MaxValue, 100), "Extreme endpoints");
var coordinator = new MapGenerationCoordinator();
MapRuntime.Current = new MapRuntime { Generation = coordinator };
Check(!coordinator.IsBusy, "Initially idle");
var map = new Map();
coordinator.Acquire("RE", () => { Check(coordinator.GeneratingMap == map, "Drain sees bound map"); coordinator.Release("RE"); });
coordinator.Bind("RE", map);
Reject(() => coordinator.Acquire("DV", () => { }), "Concurrent acquire rejected");
Reject(() => coordinator.Release("DV"), "Wrong owner release rejected");
Reject(() => coordinator.EnterExternal(), "Reentrant native generation rejected");
coordinator.Drain();
Check(!coordinator.IsBusy && coordinator.GeneratingMap == null, "Drain completes and clears binding");
coordinator.Acquire("RE", () => coordinator.Drain());
Reject(coordinator.Drain, "Recursive drain rejected");
Check(coordinator.IsBusy, "Failed drain retains ownership");
coordinator.Release("RE");
coordinator.Acquire("RE", () => { });
Reject(coordinator.Drain, "Incomplete drain rejected");
coordinator.Release("RE");
MapGenerator.mapBeingGenerated = map;
Reject(() => coordinator.Acquire("RE", () => { }), "Existing native generator rejected");
MapGenerator.mapBeingGenerated = null;
coordinator.Acquire("RE", () => coordinator.Release("RE"));
using var waiting = new ManualResetEventSlim();
using var admitted = new ManualResetEventSlim();
var preview = Task.Run(() => { waiting.Set(); coordinator.EnterExternal(); admitted.Set(); coordinator.ExitExternal(); });
Check(waiting.Wait(2000), "Preview started");
Check(!admitted.Wait(100), "Preview waits for incremental cleanup");
coordinator.Release("RE");
Check(preview.Wait(2000) && admitted.IsSet && !coordinator.IsBusy, "Preview admitted after release");
Console.WriteLine($"PASS {checks} service checks. Production scheduler/coordinator, minimal engine placeholders; no rendering, Scribe or Harmony runtime validation.");

namespace Verse
{
    public sealed class Map { }
    public static class MapGenerator { public static Map mapBeingGenerated; }
}
namespace RimExodus.Maps
{
    public sealed class MapRuntime
    {
        public static MapRuntime Current;
        public MapGenerationCoordinator Generation;
    }
}
