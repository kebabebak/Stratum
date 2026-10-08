using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

using SolarWeb.Stratum.DefModExtensions;
using SolarWeb.Stratum.Stats;
using SolarWeb.Stratum.Hooks;
using SolarWeb.Stratum.Utilities;

namespace SolarWeb.Stratum.MapComponents;

public class RoofIntegrityGrid(Map map) : MapComponent(map)
{
  private readonly short[] hitPoints = new short[map.cellIndices.NumGridCells];
  private readonly ThingDef?[] stuffDefs = new ThingDef[map.cellIndices.NumGridCells];
  private readonly UnityEngine.Color?[] glassTints = new UnityEngine.Color?[map.cellIndices.NumGridCells];
  private readonly HashSet<int> roofsNeedingRepair = [];
  public bool hasScanned;
  private object scanLockInt = new();

  private Dictionary<int, short>? loadedDamagedCells;
  private Dictionary<int, ThingDef>? loadedSavedStuff;
  private Dictionary<int, UnityEngine.Color>? loadedSavedTints;

  public HashSet<int> RoofsNeedingRepair => roofsNeedingRepair;
  internal short[] HitPointsArray => hitPoints;
  internal UnityEngine.Color?[] GlassTintsArray => glassTints;
  internal ThingDef?[] StuffDefsArray => stuffDefs;

  public override void ExposeData()
  {
    base.ExposeData();

    if (Scribe.mode == LoadSaveMode.Saving)
    {
      loadedDamagedCells = [];
      loadedSavedStuff = [];
      loadedSavedTints = [];
      for (int i = 0; i < hitPoints.Length; i++)
      {
        if (roofsNeedingRepair.Contains(i))
          loadedDamagedCells[i] = hitPoints[i];

        if (stuffDefs[i] != null)
          loadedSavedStuff[i] = stuffDefs[i]!;

        if (glassTints[i] != null)
          loadedSavedTints[i] = glassTints[i]!.Value;
      }
    }

    Scribe_Collections.Look(ref loadedDamagedCells, "damagedCells", LookMode.Value, LookMode.Value);
    Scribe_Collections.Look(ref loadedSavedStuff, "savedStuff", LookMode.Value, LookMode.Def);
    Scribe_Collections.Look(ref loadedSavedTints, "savedTints", LookMode.Value, LookMode.Value);

    if (Scribe.mode == LoadSaveMode.PostLoadInit)
    {
      scanLockInt = new object();

      if (loadedDamagedCells != null)
      {
        foreach (var kvp in loadedDamagedCells)
        {
          hitPoints[kvp.Key] = kvp.Value;
          roofsNeedingRepair.Add(kvp.Key);
        }
      }

      if (loadedSavedStuff != null)
      {
        foreach (var kvp in loadedSavedStuff)
        {
          stuffDefs[kvp.Key] = kvp.Value;
        }
      }

      if (loadedSavedTints != null)
      {
        foreach (var kvp in loadedSavedTints)
        {
          glassTints[kvp.Key] = kvp.Value;
        }
      }

      loadedDamagedCells = null;
      loadedSavedStuff = null;
      loadedSavedTints = null;
    }
  }

  public override void FinalizeInit()
  {
    base.FinalizeInit();

    if (!hasScanned)
    {
      ExecuteScan(force: true);
    }

    // Unconditional, and deliberately not folded into the guard above: RoofVFXMapComponent and
    // CustomRoofsRenderer also kick off the scan when they find hasScanned false, and map component
    // FinalizeInit order is not guaranteed. Whichever component wins the race, this is the first
    // point at which every thing on the map has spawned, so it is the first point at which the
    // hook-aware maximum is trustworthy. ReconcileRepairSet is idempotent.
    ReconcileRepairSet();

    var registry = MapHookRegistry.Get(map);
    if (registry != null)
    {
      registry.Register<MapHookRegistry.RoofChangedHandler>(MapHookRegistry.HookId.RoofChanged, Notify_StratumRoofChanged);
    }
    if (map.areaManager != null)
    {
      map.areaManager.BuildRoof?.Clear();
      map.areaManager.NoRoof?.Clear();
    }
  }

