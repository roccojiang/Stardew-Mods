﻿using Harmony;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.Buildings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BetterJunimos.Patches;
using static BetterJunimos.Patches.ListExtensions;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Characters;

namespace BetterJunimos {
    public class BetterJunimos : Mod {
        internal ModConfig Config;

        public override void Entry(IModHelper helper) {
            Config = Helper.ReadConfig<ModConfig>();

            Util.Config = Config;
            Util.Reflection = Helper.Reflection;

            JunimoAbilities junimoAbilities = new JunimoAbilities();
            junimoAbilities.Capabilities = Config.JunimoCapabilities;
            JunimoPayments junimoPayments = new JunimoPayments();
            junimoPayments.Payment = Config.JunimoPayment;

            Util.Abilities = junimoAbilities;
            Util.Payments = junimoPayments;
            Util.MaxRadius = Config.JunimoPayment.WorkForWages ? Util.UnpaidRadius : Config.JunimoImprovements.MaxRadius;

            Helper.Content.AssetEditors.Add(new JunimoEditor(Helper.Content));

            InputEvents.ButtonPressed += InputEvents_ButtonPressed;
            MenuEvents.MenuClosed += MenuEvents_MenuClosed;
            TimeEvents.AfterDayStarted += TimeEvents_AfterDayStarted;

            DoHarmonyRegistration();
        }

        private void DoHarmonyRegistration() {
            HarmonyInstance harmony = HarmonyInstance.Create("com.hawkfalcon.BetterJunimos");
            // Thank you to Cat (danvolchek) for this harmony setup implementation
            // https://github.com/danvolchek/StardewMods/blob/master/BetterGardenPots/BetterGardenPots/BetterGardenPotsMod.cs#L29
            IList<Tuple<string, Type, Type>> replacements = new List<Tuple<string, Type, Type>>();

            // Junimo Harvester patches
            Type junimoType = typeof(JunimoHarvester);
            replacements.Add("foundCropEndFunction", junimoType, typeof(PatchFindingCropEnd));
            replacements.Add("tryToHarvestHere", junimoType, typeof(PatchHarvestAttemptToCustom));
            replacements.Add("pokeToHarvest", junimoType, typeof(PatchPokeToHarvest));
            replacements.Add("update", junimoType, typeof(PatchJunimoShake));
            if (Config.JunimoImprovements.MaxRadius > Util.DefaultRadius || Config.JunimoPayment.WorkForWages) {
                replacements.Add("pathfindToRandomSpotAroundHut", junimoType, typeof(PatchPathfind));
                replacements.Add("pathFindToNewCrop_doWork", junimoType, typeof(PatchPathfindDoWork));
            }

            // Junimo Hut patches
            Type junimoHutType = typeof(JunimoHut);
            replacements.Add("areThereMatureCropsWithinRadius", junimoHutType, typeof(PatchSearchAroundHut));

            // replacements for hardcoded max junimos
            replacements.Add("Update", junimoHutType, typeof(ReplaceJunimoHutUpdate));
            replacements.Add("getUnusedJunimoNumber", junimoHutType, typeof(ReplaceJunimoHutNumber));

            // fix stupid bugs in SDV 
            Type chestType = typeof(Chest);
            replacements.Add("grabItemFromChest", chestType, typeof(ChestPatchFrom));
            replacements.Add("grabItemFromInventory", chestType, typeof(ChestPatchTo));

            foreach (Tuple<string, Type, Type> replacement in replacements) {
                MethodInfo original = replacement.Item2.GetMethods(BindingFlags.Instance | BindingFlags.Public).ToList().Find(m => m.Name == replacement.Item1);

                MethodInfo prefix = replacement.Item3.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(item => item.Name == "Prefix");
                MethodInfo postfix = replacement.Item3.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(item => item.Name == "Postfix");

                harmony.Patch(original, prefix == null ? null : new HarmonyMethod(prefix), postfix == null ? null : new HarmonyMethod(postfix));
            }
        }

        void InputEvents_ButtonPressed(object sender, EventArgsInput e) {
            if (!Context.IsWorldReady) { return; }

            if (e.Button == Config.Other.SpawnJunimoKeybind) {
                SpawnJunimoCommand();
            }
        }

        // Closed Junimo Hut menu
        void MenuEvents_MenuClosed(object sender, EventArgsClickableMenuClosed e) {
            if (!Config.JunimoPayment.WorkForWages) return;
            if (e.PriorMenu is StardewValley.Menus.ItemGrabMenu menu) {
                if (menu.specialObject != null && menu.specialObject is JunimoHut hut) {
                    CheckForWages(hut);
                }
            }
        }

        void TimeEvents_AfterDayStarted(object sender, EventArgs e) {
            if (Config.JunimoPayment.WorkForWages) {
                Util.Payments.JunimoPaymentsToday.Clear();
                Util.Payments.WereJunimosPaidToday = false;
                Util.MaxRadius = Util.UnpaidRadius;

                Farm farm = Game1.getFarm();
                foreach (JunimoHut hut in farm.buildings.OfType<JunimoHut>()) {
                    CheckForWages(hut);
                }

                if (!Util.Payments.WereJunimosPaidToday) {
                    Util.SendMessage("Junimos will not work until they are paid");
                }
            }
            
            if (!Config.FunChanges.JunimosAlwaysHaveLeafUmbrellas) {
                // reset for rainy days
                Helper.Content.InvalidateCache(@"Characters\Junimo");
            }
        }

        public static void SpawnJunimoCommand() {
            if (Game1.player.currentLocation.IsFarm) {
                Farm farm = Game1.getFarm();
                Random rand = new Random();

                IEnumerable<JunimoHut> huts = farm.buildings.OfType<JunimoHut>();
                if (huts.Count() <= 0) {
                    Util.SendMessage("There must be a Junimo Hut to spawn a Junimo");
                    return;
                }
                JunimoHut hut = huts.ElementAt(rand.Next(0, huts.Count()));
                Util.SpawnJunimoAtPosition(Game1.player.Position, hut, rand.Next(4, 100));
            }
            else {
                Util.SendMessage("Can only spawn Junimos on Farm");
            }
        }

        private void CheckForWages(JunimoHut hut) {
            if (!Util.Payments.WereJunimosPaidToday && Util.Payments.ReceivePaymentItems(hut)) {
                Util.Payments.WereJunimosPaidToday = true;
                Util.MaxRadius = Config.JunimoImprovements.MaxRadius;
                Util.SendMessage("Junimos are happy with their payment!");
            }
        }
    }
}
