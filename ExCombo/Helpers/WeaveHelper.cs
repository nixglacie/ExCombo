using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo.Helpers;

internal static unsafe class WeaveHelper {
    // Effective per-flow tuning (see FlowExecutor.Tuning), loaded before each flow is evaluated.
    private static float BaseAnimLock    => Tuning.AnimLock;
    private static float BaseActionQueue => Tuning.Queue;

    private static float RemainingGCD {
        get {
            var d = ActionManager.Instance()->GetRecastGroupDetail(57);
            return d->Total - d->Elapsed;
        }
    }
    private static float GCDTotal  => ActionManager.Instance()->GetRecastGroupDetail(57)->Total;
    private static float AnimLock  => ActionManager.Instance()->AnimationLock;

    public static bool CanWeave(int weavedCount, int maxWeaves = 2) =>
        weavedCount < maxWeaves
        && AnimLock <= BaseAnimLock
        && RemainingGCD > (BaseAnimLock + AnimLock + BaseActionQueue);

    public static bool CanDelayedWeave(int weavedCount, int maxWeaves = 2,
            float weaveStart = 1.25f, float weaveEnd = 0.6f) {
        var halfGCD = GCDTotal * 0.5f;
        var rem     = RemainingGCD;
        return weavedCount < maxWeaves
            && AnimLock <= BaseActionQueue
            && rem > (weaveEnd + AnimLock)
            && rem <= (weaveStart > halfGCD ? halfGCD : weaveStart);
    }

    public static float GCDElapsed     => ActionManager.Instance()->GetRecastGroupDetail(57)->Elapsed;

    public static bool IsGcdRolling    => GCDTotal > 0;

    public static bool IsWeaveWindowExpired() =>
        GCDTotal > 0 && RemainingGCD < (BaseAnimLock + BaseActionQueue);
}
