using HarmonyLib;
using JetBrains.Annotations;
using KeyValue.Runtime;
using Model.Definition.Data;
using Model.Ops;
using Model.Ops.Definition;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UI.Builder;
using UI.CompanyWindow;
using UnityEngine;
using UnityEngine.InputSystem.XR;

namespace NS15
{
    namespace InterchangeDesignation
    {
        [HarmonyPatch(typeof(OpsController))]
        public static class OpsControllerPatches
        {
            [HarmonyPatch("InterchangeForPosition"), HarmonyPostfix]
            public static void InterchangeForPositionPostfix(OpsCarPosition position, OpsCarPosition? origin, OpsController __instance, ref Interchange __result)
            {
                // Debug.Log($"Using {__result.Identifier} for a car at {position.Identifier}");
            }

            [HarmonyPatch("AddOrderForInboundCar"), HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> AddOrderForInboundCarTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                List<CodeInstruction> codes = instructions.ToList();
                MethodInfo mi_GetInterchange = AccessTools.Method(typeof(OpsControllerPatches), "GetInterchange");
                for (int i = 0; i < codes.Count; i++)
                {
                    if (i + 6 < codes.Count &&
                        codes[i].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 1].opcode == OpCodes.Ldarg_3 &&
                        codes[i + 2].opcode == OpCodes.Ldloca_S)
                    {
                        yield return codes[i];
                        yield return codes[i + 1];
                        yield return new CodeInstruction(OpCodes.Ldarg_S, 4);
                        yield return new CodeInstruction(OpCodes.Call, mi_GetInterchange);
                        i += 6;
                        // Debug.Log("AddOrderForInboundCar patched");
                    }
                    yield return codes[i];
                }
            }

            internal static Interchange GetInterchange(OpsController opsController, OpsCarPosition position, Industry industry)
            {
                KeyValueObject kvo = (KeyValueObject)AccessTools.Property(typeof(Industry), "KeyValueObject").GetValue(industry);
                Interchange ic;
                if (kvo.Keys.Contains("setInterchange"))
                {
                    ic = opsController.EnabledInterchanges.FirstOrDefault(ic => ic.Identifier == kvo.Get("setInterchange").StringValue) ?? InterchangeForPosition(position, null);
                    // Debug.Log($"GetInterchange returning {ic.Identifier} from set interchange {kvo.Get("setInterchange").StringValue}");
                }
                else
                {
                    ic = InterchangeForPosition(position, null);
                    // Debug.Log($"GetInterchange returning {ic.Identifier}");
                }
                return ic;

                Interchange InterchangeForPosition(OpsCarPosition position, OpsCarPosition? origin) => (Interchange)AccessTools.Method(typeof(OpsController), "InterchangeForPosition").Invoke(opsController, [position, origin]);
            }
        }

        [HarmonyPatch(typeof(IndustryContext))]
        public static class IndustryContextPatches
        {
            [HarmonyPatch("CreateCarDescriptorForOrder"), HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> CreateCarDescriptorForOrderTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                List<CodeInstruction> codes = instructions.ToList();
                MethodInfo mi_UpdatePayment = AccessTools.Method(typeof(IndustryContextPatches), "UpdatePayment");
                Label paymentJumpLabel = generator.DefineLabel();
                Label paymentZeroLabel = generator.DefineLabel();

                for (int i = 0; i < codes.Count; i++)
                {
                    if (i + 13 < codes.Count && 
                        codes[i].opcode == OpCodes.Brtrue_S &&
                        codes[i + 10].opcode == OpCodes.Br_S &&
                        codes[i + 11].opcode == OpCodes.Ldc_I4_0 &&
                        codes[i + 12].opcode == OpCodes.Stloc_S)
                    {
                        codes[i + 11].labels.Add(paymentZeroLabel);
                        codes[i + 12].labels.Add(paymentJumpLabel);
                        yield return new CodeInstruction(OpCodes.Brtrue_S, paymentZeroLabel);
                        yield return codes[i + 1];
                        yield return codes[i + 2];
                        yield return codes[i + 3];
                        yield return codes[i + 4];
                        yield return codes[i + 5];
                        yield return codes[i + 6];
                        yield return codes[i + 7];
                        yield return codes[i + 8];
                        yield return codes[i + 9];
                        yield return new CodeInstruction(OpCodes.Ldarg_1);
                        yield return new CodeInstruction(OpCodes.Call, mi_UpdatePayment);
                        yield return new CodeInstruction(OpCodes.Br_S, paymentJumpLabel);
                        yield return codes[i + 11];
                        i += 12;
                        // Debug.Log("CreateCarDescriptorForOrder patched");
                    }
                    yield return codes[i];
                }
            }

