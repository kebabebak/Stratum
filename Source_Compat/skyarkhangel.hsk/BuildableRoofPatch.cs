using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using SolarWeb.Stratum.AI.Designators;
using SolarWeb.Stratum.DefModExtensions;
using SolarWeb.Stratum.Things;
using SolarWeb.Stratum.UI;
using SolarWeb.Stratum.Utilities;
using Verse;

namespace SolarWeb.Stratum.HSK;

[StaticConstructorOnStartup]
[HarmonyPatch]
public static class BuildableRoofPatch
{
  private const string ThickRoofRemovalResearchDefName = "ThickStoneRoofRemoval";
  private const string ThickRoofRemovalResearchRequiredKey = "SolarWeb_Stratum_ThickRoofRemovalResearchRequired";

  static BuildableRoofPatch()
  {
    new Harmony("regex.Stratum.HSK").PatchAll(typeof(BuildableRoofPatch).Assembly);
  }

  private static bool IsThickRoofRemovalResearched()
  {
    if (DebugSettings.godMode)
    {
      return true;
    }

    return DefDatabase<ResearchProjectDef>
      .GetNamedSilentFail(ThickRoofRemovalResearchDefName)?.IsFinished == true;
  }

  private static bool IsPlayerBuildable(RoofDef? roofDef, BuildableRoofExtension? extension)
  {
    if (roofDef == null || extension?.buildableDef == null)
    {
      return false;
    }

    // Stratum also uses BuildableRoofExtension to supply graphics and stats for
    // natural roofs. A missing designation category means that the roof was not
    // registered as a player construction option.
    if (roofDef.isNatural || extension.designationCategory == null)
    {
      return false;
    }

    var prerequisites = extension.buildableDef.researchPrerequisites;
    // Designator_Build.Visible returns true in god mode before IsResearchFinished, and DesignateSingleCell then places the thing.
    // This prefix is the only research gate left on a roof cell, so god mode has to skip it or the visible button accepts nothing.
    if (!DebugSettings.godMode && prerequisites != null)
    {
      foreach (var prerequisite in prerequisites)
      {
        if (!prerequisite.IsFinished)
        {
          return false;
        }
      }
    }

    return true;
  }

  [HarmonyPatch(
    typeof(TrashUtility),
    nameof(TrashUtility.ShouldTrashBuilding),
    new[] { typeof(Building) })]
  [HarmonyPrefix]
  public static bool IgnoreRoofFramesForRaidTrash(Building b, ref bool __result)
  {
    if (b is not RoofFrame)
    {
      return true;
    }

    __result = false;
    return false;
  }

  [HarmonyPatch(typeof(SelectedRoof), nameof(SelectedRoof.GetGizmos))]
  [HarmonyPostfix]
  public static IEnumerable<Gizmo> UpdateRoofCommands(
    IEnumerable<Gizmo> __result,
    SelectedRoof __instance)
  {
    foreach (var gizmo in __result)
    {
      if (gizmo is Command_BuildCopyRoof command)
      {
        var roofDef = command.roofDef;
        var extension = roofDef?.GetModExtension<BuildableRoofExtension>();
        if (!IsPlayerBuildable(roofDef, extension))
        {
          continue;
        }

        if (roofDef == null
            || !BuildableRoofGenerator.RoofToDesignator.TryGetValue(roofDef, out var designator)
            || !designator.Visible)
        {
          continue;
        }
      }

      if (gizmo is Command_Action action
          && action is not Command_BuildCopyRoof
          && action.hotKey == RimWorld.KeyBindingDefOf.Designator_Deconstruct
          && __instance.def?.isThickRoof == true)
      {
        UpdateThickRoofDeconstructCommand(action, __instance);
      }

      yield return gizmo;
    }
  }

  private static void UpdateThickRoofDeconstructCommand(
    Command_Action command,
    SelectedRoof selectedRoof)
  {
    if (!IsThickRoofRemovalResearched())
    {
      command.Disabled = true;
      command.disabledReason = ThickRoofRemovalResearchRequiredKey.Translate();
      return;
    }

    if (selectedRoof.map.areaManager.NoRoof[selectedRoof.cell])
    {
      command.Disabled = true;
      command.disabledReason = "Stratum_AlreadyRemoving".Translate();
      return;
    }

    command.Disabled = false;
    command.disabledReason = null;
    command.action = delegate
    {
      if (DebugSettings.godMode)
      {
        RoofBuildings.RemoveRoofImmediately(selectedRoof.map, selectedRoof.cell, refundMaterials: true);
        return;
      }

      selectedRoof.map.areaManager.NoRoof[selectedRoof.cell] = true;
    };
  }

  [HarmonyPatch(typeof(BuildCustomRoof), nameof(BuildCustomRoof.CanDesignateCell))]
  [HarmonyPrefix]
  public static bool RejectUnavailableRoof(
    RoofDef ___roofDef,
    BuildableRoofExtension ___ext,
    ref AcceptanceReport __result)
  {
    if (IsPlayerBuildable(___roofDef, ___ext))
    {
      return true;
    }

    __result = ___roofDef?.isThickRoof == true
      ? "MessageNothingCanRemoveThickRoofs".Translate()
      : AcceptanceReport.WasRejected;
    return false;
  }
}