  internal void InitializeNaturalRoofsStuff(bool forceReevaluate = false)
  {
    var numCells = map.cellIndices.NumGridCells;
    var roofGrid = map.roofGrid;
    ThingDef fallbackStuff = GetFallbackStonyStuff(map);

    for (int i = 0; i < numCells; i++)
    {
      if (stuffDefs[i] != null && !forceReevaluate) continue;

      var roof = roofGrid.RoofAt(i);
      if (roof != null && roof.isNatural)
      {
        var cell = map.cellIndices.IndexToCell(i);
        var stuff = GetStonyStuffForCell(roof, cell, map, fallbackStuff);
        stuffDefs[i] = stuff;

        short maxHP = (short)RoofStatCache.GetMaxHitPoints(roof, stuff);
        if (!roofsNeedingRepair.Contains(i))
        {
          hitPoints[i] = maxHP;
        }
        else if (hitPoints[i] > maxHP)
        {
          hitPoints[i] = maxHP;
        }
      }
    }
  }



  private static readonly Dictionary<ThingDef, ThingDef?> rockStuffCache = new();
  private static readonly Dictionary<(RoofDef, TerrainDef), ThingDef?> terrainStuffCache = new();

  public static void ClearCaches()
  {
    rockStuffCache.Clear();
    terrainStuffCache.Clear();
  }

  public override void MapRemoved()
  {
    base.MapRemoved();
    ClearCaches();
    var registry = MapHookRegistry.Get(map);
    if (registry != null)
    {
      registry.Unregister<MapHookRegistry.RoofChangedHandler>(MapHookRegistry.HookId.RoofChanged, Notify_StratumRoofChanged);
    }
  }

  private void Notify_StratumRoofChanged(Map m, IntVec3 c, RoofDef? oldRoof, RoofDef? newRoof)
  {
    if (m != map) return;

    try
    {
      if (Find.Selector != null && Find.Selector.SelectedObjects != null && Find.Selector.SelectedObjects.Count > 0)
      {
        for (int i = Find.Selector.SelectedObjects.Count - 1; i >= 0; i--)
        {
          var obj = Find.Selector.SelectedObjects[i];
          if (obj is UI.SelectedRoof sr && sr.map == map && sr.cell == c)
          {
            if (newRoof == null || sr.def != newRoof)
            {
              Find.Selector.Deselect(sr);
            }
          }
        }
      }
    }
    catch (Exception ex)
    {
      StratumLog.Error($"Error in RoofIntegrityGrid Notify_StratumRoofChanged selection cleanup: {ex}");
    }

    if (map.areaManager != null)
    {
      if (map.areaManager.NoRoof != null) map.areaManager.NoRoof[c] = false;
      if (map.areaManager.BuildRoof != null) map.areaManager.BuildRoof[c] = false;
    }

    var region = map.regionGrid?.GetValidRegionAt_NoRebuild(c);
    region?.District?.Notify_RoofChanged();

    if (newRoof != null && RoofStatCache.IsCustomRoof(newRoof))
    {
      ThingDef? stuff = null;
      UnityEngine.Color? tint = null;
      if (DebugSettings.godMode)
      {
        var designator = Find.DesignatorManager.SelectedDesignator as AI.Designators.BuildCustomRoof;
        if (designator != null)
        {
          stuff = designator.StuffDef;
          tint = designator.SelectedTint;
        }
      }

      if (stuff == null && Patches.GravshipPlacementUtility_SpawnRoofs_Patch.CurrentLandingGravship != null)
      {
        var local = c - Patches.GravshipPlacementUtility_SpawnRoofs_Patch.CurrentLandingRoot;
        if (Patches.GravshipPlacementUtility_SpawnRoofs_Patch.CurrentRoofData != null &&
            Patches.GravshipPlacementUtility_SpawnRoofs_Patch.CurrentRoofData.TryGetValue(local, out var cellData))
        {
          stuff = cellData.stuff;
          InitializeRoof(c, newRoof, stuff, cellData.glassTint, cellData.hitPoints);
          return;
        }
      }

      InitializeRoof(c, newRoof, stuff, tint);
    }
    else
    {
      if (oldRoof != null && RoofStatCache.IsCustomRoof(oldRoof))
      {
        if (RoofBuildings.isDeconstructingRoof)
        {
          var stuff = GetStuff(c);
          var ext = oldRoof.GetModExtension<BuildableRoofExtension>();
          // Roofs placed by another mod can have no Stratum material data. In
          // particular, SOS2 creates RoofShip directly instead of going through
          // a Stratum construction frame. Do not invent a default material (and
          // a refund for resources that may never have been paid) for those roofs.
          // A roof that is not made from stuff still refunds its costList. Hay thatch is that case.
          if (ext != null && ext.buildableDef != null && (stuff != null || !ext.buildableDef.MadeFromStuff))
          {
            var costList = stuff != null
              ? ext.buildableDef.CostListAdjusted(stuff)
              : ext.buildableDef.costList;
            if (costList != null)
            {
              float refundFraction = ext.buildableDef.resourcesFractionWhenDeconstructed;
              foreach (var cost in costList)
              {
                int count = GenMath.RoundRandom(cost.count * refundFraction);
                if (count > 0)
                {
                  var deconstructItem = ThingMaker.MakeThing(cost.thingDef);
                  deconstructItem.stackCount = count;
                  GenPlace.TryPlaceThing(deconstructItem, c, map, ThingPlaceMode.Near);
                }
              }
            }
          }
        }
      }
      RemoveRoof(c);
    }
  }

