// Placemaker — persisted options and rebindable keys (Options → Mods → Placemaker).
//
// Key choices (RESEARCH-NOTES rule 26): Ctrl+P = Find It/Platter, Ctrl+Shift+P = Parking Lot Tool,
// Ctrl+G = Grid Road Generator, M = Move It. Ctrl+Shift+A is unused by the game and those mods.
// All bindings go through CS2's own binding system (SettingsUIKeyboardBinding) so conflicts show
// up in the game's key overview instead of as a silently dead hotkey (PLT learned that the hard way).

using System;
using Colossal.IO.AssetDatabase;
using Game.Input;
using Game.Modding;
using Game.Settings;
using FillWall = global::Placemaker.Core.Geometry.FillWall;

namespace Placemaker.Game.Settings
{
    public enum PreviewDetail { Simple = 0, Normal = 1, Detailed = 2 }

    [FileLocation("ModsSettings/Placemaker/Placemaker")]
    [SettingsUIGroupOrder(GroupGeneral, GroupBrush, GroupBuild, GroupKeys, GroupMaintenance)]
    [SettingsUIShowGroupName(GroupGeneral, GroupBrush, GroupBuild, GroupKeys, GroupMaintenance)]
    [SettingsUIKeyboardAction(ActionToggleTool, ActionType.Button)]
    [SettingsUIKeyboardAction(ActionPrimary, ActionType.Button)]
    [SettingsUIKeyboardAction(ActionUndo, ActionType.Button)]
    [SettingsUIKeyboardAction(ActionRedo, ActionType.Button)]
    [SettingsUIKeyboardAction(ActionRedoAlt, ActionType.Button)]
    public sealed class PlacemakerSettings : ModSetting
    {
        public const string SectionMain = "Main";
        public const string GroupGeneral = "General";
        public const string GroupKeys = "Keys";
        public const string GroupMaintenance = "Maintenance";
        public const string GroupBuild = "Build";
        public const string GroupBrush = "Brush";

        public const string ActionToggleTool = "ToggleTool";
        public const string ActionPrimary = "Primary";
        public const string ActionUndo = "Undo";
        public const string ActionRedo = "Redo";
        public const string ActionRedoAlt = "RedoAlt";

        /// <summary>Raised by the maintenance buttons; the tool/registration layer acts on them (keeps this class free of system references).</summary>
        public static event Action RemoveEverythingRequested;
        public static event Action ResetDefaultsRequested;

        public PlacemakerSettings(IMod mod) : base(mod) { SetDefaults(); }

        // ───────────── General ─────────────

        [SettingsUISection(SectionMain, GroupGeneral)]
        public bool ShowCursorTooltips { get; set; }

        [SettingsUISection(SectionMain, GroupGeneral)]
        public PreviewDetail PreviewDetail { get; set; }

        /// <summary>Sketch preview: planned surfaces get a faint tint (on) or only their outline (off).</summary>
        [SettingsUISection(SectionMain, GroupGeneral)]
        public bool SketchSurfaceFill { get; set; }

        [SettingsUISection(SectionMain, GroupGeneral)]
        public bool SnapToRoadsWhenDrawing { get; set; }

        /// <summary>Drawn/painted areas are trimmed to the edge of existing roads before planning (AreaClip).</summary>
        [SettingsUISection(SectionMain, GroupGeneral)]
        public bool KeepOffExistingRoads { get; set; }

        /// <summary>
        /// (s15) Plain Paint cuts building lots out of the painted outline (default on). Off = the legacy outline
        /// over buildings; builds never touch buildings either way (Core/Build/SiteSafety).
        /// </summary>
        // s27: superseded by the shared brush rules (Buildings / Paint over anything); kept so the id stays valid.
        [SettingsUIHidden]
        public bool PaintAvoidBuildings { get; set; }


        /// <summary>
        /// (FEEDBACK-4) Layout size in percent: scales layout distances (path loops, spacing, plaza and feature
        /// footprints, margins); props and real-dimension layouts keep their size. → SiteContext.LayoutScale.
        /// </summary>
        [SettingsUISlider(min = 25, max = 150, step = 5, scalarMultiplier = 1, unit = global::Game.UI.Unit.kPercentage)]
        [SettingsUISection(SectionMain, GroupGeneral)]
        public int LayoutSize { get; set; }

