using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using Systems.Effects;
using UnityEngine;

namespace FirstShotSmoothness.Diagnostics
{
    /// <summary>
    /// Causal tests, not optimizations. Each one suppresses a single subsystem for the local
    /// player's shots, so a comparison with it on and off shows that subsystem's share of a
    /// hitch. They change what the game does and must stay off outside a controlled test.
    /// </summary>
    internal static class Isolation
    {
        public static List<string> Install(Harmony harmony, bool muzzle, bool audio, bool impacts, bool hearing, bool muzzleLight, bool hideRounds)
        {
            var lines = new List<string>();
            if (muzzle)
            {
                Skip(harmony, lines, AccessTools.DeclaredMethod(typeof(Firearms), nameof(Firearms.PlayShotEffects), new[] { typeof(bool), typeof(float) }), nameof(SkipInLocalShot), "muzzle effects");
            }
            if (audio)
            {
                Skip(harmony, lines, AccessTools.DeclaredMethod(typeof(WeaponSoundPlayer), nameof(WeaponSoundPlayer.FireBullet), new[] { typeof(Ammo), typeof(Vector3), typeof(Vector3), typeof(float), typeof(bool), typeof(bool), typeof(bool) }), nameof(SkipInLocalShot), "weapon audio");
            }
            if (impacts)
            {
                Skip(harmony, lines, AccessTools.DeclaredMethod(typeof(EffectsCommutator), nameof(EffectsCommutator.PlayHitEffect), new[] { typeof(Shot), typeof(PlayerHitInfo) }), nameof(SkipLocalImpact), "impact effects");
            }
            if (hearing)
            {
                Skip(harmony, lines, AccessTools.DeclaredMethod(typeof(GlobalEventDispatcher), nameof(GlobalEventDispatcher.PlaySound), new[] { typeof(IPlayer), typeof(Vector3), typeof(float), typeof(AISoundType) }), nameof(SkipInLocalShot), "bot hearing");
            }
            if (muzzleLight)
            {
                Skip(harmony, lines, AccessTools.DeclaredMethod(typeof(MuzzleLight), nameof(MuzzleLight.OnShot)), nameof(SkipInLocalShot), "muzzle light");
            }
            if (hideRounds)
            {
                Hook(harmony, lines, AccessTools.DeclaredMethod(typeof(Firearms), nameof(Firearms.SetRoundIntoWeapon), new[] { typeof(Ammo), typeof(int) }), nameof(HideChambered), "hide chambered round");
                Hook(harmony, lines, AccessTools.DeclaredMethod(typeof(Firearms), nameof(Firearms.SetPatronInShellPort), new[] { typeof(EFT.AssetsManager.AmmoPoolObject), typeof(int) }), nameof(HideInPort), "hide round in shell port");
                Hook(harmony, lines, AccessTools.DeclaredMethod(typeof(Firearms), nameof(Firearms.SpawnExtractingCartridge), new[] { typeof(Vector3), typeof(EFT.AssetsManager.AmmoPoolObject) }), nameof(ShowEjected), "show ejected case");
            }
            return lines;
        }

        private static void Hook(Harmony harmony, List<string> lines, MethodBase method, string postfix, string label)
        {
            if (method == null)
            {
                lines.Add("ISOLATION MISSING " + label);
                return;
            }
            harmony.Patch(method, null, new HarmonyMethod(typeof(Isolation), postfix));
            lines.Add("ISOLATION ON " + label);
        }

        // Tests whether drawing the unseen round matters: the local player's round stays hidden
        // inside the weapon and shows again once ejected. Rounds taken out some other way, such as
        // clearing a jam, can stay hidden until a restart, which is one reason this is test-only.
        private static void HideChambered(Firearms __instance, int chamberNumber)
        {
            if (__instance.Player != null && __instance.Player.IsYourPlayer)
            {
                SetDrawn(__instance._ammoObjectInWeapon[chamberNumber], false);
            }
        }

        private static void HideInPort(Firearms __instance, EFT.AssetsManager.AmmoPoolObject ammoObject)
        {
            if (__instance.Player != null && __instance.Player.IsYourPlayer)
            {
                SetDrawn(ammoObject, false);
            }
        }

        private static void ShowEjected(EFT.AssetsManager.AmmoPoolObject __result)
        {
            SetDrawn(__result, true);
        }

        private static void SetDrawn(EFT.AssetsManager.AmmoPoolObject round, bool drawn)
        {
            if (round != null)
            {
                foreach (Renderer renderer in round.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.forceRenderingOff = !drawn;
                }
            }
        }

        private static void Skip(Harmony harmony, List<string> lines, MethodBase method, string prefix, string label)
        {
            if (method == null)
            {
                lines.Add("ISOLATION MISSING " + label);
                return;
            }
            harmony.Patch(method, new HarmonyMethod(typeof(Isolation), prefix) { priority = Priority.First });
            lines.Add("ISOLATION ON " + label);
        }

        private static bool SkipInLocalShot()
        {
            return !Probes.InLocalShot;
        }

        private static bool SkipLocalImpact(Shot info)
        {
            Player main = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            return main == null || info.PlayerProfileID != main.ProfileId;
        }
    }
}
