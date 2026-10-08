using RimWorld;
using UnityEngine;
using Verse;

using SolarWeb.Stratum.Utilities;

namespace SolarWeb.Stratum.AI.Designators;

/// <summary>
/// Dev-mode brush next to remove-roof. God mode deletes every roof in the dragged cells, including overhead mountain, and drops nothing.
/// Without god mode the button stays on the bar. Command draws it disabled and a click uses the DisabledCommand toast with the reason "God Mode required".
/// </summary>
public class Designator_AreaDeleteRoof : Designator_Cells
{
  private const string GodModeRequiredMessage = "God Mode required";

  public override bool DragDrawMeasurements => true;

  public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Areas;

  public override bool Visible => Prefs.DevMode;

  public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
  {
    ApplyGodModeGate();
    return base.GizmoOnGUI(topLeft, maxWidth, parms);
  }

  public override GizmoResult GizmoOnGUIShrunk(Vector2 topLeft, float size, GizmoRenderParms parms)
  {
    ApplyGodModeGate();
    return base.GizmoOnGUIShrunk(topLeft, size, parms);
  }

  private void ApplyGodModeGate()
  {
    disabled = !DebugSettings.godMode;
    disabledReason = disabled ? GodModeRequiredMessage : null;
  }

  public Designator_AreaDeleteRoof()
  {
    defaultLabel = "Stratum_DesignatorAreaDeleteRoof".Translate();
    defaultDesc = "Stratum_DesignatorAreaDeleteRoofDesc".Translate();
    icon = ContentFinder<Texture2D>.Get("UI/Designators/NoRoofArea");
    soundDragSustain = SoundDefOf.Designate_DragAreaAdd;
    soundDragChanged = null;
    soundSucceeded = SoundDefOf.Designate_ZoneAdd;
    useMouseIcon = true;
  }

  public override AcceptanceReport CanDesignateCell(IntVec3 c)
  {
    if (!DebugSettings.godMode || !c.InBounds(Map))
      return false;

    return Map.roofGrid.RoofAt(c) != null;
  }

  public override void DesignateSingleCell(IntVec3 c)
  {
    if (!DebugSettings.godMode)
      return;

    RoofBuildings.RemoveRoofImmediately(Map, c, refundMaterials: false);
  }

  public override void SelectedUpdate()
  {
    GenUI.RenderMouseoverBracket();
    Map.areaManager.NoRoof.MarkForDraw();
  }
}
