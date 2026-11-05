using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using Systems.Effects;
using UnityEngine;

namespace FirstShotSmoothness.Diagnostics
{
    /// <summary>What a probe times. The numbers appear in events.csv; names in session.txt.</summary>
    internal enum ProbeId
    {
        Shot,
        InitiateShot,
        OnMakingShot,
        BotHearingBroadcast,
        BallisticsCreateShot,
        MuzzleEffects,
        FireBullet,
        AudioQueueUpdate,
        InventoryOperations,
        ChamberNextRound,
        CreateItem,
        PoolPop,
        ShellEject,
        RecoilAnimation,
        BallisticsUpdate,
        ShotDelegate,
        PlayHitEffect,
        ImpactEmit,
        ApplyShot,
        Count
    }

    /// <summary>How much of the firing path gets patched.</summary>
    public enum ProbeLevel
    {
        /// <summary>
        /// Shots, trigger and impacts only. Harmony compiles what it patches, which hides that
        /// method's own first-use JIT, so this level disturbs the JIT picture the least.
        /// </summary>
        EventsOnly,

        /// <summary>Also times each stage of the firing path.</summary>
        Stages
    }

    /// <summary>
    /// The Harmony side of the diagnostics. Every target is a verified method of this game
    /// build, resolved by exact signature at startup; a target that is missing is reported and
    /// skipped, and nothing else depends on it.
    /// </summary>
    internal static class Probes
    {
        private sealed class Target
        {
            public ProbeId Id;
            public Type Type;
            public string Name;
            public Type[] Args;
            public bool Quiet;
            public Action<Harmony, MethodBase, ProbeId> Apply;
        }

        // One closed generic per probe gives Harmony a pair of static methods with the id baked
        // in, so a probe costs two clock reads and no lookup.
        private static class Timed<TTag>
        {
            public static int Id;

            public static void Prefix(out int __state)
            {
                __state = Recorder.Enter();
            }

            public static void Postfix(int __state)
            {
                Recorder.Exit(Id, __state);
            }
        }

        private struct T0 { } private struct T1 { } private struct T2 { } private struct T3 { }
        private struct T4 { } private struct T5 { } private struct T6 { } private struct T7 { }
        private struct T8 { } private struct T9 { } private struct T10 { } private struct T11 { }
        private struct T12 { } private struct T13 { } private struct T14 { } private struct T15 { }
        private struct T16 { }

