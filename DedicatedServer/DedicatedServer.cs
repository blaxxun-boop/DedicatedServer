using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace DedicatedServer;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class DedicatedServer : BaseUnityPlugin
{
	private const string ModName = "DedicatedServer";
	private const string ModVersion = "1.0.3";
	private const string ModGUID = "org.bepinex.plugins.dedicatedserver";

	private static readonly ConfigSync configSync = new(ModName) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion, ModRequired = true };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0,
	}

	public void Awake()
	{
		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);
	}

	[HarmonyPatch]
	private static class SetServerAsOwner
	{
		private static IEnumerable<MethodInfo> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.SetOwner)),
			AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.SetOwnerInternal)),
		};

		private static void Prefix(ref long uid)
		{
			if (uid == 0 && ZNet.instance.IsServer())
			{
				uid = ZDOMan.instance.m_sessionID;
			}
		}
	}

	[HarmonyPatch(typeof(ZDO), nameof(ZDO.Load))]
	private static class SetServerAsOwnerOnLoad
	{
		private static void Postfix(ZDO __instance)
		{
			if (ZNet.instance.IsServer() && !__instance.Owned)
			{
				__instance.SetOwnerInternal(ZDOMan.instance.m_sessionID);
			}
		}
	}

	[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.IsInPeerActiveArea))]
	private static class ActivateAreas
	{
		private static bool Prefix(ref bool __result, long uid)
		{
			if (uid == (ZNet.instance.IsServer() ? ZDOMan.GetSessionID() : ZNet.instance.GetServerPeer().m_uid))
			{
				__result = true;
				return false;
			}

			return true;
		}
	}

	[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ReleaseNearbyZDOS))]
	private static class DoNotAssignPeerToZDO
	{
		private static bool Prefix(long uid)
		{
			return uid == ZDOMan.GetSessionID();
		}
	}

	[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects))]
	private static class LoadActiveAreas
	{
		private static bool Prefix(ZDOMan __instance, SimulationDistance simulationDistance, List<ZDO> sectorObjects, List<ZDO>? distantSectorObjects = null)
		{
			if (ZNet.instance.IsServer() && sectorObjects != __instance.m_tempSectorObjects /* exempt the call in ZDOMan.CreateSyncList */)
			{
				HashSet<Vector2s> sectorPoints = new();
				HashSet<Vector2s> distantSectorPoints = new();

				void AddSectorPoints(Vector3 pos)
				{
					Vector2s sector = ZoneSystem.GetZone(pos);

					sectorPoints.Add(sector);
					for (int index = 1; index <= simulationDistance.NearSimulationDistance; ++index)
					{
						for (short x = (short)(sector.x - index); x <= sector.x + index; ++x)
						{
							Vector2s vector2s1 = new Vector2s(x, sector.y - index);
							Vector2s vector2s2 = new Vector2s(x, sector.y + index);
							if (ZoneSystem.instance.ZonesWithinRadius(sector, vector2s1, simulationDistance.NearSimulationDistance) || simulationDistance.IsClassic)
							{
								sectorPoints.Add(vector2s1);
							}

							if (ZoneSystem.instance.ZonesWithinRadius(sector, vector2s2, simulationDistance.NearSimulationDistance) || simulationDistance.IsClassic)
							{
								sectorPoints.Add(vector2s2);
							}
						}

						for (int y = sector.y - index + 1; y <= sector.y + index - 1; ++y)
						{
							Vector2s vector2s3 = new Vector2s(sector.x - index, y);
							Vector2s vector2s4 = new Vector2s(sector.x + index, y);
							if (ZoneSystem.instance.ZonesWithinRadius(sector, vector2s3, simulationDistance.NearSimulationDistance) || simulationDistance.IsClassic)
							{
								sectorPoints.Add(vector2s3);
							}

							if (ZoneSystem.instance.ZonesWithinRadius(sector, vector2s4, simulationDistance.NearSimulationDistance) || simulationDistance.IsClassic)
							{
								sectorPoints.Add(vector2s4);
							}
						}
					}

					List<ZDO> objects = distantSectorObjects ?? sectorObjects;
					int distantObjectsStart = 1;
					if (simulationDistance.IsClassic)
					{
						distantObjectsStart += simulationDistance.NearSimulationDistance;
					}

					for (int index = distantObjectsStart; index <= simulationDistance.TotalSimulationDistance; ++index)
					{
						for (int x = sector.x - index; x <= sector.x + index; ++x)
						{
							Vector2s vector2s = new Vector2s(x, sector.y - index);
							Vector2s sector1 = new Vector2s(x, sector.y + index);
							if (ZoneSystem.instance.ZonesWithinRadius(sector, vector2s, simulationDistance.TotalSimulationDistance, true) || simulationDistance.IsClassic)
							{
								distantSectorPoints.Add(vector2s);
							}

							if (ZoneSystem.instance.ZonesWithinRadius(sector, vector2s, simulationDistance.TotalSimulationDistance, true) || simulationDistance.IsClassic)
							{
								distantSectorPoints.Add(sector1);
							}
						}

						for (int y = sector.y - index + 1; y <= sector.y + index - 1; ++y)
						{
							Vector2s vector2s = new Vector2s(sector.x - index, y);
							Vector2s sector2 = new Vector2s(sector.x + index, y);
							if (ZoneSystem.instance.ZonesWithinRadius(sector, vector2s, simulationDistance.TotalSimulationDistance, true) || simulationDistance.IsClassic)
							{
								__instance.FindDistantObjects(vector2s, objects, __instance.m_visitedSectorIndices);
							}

							if (ZoneSystem.instance.ZonesWithinRadius(sector, vector2s, simulationDistance.TotalSimulationDistance, true) || simulationDistance.IsClassic)
							{
								__instance.FindDistantObjects(sector2, objects, __instance.m_visitedSectorIndices);
							}
						}
					}
				}

				foreach (ZNetPeer peer in ZNet.instance.m_peers)
				{
					AddSectorPoints(peer.GetRefPos());
				}

				if (!ZNet.instance.IsDedicated())
				{
					AddSectorPoints(Player.m_localPlayer?.transform.position ?? ZNet.instance.GetReferencePosition());
				}

				__instance.m_visitedSectorIndices.Clear();

				foreach (Vector2s sector in sectorPoints)
				{
					distantSectorPoints.Remove(sector);
					ZoneSystem.instance.PokeLocalZone(sector);
					if (!ZoneSystem.instance.m_zones.ContainsKey(sector))
					{
						continue;
					}

					__instance.FindObjects(sector, sectorObjects, __instance.m_visitedSectorIndices);
				}

				foreach (ZDO zdo in sectorObjects)
				{
					if (zdo.Persistent && !zdo.Owner && !__instance.IsInPeerActiveArea(zdo.GetPosition(), zdo.GetOwner()))
					{
						zdo.SetOwner(__instance.m_sessionID);
					}
				}

				List<ZDO> objects = distantSectorObjects ?? sectorObjects;
				foreach (Vector2s sector in distantSectorPoints)
				{
					ZoneSystem.instance.PokeLocalZone(sector);
					if (!ZoneSystem.instance.m_zones.ContainsKey(sector)) continue;
					__instance.FindDistantObjects(sector, objects, __instance.m_visitedSectorIndices);
				}

				return false;
			}

			return true;
		}
	}

	[HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.UpdateSpawning))]
	private static class RemoveLocalPlayerCheckFromSpawnSystem
	{
		private static readonly FieldInfo localPlayer = AccessTools.DeclaredField(typeof(Player), nameof(Player.m_localPlayer));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			bool isSkipping = false;
			List<Label>? labels = null;
			foreach (CodeInstruction instruction in instructions)
			{
				if (!isSkipping)
				{
					if (instruction.LoadsField(localPlayer))
					{
						labels = instruction.labels;
						isSkipping = true;
					}
					else
					{
						if (labels is not null)
						{
							instruction.labels.AddRange(labels);
							labels = null;
						}

						yield return instruction;
					}
				}
				else if (instruction.opcode == OpCodes.Ret)
				{
					isSkipping = false;
				}
			}
		}
	}

	[HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.UpdateSpawning))]
	private static class DropHeightmapBiomeCheck
	{
		private static readonly MethodInfo haveBiome = AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.HaveBiome));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
				if (instruction.Calls(haveBiome))
				{
					yield return new CodeInstruction(OpCodes.Pop);
					yield return new CodeInstruction(OpCodes.Ldc_I4_1);
				}
			}
		}
	}

	[HarmonyPatch(typeof(Pickable), nameof(Pickable.RPC_Pick))]
	private static class FixVanillaPickableFetchingLocalPlayer
	{
		private static readonly FieldInfo localPlayer = AccessTools.DeclaredField(typeof(Player), nameof(Player.m_localPlayer));

		private static Player mockPlayer(Player? player) => player ?? new Player { m_nview = new ZNetView() };

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
				if (instruction.LoadsField(localPlayer))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(FixVanillaPickableFetchingLocalPlayer), nameof(mockPlayer)));
				}
			}
		}
	}
}