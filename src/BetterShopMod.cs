using HarmonyLib;
using System.Reflection;
using Il2Cpp;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(BetterShop.BetterShopMod), "gregMod.BetterShop", "1.0.2", "TeamGreg Modding")]
[assembly: MelonGame("Waseku", "Data Center")]

namespace BetterShop
{
    public class BetterShopMod : MelonMod
    {
        public static BetterShopMod Instance { get; private set; }

        private GameObject _go;
        private HarmonyLib.Harmony _harmony;

        public override void OnInitializeMelon()
        {
            Instance = this;
            ClassInjector.RegisterTypeInIl2Cpp<ShopOverlay>();
            _harmony = new HarmonyLib.Harmony("gregMod.BetterShop");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            MelonLogger.Msg("[BetterShop] Initialised.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (_go != null) return;
            _go = new GameObject("BetterShopOverlay");
            Object.DontDestroyOnLoad(_go);
            _go.AddComponent<ShopOverlay>();
            MelonLogger.Msg($"[BetterShop] Overlay created in scene {sceneName}.");
        }
    }

    // ── Harmony patches ───────────────────────────────────────────────────────

    [HarmonyPatch]
    internal static class ShopPatches
    {
        /// <summary>
        /// After ButtonShopScreen runs (opens the canvas and enables the shop panel),
        /// immediately hide the vanilla item panel and hand off to our overlay.
        /// The canvas stays active so the game's cart / input handling remains intact.
        /// </summary>
        [HarmonyPatch(typeof(ComputerShop), "ButtonShopScreen")]
        [HarmonyPostfix]
        static void PostfixButtonShopScreen(ComputerShop __instance)
        {
            try
            {
                // Hide only the item-browser panel — the cart side-panel can stay hidden too
                // since our overlay has its own cart footer.
                if (__instance.shopScreen != null)
                    __instance.shopScreen.SetActive(false);
            }
            catch { /* field may be null in some scenes */ }

            ShopOverlay.Open(__instance);
        }
    }
}
