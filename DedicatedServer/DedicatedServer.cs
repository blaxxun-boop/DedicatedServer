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
	private const string ModVersion = "1.0.1";
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
		private static bool Prefix(ZDOMan __instance, int area, int distantArea, List<ZDO> sectorObjects, List<ZDO>? distantSectorObjects = null)
		{
			if (ZNet.instance.IsServer() && sectorObjects != __instance.m_tempSectorObjects /* exempt the call in ZDOMan.CreateSyncList */)
			{
				HashSet<Vector2i> sectorPoints = new();
				HashSet<Vector2i> distantSectorPoints = new();

				void AddSectorPoints(Vector3 pos)
				{
					Vector2i sector = ZoneSystem.GetZone(pos);

					sectorPoints.Add(sector);
					for (int index = 1; index <= area; ++index)
					{
						for (int _x = sector.x - index; _x <= sector.x + index; ++_x)
						{
							sectorPoints.Add(new Vector2i(_x, sector.y - index));
							sectorPoints.Add(new Vector2i(_x, sector.y + index));
						}
						for (int _y = sector.y - index + 1; _y <= sector.y + index - 1; ++_y)
						{
							sectorPoints.Add(new Vector2i(sector.x - index, _y));
							sectorPoints.Add(new Vector2i(sector.x + index, _y));
						}
					}
					for (int index = area + 1; index <= area + distantArea; ++index)
					{
						for (int _x = sector.x - index; _x <= sector.x + index; ++_x)
						{
							distantSectorPoints.Add(new Vector2i(_x, sector.y - index));
							distantSectorPoints.Add(new Vector2i(_x, sector.y + index));
						}
						for (int _y = sector.y - index + 1; _y <= sector.y + index - 1; ++_y)
						{
							distantSectorPoints.Add(new Vector2i(sector.x - index, _y));
							distantSectorPoints.Add(new Vector2i(sector.x + index, _y));
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

				foreach (Vector2i sector in sectorPoints)
				{
					ZoneSystem.instance.PokeLocalZone(sector);
					__instance.FindObjects(sector, sectorObjects);
					foreach (ZDO zdo in sectorObjects)
					{
						if (!zdo.Owned && !__instance.IsInPeerActiveArea(sector, zdo.GetOwner()))
						{
							zdo.SetOwner(__instance.m_sessionID);
						}
					}
					distantSectorPoints.Remove(sector);
				}

				List<ZDO> objects = distantSectorObjects ?? sectorObjects;
				foreach (Vector2i sector in distantSectorPoints)
				{
					ZoneSystem.instance.PokeLocalZone(sector);
					__instance.FindDistantObjects(sector, objects);
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
}