        private static void Time<TTag>(Harmony harmony, MethodBase method, ProbeId id)
        {
            Timed<TTag>.Id = (int)id;
            MethodInfo prefix = typeof(Timed<TTag>).GetMethod(nameof(Timed<TTag>.Prefix));
            MethodInfo postfix = typeof(Timed<TTag>).GetMethod(nameof(Timed<TTag>.Postfix));
            prefix.MethodHandle.GetFunctionPointer();
            postfix.MethodHandle.GetFunctionPointer();
            harmony.Patch(method, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
        }

        private static void Custom(Harmony harmony, MethodBase method, string prefix, string postfix)
        {
            harmony.Patch(method,
                prefix == null ? null : new HarmonyMethod(typeof(Probes), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(Probes), postfix));
        }

        private static Target Stage<TTag>(ProbeId id, Type type, string name, params Type[] args)
        {
            return new Target { Id = id, Type = type, Name = name, Args = args, Apply = Time<TTag> };
        }

        private static Target[] Stages()
        {
            Type controller = typeof(Player.FirearmController);
            Target[] targets =
            {
                Stage<T1>(ProbeId.InitiateShot, controller, nameof(Player.FirearmController.InitiateShot), typeof(IWeapon), typeof(Ammo), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(int), typeof(float)),
                Stage<T2>(ProbeId.OnMakingShot, typeof(Player), nameof(Player.OnMakingShot), typeof(IWeapon), typeof(Vector3)),
                Stage<T3>(ProbeId.BotHearingBroadcast, typeof(GlobalEventDispatcher), nameof(GlobalEventDispatcher.PlaySound), typeof(IPlayer), typeof(Vector3), typeof(float), typeof(AISoundType)),
                Stage<T4>(ProbeId.BallisticsCreateShot, typeof(BallisticsCalculator), nameof(BallisticsCalculator.CreateShot), typeof(Ammo), typeof(Vector3), typeof(Vector3), typeof(int), typeof(string), typeof(Item), typeof(float), typeof(int)),
                Stage<T5>(ProbeId.MuzzleEffects, typeof(Firearms), nameof(Firearms.PlayShotEffects), typeof(bool), typeof(float)),
                Stage<T6>(ProbeId.FireBullet, typeof(WeaponSoundPlayer), nameof(WeaponSoundPlayer.FireBullet), typeof(Ammo), typeof(Vector3), typeof(Vector3), typeof(float), typeof(bool), typeof(bool), typeof(bool)),
                Stage<T7>(ProbeId.AudioQueueUpdate, typeof(SuperBetterAudioQueue), nameof(SuperBetterAudioQueue.Update)),
                Stage<T8>(ProbeId.InventoryOperations, typeof(Player.FirearmController.FireOperationBase), nameof(Player.FirearmController.FireOperationBase.ProcessInventoryOperationsForSingleshotWithMalf), typeof(Weapon.EMalfunctionState).MakeByRefType(), typeof(Weapon.EMalfunctionSource).MakeByRefType()),
                Stage<T9>(ProbeId.ChamberNextRound, typeof(Firearms), nameof(Firearms.SetRoundIntoWeapon), typeof(Ammo), typeof(int)),
                Stage<T10>(ProbeId.CreateItem, typeof(ObjectsFactory), nameof(ObjectsFactory.CreateItem), typeof(Item), typeof(EFT.CameraControl.ECameraType), typeof(IPlayer), typeof(bool)),
                Stage<T11>(ProbeId.PoolPop, typeof(ObjectsFactory.Pools), nameof(ObjectsFactory.Pools.PopOrCreate), typeof(ResourceKey), typeof(bool)),
                Stage<T12>(ProbeId.ShellEject, typeof(Firearms), nameof(Firearms.SpawnExtractingCartridge), typeof(Vector3), typeof(EFT.AssetsManager.AmmoPoolObject)),
                Stage<T13>(ProbeId.RecoilAnimation, typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.Shoot), typeof(float)),
                Stage<T14>(ProbeId.BallisticsUpdate, typeof(BallisticsCalculator), nameof(BallisticsCalculator.ManualUpdate), typeof(float)),
                Stage<T16>(ProbeId.PlayHitEffect, typeof(EffectsCommutator), nameof(EffectsCommutator.PlayHitEffect), typeof(Shot), typeof(PlayerHitInfo)),
                Stage<T15>(ProbeId.ImpactEmit, typeof(Effects.Effect), nameof(Effects.Effect.Emit), typeof(Vector3), typeof(Vector3), typeof(BallisticCollider), typeof(bool), typeof(float), typeof(bool), typeof(bool), typeof(bool), typeof(EPointOfView)),
                Stage<T0>(ProbeId.ApplyShot, typeof(Player), nameof(Player.ApplyShot), typeof(DamageInfo), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(EArmorPlateCollider), typeof(ShotId))
            };
            foreach (Target target in targets)
            {
                target.Quiet = target.Id == ProbeId.AudioQueueUpdate || target.Id == ProbeId.BallisticsUpdate;
            }
            return targets;
        }

        private static Target[] Events()
        {
            Type controller = typeof(Player.FirearmController);
            return new[]
            {
                new Target { Id = ProbeId.Shot, Type = controller, Name = nameof(Player.FirearmController.Shot), Args = new[] { typeof(Weapon), typeof(Ammo), typeof(int), typeof(bool) },
                    Apply = (h, m, id) => Custom(h, m, nameof(ShotPrefix), nameof(ShotPostfix)) },
                new Target { Id = ProbeId.ShotDelegate, Type = typeof(ClientGameWorld), Name = nameof(ClientGameWorld.ShotDelegate), Args = new[] { typeof(Shot) },
                    Apply = (h, m, id) => Custom(h, m, nameof(ImpactPrefix), nameof(ImpactPostfix)) },
                new Target { Id = ProbeId.Count, Type = controller, Name = nameof(Player.FirearmController.SetTriggerPressed), Args = new[] { typeof(bool) },
                    Apply = (h, m, id) => Custom(h, m, nameof(TriggerPrefix), null) },
                new Target { Id = ProbeId.Count, Type = controller, Name = nameof(Player.FirearmController.DryShot), Args = new[] { typeof(int), typeof(bool) },
                    Apply = (h, m, id) => Custom(h, m, nameof(DryPrefix), null) },
                new Target { Id = ProbeId.Count, Type = typeof(BallisticsCalculator), Name = nameof(BallisticsCalculator.CreateShot), Args = new[] { typeof(Ammo), typeof(Vector3), typeof(Vector3), typeof(int), typeof(string), typeof(Item), typeof(float), typeof(int) },
                    Apply = (h, m, id) => Custom(h, m, null, nameof(LinkPostfix)) }
            };
        }

