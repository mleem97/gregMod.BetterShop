using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine.UI;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace BetterShop
{
    // ── Data model ────────────────────────────────────────────────────────────

    internal sealed class BShopItem
    {
        public int    ItemId;
        public string Name;
        public int    Price;
        public PlayerManager.ObjectInHand ItemType;
        public int    XpToUnlock;
        public bool   IsUnlocked;
        public bool   IsCustomColor;
        public Texture2D Icon;  // may be null
        public string Category;
        public bool   IsModItem;
    }

    // ── Overlay MonoBehaviour ─────────────────────────────────────────────────

    public class ShopOverlay : MonoBehaviour
    {
        public static ShopOverlay Instance { get; private set; }

        // ── Runtime state ──────────────────────────────────────────────────────
        private ComputerShop  _shop;
        private bool          _open;
        private List<BShopItem> _allItems    = new();
        private List<BShopItem> _filtered    = new();

        private string _search         = "";
        private string _activeCategory = "All";
        private int    _sortMode       = 0;   // 0=Price↑  1=Price↓  2=Name
        private Vector2 _itemScroll;

        // Filter-dirty cache keys
        private string _lastCategory;
        private string _lastSearch;
        private int    _lastSort = -1;

        // ── Layout ─────────────────────────────────────────────────────────────
        private const float WIN_W     = 1100f;
        private const float WIN_H     = 700f;
        private const float SIDEBAR_W = 148f;
        private const float CARD_W    = 192f;
        private const float CARD_H    = 158f;
        private Rect _winRect;

        // ── Categories + sort ─────────────────────────────────────────────────
        private static readonly string[] DefaultCategories =
            { "All", "Servers", "Networking", "Racks", "Cables", "Mods", "Other" };

        private static readonly string[] SortLabels = { "Price ↑", "Price ↓", "Name" };

        // ── Styles ────────────────────────────────────────────────────────────
        private bool       _stylesReady;
        private Texture2D  _whiteTex, _winBgTex, _cardBgTex;
        private GUIStyle   _winStyle;
        private GUIStyle   _titleStyle, _cardNameStyle, _labelStyle, _dimStyle, _priceStyle;
        private GUIStyle   _sidebarBtn, _sidebarActive;
        private GUIStyle   _sortBtn, _sortActive;
        private GUIStyle   _addBtn, _addDisabled;
        private GUIStyle   _checkoutBtn, _clearBtn, _closeBtn;
        private GUIStyle   _searchBox;

        // ── Per-frame balance cache ────────────────────────────────────────────
        private float _frameBalance;
        private int   _frameBalanceFrame = -1;

        // ── Sidebar count cache ────────────────────────────────────────────────
        private readonly Dictionary<string, int> _catCounts = new();

        // ── Placeholder icon cache (for items with no Icon texture) ───────────────
        private readonly Dictionary<string, Texture2D> _placeholderIcons = new();

        // ── Rebuild flag (deferred to first OnGUI, not synchronous in patch) ──
        private bool _needsRebuild;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake() { Instance = this; }

        public static void Open(ComputerShop shop)
            => Instance?.OpenInternal(shop);

        private void OpenInternal(ComputerShop shop)
        {
            _shop          = shop;
            _open          = true;
            _search        = "";
            _activeCategory = "All";
            _sortMode      = 0;
            _itemScroll    = Vector2.zero;
            _lastCategory  = null; // trigger filter refresh
            _winRect = new Rect(
                (Screen.width  - WIN_W) * 0.5f,
                (Screen.height - WIN_H) * 0.5f,
                WIN_W, WIN_H);
            _needsRebuild = true;   // deferred — done on first OnGUI frame
        }

        private void OnGUI()
        {
            if (!_open) return;

            // Deferred item list build (avoids blocking the Harmony patch call)
            if (_needsRebuild)
            {
                _needsRebuild = false;
                RebuildItemList();
            }

            // Keyboard input (search + Escape)
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                { CloseShop(); e.Use(); return; }

                if (e.keyCode == KeyCode.Backspace)
                {
                    if (_search.Length > 0)
                    { _search = _search[..^1]; InvalidateFilter(); }
                    e.Use();
                }
                else if (e.character != '\0' && !char.IsControl(e.character) && _search.Length < 64)
                {
                    _search += e.character;
                    InvalidateFilter();
                    e.Use();
                }
            }

            EnsureStyles();

            // Full-screen dim
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _whiteTex);
            GUI.color = Color.white;

            GUI.Box(_winRect, GUIContent.none, _winStyle);
            GUI.BeginGroup(_winRect);
            try   { DrawWindow(); }
            catch (Exception ex) { MelonLogger.Error($"[BetterShop] DrawWindow: {ex}"); }
            GUI.EndGroup();
        }

        // ── Window ────────────────────────────────────────────────────────────

        private void DrawWindow()
        {
            const float PAD = 12f;
            float y = PAD;

            // Header
            GUI.Label(new Rect(PAD, y, 180f, 28f), "SHOP", _titleStyle);

            // Compute balance once per frame (cached)
            float balance  = GetFrameBalance();
            int   cartTotal = _shop?.currentPrice ?? 0;
            int   cartCount = _shop?.cartUIItems?.Count ?? 0;

            GUI.Label(new Rect(PAD + 190f, y + 6f, 200f, 20f),
                $"Balance:  {balance:N2} ₵", _labelStyle);

            if (cartTotal > 0)
            {
                GUI.color = new Color(1f, 0.85f, 0.28f);
                GUI.Label(new Rect(PAD + 400f, y + 6f, 220f, 20f),
                    $"Cart: {cartCount} item{(cartCount == 1 ? "" : "s")} · {cartTotal:N0} ₵", _labelStyle);
                GUI.color = Color.white;
            }

            if (GUI.Button(new Rect(WIN_W - PAD - 90f, y, 90f, 28f), "← Back", _closeBtn))
            { CloseShop(); return; }

            y += 38f;

            // Search + sort row
            GUI.Label(new Rect(PAD + SIDEBAR_W + 6f, y + 3f, 52f, 20f), "Search:", _dimStyle);
            var searchR = new Rect(PAD + SIDEBAR_W + 62f, y, 258f, 24f);
            GUI.Box(searchR, GUIContent.none, _searchBox);
            GUI.Label(new Rect(searchR.x + 5f, searchR.y + 3f, searchR.width - 10f, 18f),
                      _search.Length > 0 ? _search + "▌" : "filter items...", _dimStyle);

            float sortX = PAD + SIDEBAR_W + 332f;
            GUI.Label(new Rect(sortX, y + 3f, 36f, 20f), "Sort:", _dimStyle);
            for (int i = 0; i < SortLabels.Length; i++)
            {
                var sty = _sortMode == i ? _sortActive : _sortBtn;
                if (GUI.Button(new Rect(sortX + 40f + i * 84f, y, 80f, 24f), SortLabels[i], sty))
                { _sortMode = i; InvalidateFilter(); }
            }
            y += 32f;

            // Divider
            Divider(PAD, y, WIN_W - PAD * 2f); y += 5f;

            // Body
            float bodyH = WIN_H - y - 52f;
            DrawSidebar(new Rect(PAD, y, SIDEBAR_W, bodyH));
            DrawGrid(new Rect(PAD + SIDEBAR_W + 5f, y, WIN_W - PAD * 2f - SIDEBAR_W - 5f, bodyH), balance);
            y += bodyH + 4f;

            // Divider
            Divider(PAD, y, WIN_W - PAD * 2f); y += 4f;

            // Footer / cart bar
            DrawFooter(new Rect(PAD, y, WIN_W - PAD * 2f, 44f), balance);
        }

        // ── Sidebar ───────────────────────────────────────────────────────────

        private void DrawSidebar(Rect r)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.04f);
            GUI.DrawTexture(r, _whiteTex);
            GUI.color = Color.white;

            float y = r.y + 6f;
            foreach (var cat in GetCategories())
            {
                // Use pre-computed counts — no per-frame LINQ over all items
                _catCounts.TryGetValue(cat, out int count);

                if (count == 0 && cat != "All") continue;

                bool   active = _activeCategory == cat;
                string label  = $"{cat}  ({count})";
                var    sty    = active ? _sidebarActive : _sidebarBtn;

                if (GUI.Button(new Rect(r.x + 4f, y, r.width - 8f, 28f), label, sty))
                {
                    _activeCategory = cat;
                    InvalidateFilter();
                    _itemScroll = Vector2.zero;
                }
                y += 32f;
            }
        }

        // ── Grid ─────────────────────────────────────────────────────────────

        private void DrawGrid(Rect area, float balance)
        {
            RefreshFiltered();

            int count = _filtered.Count;
            int cols  = Mathf.Max(1, Mathf.FloorToInt((area.width - 18f) / (CARD_W + 8f)));
            int rows  = count == 0 ? 1 : Mathf.CeilToInt((float)count / cols);
            float contentH = rows * (CARD_H + 8f);

            _itemScroll = GUI.BeginScrollView(area, _itemScroll,
                new Rect(0f, 0f, area.width - 18f, contentH));

            if (count == 0)
            {
                GUI.Label(new Rect(12f, 12f, area.width - 30f, 24f),
                    "No items match your filter.", _dimStyle);
            }

            for (int i = 0; i < count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                DrawCard(
                    new Rect(col * (CARD_W + 8f), row * (CARD_H + 8f), CARD_W, CARD_H),
                    _filtered[i], balance);
            }

            GUI.EndScrollView();
        }

        private void DrawCard(Rect r, BShopItem item, float balance)
        {
            GUI.DrawTexture(r, _cardBgTex);

            // Accent bar (category colour, left edge)
            GUI.color = CategoryColor(item.Category);
            GUI.DrawTexture(new Rect(r.x, r.y, 3f, r.height), _whiteTex);
            GUI.color = Color.white;

            const float CP = 8f;
            float cx = r.x + CP + 3f;
            float cy = r.y + CP;
            float cw = r.width - CP * 2f - 3f;

            // Icon (top-right) — use placeholder when the item has no sprite
            Texture2D iconTex = item.Icon ?? GetOrCreatePlaceholder(item.Category);
            GUI.DrawTexture(new Rect(r.xMax - 46f, r.y + 5f, 40f, 40f), iconTex);

            // Name (word-wrap for long names)
            GUI.Label(new Rect(cx, cy, cw - 46f, 36f), item.Name, _cardNameStyle);
            cy += 36f;

            // Category label
            GUI.color = CategoryColor(item.Category);
            GUI.Label(new Rect(cx, cy, cw, 15f), item.Category, _dimStyle);
            GUI.color = Color.white;
            cy += 17f;

            // Unlock state
            bool locked = !item.IsUnlocked;
            if (locked)
            {
                GUI.color = new Color(1f, 0.55f, 0.18f);
                GUI.Label(new Rect(cx, cy, cw, 14f),
                          item.XpToUnlock > 0 ? $"Req. {item.XpToUnlock:N0} XP" : "Locked",
                          _dimStyle);
                GUI.color = Color.white;
                cy += 16f;
            }

            // Price + Add-to-cart button (bottom area)
            float priceY = r.y + r.height - 32f;
            GUI.Label(new Rect(cx, priceY + 2f, 100f, 20f), $"{item.Price:N0} ₵", _priceStyle);

            bool  canAdd   = !locked && balance >= item.Price;
            var   btnStyle = canAdd ? _addBtn : _addDisabled;
            GUI.color = canAdd ? Color.white : new Color(1f, 1f, 1f, 0.40f);
            if (GUI.Button(new Rect(r.xMax - 62f, priceY - 1f, 58f, 26f), "+ Cart", btnStyle))
            {
                if (canAdd) AddToCart(item);
            }
            GUI.color = Color.white;
        }

        // ── Footer ────────────────────────────────────────────────────────────

        private void DrawFooter(Rect r, float balance)
        {
            int cartTotal = _shop?.currentPrice ?? 0;
            int cartCount = _shop?.cartUIItems?.Count ?? 0;

            if (cartTotal <= 0)
            {
                GUI.color = new Color(0.45f, 0.45f, 0.45f);
                GUI.Label(new Rect(r.x, r.y + 14f, 380f, 20f),
                    "Cart is empty — click  + Cart  on items above to queue a delivery", _dimStyle);
                GUI.color = Color.white;
                return;
            }

            // Cart summary
            GUI.color = new Color(1f, 0.85f, 0.28f);
            GUI.Label(new Rect(r.x, r.y + 12f, 340f, 20f),
                $"Cart: {cartCount} item{(cartCount == 1 ? "" : "s")}  ·  {cartTotal:N0} ₵  queued for delivery",
                _labelStyle);
            GUI.color = Color.white;

            // Clear
            if (GUI.Button(new Rect(r.xMax - 310f, r.y + 6f, 150f, 32f), "Clear Cart", _clearBtn))
            {
                try { _shop?.ButtonClear(); } catch { }
            }

            // Checkout
            bool canCheckout = balance >= cartTotal;
            GUI.color = canCheckout ? Color.white : new Color(1f, 0.4f, 0.4f);
            if (GUI.Button(new Rect(r.xMax - 152f, r.y + 6f, 148f, 32f), "Checkout →", _checkoutBtn)
                && canCheckout)
            {
                Checkout();
            }
            GUI.color = Color.white;
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void AddToCart(BShopItem item)
        {
            try
            {
                _shop?.ButtonBuyShopItem(item.ItemId, item.Price, item.ItemType,
                                         item.Name, item.IsCustomColor);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[BetterShop] AddToCart failed: {ex}");
            }
        }

        private void Checkout()
        {
            try
            {
                _shop?.ButtonCheckOut();
                _open = false;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[BetterShop] Checkout failed: {ex}");
            }
        }

        private void CloseShop()
        {
            _open = false;
            try { _shop?.ButtonReturnMainScreen(); } catch { }
        }

        // ── Item list building ────────────────────────────────────────────────

        private void RebuildItemList()
        {
            _allItems.Clear();

            // ── Vanilla items (read ShopItem components from shopItemParent) ──
            if (_shop?.shopItemParent != null)
            {
                var shopItems = _shop.shopItemParent
                    .GetComponentsInChildren<ShopItem>(true);
                foreach (var si in shopItems)
                {
                    if (si?.shopItemSO == null) continue;
                    var so = si.shopItemSO;
                    _allItems.Add(new BShopItem
                    {
                        ItemId       = so.itemID,
                        Name         = so.itemName,
                        Price        = so.price,
                        ItemType     = so.itemType,
                        XpToUnlock   = so.xpToUnlock,
                        IsUnlocked   = si.isUnlocked,
                        IsCustomColor = so.isCustomColor,
                        Icon         = so.sprite?.texture,
                        Category     = MapCategory(so.itemType),
                        IsModItem    = false,
                    });
                }
            }

            // ── Mod items (from ModLoader.instance) ───────────────────────────
            var loader = ModLoader.instance;
            if (loader?.modShopItemsParent != null)
            {
                var modItems = loader.modShopItemsParent
                    .GetComponentsInChildren<ModShopItem>(true);
                foreach (var mi in modItems)
                {
                    if (mi?.config == null) continue;
                    _allItems.Add(new BShopItem
                    {
                        ItemId       = mi.modID,
                        Name         = mi.config.itemName,
                        Price        = mi.config.price,
                        ItemType     = PlayerManager.ObjectInHand.ModItem,
                        XpToUnlock   = 0,
                        IsUnlocked   = true,
                        IsCustomColor = false,
                        Icon         = mi.itemIcon?.sprite?.texture,
                        Category     = "Mods",
                        IsModItem    = true,
                    });
                }
            }

            // External API-registered items
            foreach (var item in BetterShopAPI.ExternalItems)
                _allItems.Add(item);

            // Cache per-category counts so sidebar doesn't LINQ every frame
            _catCounts.Clear();
            _catCounts["All"] = _allItems.Count;
            foreach (var item in _allItems)
            {
                _catCounts.TryGetValue(item.Category, out int n);
                _catCounts[item.Category] = n + 1;
            }

            int modCount = _catCounts.GetValueOrDefault("Mods", 0);
            MelonLogger.Msg($"[BetterShop] Loaded {_allItems.Count} shop items ({modCount} mod items).");
            InvalidateFilter();
        }

        private void RefreshFiltered()
        {
            bool dirty = _activeCategory != _lastCategory
                      || _search         != _lastSearch
                      || _sortMode       != _lastSort;
            if (!dirty) return;

            _lastCategory = _activeCategory;
            _lastSearch   = _search;
            _lastSort     = _sortMode;

            string q = _search.Trim().ToLowerInvariant();

            IEnumerable<BShopItem> subset = _activeCategory == "All"
                ? _allItems
                : _allItems.Where(i => i.Category == _activeCategory);

            if (!string.IsNullOrEmpty(q))
                subset = subset.Where(i =>
                    i.Name.ToLowerInvariant().Contains(q) ||
                    i.Category.ToLowerInvariant().Contains(q));

            _filtered = _sortMode switch
            {
                1 => subset.OrderByDescending(i => i.Price).ThenBy(i => i.Name).ToList(),
                2 => subset.OrderBy(i => i.Name).ToList(),
                _ => subset.OrderBy(i => i.Price).ThenBy(i => i.Name).ToList()
            };
        }

        private void InvalidateFilter() { _lastCategory = null; }

        // ── Helpers ───────────────────────────────────────────────────────────

        // ── Category helpers ──────────────────────────────────────────────

        private static IEnumerable<string> GetCategories()
        {
            foreach (var c in DefaultCategories) yield return c;
            foreach (var c in BetterShopAPI.ExtraCategories.Keys) yield return c;
        }

        private static string MapCategory(PlayerManager.ObjectInHand t) => t switch
        {
            PlayerManager.ObjectInHand.Server1U or
            PlayerManager.ObjectInHand.Server2U or
            PlayerManager.ObjectInHand.Server3U     => "Servers",
            PlayerManager.ObjectInHand.Switch    or
            PlayerManager.ObjectInHand.PatchPanel or
            PlayerManager.ObjectInHand.SFPModule or
            PlayerManager.ObjectInHand.SFPBox        => "Networking",
            PlayerManager.ObjectInHand.Rack          => "Racks",
            PlayerManager.ObjectInHand.CableSpinner  => "Cables",
            PlayerManager.ObjectInHand.ModItem       => "Mods",
            _                                        => "Other"
        };

        private static Color CategoryColor(string cat)
        {
            return cat switch
            {
                "Servers"    => new Color(0.35f, 0.65f, 1.00f),
                "Networking" => new Color(0.70f, 0.35f, 1.00f),
                "Racks"      => new Color(0.80f, 0.50f, 0.20f),
                "Cables"     => new Color(0.20f, 0.85f, 0.90f),
                "Mods"       => new Color(0.40f, 0.90f, 0.40f),
                _ => BetterShopAPI.ExtraCategories.TryGetValue(cat, out var c)
                     ? c : new Color(0.55f, 0.55f, 0.55f)
            };
        }

        /// <summary>Returns a lazily-created 40×40 placeholder icon tinted by category colour.</summary>
        private Texture2D GetOrCreatePlaceholder(string category)
        {
            if (_placeholderIcons.TryGetValue(category, out var cached)) return cached;

            const int S = 40;
            Color cat  = CategoryColor(category);
            Color bg   = new Color(cat.r * 0.12f, cat.g * 0.12f, cat.b * 0.12f, 1f);
            Color edge = new Color(cat.r * 0.65f, cat.g * 0.65f, cat.b * 0.65f, 1f);
            Color mid  = new Color(cat.r * 0.28f, cat.g * 0.28f, cat.b * 0.28f, 1f);

            var pix = new Color[S * S];
            for (int py = 0; py < S; py++)
            for (int px = 0; px < S; px++)
            {
                bool border = px < 2 || py < 2 || px >= S - 2 || py >= S - 2;
                bool inner  = px >= 10 && px < S - 10 && py >= 10 && py < S - 10;
                pix[py * S + px] = border ? edge : inner ? mid : bg;
            }

            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.SetPixels(pix);
            tex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(tex);
            return _placeholderIcons[category] = tex;
        }

        /// <summary>Direct read from PlayerManager.playerClass.money — no reflection needed.</summary>
        private static float ReadBalance()
        {
            try   { return PlayerManager.instance?.playerClass?.money ?? 0f; }
            catch { return 0f; }
        }

        /// <summary>Reads the balance once per rendered frame; subsequent calls
        /// in the same frame return the cached value at no cost.</summary>
        private float GetFrameBalance()
        {
            int frame = Time.frameCount;
            if (frame != _frameBalanceFrame)
            {
                _frameBalance      = ReadBalance();
                _frameBalanceFrame = frame;
            }
            return _frameBalance;
        }

        private void Divider(float x, float y, float w)
        {
            GUI.color = new Color(0.28f, 0.28f, 0.33f);
            GUI.DrawTexture(new Rect(x, y, w, 1f), _whiteTex);
            GUI.color = Color.white;
        }

        // ── Style initialisation ──────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _whiteTex = MakeTex(1, 1, Color.white);
            UnityEngine.Object.DontDestroyOnLoad(_whiteTex);

            _winBgTex = MakeTex(2, 2, new Color(0.07f, 0.07f, 0.10f, 0.98f));
            UnityEngine.Object.DontDestroyOnLoad(_winBgTex);
            _winStyle = new GUIStyle { normal = { background = _winBgTex }, padding = new RectOffset() };

            _cardBgTex = MakeTex(2, 2, new Color(0.13f, 0.13f, 0.17f, 1f));
            UnityEngine.Object.DontDestroyOnLoad(_cardBgTex);

            _titleStyle = new GUIStyle()
            {
                fontSize  = 16,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white }
            };

            _cardNameStyle = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                wordWrap  = true,
                normal    = { textColor = Color.white }
            };

            _labelStyle = new GUIStyle()
            {
                fontSize = 13,
                normal   = { textColor = Color.white }
            };

            _dimStyle = new GUIStyle()
            {
                fontSize = 12,
                normal   = { textColor = new Color(0.72f, 0.72f, 0.78f) }
            };

            _priceStyle = new GUIStyle()
            {
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = new Color(1f, 0.85f, 0.28f) }
            };

            // ── Shared textures ──────────────────────────────────────────────
            var sidebarBg     = MakeTex(2, 2, new Color(0.18f, 0.18f, 0.22f));
            var sidebarActBg  = MakeTex(2, 2, new Color(0.20f, 0.36f, 0.62f));
            var hoverBg       = MakeTex(2, 2, new Color(0.26f, 0.26f, 0.32f));
            var sortBg        = MakeTex(2, 2, new Color(0.20f, 0.20f, 0.25f));
            var sortActBg     = MakeTex(2, 2, new Color(0.22f, 0.38f, 0.62f));
            var addBg         = MakeTex(2, 2, new Color(0.14f, 0.48f, 0.24f));
            var disabledBg    = MakeTex(2, 2, new Color(0.20f, 0.20f, 0.20f));
            var checkoutBg    = MakeTex(2, 2, new Color(0.58f, 0.38f, 0.04f));
            var clearBg       = MakeTex(2, 2, new Color(0.35f, 0.14f, 0.14f));
            var closeBg       = MakeTex(2, 2, new Color(0.22f, 0.22f, 0.28f));
            var searchBg      = MakeTex(2, 2, new Color(0.12f, 0.12f, 0.16f));
            foreach (var t in new[] { sidebarBg, sidebarActBg, hoverBg, sortBg, sortActBg,
                                       addBg, disabledBg, checkoutBg, clearBg, closeBg, searchBg })
                UnityEngine.Object.DontDestroyOnLoad(t);

            // ── Sidebar buttons ──────────────────────────────────────────────
            var sidePad = new RectOffset(); sidePad.left = 10; sidePad.right = 4;

            _sidebarBtn = new GUIStyle()
            {
                fontSize  = 12,
                alignment = TextAnchor.MiddleLeft,
                padding   = sidePad,
                normal    = { background = sidebarBg,    textColor = new Color(0.82f, 0.82f, 0.86f) },
                hover     = { background = hoverBg,      textColor = Color.white },
                active    = { background = sidebarActBg, textColor = Color.white },
            };

            // active variant — rebuild from scratch (can't copy non-skin GUIStyle in Il2Cpp)
            _sidebarActive = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding   = sidePad,
                normal    = { background = sidebarActBg, textColor = Color.white },
                hover     = { background = hoverBg,      textColor = Color.white },
                active    = { background = sidebarActBg, textColor = Color.white },
            };

            // ── Sort buttons ─────────────────────────────────────────────────
            _sortBtn = new GUIStyle()
            {
                fontSize = 12,
                normal   = { background = sortBg,    textColor = new Color(0.80f, 0.80f, 0.84f) },
                hover    = { background = hoverBg,   textColor = Color.white },
                active   = { background = sortActBg, textColor = Color.white },
            };
            _sortActive = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { background = sortActBg, textColor = Color.white },
                hover     = { background = hoverBg,   textColor = Color.white },
                active    = { background = sortActBg, textColor = Color.white },
            };

            // ── Add-to-cart ──────────────────────────────────────────────────
            _addBtn = new GUIStyle()
            {
                fontSize  = 11,
                fontStyle = FontStyle.Bold,
                normal    = { background = addBg,   textColor = Color.white },
                hover     = { background = hoverBg, textColor = Color.white },
            };
            _addDisabled = new GUIStyle()
            {
                fontSize  = 11,
                fontStyle = FontStyle.Bold,
                normal    = { background = disabledBg, textColor = new Color(0.50f, 0.50f, 0.50f) },
                hover     = { background = disabledBg, textColor = new Color(0.50f, 0.50f, 0.50f) },
            };

            // ── Checkout ─────────────────────────────────────────────────────
            _checkoutBtn = new GUIStyle()
            {
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                normal    = { background = checkoutBg, textColor = Color.white },
                hover     = { background = hoverBg,    textColor = Color.white },
            };

            // ── Clear cart ───────────────────────────────────────────────────
            _clearBtn = new GUIStyle()
            {
                fontSize = 12,
                normal   = { background = clearBg, textColor = new Color(1f, 0.72f, 0.72f) },
                hover    = { background = hoverBg, textColor = Color.white },
            };

            // ── Back button ──────────────────────────────────────────────────
            _closeBtn = new GUIStyle()
            {
                fontSize = 13,
                normal   = { background = closeBg, textColor = new Color(0.85f, 0.85f, 0.90f) },
                hover    = { background = hoverBg, textColor = Color.white },
            };

            // ── Search box ───────────────────────────────────────────────────
            var searchPad = new RectOffset(); searchPad.left = 4; searchPad.right = 4;
            searchPad.top = 2; searchPad.bottom = 2;
            _searchBox = new GUIStyle
            {
                normal  = { background = searchBg },
                padding = searchPad
            };
        }

        private static Texture2D MakeTex(int w, int h, Color c)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = c;
            var t = new Texture2D(w, h);
            t.SetPixels(pix);
            t.Apply();
            return t;
        }
    }
}