        /// <summary>(FEEDBACK-4) The one edge margin (m at 100 % layout size) between the outline and the layout. → SiteContext.EdgeMargin.</summary>
        [SettingsUISlider(min = 0f, max = 10f, step = 0.5f, scalarMultiplier = 1)]
        [SettingsUICustomFormat(fractionDigits = 1)]
        [SettingsUISection(SectionMain, GroupGeneral)]
        public float EdgeMargin { get; set; }

        // ───────────── Brush rules (Block paint; plain Paint ignores them) ─────────────
        // Mirrored in the panel's Block paint "Rules" row (Core/Geometry/BrushRules). Ids are save format.

        /// <summary>(s27) Master toggle: every stop (and the block lock) off — Paint's old behaviour.</summary>
        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushPaintOverAnything { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushStopRoads { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushStopBuildings { get; set; }

        /// <summary>(s27) Other Placemaker builds (owner lot + zone blocks).</summary>
        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushStopBuilds { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushStopLots { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushStopZoned { get; set; }

        /// <summary>(s49) Also keep off zoned cells nobody built on yet (default off: vacant zoned land is paintable).</summary>
        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushStopAllZoned { get; set; }

        /// <summary>Block paint also keeps off visible zone cells nobody zoned yet (id kept from s11).</summary>
        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BlockPaintAvoidEmptyZones { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushStopWater { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushStayInBlock { get; set; }

        /// <summary>(s27) Draw / Roads / Block outlines are clipped to free land (the polygon stops), not only off other builds.</summary>
        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BrushDrawClip { get; set; }

        // (s43) Paint bucket walls (Core/Geometry/FillWalls): lines trace the block, areas cut it. Ids are save format.
        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BucketWallRoads { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BucketWallPaths { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BucketWallBikes { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BucketWallTracks { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BucketWallWater { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BucketWallLots { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        public bool BucketWallZoned { get; set; }

        [SettingsUISlider(min = 0f, max = 20f, step = 1f, scalarMultiplier = 1)]
        [SettingsUISection(SectionMain, GroupBrush)]
        public float BrushSnapDistance { get; set; }

        [SettingsUISlider(min = 0, max = 6, step = 1, scalarMultiplier = 1)]
        [SettingsUISection(SectionMain, GroupBrush)]
        public int BrushSmoothing { get; set; }

        [SettingsUISection(SectionMain, GroupBrush)]
        [SettingsUIButton]
        public bool ResetBrushRules
        {
            set
            {
                try { SetBrushRules(global::Placemaker.Core.Geometry.BrushRules.Defaults()); ApplyAndSave(); }
                catch (Exception e) { Mod.Log.Error(e, "Placemaker: reset brush rules failed"); }
            }
        }

        public global::Placemaker.Core.Geometry.BrushRules GetBrushRules() => new global::Placemaker.Core.Geometry.BrushRules
        {
            Roads = BrushStopRoads, Buildings = BrushStopBuildings, Lots = BrushStopLots, Zoned = BrushStopZoned, AllZoned = BrushStopAllZoned,
            EmptyZones = BlockPaintAvoidEmptyZones, Water = BrushStopWater, StayInBlock = BrushStayInBlock,
            Snap = BrushSnapDistance, Smoothing = BrushSmoothing,
            Builds = BrushStopBuilds, Anything = BrushPaintOverAnything, DrawClip = BrushDrawClip,
            BucketWalls = (BucketWallRoads ? FillWall.Roads : 0) | (BucketWallPaths ? FillWall.Paths : 0) | (BucketWallBikes ? FillWall.Bikes : 0)
                | (BucketWallTracks ? FillWall.Tracks : 0) | (BucketWallWater ? FillWall.Water : 0) | (BucketWallLots ? FillWall.Lots : 0)
                | (BucketWallZoned ? FillWall.Zoned : 0),
        }.Normalized();

        public void SetBrushRules(global::Placemaker.Core.Geometry.BrushRules r)
        {
            r = (r ?? global::Placemaker.Core.Geometry.BrushRules.Defaults()).Normalized();
            BrushStopRoads = r.Roads; BrushStopBuildings = r.Buildings; BrushStopLots = r.Lots; BrushStopZoned = r.Zoned; BrushStopAllZoned = r.AllZoned;
            BlockPaintAvoidEmptyZones = r.EmptyZones; BrushStopWater = r.Water; BrushStayInBlock = r.StayInBlock;
            BrushSnapDistance = r.Snap; BrushSmoothing = r.Smoothing;
            BrushStopBuilds = r.Builds; BrushPaintOverAnything = r.Anything; BrushDrawClip = r.DrawClip;
            var w = r.BucketWalls;
            BucketWallRoads = (w & FillWall.Roads) != 0; BucketWallPaths = (w & FillWall.Paths) != 0; BucketWallBikes = (w & FillWall.Bikes) != 0;
            BucketWallTracks = (w & FillWall.Tracks) != 0; BucketWallWater = (w & FillWall.Water) != 0; BucketWallLots = (w & FillWall.Lots) != 0;
            BucketWallZoned = (w & FillWall.Zoned) != 0;
        }

        // ───────────── Keys ─────────────

        [SettingsUIKeyboardBinding(BindingKeyboard.A, ActionToggleTool, ctrl: true, shift: true)]
        [SettingsUISection(SectionMain, GroupKeys)]
        public ProxyBinding ToggleToolBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Enter, ActionPrimary)]
        [SettingsUISection(SectionMain, GroupKeys)]
        public ProxyBinding PrimaryBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Z, ActionUndo, ctrl: true)]
        [SettingsUISection(SectionMain, GroupKeys)]
        public ProxyBinding UndoBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Y, ActionRedo, ctrl: true)]
        [SettingsUISection(SectionMain, GroupKeys)]
        public ProxyBinding RedoBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Z, ActionRedoAlt, ctrl: true, shift: true)]
        [SettingsUISection(SectionMain, GroupKeys)]
        public ProxyBinding RedoAltBinding { get; set; }

        [SettingsUISection(SectionMain, GroupKeys)]
        [SettingsUIButton]
        public bool ResetKeys
        {
            set
            {
                try { ResetKeyBindings(); }
                catch (Exception e) { Mod.Log.Warn("Placemaker: resetting key bindings failed: " + e.Message); }
            }
        }

        // ───────────── Maintenance ─────────────

        [SettingsUISection(SectionMain, GroupMaintenance)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool RemoveEverything
        {
            set
            {
                try { RemoveEverythingRequested?.Invoke(); }
                catch (Exception e) { Mod.Log.Error(e, "Placemaker: remove everything failed"); }
            }
        }

        [SettingsUISection(SectionMain, GroupMaintenance)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool ResetDefaults
        {
            set
            {
                try
                {
                    SetDefaults();
                    ResetDefaultsRequested?.Invoke();
                    ApplyAndSave();
                }
                catch (Exception e) { Mod.Log.Error(e, "Placemaker: reset defaults failed"); }
            }
        }

        // ───────────── Building (forwarded to the build workstream's systems) ─────────────

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool ClearExistingTreesAndProps { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool KeepZonedBuildingsOff { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool RecoverRejectedPieces { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool AutoExcludeCollisions { get; set; }

        /// <summary>(s53) Placemaker anarchy: our own pieces are never rejected for overlapping each other or decorations.</summary>
        [SettingsUISection(SectionMain, GroupBuild)]
        public bool IgnoreOwnCollisions { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool OverlayPavedSurfaces { get; set; }

        /// <summary>(s21) Decals take no space: emitted so the game never collides them (BuildService.SinkDecals).</summary>
        [SettingsUISection(SectionMain, GroupBuild)]
        public bool DecalsNeverCollide { get; set; }

        /// <summary>(s23) Decorative decals built see-through (runtime clones per opacity tier, DecalOpacitySystem).</summary>
        [SettingsUISection(SectionMain, GroupBuild)]
        public bool SeeThroughDecals { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool ChargeNetConstruction { get; set; }

        /// <summary>(fork, Workstream A) Removing a build puts back the ground and paint it changed (LandRestoreSystem).</summary>
        [SettingsUISection(SectionMain, GroupBuild)]
        public bool RestoreLandOnRemove { get; set; }

        /// <summary>(fork, C-R3) Farmstead builds place a working vanilla farm (hub, farm building, field lots). Default off.</summary>
        [SettingsUISection(SectionMain, GroupBuild)]
        public bool FarmsProduce { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool SelectAsOneThing { get; set; }

        /// <summary>(s30) After each build, write the live terrain, water, nets and objects around it to ModsData/Placemaker/debug.</summary>
        [SettingsUISection(SectionMain, GroupMaintenance)]
        public bool DebugTerrainDump { get; set; }

        /// <summary>(s17) Park attractiveness of builds counts for the city's tourism (owner lot AttractivenessProvider).</summary>
        [SettingsUISection(SectionMain, GroupBuild)]
        public bool ApplyParkStats { get; set; }

        /// <summary>(s17) A hidden companion park building carries every park stat (leisure, coverage, upkeep…).</summary>
        [SettingsUISection(SectionMain, GroupBuild)]
        public bool RealParkStats { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool ConfirmBulldoze { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool SurfaceClones { get; set; }

        /// <summary>Widens AreaBatchSystem queries so ghost surfaces render textured (reflection hack; off by default).</summary>
        [SettingsUISection(SectionMain, GroupBuild)]
        public bool TexturedPreviewSurfaces { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool ParkingEconomy { get; set; }

        [SettingsUISlider(min = 0, max = 50, step = 1, scalarMultiplier = 1)]
        [SettingsUISection(SectionMain, GroupBuild)]
        [SettingsUIDisableByCondition(typeof(PlacemakerSettings), nameof(ParkingEconomyOff))]
        public int DefaultParkingFee { get; set; }

        [SettingsUISection(SectionMain, GroupBuild)]
        public bool ExperimentalParkingCompanion { get; set; }

        [SettingsUISlider(min = 0.5f, max = 3f, step = 0.1f, scalarMultiplier = 1)]
        [SettingsUICustomFormat(fractionDigits = 1)]
        [SettingsUISection(SectionMain, GroupBuild)]
        [SettingsUIDisableByCondition(typeof(PlacemakerSettings), nameof(CompanionOff))]
        public float CompanionUpkeepFactor { get; set; }

        public bool ParkingEconomyOff() => !ParkingEconomy;
        public bool CompanionOff() => !ExperimentalParkingCompanion;

        public override void Apply()
        {
            base.Apply();
            PushToServices();
        }

        /// <summary>The build/economy systems read static switches (docs/proposals/gamebuild.md §6).</summary>
        public void PushToServices()
        {
            try
            {
                global::Placemaker.Game.Build.BuildService.AutoExcludeCollisions = AutoExcludeCollisions;
                global::Placemaker.Game.Build.BuildService.IgnoreOwnCollisions = IgnoreOwnCollisions;
                global::Placemaker.Core.Build.Declutter.Anarchy = IgnoreOwnCollisions;
                global::Placemaker.Game.Build.BuildService.ClearExistingObjects = ClearExistingTreesAndProps;
                global::Placemaker.Game.Build.BuildService.BlockZoning = KeepZonedBuildingsOff;
                global::Placemaker.Game.Build.BuildService.RecoverRejectedPieces = RecoverRejectedPieces;
                global::Placemaker.Game.Build.BuildService.OverlayPavedSurfaces = OverlayPavedSurfaces;
                global::Placemaker.Game.Build.BuildService.SinkDecals = DecalsNeverCollide;
                global::Placemaker.Game.Build.DecalOpacitySystem.FeatureEnabled = SeeThroughDecals;
                global::Placemaker.Game.Build.BuildService.ChargeNetConstruction = ChargeNetConstruction;
                global::Placemaker.Game.Build.BuildService.DebugTerrainDump = DebugTerrainDump;
                global::Placemaker.Game.Restore.LandRestoreSystem.Enabled = RestoreLandOnRemove;
                global::Placemaker.Game.Build.BuildService.FarmEconomy = FarmsProduce;
                if (global::Placemaker.Game.Build.BuildService.ApplyParkStats != ApplyParkStats
                    || global::Placemaker.Game.Economy.ParkCompanionSystem.Enabled != RealParkStats)
                    global::Placemaker.Game.Groups.GroupRegistrySystem.ParkStatsSettingsChanged = true;
                global::Placemaker.Game.Build.BuildService.ApplyParkStats = ApplyParkStats;
                global::Placemaker.Game.Economy.ParkCompanionSystem.Enabled = RealParkStats;
                global::Placemaker.Game.Groups.OwnerAreaPrefabSystem.FeatureEnabled = SelectAsOneThing;
                global::Placemaker.Game.Groups.Patches.RaycastLiftPatch.Enabled = SelectAsOneThing;
                global::Placemaker.Game.Groups.Patches.BulldozeConfirmationPatch.Enabled = ConfirmBulldoze;
                global::Placemaker.Game.Build.SurfaceCloneSystem.FeatureEnabled = SurfaceClones;
                global::Placemaker.Game.Build.GhostSurfaceSystem.FeatureEnabled = TexturedPreviewSurfaces;
                global::Placemaker.Game.Economy.ParkingEconomySystem.FeatureEnabled = ParkingEconomy;
                global::Placemaker.Game.Economy.ParkingEconomySystem.DefaultFee = DefaultParkingFee;
                global::Placemaker.Game.Economy.ParkingCompanionSystem.CompanionEnabled = ExperimentalParkingCompanion;
                global::Placemaker.Game.Economy.ParkingCompanionUpkeepSystem.UpkeepFactor = CompanionUpkeepFactor;
            }
            catch (Exception e) { Mod.Log.Warn("Placemaker: build options not applied: " + e.Message); }
        }

        // ───────────── Persisted tool state (not shown) ─────────────

        /// <summary>CS2 resets a tool's selectedSnap to "all" whenever it is created; we restore the player's choice.</summary>
        [SettingsUIHidden] public int SnapMask { get; set; }
        [SettingsUIHidden] public bool SnapMaskSaved { get; set; }
        [SettingsUIHidden] public float BrushSize { get; set; }
        [SettingsUIHidden] public bool BrushSquare { get; set; }
        [SettingsUIHidden] public int LastAreaMode { get; set; }
        [SettingsUIHidden] public string LastRecipe { get; set; }
        [SettingsUIHidden] public bool PanelCollapsed { get; set; }

        public override void SetDefaults()
        {
            ShowCursorTooltips = true;
            PreviewDetail = PreviewDetail.Normal;
            SketchSurfaceFill = true;
            SnapToRoadsWhenDrawing = true;
            KeepOffExistingRoads = true;
            PaintAvoidBuildings = true;
            SetBrushRules(global::Placemaker.Core.Geometry.BrushRules.Defaults());
            LayoutSize = (int)System.Math.Round(global::Placemaker.Core.SiteContext.DefaultLayoutScale * 100f);
            EdgeMargin = global::Placemaker.Core.SiteContext.DefaultEdgeMargin;
            BrushSize = 150f;
            BrushSquare = false;
            LastAreaMode = 0;
            LastRecipe = "";
            PanelCollapsed = false;
            AutoExcludeCollisions = true;
            IgnoreOwnCollisions = true;
            ClearExistingTreesAndProps = true;
            KeepZonedBuildingsOff = true;
            RecoverRejectedPieces = true;
            OverlayPavedSurfaces = true;
            DecalsNeverCollide = true;
            SeeThroughDecals = true;
            ChargeNetConstruction = true;
            SelectAsOneThing = true;
            DebugTerrainDump = false;
            RestoreLandOnRemove = true;
            FarmsProduce = false;
            ApplyParkStats = true;
            RealParkStats = true;
            ConfirmBulldoze = true;
            SurfaceClones = true;
            TexturedPreviewSurfaces = false;
            ParkingEconomy = true;
            DefaultParkingFee = 0;
            ExperimentalParkingCompanion = false;
            CompanionUpkeepFactor = 1f;
            // Snap mask and bindings are intentionally not reset here: the snap mask keeps the
            // player's last choice, bindings have their own reset button.
        }
    }
}