        /// <summary>Patches what the level calls for. Returns one line per target for the report.</summary>
        public static List<string> Install(Harmony harmony, ProbeLevel level)
        {
            var lines = new List<string>();
            var targets = new List<Target>(Events());
            if (level == ProbeLevel.Stages)
            {
                targets.AddRange(Stages());
                // Audio clip state is read where the clip is handed to the queue.
                targets.Add(new Target
                {
                    Id = ProbeId.Count,
                    Type = typeof(SuperBetterAudioQueue),
                    Name = nameof(SuperBetterAudioQueue.Enqueue),
                    Args = new[] { typeof(AudioClip), typeof(AudioClip), typeof(float), typeof(double), typeof(float), typeof(float), typeof(float) },
                    Apply = (h, m, id) => Custom(h, m, nameof(EnqueuePrefix), null)
                });
            }
            foreach (Target target in targets)
            {
                string label = target.Type.Name + "." + target.Name;
                MethodBase method = AccessTools.DeclaredMethod(target.Type, target.Name, target.Args);
                if (method == null)
                {
                    lines.Add("MISSING " + label);
                    continue;
                }
                try
                {
                    if (target.Id != ProbeId.Count)
                    {
                        Recorder.Quiet[(int)target.Id] = target.Quiet;
                    }
                    target.Apply(harmony, method, target.Id);
                    lines.Add("ok " + label);
                }
                catch (Exception e)
                {
                    lines.Add("FAILED " + label + ": " + e.Message);
                }
            }
            return lines;
        }

        /// <summary>
        /// Compiles this plugin's own code ahead of the first shot. Harmony compiles the patched
        /// method but not the prefix it calls, and the generics here compile on first use: left
        /// alone, a tool looking for first-shot JIT would itself be first-shot JIT.
        /// </summary>
        public static int Warm()
        {
            int prepared = 0;
            foreach (Type type in typeof(Probes).Assembly.GetTypes())
            {
                if (type.ContainsGenericParameters)
                {
                    continue;
                }
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!method.IsAbstract && !method.ContainsGenericParameters)
                    {
                        // The call the game's own JitWarmUpper uses. PrepareMethod also works on plain methods, not closed generics.
                        method.MethodHandle.GetFunctionPointer();
                        prepared++;
                    }
                }
            }

