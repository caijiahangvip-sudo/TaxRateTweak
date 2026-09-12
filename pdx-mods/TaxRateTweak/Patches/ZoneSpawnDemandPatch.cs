using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Game.Simulation;
using HarmonyLib;
using TaxRateTweak.Services;
using TaxRateTweak.Systems;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TaxRateTweak.Patches
{
    [HarmonyPatch(typeof(ZoneSpawnSystem), "OnUpdate")]
    public static class ZoneSpawnDemandPatch
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var calls = code.Where(instruction => instruction.operand is MethodInfo method &&
                method.DeclaringType == typeof(JobChunkExtensions) && method.Name == "ScheduleParallel" &&
                method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(ZoneSpawnSystem.EvaluateSpawnAreas) &&
                method.GetParameters().Length == 3 && !method.GetParameters()[0].ParameterType.IsByRef).ToList();
            if (calls.Count != 1) throw new InvalidOperationException("Unsupported ZoneSpawnSystem scheduling layout; keeping vanilla simulation.");
            foreach (var instruction in code)
            {
                if (ReferenceEquals(instruction, calls[0]))
                {
                    var loadSystem = new CodeInstruction(OpCodes.Ldarg_0);
                    loadSystem.labels.AddRange(instruction.labels);
                    instruction.labels.Clear();
                    yield return loadSystem;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(ZoneSpawnDemandPatch), nameof(Schedule));
                }
                yield return instruction;
            }
        }

        public static JobHandle Schedule(ZoneSpawnSystem.EvaluateSpawnAreas original, EntityQuery query,
            JobHandle dependency, ZoneSpawnSystem system)
        {
            var demand = system.World.GetExistingSystemManaged<FiveDensityDemandSystem>();
            if (!Mod.FiveDensityReady || !Settings.Enabled || !Settings.EnableDensityTax || demand == null || !demand.Ready)
                return original.ScheduleParallel(query, dependency);

            var groups = new BuildingGroups(Allocator.TempJob);
            var partition = new PartitionJob { Original = original, Categories = demand.Categories, Groups = groups };
            var partitionHandle = partition.Schedule(dependency);
            var evaluation = new EvaluateJob
            {
                Original = original, Other = groups.Other, Low = groups.Low, Row = groups.Row,
                Medium = groups.Medium, High = groups.High, LowRent = groups.LowRent, Demands = demand.Levels
            };
            var evaluationHandle = evaluation.ScheduleParallel(query, partitionHandle);
            demand.AddReader(evaluationHandle);
            return groups.Dispose(evaluationHandle);
        }

        private struct BuildingGroups
        {
            public NativeList<ArchetypeChunk> Other;
            public NativeList<ArchetypeChunk> Low;
            public NativeList<ArchetypeChunk> Row;
            public NativeList<ArchetypeChunk> Medium;
            public NativeList<ArchetypeChunk> High;
            public NativeList<ArchetypeChunk> LowRent;

            public BuildingGroups(Allocator allocator)
            {
                Other = new NativeList<ArchetypeChunk>(64, allocator);
                Low = new NativeList<ArchetypeChunk>(64, allocator);
                Row = new NativeList<ArchetypeChunk>(64, allocator);
                Medium = new NativeList<ArchetypeChunk>(64, allocator);
                High = new NativeList<ArchetypeChunk>(64, allocator);
                LowRent = new NativeList<ArchetypeChunk>(64, allocator);
            }

            public NativeList<ArchetypeChunk> Get(int category)
            {
                switch (category)
                {
                    case 0: return Low;
                    case 1: return Row;
                    case 2: return Medium;
                    case 3: return High;
                    case 4: return LowRent;
                    default: return Other;
                }
            }

            public JobHandle Dispose(JobHandle dependency)
            {
                var first = JobHandle.CombineDependencies(Other.Dispose(dependency), Low.Dispose(dependency), Row.Dispose(dependency));
                var second = JobHandle.CombineDependencies(Medium.Dispose(dependency), High.Dispose(dependency), LowRent.Dispose(dependency));
                return JobHandle.CombineDependencies(first, second);
            }
        }

        [BurstCompile]
        private struct PartitionJob : IJob
        {
            public ZoneSpawnSystem.EvaluateSpawnAreas Original;
            [ReadOnly] public NativeParallelHashMap<Entity, int> Categories;
            public BuildingGroups Groups;

            public void Execute()
            {
                for (int index = 0; index < Original.m_BuildingChunks.Length; index++)
                {
                    var chunk = Original.m_BuildingChunks[index];
                    var zoneType = chunk.GetSharedComponent(Original.m_BuildingSpawnGroupType).m_ZoneType;
                    Entity zone = Original.m_ZonePrefabs[zoneType];
                    int category = Categories.TryGetValue(zone, out int found) ? found : -1;
                    Groups.Get(category).Add(chunk);
                }
            }
        }

        [BurstCompile]
        private struct EvaluateJob : IJobChunk
        {
            public ZoneSpawnSystem.EvaluateSpawnAreas Original;
            [ReadOnly] public NativeList<ArchetypeChunk> Other;
            [ReadOnly] public NativeList<ArchetypeChunk> Low;
            [ReadOnly] public NativeList<ArchetypeChunk> Row;
            [ReadOnly] public NativeList<ArchetypeChunk> Medium;
            [ReadOnly] public NativeList<ArchetypeChunk> High;
            [ReadOnly] public NativeList<ArchetypeChunk> LowRent;
            public DensityDemand.Values Demands;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                if (Original.m_SpawnCommercial != 0 || Original.m_SpawnIndustrial != 0 || Original.m_SpawnStorage != 0)
                {
                    var commercial = Original;
                    commercial.m_SpawnResidential = 0;
                    commercial.m_BuildingChunks = Other;
                    commercial.Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
                }
                for (int category = 0; category < DensityDemand.Count; category++)
                {
                    int value = Demands.Get(category);
                    var buildings = category == 0 ? Low : category == 1 ? Row : category == 2 ? Medium : category == 3 ? High : LowRent;
                    if (value < Original.m_MinDemand || buildings.Length == 0) continue;
                    var residential = Original;
                    residential.m_BuildingChunks = buildings;
                    residential.m_ResidentialDemands = new int3(value);
                    residential.m_SpawnResidential = 1;
                    residential.m_SpawnCommercial = 0;
                    residential.m_SpawnIndustrial = 0;
                    residential.m_SpawnStorage = 0;
                    residential.Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
                }
            }
        }
    }
}
