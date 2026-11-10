// Placemaker — keeps the park stats of loaded builds in line with their receipts and the settings (GPL-3.0).
// Attractiveness lives either on the owner lot (vanilla AttractivenessProvider, BuildService.ParkStats.cs) or,
// with "Real park stats", on the companion park building (Economy/ParkCompanionSystem). After a load we repair:
//  * companion setting off → companions are removed (PreTool) and the lot gets attractiveness back;
//  * a companion whose prefab did not resolve (clone list lost) → removed and created again from the receipt;
//  * the lot carries attractiveness only while there is no companion and "counts for tourism" is on.

using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Prefabs;
using Placemaker.Core.Build;
using Placemaker.Game.Build;
using Placemaker.Game.Economy;
using Unity.Entities;
using Unity.Mathematics;

namespace Placemaker.Game.Groups
{
    public sealed partial class GroupRegistrySystem
    {
        /// <summary>Set by the settings when "Park attractiveness counts" or "Real park stats" changed; synced next update.</summary>
        public static bool ParkStatsSettingsChanged;

        private void SyncParkStats()
        {
            int lotAdded = 0, lotRemoved = 0, removed = 0, requested = 0;
            var companions = World.GetOrCreateSystemManaged<ParkCompanionSystem>();
            var cleanup = World.GetOrCreateSystemManaged<ParkCompanionCleanupSystem>();
            var members = new List<Entity>();
            foreach (var kv in _records)
            {
                try
                {
                    var g = EntityManager.GetComponentData<PlacemakerGroup>(kv.Value);
                    var rec = GetRecord(kv.Key);
                    if (rec?.ParkStats == null) continue;

                    Entity companion = Entity.Null;
                    members.Clear();
                    GetMembers(kv.Key, members);
                    foreach (var m in members)
                        if (EntityManager.Exists(m) && EntityManager.GetComponentData<PlacemakerMember>(m).Role == MemberRole.ParkCompanion) { companion = m; break; }
                    bool wanted = BuildService.ApplyParkStats && companions.Available && rec.ParkStats.Applied.ContainsKey(ParkStatIds.Companion);
                    if (companion != Entity.Null)
                    {
                        var prefab = EntityManager.HasComponent<PrefabRef>(companion) ? EntityManager.GetComponentData<PrefabRef>(companion).m_Prefab : Entity.Null;
                        bool broken = prefab == Entity.Null || !EntityManager.Exists(prefab) || !EntityManager.HasComponent<ParkData>(prefab);
                        if (!wanted || broken)
                        {
                            float2 at = default; float yaw = 0;
                            if (EntityManager.HasComponent<global::Game.Objects.Transform>(companion))
                            {
                                var t = EntityManager.GetComponentData<global::Game.Objects.Transform>(companion);
                                at = t.m_Position.xz;
                                var f = math.mul(t.m_Rotation, new float3(0, 0, 1));
                                yaw = math.atan2(f.x, f.z);
                            }
                            if (!rec.ParkStats.Applied.ContainsKey(ParkStatIds.CompanionX) && EntityManager.HasComponent<global::Game.Objects.Transform>(companion))
                            {
                                // a companion from before the fork: keep where it stood, so turning the setting on again restores it
                                rec.ParkStats.Applied[ParkStatIds.CompanionX] = at.x;
                                rec.ParkStats.Applied[ParkStatIds.CompanionZ] = at.y;
                                rec.ParkStats.Applied[ParkStatIds.CompanionYaw] = yaw;
                                WriteRecord(kv.Value, rec);
                            }
                            cleanup.Remove(companion);
                            companion = Entity.Null;
                            removed++;
                            if (wanted && broken)
                            {
                                companions.Request(kv.Key, ParkPrefabKey.From(rec.ParkStats.Applied, rec.ParkStats.LeisureType == (int)LeisureKind.CityBeach).Name, at, yaw);
                                requested++;
                            }
                        }
                    }
                    else if (wanted && rec.ParkStats.Applied.TryGetValue(ParkStatIds.Companion, out var state))
                    {
                        // (fork, B-R5) Requested but never created (0), or created and lost since (1): request it again where
                        // the build put it. Receipts from before the fork have no position: they keep tourism only.
                        if (rec.ParkStats.Applied.TryGetValue(ParkStatIds.CompanionX, out var x) && rec.ParkStats.Applied.TryGetValue(ParkStatIds.CompanionZ, out var z))
                        {
                            rec.ParkStats.Applied.TryGetValue(ParkStatIds.CompanionYaw, out var yaw);
                            companions.Request(kv.Key, ParkPrefabKey.From(rec.ParkStats.Applied, rec.ParkStats.LeisureType == (int)LeisureKind.CityBeach).Name,
                                new float2((float)x, (float)z), (float)yaw);
                            requested++;
                            Mod.Log.Info($"Placemaker: the park companion of group {kv.Key} was {(state == 1 ? "lost" : "never created")}; requested again.");
                        }
                        else if (state == 1)
                            Mod.Log.Warn($"Placemaker: the park companion of group {kv.Key} is gone (removed by another tool?); its lot carries attractiveness again.");
                    }

                    if (companion != Entity.Null) companions.Watch(kv.Key, companion);
                    var owner = g.Owner;
                    if (owner == Entity.Null || !EntityManager.Exists(owner)) continue;
                    bool has = rec.ParkStats.Applied.TryGetValue(ParkStatIds.InGameAttractiveness, out double value) && value > 0;
                    bool present = EntityManager.HasComponent<AttractivenessProvider>(owner);
                    bool lotShould = BuildService.ApplyParkStats && has && companion == Entity.Null;
                    if (present && !lotShould)
                    {
                        EntityManager.RemoveComponent<AttractivenessProvider>(owner);
                        lotRemoved++;
                    }
                    else if (lotShould && !present && BuildService.ApplyAttractiveness(EntityManager, owner, value, 0) > 0)
                        lotAdded++;
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"Placemaker: park stats of group {kv.Key} not synced: {e.Message}");
                }
            }
            if (lotAdded + lotRemoved + removed + requested > 0)
                Mod.Log.Info($"Placemaker park stats after load: lot attractiveness {lotAdded} restored / {lotRemoved} removed, "
                    + $"{removed} companions removed, {requested} recreated.");
        }
    }
}
