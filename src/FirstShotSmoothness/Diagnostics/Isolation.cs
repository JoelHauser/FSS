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
        public static List<string> Install(Harmony harmony, bool muzzle, bool audio, bool impacts, bool hearing)
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
            return lines;
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
