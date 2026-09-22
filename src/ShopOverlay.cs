using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine.UI;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime.Attributes;

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
        private Vector2 _cartScroll;

        // Filter-dirty cache keys
        private string _lastCategory;
        private string _lastSearch;
        private int    _lastSort = -1;

        // ── Layout (Webshop: ~90% × 90% des Screens, helle Store-Optik) ─────
        private const float CART_W    = 300f;
        private const float CARD_W    = 224f;
        private const float CARD_H    = 212f;
        private const int   WIN_ID    = 424242;
        private const float HEADER_H  = 64f;
        private Rect _winRect;
        private bool _winPlaced;

        // ~81% der Screenflaeche (0.9 × 0.9), mit Mindest-/Maximalwerten.
        private float WinW => Mathf.Clamp(Screen.width * 0.9f, 1000f, 2200f);
        private float WinH => Mathf.Clamp(Screen.height * 0.9f, 640f, 1400f);

        private void ClampWindow()
        {
            float w = WinW, h = WinH;
            float x = _winPlaced ? _winRect.x : (Screen.width  - w) * 0.5f;
            float y = _winPlaced ? _winRect.y : (Screen.height - h) * 0.5f;
            _winRect = new Rect(
                Mathf.Clamp(x, 8f, Mathf.Max(8f, Screen.width  - w - 8f)),
                Mathf.Clamp(y, 8f, Mathf.Max(8f, Screen.height - h - 8f)),
                w, h);
            _winPlaced = true;
        }

        // ── Categories + sort ─────────────────────────────────────────────────
        private static readonly string[] DefaultCategories =
            { "All", "Servers", "Networking", "Racks", "Cables", "Mods", "Other" };

        private static readonly string[] SortLabels = { "Price ↑", "Price ↓", "Name" };

        // ── Styles (heller Webshop: weiss/Hellgrau, Navy-Header, Orange) ────
        private bool       _stylesReady;
        private Texture2D  _whiteTex, _winBgTex, _cardBgTex;
        private GUIStyle   _winStyle;
        private GUIStyle   _titleStyle, _cardNameStyle, _labelStyle, _dimStyle, _priceStyle;
        private GUIStyle   _sidebarBtn, _sidebarActive;
        private GUIStyle   _sortBtn, _sortActive;
        private GUIStyle   _addBtn, _addDisabled;
        private GUIStyle   _checkoutBtn, _clearBtn, _closeBtn;
        private GUIStyle   _searchBox;
        private GUIStyle   _logoStyle, _tagStyle, _headerLabel, _balanceStyle, _cartBtn;
        private GUIStyle   _headerSearchBox, _searchHintStyle, _promoStyle, _countStyle;
        private GUIStyle   _pillBtn, _pillActive, _sectionTitle, _cartNameStyle;
        private GUIStyle   _cartTotalStyle, _starsStyle, _catLabelStyle, _stockOk, _stockLock;
        private GUIStyle   _trustStyle, _summaryStyle, _cardPriceStyle;

        // ── Per-frame balance cache ────────────────────────────────────────────
        private float _frameBalance;
        private int   _frameBalanceFrame = -1;

        // ── Sidebar count cache ────────────────────────────────────────────────
        private readonly Dictionary<string, int> _catCounts = new();

        // ── Placeholder icon cache (for items with no Icon texture) ───────────────
        private readonly Dictionary<string, Texture2D> _placeholderIcons = new();

        // ── Rebuild flag (deferred to first OnGUI, not synchronous in patch) ──
        private bool _needsRebuild;

        // ── Vanilla panel tracking (we hide the vanilla UI group while open — must restore!) ──
        private readonly List<GameObject> _hiddenVanilla = new();

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake() { Instance = this; }

        /// <summary>
        /// Tries to open the overlay for the given shop. Returns false (leaving the
        /// vanilla UI untouched) when the overlay or shop isn't usable.
        /// </summary>
        public static bool Open(ComputerShop shop)
        {
            if (Instance == null || shop == null)
                return false;
            return Instance.OpenInternal(shop);
        }

        private bool OpenInternal(ComputerShop shop)
        {
            GameObject panel = null;
            try { panel = shop.shopScreen; } catch { panel = null; }
            if (panel == null)
                return false;

            _shop          = shop;
            _open          = true;
            _search        = "";
            _activeCategory = "All";
            _sortMode      = 0;
            _itemScroll    = Vector2.zero;
            _cartScroll    = Vector2.zero;
            _lastCategory  = null; // trigger filter refresh
            ClampWindow();

            // Hide the whole vanilla shop UI group (not just shopScreen) so no
            // vanilla UI shines through behind our overlay. Game logic
            // (cart data, buy/checkout methods) doesn't need visuals.
            // Canvas-Typ bewusst vermieden (fehlt in IL2CPP-Dummies):
            // stattdessen alle Geschwister unter dem Parent mitschalten.
            try
            {
                HideVanillaGroup(panel);
            }
            catch { _hiddenVanilla.Clear(); }

            _needsRebuild = true;   // deferred — done on first OnGUI frame
            return true;
        }

        /// <summary>Hides the vanilla UI group (panel + siblings). Restored on close.</summary>
        private void HideVanillaGroup(GameObject panel)
        {
            _hiddenVanilla.Clear();
            if (panel == null) return;
            try
            {
                Transform parent = null;
                try { parent = panel.transform?.parent; } catch { parent = null; }
                if (parent == null)
                {
                    try { if (panel.activeSelf) { panel.SetActive(false); _hiddenVanilla.Add(panel); } }
                    catch { }
                    return;
                }
                int n = 0;
                try { n = parent.childCount; } catch { n = 0; }
                for (int i = 0; i < n; i++)
                {
                    GameObject go = null;
                    try { go = parent.GetChild(i)?.gameObject; } catch { go = null; }
                    if (go == null) continue;
                    try { if (go.activeSelf) { go.SetActive(false); _hiddenVanilla.Add(go); } }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>Restores everything we hid, if still alive.</summary>
        private void RestoreVanillaPanel()
        {
            if (_hiddenVanilla.Count == 0)
                return;
            foreach (var go in _hiddenVanilla)
            {
                if (go == null) continue;
                try
                {
                    // Accessing a destroyed IL2CPP object throws — treat as gone.
                    var t = go.transform;
                    go.SetActive(true);
                }
                catch { /* destroyed with its scene — nothing to restore */ }
            }
            _hiddenVanilla.Clear();
        }

        /// <summary>Checks the stored shop reference is still alive.</summary>
        private bool IsShopAlive()
        {
            try
            {
                if (_shop == null)
                    return false;
                var go = _shop.gameObject;
                return go != null;
            }
            catch { return false; }
        }

        private void OnGUI()
        {
            if (!_open) return;

            // Shop destroyed underneath us (scene change) — restore vanilla UI and bail.
            if (!IsShopAlive())
            {
                RestoreVanillaPanel();
                _open = false;
                return;
            }

            // Deferred item list build (avoids blocking the Harmony patch call).
            // Wrapped: IL2CPP generic lookups can throw on stripped builds.
            if (_needsRebuild)
            {
                _needsRebuild = false;
                try { RebuildItemList(); }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[BetterShop] RebuildItemList: {ex.GetBaseException().Message}");
                }
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
            ClampWindow();

            // Full-screen dim (IPAM-like)
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _whiteTex);
            GUI.color = Color.white;

            try
            {
                _winRect = GUI.Window(WIN_ID, _winRect, (GUI.WindowFunction)DrawWindowFunc, GUIContent.none, _winStyle);
            }
            catch (Exception ex) { MelonLogger.Error($"[BetterShop] DrawWindow: {ex}"); }
        }

        private void DrawWindowFunc(int id)
        {
            try   { DrawWindow(); }
            catch (Exception ex) { MelonLogger.Error($"[BetterShop] DrawWindow: {ex}"); }
            // Drag ueber den Header-Bereich (nicht ueber Content-Klicks).
            try
            {
                var e = Event.current;
                if (e != null && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag)
                    && new Rect(0f, 0f, _winRect.width, HEADER_H).Contains(e.mousePosition))
                    GUI.DragWindow(new Rect(0f, 0f, _winRect.width, HEADER_H));
            }
            catch { }
        }

        // ── Deal-of-the-day Promo (aus RebuildItemList gecacht) ───────────────
        private string _dealText = "";

        // ── Window (Webshop: Header, Promo, Kategorie-Pills, Grid, Warenkorb) ─

        private void DrawWindow()
        {
            float W = _winRect.width;
            float H = _winRect.height;
            const float PAD = 14f;

            // Heller Store-Hintergrund + duenner Rahmen
            GUI.color = new Color(0.80f, 0.83f, 0.88f);
            GUI.DrawTexture(new Rect(0f, 0f, W, 1f), _whiteTex);
            GUI.DrawTexture(new Rect(0f, H - 1f, W, 1f), _whiteTex);
            GUI.DrawTexture(new Rect(0f, 0f, 1f, H), _whiteTex);
            GUI.DrawTexture(new Rect(W - 1f, 0f, 1f, H), _whiteTex);
            GUI.color = Color.white;

            float y = 0f;
            DrawStoreHeader(W);
            y += HEADER_H;

            // Promo-Zeile (Deal of the day)
            GUI.color = new Color(1f, 0.94f, 0.84f);
            GUI.DrawTexture(new Rect(0f, y, W, 26f), _whiteTex);
            GUI.color = Color.white;
            GUI.Label(new Rect(PAD, y + 5f, W - PAD * 2f, 18f), _dealText, _promoStyle);
            y += 26f;

            // Kategorie-Pills
            y += 6f;
            DrawCategoryPills(new Rect(PAD, y, W - PAD * 2f, 32f));
            y += 38f;

            // Toolbar: Trefferzahl links, Sortierung rechts
            RefreshFiltered();
            GUI.Label(new Rect(PAD, y + 4f, 300f, 20f),
                $"{_filtered.Count} product{(_filtered.Count == 1 ? "" : "s")}", _countStyle);
            float sx = W - PAD;
            for (int i = SortLabels.Length - 1; i >= 0; i--)
            {
                var sty = _sortMode == i ? _sortActive : _sortBtn;
                sx -= 86f;
                if (GUI.Button(new Rect(sx, y, 82f, 24f), SortLabels[i], sty))
                { _sortMode = i; InvalidateFilter(); }
                sx -= 4f;
            }
            GUI.Label(new Rect(sx - 44f, y + 4f, 44f, 20f), "Sort:", _dimStyle);
            y += 30f;

            Divider(PAD, y, W - PAD * 2f); y += 6f;

            // Body: Produktgrid | Warenkorb
            float footerH = 52f;
            float bodyH = H - y - footerH - 8f;
            float gridW = W - PAD * 2f - CART_W - 10f;
            DrawGrid(new Rect(PAD, y, gridW, bodyH), GetFrameBalance());
            DrawCartPanel(new Rect(PAD + gridW + 10f, y, CART_W, bodyH), GetFrameBalance());

            // Footer: Trust-Badges + Summen
            DrawFooter(new Rect(PAD, H - footerH, W - PAD * 2f, footerH - 8f), GetFrameBalance());
        }

        // ── Store-Header (Navy: Logo, Suche, Balance, Warenkorb, Schliessen) ──

        private void DrawStoreHeader(float W)
        {
            const float PAD = 14f;
            GUI.color = new Color(0.04f, 0.07f, 0.13f);
            GUI.DrawTexture(new Rect(0f, 0f, W, HEADER_H), _whiteTex);
            // Orange Akzentlinie unten
            GUI.color = new Color(1f, 0.42f, 0f);
            GUI.DrawTexture(new Rect(0f, HEADER_H - 3f, W, 3f), _whiteTex);
            GUI.color = Color.white;

            GUI.Label(new Rect(PAD, 8f, 260f, 28f), "GREGSTORE", _logoStyle);
            GUI.Label(new Rect(PAD + 2f, 36f, 260f, 18f), "Servers & Network Gear", _tagStyle);

            // Suche (zentriert)
            float searchW = Mathf.Min(440f, W * 0.32f);
            float sx = (W - searchW) * 0.5f;
            var searchR = new Rect(sx, 19f, searchW, 26f);
            GUI.Box(searchR, GUIContent.none, _headerSearchBox);
            GUI.Label(new Rect(searchR.x + 8f, searchR.y + 5f, searchR.width - 16f, 18f),
                      _search.Length > 0 ? _search + "▌" : "Search servers, switches, cables ...", _searchHintStyle);

            // Balance
            float balance = GetFrameBalance();
            GUI.Label(new Rect(W - PAD - 330f, 22f, 200f, 22f),
                $"{balance:N0} ₵", _balanceStyle);

            // Warenkorb-Button mit Stueckzahl
            int cartCount = 0;
            try { cartCount = _shop?.cartUIItems?.Count ?? 0; } catch { }
            if (GUI.Button(new Rect(W - PAD - 190f, 17f, 110f, 30f), $"Cart ({cartCount})", _cartBtn))
            { /* Warenkorb ist rechts dauerhaft sichtbar */ }

            // Schliessen
            if (GUI.Button(new Rect(W - PAD - 64f, 17f, 64f, 30f), "× Close", _closeBtn))
            { CloseShop(); return; }
        }

        // ── Kategorie-Pills (horizontale Webshop-Navigation) ──────────────────

        private void DrawCategoryPills(Rect r)
        {
            float x = r.x;
            foreach (var cat in GetCategories())
            {
                _catCounts.TryGetValue(cat, out int count);
                if (count == 0 && cat != "All") continue;

                bool active = _activeCategory == cat;
                string label = $"{cat} ({count})";
                // Breite grob aus Textlaenge schaetzen (IMGUI ohne CalcSize-Overhead)
                float w = Mathf.Clamp(34f + label.Length * 7.2f, 70f, 190f);
                if (x + w > r.xMax) break;

                if (GUI.Button(new Rect(x, r.y, w, r.height), label, active ? _pillActive : _pillBtn))
                {
                    _activeCategory = cat;
                    InvalidateFilter();
                    _itemScroll = Vector2.zero;
                }
                x += w + 8f;
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

            _itemScroll = SafeScroll.Begin(area, _itemScroll,
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

            SafeScroll.End();
        }

        [HideFromIl2Cpp]
        private void DrawCard(Rect r, BShopItem item, float balance)
        {
            // Weisse Karte + heller Rahmen
            GUI.DrawTexture(r, _whiteTex);
            GUI.color = new Color(0.82f, 0.85f, 0.90f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1f), _whiteTex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 1f, r.width, 1f), _whiteTex);
            GUI.DrawTexture(new Rect(r.x, r.y, 1f, r.height), _whiteTex);
            GUI.DrawTexture(new Rect(r.xMax - 1f, r.y, 1f, r.height), _whiteTex);
            GUI.color = Color.white;

            // Kategorie-Farbbalken oben
            GUI.color = CategoryColor(item.Category);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 4f), _whiteTex);
            GUI.color = Color.white;

            const float CP = 10f;
            float cx = r.x + CP;
            float cw = r.width - CP * 2f;
            float cy = r.y + 10f;

            // Produktbild-Flaeche (Icon zentriert, 64px)
            float imgH = 76f;
            GUI.color = new Color(0.96f, 0.97f, 0.98f);
            GUI.DrawTexture(new Rect(cx, cy, cw, imgH), _whiteTex);
            GUI.color = Color.white;
            Texture2D iconTex = item.Icon ?? GetOrCreatePlaceholder(item.Category);
            float iconS = 60f;
            GUI.DrawTexture(new Rect(cx + (cw - iconS) * 0.5f, cy + (imgH - iconS) * 0.5f, iconS, iconS), iconTex);
            cy += imgH + 6f;

            // Name (2 Zeilen)
            GUI.Label(new Rect(cx, cy, cw, 36f), item.Name, _cardNameStyle);
            cy += 38f;

            // Bewertung (deterministisch aus ItemId) + Kategorie
            int stars = 3 + (Mathf.Abs(item.ItemId * 7 + item.Price) % 3);
            int reviews = 3 + (Mathf.Abs(item.ItemId * 13) % 97);
            GUI.Label(new Rect(cx, cy, cw, 16f),
                $"{new string('★', stars)}  ({reviews})", _starsStyle);
            cy += 17f;
            GUI.color = CategoryColor(item.Category);
            GUI.Label(new Rect(cx, cy, cw, 15f), item.Category.ToUpperInvariant(), _catLabelStyle);
            GUI.color = Color.white;
            cy += 17f;

            // Verfuegbarkeit
            bool locked = !item.IsUnlocked;
            if (locked)
            {
                GUI.Label(new Rect(cx, cy, cw, 15f),
                    item.XpToUnlock > 0 ? $"Req. {item.XpToUnlock:N0} XP to unlock" : "Locked",
                    _stockLock);
            }
            else
            {
                GUI.Label(new Rect(cx, cy, cw, 15f), "● In stock — ships today", _stockOk);
            }
            cy += 18f;

            // Preis + Add-to-Cart (unten)
            float btnH = 30f;
            float priceY = r.y + r.height - btnH - 30f;
            GUI.Label(new Rect(cx, priceY, cw, 24f), $"{item.Price:N0} ₵", _cardPriceStyle);

            bool canAdd = !locked && balance >= item.Price;
            var btnRect = new Rect(cx, r.y + r.height - btnH - 6f, cw, btnH);
            GUI.color = canAdd ? Color.white : new Color(1f, 1f, 1f, 0.55f);
            if (GUI.Button(btnRect, "Add to Cart", canAdd ? _addBtn : _addDisabled))
            {
                if (canAdd) AddToCart(item);
            }
            GUI.color = Color.white;
        }

        // ── Cart sidebar ──────────────────────────────────────────────────────

        private void DrawCartPanel(Rect r, float balance)
        {
            // Weisse Warenkorb-Karte + heller Rahmen
            GUI.DrawTexture(r, _whiteTex);
            GUI.color = new Color(0.82f, 0.85f, 0.90f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1f), _whiteTex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 1f, r.width, 1f), _whiteTex);
            GUI.DrawTexture(new Rect(r.x, r.y, 1f, r.height), _whiteTex);
            GUI.DrawTexture(new Rect(r.xMax - 1f, r.y, 1f, r.height), _whiteTex);
            GUI.color = Color.white;

            float cx = r.x + 10f;
            float cw = r.width - 20f;
            float cy = r.y + 8f;

            int cartCount = 0;
            int cartTotal = 0;
            try { cartCount = _shop?.cartUIItems?.Count ?? 0; } catch { }
            try { cartTotal = _shop?.currentPrice ?? 0; } catch { }

            GUI.Label(new Rect(cx, cy, cw, 22f), $"Shopping Cart ({cartCount})", _sectionTitle);
            cy += 26f;
            Divider(cx, cy, cw); cy += 6f;

            float listH = r.height - (cy - r.y) - 108f;
            var listR = new Rect(cx, cy, cw, Mathf.Max(60f, listH));

            List<ShopCartItem> items = new();
            try
            {
                var raw = _shop?.cartUIItems;
                if (raw != null)
                    foreach (var ci in raw)
                    {
                        if (ci == null) continue;
                        try { var _ = ci.gameObject; items.Add(ci); }
                        catch { /* destroyed — skip */ }
                    }
            }
            catch { }

            if (items.Count == 0)
            {
                GUI.Label(new Rect(cx + 2f, cy + 8f, cw - 4f, 44f),
                    "Your cart is empty.\nAdd products with \"Add to Cart\".", _dimStyle);
            }
            else
            {
                float rowH = 44f;
                float contentH = items.Count * (rowH + 4f);
                _cartScroll = SafeScroll.Begin(listR, _cartScroll,
                    new Rect(0f, 0f, listR.width - 14f, contentH));
                float ry = 0f;
                foreach (var ci in items)
                {
                    DrawCartRow(new Rect(0f, ry, listR.width - 14f, rowH), ci);
                    ry += rowH + 4f;
                }
                SafeScroll.End();
            }
            cy += listR.height + 6f;

            Divider(cx, cy, cw); cy += 6f;

            GUI.Label(new Rect(cx, cy, cw, 18f), $"Subtotal:  {cartTotal:N0} ₵", _labelStyle);
            cy += 20f;
            GUI.Label(new Rect(cx, cy, cw, 18f), "Shipping:  Free", _stockOk);
            cy += 22f;
            GUI.Label(new Rect(cx, cy, cw, 22f), $"Total:  {cartTotal:N0} ₵", _cartTotalStyle);
            cy += 26f;

            if (GUI.Button(new Rect(cx, cy, cw, 28f), "Clear", _clearBtn))
            {
                try { _shop?.ButtonClear(); } catch { }
            }
            cy += 32f;

            bool canCheckout = cartTotal > 0 && balance >= cartTotal;
            GUI.color = canCheckout ? Color.white : new Color(1f, 1f, 1f, 0.55f);
            if (GUI.Button(new Rect(cx, cy, cw, 32f), "Proceed to Checkout →", _checkoutBtn)
                && canCheckout)
            {
                GUI.color = Color.white;
                Checkout();
                return;
            }
            GUI.color = Color.white;
        }

        [HideFromIl2Cpp]
        private void DrawCartRow(Rect r, ShopCartItem ci)
        {
            string name = "?";
            int qty = 1, line = 0;
            try { name = ci.itemName ?? ci.ItemID.ToString(); } catch { }
            try { qty = ci.Quantity; } catch { }
            try { line = ci.TotalPrice; } catch { }

            GUI.color = new Color(0.96f, 0.97f, 0.98f);
            GUI.DrawTexture(r, _whiteTex);
            GUI.color = Color.white;

            GUI.Label(new Rect(r.x + 6f, r.y + 2f, r.width - 76f, 22f), name, _cartNameStyle);
            GUI.Label(new Rect(r.x + 6f, r.y + 22f, r.width - 76f, 18f),
                $"×{qty}  ·  {line:N0} ₵", _dimStyle);

            if (GUI.Button(new Rect(r.xMax - 64f, r.y + 9f, 28f, 26f), "−", _sortBtn))
            {
                try { ci.OnRemoveClicked(); } catch (Exception ex)
                { MelonLogger.Error($"[BetterShop] Cart− failed: {ex.GetBaseException().Message}"); }
            }
            if (GUI.Button(new Rect(r.xMax - 32f, r.y + 9f, 28f, 26f), "+", _sortBtn))
            {
                try { ci.OnAddClicked(); } catch (Exception ex)
                { MelonLogger.Error($"[BetterShop] Cart+ failed: {ex.GetBaseException().Message}"); }
            }
        }

        // ── Footer (Trust-Badges + Zusammenfassung) ────────────────────────

        private void DrawFooter(Rect r, float balance)
        {
            int cartTotal = 0;
            int cartCount = 0;
            try { cartTotal = _shop?.currentPrice ?? 0; } catch { }
            try { cartCount = _shop?.cartUIItems?.Count ?? 0; } catch { }

            GUI.Label(new Rect(r.x, r.y + 12f, 560f, 20f),
                "✓ Secure checkout    ✓ 2-year warranty    ✓ Free delivery    ✓ 30-day returns",
                _trustStyle);

            string summary = cartTotal <= 0
                ? "Cart is empty — add products above to queue a delivery"
                : $"{cartCount} item{(cartCount == 1 ? "" : "s")} · Total {cartTotal:N0} ₵ · Balance {balance:N0} ₵";
            GUI.Label(new Rect(r.xMax - 560f, r.y + 12f, 560f, 20f), summary, _summaryStyle);
        }

        // ── Actions ───────────────────────────────────────────────────────────

        [HideFromIl2Cpp]
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
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[BetterShop] Checkout failed: {ex}");
                return;
            }
            RestoreVanillaPanel();
            _open = false;
        }

        private void CloseShop()
        {
            _open = false;
            RestoreVanillaPanel();
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

            // Deal of the day: guenstigstes freigeschaltetes Vanilla-Item.
            try
            {
                var deal = _allItems
                    .Where(i => !i.IsModItem && i.IsUnlocked)
                    .OrderBy(i => i.Price)
                    .FirstOrDefault();
                _dealText = deal != null
                    ? $"★ Deal of the day: {deal.Name} — {deal.Price:N0} ₵  ·  Free delivery  ·  2-year warranty included"
                    : "Free delivery on all orders  ·  2-year warranty included";
            }
            catch { _dealText = "Free delivery on all orders  ·  2-year warranty included"; }

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
            GUI.color = new Color(0.14f, 0.17f, 0.22f, 0.8f);
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

            // Heller Store-Hintergrund
            _winBgTex = MakeTex(2, 2, new Color(0.93f, 0.94f, 0.96f, 1f));
            UnityEngine.Object.DontDestroyOnLoad(_winBgTex);
            _winStyle = new GUIStyle { normal = { background = _winBgTex }, padding = new RectOffset() };

            _cardBgTex = MakeTex(2, 2, Color.white);
            UnityEngine.Object.DontDestroyOnLoad(_cardBgTex);

            // Textfarben: dunkles Navy auf hell, weiss auf Navy
            var ink     = new Color(0.10f, 0.13f, 0.18f);
            var inkDim  = new Color(0.42f, 0.46f, 0.53f);
            var paper   = new Color(0.97f, 0.98f, 0.99f);
            var orange  = new Color(1f, 0.42f, 0f);
            var orangeD = new Color(0.85f, 0.34f, 0f);
            var green   = new Color(0.10f, 0.55f, 0.22f);
            var navyTx  = Color.white;

            _titleStyle = new GUIStyle()
            {
                fontSize  = 15,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = ink }
            };

            _cardNameStyle = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                wordWrap  = true,
                normal    = { textColor = ink }
            };

            _labelStyle = new GUIStyle()
            {
                fontSize = 13,
                normal   = { textColor = ink }
            };

            _dimStyle = new GUIStyle()
            {
                fontSize = 12,
                normal   = { textColor = inkDim }
            };

            _priceStyle = new GUIStyle()
            {
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = new Color(1f, 0.85f, 0.28f) }
            };

            // ── Shared textures (Store) ──────────────────────────────────────
            var pillBg        = MakeTex(2, 2, Color.white);
            var pillActBg     = MakeTex(2, 2, orange);
            var hoverBg       = MakeTex(2, 2, new Color(0.90f, 0.92f, 0.95f));
            var sortBg        = MakeTex(2, 2, Color.white);
            var sortActBg     = MakeTex(2, 2, new Color(0.04f, 0.07f, 0.13f));
            var addBg         = MakeTex(2, 2, orange);
            var addHoverBg    = MakeTex(2, 2, orangeD);
            var disabledBg    = MakeTex(2, 2, new Color(0.85f, 0.87f, 0.90f));
            var checkoutBg    = MakeTex(2, 2, orange);
            var clearBg       = MakeTex(2, 2, Color.white);
            var closeBg       = MakeTex(2, 2, new Color(0.16f, 0.22f, 0.32f));
            var searchBg      = MakeTex(2, 2, new Color(0.06f, 0.08f, 0.11f));
            var headerBoxBg   = MakeTex(2, 2, new Color(0.13f, 0.18f, 0.28f));
            var cartBtnBg     = MakeTex(2, 2, new Color(0.16f, 0.22f, 0.32f));
            foreach (var t in new[] { pillBg, pillActBg, hoverBg, sortBg, sortActBg,
                                        addBg, addHoverBg, disabledBg, checkoutBg, clearBg,
                                        closeBg, searchBg, headerBoxBg, cartBtnBg })
                UnityEngine.Object.DontDestroyOnLoad(t);

            // ── Sidebar buttons (ungenutzt im Store-Layout, kept for compat) ──
            var sidePad = new RectOffset(); sidePad.left = 10; sidePad.right = 4;

            _sidebarBtn = new GUIStyle()
            {
                fontSize  = 12,
                alignment = TextAnchor.MiddleLeft,
                padding   = sidePad,
                normal    = { background = pillBg,    textColor = ink },
                hover     = { background = hoverBg,   textColor = ink },
                active    = { background = pillActBg, textColor = Color.white },
            };

            // active variant — rebuild from scratch (can't copy non-skin GUIStyle in Il2Cpp)
            _sidebarActive = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding   = sidePad,
                normal    = { background = pillActBg, textColor = Color.white },
                hover     = { background = hoverBg,   textColor = ink },
                active    = { background = pillActBg, textColor = Color.white },
            };

            // ── Sort buttons ─────────────────────────────────────────────────
            _sortBtn = new GUIStyle()
            {
                fontSize = 12,
                normal   = { background = sortBg,    textColor = inkDim },
                hover    = { background = hoverBg,   textColor = ink },
                active   = { background = sortActBg, textColor = Color.white },
            };
            _sortActive = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { background = sortActBg, textColor = Color.white },
                hover     = { background = hoverBg,   textColor = ink },
                active    = { background = sortActBg, textColor = Color.white },
            };

            // ── Add-to-cart (Orange) ───────────────────────────────────────────
            _addBtn = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { background = addBg,      textColor = Color.white },
                hover     = { background = addHoverBg, textColor = Color.white },
            };
            _addDisabled = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { background = disabledBg, textColor = new Color(0.55f, 0.57f, 0.60f) },
                hover     = { background = disabledBg, textColor = new Color(0.55f, 0.57f, 0.60f) },
            };

            // ── Checkout (Orange, gross) ───────────────────────────────────────
            _checkoutBtn = new GUIStyle()
            {
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                normal    = { background = checkoutBg, textColor = Color.white },
                hover     = { background = addHoverBg, textColor = Color.white },
            };

            // ── Clear (weiss, roter Text) ──────────────────────────────────────
            _clearBtn = new GUIStyle()
            {
                fontSize = 12,
                normal   = { background = clearBg, textColor = new Color(0.75f, 0.20f, 0.20f) },
                hover    = { background = hoverBg, textColor = new Color(0.75f, 0.20f, 0.20f) },
            };

            // ── Back/Close (Navy-hell) ─────────────────────────────────────────
            _closeBtn = new GUIStyle()
            {
                fontSize = 13,
                normal   = { background = closeBg, textColor = navyTx },
                hover    = { background = hoverBg, textColor = ink },
            };

            // ── Search box (alt) ───────────────────────────────────────────────
            var searchPad = new RectOffset(); searchPad.left = 4; searchPad.right = 4;
            searchPad.top = 2; searchPad.bottom = 2;
            _searchBox = new GUIStyle
            {
                normal  = { background = searchBg },
                padding = searchPad
            };

            // ── Store-Header ───────────────────────────────────────────────────
            _logoStyle = new GUIStyle()
            {
                fontSize  = 22,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white }
            };
            _tagStyle = new GUIStyle()
            {
                fontSize = 12,
                normal   = { textColor = new Color(0.65f, 0.72f, 0.82f) }
            };
            _headerLabel = new GUIStyle()
            {
                fontSize = 13,
                normal   = { textColor = navyTx }
            };
            _balanceStyle = new GUIStyle()
            {
                fontSize  = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = new Color(1f, 0.85f, 0.28f) }
            };
            _cartBtn = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { background = cartBtnBg, textColor = Color.white },
                hover     = { background = hoverBg,   textColor = ink },
            };
            var headerBoxPad = new RectOffset();
            headerBoxPad.left = 4; headerBoxPad.right = 4;
            headerBoxPad.top = 2; headerBoxPad.bottom = 2;
            _headerSearchBox = new GUIStyle
            {
                normal  = { background = headerBoxBg },
                padding = headerBoxPad
            };
            _searchHintStyle = new GUIStyle()
            {
                fontSize = 12,
                normal   = { textColor = new Color(0.60f, 0.66f, 0.74f) }
            };

            // ── Promo / Pills / Toolbar ────────────────────────────────────────
            _promoStyle = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = new Color(0.55f, 0.30f, 0.05f) }
            };
            _pillBtn = new GUIStyle()
            {
                fontSize = 12,
                normal   = { background = pillBg,    textColor = ink },
                hover    = { background = hoverBg,   textColor = ink },
                active   = { background = pillActBg, textColor = Color.white },
            };
            _pillActive = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { background = pillActBg, textColor = Color.white },
                hover     = { background = addHoverBg, textColor = Color.white },
                active    = { background = pillActBg,  textColor = Color.white },
            };
            _countStyle = new GUIStyle()
            {
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = ink }
            };

            // ── Produktkarte ───────────────────────────────────────────────────
            _cardPriceStyle = new GUIStyle()
            {
                fontSize  = 16,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = ink }
            };
            _starsStyle = new GUIStyle()
            {
                fontSize = 12,
                normal   = { textColor = orangeD }
            };
            _catLabelStyle = new GUIStyle()
            {
                fontSize  = 10,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = inkDim }
            };
            _stockOk = new GUIStyle()
            {
                fontSize = 11,
                normal   = { textColor = green }
            };
            _stockLock = new GUIStyle()
            {
                fontSize  = 11,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = orangeD }
            };

            // ── Warenkorb ──────────────────────────────────────────────────────
            _sectionTitle = new GUIStyle()
            {
                fontSize  = 15,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = ink }
            };
            _cartNameStyle = new GUIStyle()
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = ink }
            };
            _cartTotalStyle = new GUIStyle()
            {
                fontSize  = 15,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = ink }
            };

            // ── Footer ─────────────────────────────────────────────────────────
            _trustStyle = new GUIStyle()
            {
                fontSize = 12,
                normal   = { textColor = green }
            };
            _summaryStyle = new GUIStyle()
            {
                fontSize  = 12,
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = inkDim }
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