  public void ExecuteScan(bool force = false)
  {
    lock (scanLockInt)
    {
      if (hasScanned && !force) return;
      InitializeNaturalRoofsStuff(forceReevaluate: force || !hasScanned);
      hasScanned = true;
      ParallelMapScanner.ExecuteScan(this, force);
      ReconcileRepairSet();
    }
  }

  public override void MapComponentUpdate()
  {
  }

  /// <summary>
  /// Assigns full hit points to cells that carry a Stratum roof but have no stored value.
  /// </summary>
  /// <remarks>
  /// Undamaged cells are not persisted; they are reconstructed here on load. The maximum used must
  /// be the hook-aware one, or a roof whose maximum a compat layer raised (a skylight panel with a
  /// reinforcing frame under it, say) comes back permanently short of full and is flagged for
  /// repair forever. <see cref="ParallelMapScanner"/> collects the indices but cannot resolve that
  /// maximum from a worker thread, so it hands them here.
  ///
  /// Writes the array directly rather than going through <see cref="SetHitPoints"/>: that dirties
  /// the map mesh per cell, and the caller runs during load where the drawer is regenerated
  /// wholesale afterwards anyway.
  /// </remarks>
  internal void InitializeUninitializedCells(List<int> indices)
  {
    if (indices == null || indices.Count == 0) return;

    var roofGrid = map.roofGrid;
    for (int n = 0; n < indices.Count; n++)
    {
      int index = indices[n];
      if (index < 0 || index >= hitPoints.Length) continue;

      var roof = roofGrid.RoofAt(index);
      if (roof == null || !RoofStatCache.IsCustomRoof(roof)) continue;

      short maxHP = GetMaxHitPoints(map.cellIndices.IndexToCell(index));
      if (maxHP <= 0) continue;

      hitPoints[index] = maxHP;
      roofsNeedingRepair.Remove(index);
    }
  }

