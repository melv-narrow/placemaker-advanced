// Placemaker: tells the player when a working farm stands empty (GPL-3.0). SPEC C-R3.
//
// The farm company is vanilla's: IndustrialSpawnSystem creates one when a raw material is wanted, and it picks a free
// farm building by the fields' resource (decompiled 1.6.3f1, PropertyUtils ExtractorFindCompanyJob). The game runner
// showed a livestock farm getting a company on fields that read zero fertility, so fertility alone does not predict it.
// The farm is checked twice once the simulation runs: the fields' resource is logged early, and after a while the farm
// building is checked for a farm company. None by then is logged as a warning and kept with the receipt
// ("farm.company" = 0), with the fertility as the likely reason.

using System;
using System.Collections.Generic;
using Game;
using Game.Areas;
using Game.Common;
using Game.Simulation;
using Placemaker.Game.Groups;
using Unity.Entities;
using UnityEngine.Scripting;

namespace Placemaker.Game.Economy
{
    public sealed partial class FarmWatchSystem : GameSystemBase
    {
        private const uint FertilityAfterSimFrames = 256;
        private const uint CompanyAfterSimFrames = 8192;

        private struct Watch { public int GroupId; public Entity Hub; public uint SimFrame; public bool FertilityLogged; public double Resource; }

        private readonly List<Watch> _watch = new List<Watch>();
        private SimulationSystem _simulation;
        private GroupRegistrySystem _registry;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            _registry = World.GetOrCreateSystemManaged<GroupRegistrySystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            _watch.Clear();
        }

        /// <summary>Watches a new farm: its fields' resource, then whether a farm company moved in.</summary>
        public void Add(int groupId, Entity hub)
        {
            if (hub == Entity.Null) return;
            _watch.RemoveAll(w => w.GroupId == groupId);
            _watch.Add(new Watch { GroupId = groupId, Hub = hub, SimFrame = _simulation.frameIndex });
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (_watch.Count == 0 || (UnityEngine.Time.frameCount & 15) != 0) return;
            for (int i = _watch.Count - 1; i >= 0; i--)
            {
                var w = _watch[i];
                if (!EntityManager.Exists(w.Hub) || EntityManager.HasComponent<Deleted>(w.Hub)) { _watch.RemoveAt(i); continue; }
                uint ran = _simulation.frameIndex - w.SimFrame;
                try
                {
                    if (!w.FertilityLogged && ran >= FertilityAfterSimFrames)
                    {
                        w.Resource = FieldResource(w.Hub, out int fields);
                        w.FertilityLogged = true;
                        _watch[i] = w;
                        Mod.Log.Info($"Placemaker farm of group {w.GroupId}: {fields} field(s), natural resource under them {w.Resource:F0}.");
                    }
                    if (ran >= CompanyAfterSimFrames)
                    {
                        _watch.RemoveAt(i);
                        CheckCompany(w);
                    }
                }
                catch (Exception e)
                {
                    _watch.RemoveAt(i);
                    Mod.Log.Warn($"Placemaker farm check of group {w.GroupId} failed: {e.Message}");
                }
            }
        }

        private double FieldResource(Entity hub, out int fields)
        {
            double resource = 0;
            fields = 0;
            if (!EntityManager.HasBuffer<SubArea>(hub)) return 0;
            var subs = EntityManager.GetBuffer<SubArea>(hub, true);
            for (int i = 0; i < subs.Length; i++)
            {
                var a = subs[i].m_Area;
                if (!EntityManager.Exists(a) || !EntityManager.HasComponent<Extractor>(a)) continue;
                fields++;
                resource += EntityManager.GetComponentData<Extractor>(a).m_ResourceAmount;
            }
            return resource;
        }

        /// <summary>A farm company rents the building attached to the hub.</summary>
        private bool HasCompany(int groupId, Entity hub)
        {
            var members = new List<Entity>();
            _registry.GetMembers(groupId, members);
            foreach (var m in members)
            {
                if (!EntityManager.Exists(m) || !EntityManager.HasComponent<global::Game.Objects.Attached>(m)) continue;
                if (EntityManager.GetComponentData<global::Game.Objects.Attached>(m).m_Parent != hub) continue;
                if (!EntityManager.HasBuffer<global::Game.Buildings.Renter>(m)) continue;
                var renters = EntityManager.GetBuffer<global::Game.Buildings.Renter>(m, true);
                for (int i = 0; i < renters.Length; i++)
                    if (EntityManager.Exists(renters[i].m_Renter) && EntityManager.HasComponent<global::Game.Companies.ExtractorCompany>(renters[i].m_Renter)) return true;
            }
            return false;
        }

        private void CheckCompany(Watch w)
        {
            bool company = HasCompany(w.GroupId, w.Hub);
            if (company) Mod.Log.Info($"Placemaker farm of group {w.GroupId}: a farm company has moved in.");
            else Mod.Log.Warn($"Placemaker farm of group {w.GroupId}: no farm company has moved in yet (natural resource under the fields {w.Resource:F0}). "
                + "Crop farms need fertile land (the Natural resources map); the city's demand for farm goods decides when a company comes.");
            var rec = _registry.GetRecord(w.GroupId);
            if (rec != null && _registry.TryGetRecordEntity(w.GroupId, out var record))
            {
                rec.Facts["farm.company"] = company ? 1 : 0;
                rec.Facts["farm.resource"] = Math.Round(w.Resource);
                _registry.WriteRecord(record, rec);
            }
        }
    }
}
