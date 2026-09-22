# Changelog

## 1.0.3

- Fixed the vanilla `shopScreen` staying hidden after closing BetterShop (shop no longer soft-locks the vanilla UI).
- The vanilla panel is now hidden only after the overlay confirmed it could open.
- Failed checkout keeps the overlay open instead of closing it.
- Overlay auto-closes cleanly when its shop reference is destroyed (scene change).
- `RebuildItemList` exceptions are caught per open instead of breaking the UI frame.
- `SafeScroll.End` failure notice is logged once instead of every frame.

- Applied the BetterShop Harmony patches during mod initialization so the custom shop opens when the vanilla shop button is pressed.

## 1.0.1

- Added a manual clipped-group scroll fallback for Data Center builds where `GUI.BeginScrollView` fails IL2CPP unstripping.

## 1.0.0

- Rebuilt against Data Center 1.1.0 / Unity 6000.4.12f1.