  internal void ReconcileRepairSet()
  {
    var roofGrid = map.roofGrid;

    if (!MapHookRegistry.HasRoofMaxHitPointsHandlers(map))
    {
      roofsNeedingRepair.RemoveWhere(i =>
        i < 0 || i >= hitPoints.Length || roofGrid.RoofAt(i) == null || hitPoints[i] <= 0);
      return;
    }

    int numCells = map.cellIndices.NumGridCells;
    for (int i = 0; i < numCells; i++)
    {
      var roof = roofGrid.RoofAt(i);
      if (roof == null || !RoofStatCache.IsCustomRoof(roof))
      {
        roofsNeedingRepair.Remove(i);
        continue;
      }

      short maxHP = GetMaxHitPoints(map.cellIndices.IndexToCell(i));
      if (maxHP <= 0) continue;

      if (hitPoints[i] > maxHP) hitPoints[i] = maxHP;

      if (hitPoints[i] > 0 && hitPoints[i] < maxHP)
        roofsNeedingRepair.Add(i);
      else
        roofsNeedingRepair.Remove(i);
    }
  }

  public void InitializeRoof(IntVec3 cell, RoofDef def, ThingDef? stuff = null, UnityEngine.Color? glassTint = null, short? currentHP = null)
  {
    if (!cell.InBounds(map)) return;
    int index = map.cellIndices.CellToIndex(cell);
    if (RoofStatCache.IsCustomRoof(def))
    {
      // Folded rather than read back through GetMaxHitPoints(cell): the base has to come from the
      // def and stuff being installed, which stuffDefs does not carry yet, and not every caller has
      // written the roof grid by the time it gets here. Handlers that need the roof grid simply
      // decline, which is the pre-hook behaviour rather than a wrong answer.
      int baseMaxHP = RoofStatCache.GetMaxHitPoints(def, stuff);
      short maxHP = (short)MapHookRegistry.GetCellRoofMaxHitPoints(map, cell, baseMaxHP);
      hitPoints[index] = currentHP ?? maxHP;
      stuffDefs[index] = stuff;
      glassTints[index] = glassTint;

      if (hitPoints[index] < maxHP)
        roofsNeedingRepair.Add(index);
      else
        roofsNeedingRepair.Remove(index);
    }
  }

  public void RemoveRoof(IntVec3 cell)
  {
    if (!cell.InBounds(map)) return;
    int index = map.cellIndices.CellToIndex(cell);
    hitPoints[index] = 0;
    stuffDefs[index] = null;
    glassTints[index] = null;
    roofsNeedingRepair.Remove(index);
  }

  public short GetHitPoints(IntVec3 cell)
  {
    if (!cell.InBounds(map)) return 0;
    return hitPoints[map.cellIndices.CellToIndex(cell)];
  }

  /// <summary>
  /// Whether this cell's roof is below full hit points.
  /// </summary>
  /// <remarks>
  /// One hash lookup against the repair set, which every hit-point write already keeps in sync. Rendering
  /// asks this per cell to decide whether damage scratches are needed at all; answering it via
  /// <see cref="GetMaxHitPoints(IntVec3)"/> instead costs a roof grid read, a stuff read, a lock and a
  /// hook dispatch for cells that are almost always undamaged.
  /// </remarks>
  public bool IsDamaged(IntVec3 cell)
  {
    if (!cell.InBounds(map)) return false;
    return roofsNeedingRepair.Contains(map.cellIndices.CellToIndex(cell));
  }

  public bool IsDamaged(int index) => roofsNeedingRepair.Contains(index);

