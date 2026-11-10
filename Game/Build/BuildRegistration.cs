// Placemaker — system registration of the game-build workstream (GPL-3.0).
// Called once from Mod.OnLoad by the lead: BuildRegistration.Register(updateSystem).

using Game;
using Placemaker.Game.Assets;
using Placemaker.Game.Economy;
using Placemaker.Game.Groups;

namespace Placemaker.Game.Build
{
    public static class BuildRegistration
    {
        public static void Register(UpdateSystem updateSystem)
        {
            // ── Prefabs ──────────────────────────────────────────────────────────────
            // Runtime prefabs must be added in PrefabUpdate BEFORE PrefabInitializeSystem, or they
            // miss Initialize/LateInitialize and never get an archetype (RESEARCH-NOTES 1). Both
            // run unconditionally so saves that reference the clones by name always resolve (rule 3).
            updateSystem.UpdateBefore<SurfaceCloneSystem, global::Game.Prefabs.PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateBefore<DecalOpacitySystem, global::Game.Prefabs.PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateBefore<ParkingCompanionPrefabSystem, global::Game.Prefabs.PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateBefore<ParkCompanionPrefabSystem, global::Game.Prefabs.PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            // "Placemaker Area" owner lot (US7): one selectable/bulldozable thing per build.
            updateSystem.UpdateBefore<OwnerAreaPrefabSystem, global::Game.Prefabs.PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);

            // ── Catalog & selection UI ─────────────────────────────────────────────────
            // UIUpdate ticks while the simulation is paused (GameSimulation does not), and
            // SelectedInfoUISystem drives its sections there.
            updateSystem.UpdateAt<AssetCatalogSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<PlacemakerGroupSection>(SystemUpdatePhase.UIUpdate);

            // ── Build transaction ───────────────────────────────────────────────────────
            // ToolUpdate: tools run here and ToolOutputSystem reads the active tool's applyMode right
            // after. The service decides first, then the build tool hands the mode to the game.
            updateSystem.UpdateAt<BuildToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateBefore<BuildService, BuildToolSystem>(SystemUpdatePhase.ToolUpdate);
            // Land restore (fork, Workstream A): before the build service, so an Edit's rebuild never writes the ground in
            // the frame the old group's ground is put back.
            updateSystem.UpdateBefore<Placemaker.Game.Restore.LandRestoreSystem, BuildService>(SystemUpdatePhase.ToolUpdate);
            // Ghost preview: temporary surfaces are created in Modification1 and triangulated before ModificationEnd;
            // tagging them there (still Updated) lets AreaBatchSystem (PreCulling) render their material.
            updateSystem.UpdateAt<GhostSurfaceSystem>(SystemUpdatePhase.ModificationEnd);

            // ── Groups ─────────────────────────────────────────────────────────────────
            // Removal marks Deleted in PreTool: SubElementDeleteSystem (PostTool) cascades and
            // Game.Net.ReferencesSystem (Modification2B) clears node references in the same frame
            // (RESEARCH-NOTES 11–12).
            updateSystem.UpdateAt<GroupRemovalSystem>(SystemUpdatePhase.PreTool);
            // ModificationEnd: deleted members of this frame still exist (CleanUp is at frame end), so
            // carrier SubNet links are dropped before anything reads them next frame; runs while paused.
            updateSystem.UpdateAt<GroupRegistrySystem>(SystemUpdatePhase.ModificationEnd);

            // ── Economy (isolated; failures disable it, never the build) ───────────────
            // Fee/comfort must be written AFTER ParkingLaneDataSystem, which zeroes them for lanes whose
            // root owner is not a parking building (RESEARCH-NOTES 17).
            updateSystem.UpdateAfter<ParkingEconomySystem, global::Game.Pathfind.ParkingLaneDataSystem>(SystemUpdatePhase.ModificationEnd);
            // Companion components must exist before Game.Buildings.InitializeSystem sees the Building.
            updateSystem.UpdateBefore<ParkingCompanionSystem, global::Game.Buildings.InitializeSystem>(SystemUpdatePhase.Modification2);
            updateSystem.UpdateBefore<ParkCompanionSystem, global::Game.Buildings.InitializeSystem>(SystemUpdatePhase.Modification2);
            updateSystem.UpdateAt<ParkCompanionCleanupSystem>(SystemUpdatePhase.PreTool);
            // Upkeep usage / workers are set before vanilla charges upkeep and hires.
            updateSystem.UpdateBefore<ParkingCompanionUpkeepSystem, global::Game.Simulation.CityServiceUpkeepSystem>(SystemUpdatePhase.GameSimulation);
        }
    }
}
