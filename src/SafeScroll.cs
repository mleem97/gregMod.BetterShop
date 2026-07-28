using System;
using MelonLoader;
using UnityEngine;

namespace BetterShop
{
    // Data Center 1.1.0 can fail to unstrip Unity's GUI.BeginScrollView.
    // Keep the normal IMGUI path, then fall back to clipped groups and wheel
    // scrolling if the game rejects that wrapper at runtime.
    internal static class SafeScroll
    {
        private static bool _manualFallback;
        private static bool _fallbackLogged;
        private static bool _manualFrame;
        private static Rect _viewport;
        private static Rect _content;

        public static Vector2 Begin(Rect viewport, Vector2 scroll, Rect content)
        {
            if (!_manualFallback)
            {
                try
                {
                    _manualFrame = false;
                    return GUI.BeginScrollView(viewport, scroll, content);
                }
                catch (Exception ex)
                {
                    _manualFallback = true;
                    if (!_fallbackLogged)
                    {
                        _fallbackLogged = true;
                        MelonLogger.Warning($"[BetterShop] GUI.BeginScrollView unavailable ({ex.GetType().Name}); using manual scroll fallback.");
                    }
                }
            }

            _manualFrame = true;
            _viewport = viewport;
            _content = content;

            float maxY = Mathf.Max(0f, content.height - viewport.height);
            if (Event.current != null
                && Event.current.type == EventType.ScrollWheel
                && viewport.Contains(Event.current.mousePosition))
            {
                scroll.y = Mathf.Clamp(scroll.y + Event.current.delta.y * 48f, 0f, maxY);
                Event.current.Use();
            }

            scroll.y = Mathf.Clamp(scroll.y, 0f, maxY);
            GUI.BeginGroup(viewport);
            GUI.BeginGroup(new Rect(0f, -scroll.y, content.width, content.height));
            return scroll;
        }

        public static void End()
        {
            if (_manualFrame)
            {
                GUI.EndGroup();
                GUI.EndGroup();
                _manualFrame = false;
                return;
            }

            try { GUI.EndScrollView(); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BetterShop] GUI.EndScrollView failed ({ex.GetType().Name}); continuing.");
            }
        }
    }
}