  public void SetHitPoints(IntVec3 cell, short hp)
  {
    if (!cell.InBounds(map)) return;
    int index = map.cellIndices.CellToIndex(cell);
    var roof = map.roofGrid.RoofAt(cell);
    if (roof == null) return;

    short maxHP = GetMaxHitPoints(cell);
    hitPoints[index] = (short)UnityEngine.Mathf.Clamp(hp, 0, maxHP);

    if (hitPoints[index] < maxHP)
      roofsNeedingRepair.Add(index);
    else
      roofsNeedingRepair.Remove(index);

    map.mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Roofs);
  }

  public float GetEffectiveInsulation(IntVec3 cell)
  {
    if (!cell.InBounds(map)) return 0f;
    var roof = map.roofGrid.RoofAt(cell);
    if (roof == null) return 0f;
    return RoofStatCache.GetEffectiveInsulation(roof, GetStuff(cell));
  }

  public short GetMaxHitPoints(IntVec3 cell)
  {
    if (!cell.InBounds(map)) return 0;
    var roof = map.roofGrid.RoofAt(cell);
    if (roof == null) return 0;
    int maxHp = RoofStatCache.GetMaxHitPoints(roof, GetStuff(cell));
    maxHp = MapHookRegistry.GetCellRoofMaxHitPoints(map, cell, maxHp);
    return (short)maxHp;
  }

  public ThingDef? GetStuff(IntVec3 cell)
  {
    if (!cell.InBounds(map)) return null;
    return stuffDefs[map.cellIndices.CellToIndex(cell)];
  }

  public UnityEngine.Color? GetGlassTint(IntVec3 cell)
  {
    if (!cell.InBounds(map)) return null;
    return glassTints[map.cellIndices.CellToIndex(cell)];
  }

  public UnityEngine.Color? GetGlassTint(int index)
  {
    if (index < 0 || index >= glassTints.Length) return null;
    return glassTints[index];
  }

  /// <summary>
  /// Mitigates <paramref name="amount"/> through the roof's damage threshold and armour, honouring
  /// any compat hook that wants to own the calculation outright.
  /// </summary>
  /// <remarks>
  /// Shared by <see cref="TakeDamage"/> and <see cref="WouldSurviveDamage"/> so the question
  /// "will this roof stop the skyfaller?" and the answer applied on impact cannot drift apart.
  /// They used to be computed separately, which let a roof pass the pre-impact check and then
  /// survive the impact anyway -- destroying the pod and everything inside it.
  /// </remarks>
  private float ComputeEffectiveDamage(IntVec3 cell, RoofDef roof, ThingDef? stuff, float amount, float penetration, DamageInfo? dinfo, bool bypassProtection = false)
  {
    float effectiveDamage = amount;

    if (MapHookRegistry.TryCalculateRoofDamage(map, roof, stuff, amount, penetration, dinfo, ref effectiveDamage))
      return effectiveDamage;

    // A roof that is already burning takes thermal damage rather than another impact. Applying
    // the ballistic threshold here makes any roof with DT 3 or higher immune to RoofFire, whose
    // maximum damage roll is only 3, leaving an otherwise valid fire burning forever.
    if (bypassProtection) return effectiveDamage;

    float dt = RoofStatCache.GetDamageThreshold(roof, stuff);
    dt = MapHookRegistry.GetCellRoofDamageThreshold(map, cell, dt);

    effectiveDamage -= dt;
    if (effectiveDamage <= 0) return 0f;

    float ar = RoofStatCache.GetArmorRating(roof, stuff);
    ar = MapHookRegistry.GetCellRoofArmorRating(map, cell, ar);

    float effectiveArmor = System.Math.Max(0f, ar - penetration);
    return effectiveDamage * (1f - effectiveArmor);
  }

  /// <summary>
  /// Would the roof over <paramref name="cell"/> still be standing after taking
  /// <paramref name="amount"/> damage? Returns false when there is no roof to begin with.
  /// </summary>
  /// <remarks>
  /// Deliberately deterministic where <see cref="TakeDamage"/> uses <c>GenMath.RoundRandom</c>:
  /// rounding the damage down means a borderline roof reports as surviving, which routes the
  /// skyfaller elsewhere. Erring toward relocation is always the safe direction.
  /// </remarks>
  public bool WouldSurviveDamage(IntVec3 cell, float amount, float penetration = 0f)
  {
    if (!cell.InBounds(map)) return false;
    int index = map.cellIndices.CellToIndex(cell);
    if (hitPoints[index] <= 0) return false;

    var roof = map.roofGrid.RoofAt(cell);
    if (roof == null) return false;

    float effectiveDamage = ComputeEffectiveDamage(cell, roof, stuffDefs[index], amount, penetration, null);
    if (effectiveDamage <= 0) return true;

    return hitPoints[index] > UnityEngine.Mathf.FloorToInt(effectiveDamage);
  }

  public void TakeDamage(IntVec3 cell, float amount, float penetration = 0f, DamageInfo? dinfo = null)
  {
    TakeDamageInternal(cell, amount, penetration, dinfo, bypassProtection: false);
  }

  internal void TakeFireDamage(IntVec3 cell, float amount)
  {
    TakeDamageInternal(cell, amount, 0f, new DamageInfo(DamageDefOf.Flame, amount), bypassProtection: true);
  }

  private void TakeDamageInternal(IntVec3 cell, float amount, float penetration, DamageInfo? dinfo, bool bypassProtection)
  {
    if (dinfo != null && !dinfo.Value.Def.harmsHealth) return;

    if (!cell.InBounds(map)) return;
    int index = map.cellIndices.CellToIndex(cell);
    if (hitPoints[index] <= 0) return;

    var roof = map.roofGrid.RoofAt(cell);
    if (roof == null) return;

    var stuff = stuffDefs[index];

    float effectiveDamage = ComputeEffectiveDamage(cell, roof, stuff, amount, penetration, dinfo, bypassProtection);
    if (effectiveDamage <= 0) return;

    int finalDamage = GenMath.RoundRandom(effectiveDamage);
    if (finalDamage <= 0) return;

    var maxHP = GetMaxHitPoints(cell);
    hitPoints[index] -= (short)finalDamage;

    if (hitPoints[index] <= 0)
    {
      hitPoints[index] = 0;
      var tint = glassTints[index];
      glassTints[index] = null;
      roofsNeedingRepair.Remove(index);

      // Keep the material available while vanilla applies collapse damage. The
      // collapse-damage patch reads it from this grid for the affected cell.
      RoofCollapserImmediate.DropRoofInCells(cell, map);
      stuffDefs[index] = null;

      map.mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Roofs);

      var ext = roof.GetModExtension<BuildableRoofExtension>();
      if (ext != null)
      {
        if (Find.PlaySettings != null && Find.PlaySettings.autoRebuild && map.areaManager?.Home != null && map.areaManager.Home[cell])
        {
          map.GetComponent<RoofConstructionTracker>()?.RebuildRoof(cell, roof, ext, stuff, tint);
        }

        int debrisCount = Rand.RangeInclusive(1, 2);
        for (int i = 0; i < debrisCount; i++)
        {
          if (roof.collapseLeavingThingDef == null) continue;

          var debris = ThingMaker.MakeThing(roof.collapseLeavingThingDef);
          GenPlace.TryPlaceThing(debris, cell, map, ThingPlaceMode.Near);
        }
      }
    }
    else if (hitPoints[index] < maxHP)
    {
      roofsNeedingRepair.Add(index);
      map.mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Roofs);
    }
  }

  public void Repair(IntVec3 cell, int amount)
  {
    if (map == null || !cell.InBounds(map)) return;
    int index = map.cellIndices.CellToIndex(cell);
    var maxHP = GetMaxHitPoints(cell);

    if (hitPoints[index] > 0 && hitPoints[index] < maxHP)
    {
      hitPoints[index] += (short)amount;
      if (hitPoints[index] >= maxHP)
      {
        hitPoints[index] = maxHP;
        roofsNeedingRepair.Remove(index);
      }
      map.mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Roofs);
    }
  }

  public static ThingDef GetFallbackStonyStuff(Map map)
  {
    if (map != null)
    {
      var tile = map.Tile;
      var pocketParent = map.PocketMapParent;
      if (!tile.Valid && pocketParent?.sourceMap != null)
      {
        tile = pocketParent.sourceMap.Tile;
      }

      if (tile.Valid)
      {
        var rockTypes = Find.World.NaturalRockTypesIn(tile);
        if (rockTypes != null)
        {
          foreach (var rockDef in rockTypes)
          {
            var blocks = GetStonyStuffForRock(rockDef);
            if (blocks != null) return blocks;
          }
        }
      }
    }

    return DefDatabase<ThingDef>.GetNamed("BlocksGranite");
  }

  public static ThingDef? GetStonyStuffForTerrain(RoofDef roof, TerrainDef floor)
  {
    if (floor == null || roof == null) return null;

    var key = (roof, floor);
    if (terrainStuffCache.TryGetValue(key, out var cached))
    {
      return cached;
    }

    ThingDef? stuff = null;
    var ext = roof.GetModExtension<BuildableRoofExtension>();
    if (ext != null && ext.terrainToStuff.TryGetValue(floor, out stuff))
    {
      terrainStuffCache[key] = stuff;
      return stuff;
    }

    // Secondary fallback: check if any custom roof extension has this mapping
    foreach (var rDef in DefDatabase<RoofDef>.AllDefs)
    {
      var rExt = rDef.GetModExtension<BuildableRoofExtension>();
      if (rExt != null && rExt.terrainToStuff.TryGetValue(floor, out stuff))
      {
        terrainStuffCache[key] = stuff;
        return stuff;
      }
    }

    terrainStuffCache[key] = null;
    return null;
  }

  public static ThingDef GetStonyStuffForCell(RoofDef roof, IntVec3 cell, Map map, ThingDef? fallbackStuff = null)
  {
    if (cell.InBounds(map))
    {
      var edifice = cell.GetEdifice(map);
      if (edifice != null && edifice.def.building != null && edifice.def.building.isNaturalRock)
      {
        var blocksDef = GetStonyStuffForRock(edifice.def);
        if (blocksDef != null) return blocksDef;
      }

      var floor = cell.GetTerrain(map);
      if (floor != null)
      {
        var stuff = GetStonyStuffForTerrain(roof, floor);
        if (stuff != null) return stuff;
      }

      foreach (var adj in GenRadial.RadialCellsAround(cell, 3f, useCenter: false))
      {
        if (!adj.InBounds(map)) continue;
        var adjEdifice = adj.GetEdifice(map);
        if (adjEdifice?.def.building?.isNaturalRock == true)
        {
          var blocksDef = GetStonyStuffForRock(adjEdifice.def);
          if (blocksDef != null) return blocksDef;
        }
      }
    }

    return fallbackStuff ?? GetFallbackStonyStuff(map);
  }

  private static ThingDef? GetStonyStuffForRock(ThingDef rockDef)
  {
    if (rockStuffCache.TryGetValue(rockDef, out var cached))
    {
      return cached;
    }

    ThingDef? blocks = GetStonyStuffFromButcherProducts(rockDef);
    if (blocks != null)
    {
      rockStuffCache[rockDef] = blocks;
      return blocks;
    }

    if (rockDef.building?.mineableThing != null)
    {
      blocks = GetStonyStuffFromButcherProducts(rockDef.building.mineableThing);
      if (blocks != null)
      {
        rockStuffCache[rockDef] = blocks;
        return blocks;
      }
    }

    rockStuffCache[rockDef] = null;
    return null;
  }

  private static ThingDef? GetStonyStuffFromButcherProducts(ThingDef def)
  {
    if (def.butcherProducts != null)
    {
      foreach (var product in def.butcherProducts)
      {
        if (product.thingDef?.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) == true)
        {
          return product.thingDef;
        }
      }
    }
    return null;
  }
}
