# Native FullMenu style vocabulary (Hub visual true-up reference)

Source: `.modding/ui-dumps/fullmenu.txt` — captured 2026-06-11, 7 pages (BizMan,
Persona, Contacts, Rivals, MyEmployees, EconoView, MarketInsider). All values
CONFIRMED from the dump (renderer-level read of the live UI). Canvas reference
resolution is 3840×2160 — halve sizes for 1080p-feel comparisons.

## Page anatomy (BizMan list view, the Hub's closest analog)
- Page root: full-bleed [3840×1750] under `Canvas/AppsContainer/<App>` with CanvasGroup.
- Multi-column layout via named `Layout 50-25-25` / `Layout 30-70` containers.
- **Section header**: height 100, TMP **50pt #FFFFFF**, with a 40×40 #FFFFFF icon child.
- **Vertical splitters** between columns: 4px wide, `Image(#FFFFFF@0.15)`.
- Horizontal splitter variant: `Square-With-Padding-8` sprite, 8px tall, #FFFFFF@0.15.

## Tables / lists
- Table = `BaTable` + `Headers` row (height 120, HorizontalLayoutGroup) +
  **ListBackground**: `white-rounded-box` sprite tinted **#262B40 @ 0.43** (the
  translucent dark blue-grey every native list sits on) + EnhancedScroller.
- **Scrollbar**: 12px wide, `Circle-12` sprite **#7D8186** (also seen: `Scrollbar`
  sprite #898E95 / #BEC1C4).
- List rows (Contacts/Employees pattern): `white-round-corner-drop` sprite at
  **#7F7F7F @ 0.70** (row card), #FFFFFF@1.0 for highlighted/hovered.
- Row inner text: **31pt #FFFFFF** primary, **28pt #FFFFFF** secondary.

## Tab menus (BizMan business view)
- Tab row height 100, each tab a Button: TMP **50pt #CACDCE** (idle); active tab
  indicated by **SplitterWithIndicator**: full-width 8px line #FFFFFF@0.15 with a
  600×8 #FFFFFF@1.0 `Indicator` segment under the active tab.

## Panels
- `WhiteBox`/`whitebox` sprite #FFFFFF@1.0 = light content panel (dark text on it:
  30pt **#111519**, 28pt #000000).
- `box-blue-round-bordered-52` = bordered input/box variant.
- Corner pieces: `white-box-top-left-corner` / `white-box-top-right-corner` #FFFFFF.

## Text scale ladder (most → least frequent)
- 60pt #FFFFFF — page titles
- 50pt #FFFFFF / #CACDCE — section headers / tab labels
- 44pt #FFFFFF / **#8795A0** (muted) — large values / sublabels
- 31–32pt #FFFFFF — row primary
- 27–28pt #FFFFFF — row secondary
- 30pt #111519, 28pt #000000 — dark-on-white (inside WhiteBox panels)

## True-up checklist for the Hub native page (MPHubNativePage / MPCanvasUI hub)
- [ ] List backgrounds → white-rounded-box #262B40@0.43 (ours: owned rounded sprite, custom shade)
- [ ] Section headers → 50pt #FFFFFF + icon, height 100 (scaled to our canvas)
- [ ] Row cards → white-round-corner-drop #7F7F7F@0.70 idiom
- [ ] Row text → 31/28pt ladder (scaled)
- [ ] Scrollbar → 12px #7D8186
- [ ] Splitters → #FFFFFF@0.15
- [ ] Tab idle color #CACDCE + indicator idiom if the Hub grows tabs
- [ ] Muted text → #8795A0 (we use greys — align)
- Native sprites are obtainable at runtime by name lookup (same approach as the
  captured rounded sprite) — but the grey-cast lesson applies: verify the sprite's
  base color before tinting (findings: hub grey-whites bug).