            var id = new MongoID(true);
            Templates.TryGetValue(id, out _);
            Templates.Add(id, 0);
            Templates.Clear();
            Instances.TryGetValue("", out _);
            Instances.Add("", 0);
            Instances.Clear();
            App.Weapons.Add(0);
            App.Clear();
            NameTable.Add("");
            NameTable.Clear();
            return prepared;
        }

        // ---- shot context -----------------------------------------------------------------

        private sealed class Seen
        {
            public readonly HashSet<int> Weapons = new HashSet<int>();
            public readonly HashSet<int> Instances = new HashSet<int>();
            public readonly HashSet<int> Ammo = new HashSet<int>();

            public void Clear()
            {
                Weapons.Clear();
                Instances.Clear();
                Ammo.Clear();
            }
        }

        // Ids are small numbers, first seen first; the names only go in the table at first sight.
        public static readonly List<string> NameTable = new List<string>(256);
        private static readonly Dictionary<MongoID, int> Templates = new Dictionary<MongoID, int>(256);
        private static readonly Dictionary<string, int> Instances = new Dictionary<string, int>(256);
        private static readonly Seen App = new Seen();
        private static readonly Seen Raid = new Seen();

        private static bool _shotInApp;
        private static bool _shotInRaid;
        private static long _lastLocalShot;
        private static Player.FirearmController _lastController;
        private static string _localProfile;

        /// <summary>True while the local player's shot runs, for the isolation switches.</summary>
        public static bool InLocalShot;

        public static void OnRaidStart(string localProfile)
        {
            _localProfile = localProfile;
            Raid.Clear();
            _shotInRaid = false;
            _lastLocalShot = 0;
            _lastController = null;
            Recorder.CurrentShot = 0;
        }

        private static int TemplateIndex(MongoID id, Item item)
        {
            if (!Templates.TryGetValue(id, out int index))
            {
                index = NameTable.Count;
                NameTable.Add(id.ToString() + " " + item.Name);
                Templates.Add(id, index);
            }
            return index;
        }

        private static int InstanceIndex(string id)
        {
            if (!Instances.TryGetValue(id, out int index))
            {
                index = NameTable.Count;
                NameTable.Add(id);
                Instances.Add(id, index);
            }
            return index;
        }

        private static Shooter Classify(Player player)
        {
            if (player.IsYourPlayer)
            {
                return Shooter.Local;
            }
            return player.IsAI ? Shooter.Bot : Shooter.Remote;
        }

        // ---- patches ----------------------------------------------------------------------

        private static void ShotPrefix(Player.FirearmController __instance, Weapon weapon, Ammo ammo, out int __state)
        {
            __state = -1;
            Session session = Recorder.Current;
            if (session == null || Environment.CurrentManagedThreadId != Recorder.MainThread)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            Player player = __instance._player;
            Shooter shooter = Classify(player);
            int weaponTemplate = TemplateIndex(weapon.TemplateId, weapon);
            int ammoTemplate = TemplateIndex(ammo.TemplateId, ammo);
            int weaponInstance = InstanceIndex(weapon.Id);

            ShotFlags flags = ShotFlags.None;
            long sinceLast = 0;
            if (shooter == Shooter.Local)
            {
                if (!_shotInApp) { flags |= ShotFlags.FirstInApp; _shotInApp = true; }
                if (!_shotInRaid) { flags |= ShotFlags.FirstInRaid; _shotInRaid = true; }
                if (App.Weapons.Add(weaponTemplate)) { flags |= ShotFlags.WeaponTemplateFirstInApp; }
                if (Raid.Weapons.Add(weaponTemplate)) { flags |= ShotFlags.WeaponTemplateFirstInRaid; }
                if (Raid.Instances.Add(weaponInstance)) { flags |= ShotFlags.WeaponInstanceFirstInRaid; }
                if (App.Ammo.Add(ammoTemplate)) { flags |= ShotFlags.AmmoFirstInApp; }
                if (Raid.Ammo.Add(ammoTemplate)) { flags |= ShotFlags.AmmoFirstInRaid; }
                if (!ReferenceEquals(_lastController, __instance)) { flags |= ShotFlags.FirstSinceDraw; _lastController = __instance; }
                sinceLast = _lastLocalShot == 0 ? 0 : now - _lastLocalShot;
                _lastLocalShot = now;
            }
            if (__instance.IsSilenced)
            {
                flags |= ShotFlags.Silenced;
            }

            long alive = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.AllAlivePlayersList.Count : 0;
            long id = session.AddShot(shooter, weaponTemplate, weaponInstance, ammoTemplate, flags, sinceLast, alive, now);
            if (shooter == Shooter.Local)
            {
                Recorder.CurrentShot = id;
                InLocalShot = true;
            }
            __state = Recorder.Enter();
        }

        private static void ShotPostfix(int __state)
        {
            InLocalShot = false;
            Recorder.Exit((int)ProbeId.Shot, __state);
        }

        private static void ImpactPrefix(Shot shotResult, out int __state)
        {
            __state = -1;
            Session session = Recorder.Current;
            if (session == null || Environment.CurrentManagedThreadId != Recorder.MainThread)
            {
                return;
            }

            BallisticCollider hit = shotResult.HittedBallisticCollider;
            long kind = hit == null ? 0 : hit is BodyPartCollider ? 2 : 1;
            long material = hit == null ? 255 : (long)hit.TypeOfMaterial;
            long local = shotResult.PlayerProfileID == _localProfile ? 1 : 0;
            session.AddEvent(EventKind.Impact, 0, 0, 0, 0, shotResult.FireIndex, kind | material << 8 | local << 16, Stopwatch.GetTimestamp());
            __state = Recorder.Enter();
        }

        private static void ImpactPostfix(int __state)
        {
            Recorder.Exit((int)ProbeId.ShotDelegate, __state);
        }

        private static void TriggerPrefix(Player.FirearmController __instance, bool pressed)
        {
            Session session = Recorder.Current;
            if (session != null && __instance._player.IsYourPlayer)
            {
                session.AddEvent(pressed ? EventKind.TriggerPress : EventKind.TriggerRelease, 0, 0, 0, 0, 0, 0, Stopwatch.GetTimestamp());
            }
        }

        private static void DryPrefix(Player.FirearmController __instance)
        {
            Session session = Recorder.Current;
            if (session != null && __instance._player.IsYourPlayer)
            {
                session.AddEvent(EventKind.DryFire, 0, 0, 0, 0, 0, 0, Stopwatch.GetTimestamp());
            }
        }

        /// <summary>
        /// Ties the ballistics fire index to a shot id, so impacts can find their shot. Hooked on
        /// CreateShot rather than RegisterShot: that one is an empty virtual, and the JIT may
        /// inline an empty base call past any patch.
        /// </summary>
        private static void LinkPostfix(Shot __result)
        {
            Session session = Recorder.Current;
            if (session != null && InLocalShot)
            {
                session.AddEvent(EventKind.ShotLink, 0, Recorder.CurrentShot, 0, 0, __result.FireIndex, 0, Stopwatch.GetTimestamp());
            }
        }

        private static void EnqueuePrefix(AudioClip clip1, AudioClip clip2)
        {
            Session session = Recorder.Current;
            if (session != null && InLocalShot)
            {
                session.AddEvent(EventKind.ClipState, 0, Recorder.CurrentShot, 0, 0, ClipFacts(clip1), ClipFacts(clip2), Stopwatch.GetTimestamp());
            }
        }

        /// <summary>Load state in the low byte, load type above it, -1 for no clip.</summary>
        private static long ClipFacts(AudioClip clip)
        {
            return clip == null ? -1 : (long)clip.loadState | (long)clip.loadType << 8;
        }
    }
}