            public static int UpdatePayment(int payment, Order order)
            {
                // Debug.Log($"Payment pre-process: {payment}");
                Industry industry = OpsController.Shared.AllIndustries.First(ind => ind.Components.Any(comp => comp.Identifier == order.Destination.Identifier));
                if (industry == null) { return payment; }
                KeyValueObject kvo = (KeyValueObject)AccessTools.Property(typeof(Industry), "KeyValueObject").GetValue(industry);
                if (kvo == null || !kvo.Keys.Contains("setInterchange")) { return payment; }
                payment = (int)(payment * Main.settings.PayMultiplier);
                // Debug.Log($"Payment post-process: {payment}");
                return payment;
            }
        }

        [HarmonyPatch]
        public static class IndustryDetailBuilderPatch_AddContractSection
        {
            static string[] blacklistIndustryIdentifier = ["legoscrosstraffic"];
            public static MethodBase TargetMethod() 
            {
                return typeof(LocationsPanelBuilder).GetNestedType("IndustryDetailBuilder", BindingFlags.NonPublic).GetMethod("AddContractSection", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            public static void Postfix(Industry ____industry, UIPanelBuilder builder)
            {
                if (blacklistIndustryIdentifier.Contains(____industry.identifier)) { return; }

                List<IndustryComponent> components = ____industry.Components.Where(NeedsInterchange).ToList();

                if (components.Count == 0) { return; }

                KeyValueObject kvo = (KeyValueObject)AccessTools.Property(typeof(Industry), "KeyValueObject").GetValue(____industry);

                Interchange[] interchanges = OpsController.Shared.AllInterchanges;

                List<string> interchangeNames = new();

                foreach (Interchange ic in interchanges)
                {
                    string icName = ic.DisplayName + (!ic.Disabled && !ic.ProgressionDisabled && !ic.Industry.ProgressionDisabled ? "" : " (Not in service)");
                    interchangeNames.Add(icName);
                }
                
                int interchangeId = kvo.Keys.Contains("setInterchange") ? Array.FindIndex(interchanges, ic => ic.Identifier == kvo.Get("setInterchange").StringValue) + 1 : 0;

                interchangeNames.Insert(0, "(No specified interchange)");

                builder.AddSection("Specify Interchange", delegate (UIPanelBuilder icBuilder)
                {
                    icBuilder.AddField("Select", icBuilder.AddDropdown(interchangeNames, interchangeId, UpdateSetInterchange).Tooltip("Affected components", string.Join("\n", components.Select(c => c.DisplayName))));

                    if (interchangeId != 0)
                    {
                        string payDiffString = Main.settings.PayMultiplier > 1.01 ? $"{(int)(Main.settings.PayMultiplier * 100) - 100}% more" : Main.settings.PayMultiplier < 0.99 ? $"{100 - (int)(Main.settings.PayMultiplier * 100)}% less" : "no different";

                        icBuilder.AddField("", $"This customer will pay " + payDiffString + " for having to re-route their loads.").Tooltip("Tip:", "As long this is set to an interchange, the payment percentage will apply regardless if the interchange is *actually* enabled, falling back to vanilla logic.\nPay percentage is configurable in settings.");
                    }

                    void UpdateSetInterchange(int id)
                    {
                        kvo.Set("setInterchange", id == 0 ? null : interchanges[id - 1].Identifier);
                        interchangeId = id;
                        // Debug.Log($"Set interchange {interchanges[id - 1].Identifier} for industry {____industry.identifier}");
                        builder.Rebuild();
                    }
                });

                bool NeedsInterchange(IndustryComponent c)
                {
                    if (!c.IsVisible) { return false; }
                    if (c is IndustryLoader) { return ((IndustryLoader)c).orderAwayLoaded; }
                    if (c is IndustryUnloader) { return ((IndustryUnloader)c).orderAwayEmpties || ((IndustryUnloader)c).orderLoads; }
                    if (c is ProgressionIndustryComponent || c is TeamTrack) { return true; }
                    return false;
                }
            }
        }
    }
}

