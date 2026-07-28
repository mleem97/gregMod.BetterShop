using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace BetterShop
{
    /// <summary>
    /// Public API for registering custom shop categories and items into BetterShop.
    /// Call from other mods' OnInitializeMelon or after the scene loads.
    /// </summary>
    public static class BetterShopAPI
    {
        // Custom categories: name → accent colour
        internal static readonly Dictionary<string, Color> ExtraCategories = new();

        // Externally registered items (persists across shop opens)
        internal static readonly List<BShopItem> ExternalItems = new();

        // ── Category registration ─────────────────────────────────────────────

        /// <summary>Register a new shop category with a default grey accent colour.</summary>
        public static void RegisterCategory(string name)
            => RegisterCategory(name, new Color(0.55f, 0.55f, 0.55f));

        /// <summary>Register a new shop category with a custom accent colour.</summary>
        public static void RegisterCategory(string name, Color accentColor)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            ExtraCategories[name] = accentColor;
        }

        // ── Item registration ─────────────────────────────────────────────────

        /// <summary>
        /// Add an item to BetterShop. <paramref name="category"/> may be a built-in
        /// category name ("Servers", "Networking", "Racks", "Cables", "Mods", "Other")
        /// or any custom category previously registered with RegisterCategory().
        /// </summary>
        public static void RegisterItem(
            string                     category,
            int                        itemId,
            string                     displayName,
            int                        price,
            PlayerManager.ObjectInHand itemType    = PlayerManager.ObjectInHand.ModItem,
            Texture2D                  icon        = null,
            int                        xpToUnlock  = 0)
        {
            ExternalItems.Add(new BShopItem
            {
                ItemId        = itemId,
                Name          = string.IsNullOrWhiteSpace(displayName) ? "Unknown Item" : displayName,
                Price         = price,
                ItemType      = itemType,
                XpToUnlock    = xpToUnlock,
                IsUnlocked    = true,
                IsCustomColor = false,
                Icon          = icon,
                Category      = string.IsNullOrWhiteSpace(category) ? "Mods" : category,
                IsModItem     = true,
            });
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        /// <summary>Remove all externally registered items (call before re-registering to avoid duplication).</summary>
        public static void ClearItems() => ExternalItems.Clear();

        /// <summary>Remove all externally registered categories.</summary>
        public static void ClearCategories() => ExtraCategories.Clear();
    }
}
