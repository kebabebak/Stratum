using HarmonyLib;
using RimWorld;
using Verse;

using SolarWeb.Stratum.Utilities;

namespace SolarWeb.Stratum.Patches;

/// <summary>
/// The removal brush accepts a cell that already has a roof, is in bounds, is not fogged, and is not already marked NoRoof.
/// A thick roof still reports MessageNothingCanRemoveThickRoofs. Designator_AreaNoThickRoof overrides CanDesignateCell, so this result does not apply to it.
/// God mode on Designator_AreaNoRoof itself clears the roof with the deconstruct refund. The roof-changed hook then clears the NoRoof bit, so pawns get no job.
/// The thick-roof subclass keeps the pawn designation.
/// </summary>
[HarmonyPatch]
public static class Designator_AreaNoRoof_Patch
{
  [HarmonyPatch(typeof(Designator_AreaNoRoof), nameof(Designator_AreaNoRoof.CanDesignateCell))]
  [HarmonyPostfix]
  public static void Postfix(IntVec3 c, Designator_AreaNoRoof __instance, ref AcceptanceReport __result)
  {
    __result = true;
    if (!c.Roofed(__instance.Map) || !c.InBounds(__instance.Map) || c.Fogged(__instance.Map) || __instance.Map.areaManager.NoRoof[c])
    {
      __result = false;
    }

    var roofDef = __instance.Map.roofGrid.RoofAt(c);
    if (roofDef != null && roofDef.isThickRoof)
    {
      __result = "MessageNothingCanRemoveThickRoofs".Translate();
    }
  }

  [HarmonyPatch(typeof(Designator_AreaNoRoof), nameof(Designator_AreaNoRoof.DesignateSingleCell))]
  [HarmonyPostfix]
  public static void DesignateSingleCell_Postfix(IntVec3 c, Designator_AreaNoRoof __instance)
  {
    if (!DebugSettings.godMode || __instance.GetType() != typeof(Designator_AreaNoRoof))
      return;

    RoofBuildings.RemoveRoofImmediately(__instance.Map, c, refundMaterials: true);
  }
}
