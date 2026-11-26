# Changelog

All notable changes to Polymorph. Newest first.
Download the latest version from the [website](https://molossen.github.io/polymorph/); installed copies update themselves
(green **Update** button in the menu bar).

| Version | Highlights |
|---|---|
| [1.9.0](#190) | New MAP mode: the whole game map with its levels of detail, new lighting, water, clouds |
| [1.8.9](#189) | My own shortcuts (any command, export / import), Ctrl+X cut |
| [1.8.8](#188) | Ctrl+C / Ctrl+V of objects, also from MAP to MODEL mode |
| [1.8.7](#187) | F frames the view while painting terrain layers |
| [1.8.6](#186) | Delete / Ctrl+Z quick on very big scenes, less memory |
| [1.8.5](#185) | View radius in MODEL mode, whole game map can be hidden |
| [1.8.4](#184) | MAP mode view radius for big custom maps |
| [1.8.3](#183) | Asset Browser pictures, gizmo on the model, updates over a running AI app |
| [1.8.2](#182) | Panels can leave the window (other monitors) |
| [1.8.1](#181) | Asset Browser docks at the bottom, MAP mode save and undo |
| [1.8.0](#180) | Ped Forge: any character or animal becomes a FiveM ped |
| [1.7.9](#179) | FBX import, no more 60k vertex warning |
| [1.7.8](#178) | Multi-selection X / Y / Z fields, viewport [+] menu |
| [1.7.7](#177) | Scan button in the toolbar, demo video |
| [1.7.6](#176) | Scan to block-out goes on to a clean room |
| [1.7.5](#175) | The AI sees the block-out clearly over the scan |
| [1.7.4](#174) | Scan to block-out in one button: copy the prompt, the AI does the rest |
| [1.7.3](#173) | Scan to block-out: level, square and scale a splat scan, then let an AI model the room through MCP |
| [1.7.2](#172) | Bone animations exported to GTA (.ycd), played by the model on its own |
| [1.7.1](#171) | Skinned GTA export: bones + weights in the .ydr |
| [1.7.0](#170) | Target Weld, FFD, ProOptimizer, easier UV selection, shapes + Lathe, bones / Skin / weight paint |
| [1.6.5](#165) | Fix: start-up crash on Chinese / Japanese / Korean Windows (shader "unexpected end of file") |
| [1.6.4](#164) | Edges easier to click inside a room |
| [1.6.3](#163) | Scale box: stretch the selection from a side, an edge or a corner |
| [1.6.2](#162) | QuickSlice, no see-through wires when zoomed out, faster Wall Opening, edge clicks |
| [1.6.1](#161) | Fix: MLO exports crashed the FiveM client |
| [1.6.0](#160) | Molo Paint: 80 alphas, 80 reliefs, eyedropper, radial symmetry |
| [1.5.3](#153) | Line: rounded / chamfered corners, sweep your own profile |
| [1.5.2](#152) | Line tool: draw door frames, beams, pipes and extrusions |
| [1.5.0](#150) | Molo Paint node materials |
| [1.4.8](#148) | Molo Paint: picked-parts mask, drips brush |
| [1.4.6](#146) | Molo Paint: procedural textures, 14 blend modes, filters |
| [1.4.5](#145) | GIMS-style model export |
| [1.4.3](#143) | Multi loop cut, knife options, grow / shrink selection |
| [1.4.0](#140) | Gaussian splat scans, path-traced photo render |
| [1.3.0](#130) | Python inside Polymorph |
| [1.2.0](#120) | C# scripting inside Polymorph |
| [1.1.0](#110) | One-click updates, animation, Wall Opening, texture library |
| [1.0.0](#100) | First public release |

---

## 1.9.0

**MAP mode rebuilt**: a new engine for the map view, with our own tools kept.

- **The whole game map at once, with its levels of detail.** Every map object of the game (about 800,000) is read in a
  few seconds (kept in a cache for the next starts) and drawn the way the game does: far away one simplified model
  stands for a district, closer its children take over, up to the full detail near the camera. Typically 60 FPS where
  the previous version ran at about 30.
- **Read only until you click.** A click on any object of the world opens its .ymap for editing and selects it. The
  rest stays light (models on the graphics card only, let go when unused).
- **What the game shows in free roam**: maps only switched on by missions (prologue, cutscene stand-ins, alternate
  states) are left out; models shared by several archetypes and far tree cards now show (they were missing).
- **New lighting**: sun or moon of the hour, light from the sky and from the ground, shiny surfaces
  mirroring the sky, distance haze towards the sun.
- **HDR image**: light beyond white, filmic curve, the eye getting used to the dark at night, glow around bright
  lights, ambient occlusion in nooks and under objects.
- **Sky worked out from the air** (blue overhead, red sunsets) and **clouds in volume** drifting with the wind
  (weather presets).
- **Sun shadows in three slices of the view**, up to 800 m, also from the far hills and buildings.
- **The game's water**: sea, lakes and rivers from water.xml, with waves, foam at the shore, the sky and the buildings
  mirrored, the bottom seen through shallow water.
- **City lights at night** seen from far away (the game's distant lights).
- **Relief (PXM parallax)** on the ground, stones and walls that have a height map.
- Each effect can be switched off in the World panel (Sky, Haze, Water, Night lights, Relief, New light).

## 1.8.9

- **My own shortcuts** (Edit > Preferences > Keymap): give any command your own key, with Ctrl / Shift / Alt. Click
  **Set key** and press the key; Esc cancels, Backspace removes it. Your keys go before the keymap's, so they can also
  replace one. A key belongs to one command (giving it to another moves it).
- **Right-click a command in the search** (F3 / X) to give it a key straight away. The search shows your keys.
- **Free a key**: a keymap key you don't want does nothing anymore.
- **Export / import my keymap** as a .json file, to keep it or share it with other people.
- **Ctrl+X cuts objects** (copy + delete), Ctrl+V pastes them back, also in the other mode (MAP / MODEL). A cut object
  pasted back keeps its name.

## 1.8.8

- **Copy / paste objects**: Ctrl+C copies the selected objects (with their children and materials), Ctrl+V pastes them at
  the same place, Ctrl+Shift+V pastes them at the 3D cursor. Also in the Edit menu.
- Works between modes: copy a game prop in MAP mode, switch to MODEL mode, Ctrl+V gives a plain editable model at the
  same place. Paste back in MAP mode adds it to your build.
- Ctrl+Z takes a paste back.

## 1.8.7
- **Terrain painting (4 layers): F frames the view on the object** like everywhere else. It used to switch to the fill
  tool, which also stopped the terrain brush.

## 1.8.6
- **Very big scenes (millions of faces, tens of thousands of objects): Delete and Ctrl+Z are quick again.** The undo
  history no longer keeps a full copy of the scene: it shares every mesh with the scene and only the meshes an edit may
  change (the selection) are copied; a copy the edit did not need is given back at once. The history has a memory budget
  (1.5 GB), the oldest steps go past it. Measured on 20 000 objects / 8.5 M faces: delete 3.4 s -> 0.1 s, Ctrl+Z
  0.8 s -> 0.1 s, history after 15 edits 2.8 GB -> 1.0 GB, and no more extra copy of the whole scene in memory.

## 1.8.5
- **View radius in MODEL mode too:** Views > *Only show what is near the camera* + *View radius*. On a very big scene only
  the objects within that distance of the camera are drawn and can be picked (the selected one always shows). Same
  setting as in the MAP panel.
- **MAP mode:** with the view radius on, the game's far models (LOD / SLOD) beyond it go too (before, the objects only
  switched to their LOD). New **None** button in the map levels: hides the whole game map, only your own objects stay.

## 1.8.4
- **MAP mode view radius:** in the MAP panel, *Only show what is near the camera* + *View radius* (50 to 3000 m). On a big
  custom map only the objects around the camera are drawn and can be picked, so the editor stays smooth whatever the
  angle (no more looking straight down to avoid lag). The selected object always shows. Remembered between sessions.

## 1.8.3
- **Asset Browser pictures:** the ones on screen are made first (scroll down a long list: no wait for what you scrolled
  past), three models load at once, their textures are loaded for real before the picture is taken (no more white
  props), and freed after (it no longer slows down with time). **Refresh** reads the folder again and makes the pictures
  of the list again. Pictures made white by older versions are not reused.
- **Gizmo on the model:** a model whose pivot is far from it (imported with its pivot at the scene origin) gets the move /
  rotate / scale gizmo on itself, not far away.
- **Updates:** the installer stops the Polymorph MCP server (the Python an AI app such as ChatGPT or Claude keeps running)
  before replacing files, so no more "Error opening file for writing ... pyembed". No need to uninstall first.

## 1.8.2
- **Panels can leave the window.** Drag a floating panel (Asset Browser, Material Editor, any window with a title bar) out
  of Polymorph by its title: it becomes a window of its own on the desktop, on another monitor too, no longer cut by the
  main window. Drag it back over Polymorph and it is inside again. Files dropped on it are imported like on the main
  window. Can be switched off in Edit > Preferences > Interface (applied at the next start).

## 1.8.1
- **Asset Browser: Dock at the bottom.** A button in the browser attaches it along the bottom of the views (above the
  timeline), full width between the side panels; drag its top edge to make it taller or shorter. *Float* makes it a free
  window again. Remembered between sessions.
- **MAP mode undo:** after deleting faces (or any edit) on what you model on the map or on a game model opened in place,
  Ctrl+Z brings the mesh back and the view redraws it at once. Checked with the real keys (Delete, Ctrl+Z, redo).
- **MAP mode Save / Save As:** File > Save (Ctrl+S) now saves what you modeled on the map into a .mmx (the first time and
  on Save As a file window asks where), and still writes the map resource (.ymap, new models). File > Open of a .mmx in MAP
  mode puts its objects back on the map, at the same place, editable. The title bar shows the MAP file.

## 1.8.0
- **Ped Forge** (new tab, next to Weapon and Vehicle): any character or animal becomes a FiveM add-on ped.
  1. *Show the donor*: a game ped (man, woman, deer, boar, cow, dog, cat, chimp...) comes in as an orange ghost.
  2. *Fit my model on it*: same height, feet on its feet.
  3. *Make the ped*: your model is skinned automatically on the donor's skeleton (weights taken from its body); arms held
     differently (T-pose on an A-pose ped) are brought to the donor's pose by themselves. Writes a ready resource:
     .ydd, .ytd, .ymt, .yft (donor's skeleton, ragdoll and animations), peds metadata, fxmanifest and a `/name` command.
  Checked in FiveM: a T-pose .fbx character walks and idles with the game's animations.

## 1.7.9
- **FBX import** (binary and ASCII, FBX 2010 and newer): meshes with their polygons, UVs, vertex colours, materials and
  textures (next to the file, in a texture folder or embedded), the object hierarchy, pivots and axis / unit settings.
  File > Import, drag and drop, and the Asset Browser all take .fbx. Skinned meshes come in as static meshes (warning shown).
- No more 60,000 vertex warning: big models are cut into 65k-vertex pieces automatically at export, so a model of any
  size goes to GTA. The checker only adds a note above 500,000 vertices (game performance).
- GTA settings: *Join models up to (vertices)* can now go up to 2,000,000.

## 1.7.8
- Fix: with several objects selected, the X / Y / Z fields at the bottom move, turn or scale all of them together (they keep their spacing)
- Fix: the viewport's [+] menu could not be clicked (the Scene Explorer's resize grip was covering it)

## 1.7.7

- **Scan button** in the top toolbar (next to Assets): no scan yet, it imports one (.ply / .spz / .splat); a scan in the
  scene, it selects it, opens its panel and copies the block-out prompt for your AI.
- Importing a scan opens the Object tab, so the *Copy the block-out prompt for AI (MCP)* button is in view.
- Search: *Scan: copy the block-out prompt for AI (MCP)*.
- Demo: a world made in Marble by World Labs, turned into a GTA-ready room
  ([video](https://molossen.github.io/polymorph/scan-to-blockout.mp4)).

---

## 1.7.6

- **From block-out to a clean room**: the block-out prompt now goes on to rebuild the room properly over the block-out:
  one inward room shell with real openings and reveals, baseboards, door and window casings, door leaves, window glass,
  stairs with treads, risers, handrail and balusters, GTA optimised (no hidden faces). Furniture is left to props.
- `splat.view`: back faces are not drawn, so a room shell seen from outside opens up like a cut-away; faces crossing the
  camera are clipped instead of dropped; trims and voids have their own colours.

---

## 1.7.5

- **Scan to block-out, clearer**: the pictures the AI looks at (`splat.view`) now draw the block-out properly: only the
  nearest surface in each pixel, shaded, edges only where they are seen, and the scan kept in front where it is nearer
  (furniture before a wall). Pieces are coloured by kind (walls light, doors orange, windows blue, stairs green,
  furniture in colours); `face_alpha` 1 draws a solid clay block-out.

---

## 1.7.4

- **Scan to block-out, simpler**: the scan panel keeps a single button, *Copy the block-out prompt for AI (MCP)*.
  Paste it to your AI assistant connected to Polymorph: it stands the scan up, gives it its real size and models the
  room over it. Levelling, squaring and real size are now MCP tools (`splat.level`, `splat.square`, `splat.real_size`).

---

## 1.7.3

- **Scan to block-out**: three new buttons in the scan panel. *Level on the floor* finds the floor of the scan and
  makes it flat at height 0, *Square the walls* turns it so its walls run along X / Y, *Real size* scales it from the
  ceiling height you give.
- **Block-out by AI through MCP**: new `splat.*` tools let an AI assistant read a scan: views from any camera with the
  block-out drawn over it, plan cuts with a metre grid, floor / ceiling / furniture heights, distances to walls, and the
  big flat planes. *Copy the block-out prompt* in the scan panel gives it the steps: walls split around doors and
  windows, door leaves, stairs, furniture as simple boxes, then the GTA check.

---

## 1.7.2

- **Bone animations to GTA**: bones keyed on the timeline (and the skinned object itself) become a clip of the
  resource's `_anims.ycd`, named like the model, with each bone's position and rotation under its parent (the bone
  tags of the exported skeleton), at the timeline's frames. The archetype gets the clip dictionary and flags
  1024 + 512 so the model plays it by itself, like the animated props of Molo maps. "Export .ycd" writes the same clip.

---

## 1.7.1

- **Skinned GTA export**: an object with a Skin modifier (last in its stack) is exported as a skinned .ydr, like the
  game's cutscene props: the mesh in its bind pose, a skeleton (a root bone + your bones, with the game's bone tags)
  and up to 4 weights per vertex. Works in the full export and in "Export selected" (.ydr).

---

## 1.7.0

- **Target Weld** (Edit Poly, under Collapse, or search): click a vertex, then the one to weld it onto. The second one
  stays where it is. Stays on for the next weld; right click or Esc to stop.
- **FFD 2x2x2 / 3x3x3 / 4x4x4** (Add Modifier > Deform): a cage around the object. "Edit control points" shows it:
  click a point (Shift adds), drag on empty space to box-select, drag a point to bend the mesh smoothly. X / Y / Z lock
  the move on an axis, A selects all. Reset, Conform, "Only in volume".
- **ProOptimizer** (Add Modifier > Generate): fewer polygons, same look. Keeps UVs, vertex colours, materials and
  smoothing exact, never moves open borders, UV seams or material borders. Made for LODs: put it on the HD model,
  lower the %. A 200 000-polygon room is halved in about 2 s, then the result is kept.
- **Easier UV editor selection**: Vertex / Edge / Face / Island modes (buttons or 1 2 3 4). What the mouse is
  over is highlighted, a click takes the nearest point / edge / face (no need to hit inside a tiny face; stacked faces:
  the smallest one), Shift adds or removes, Ctrl removes, drag on empty space box-selects, L takes the island under
  the mouse, A all, Alt+A nothing. Moving, rotating, scaling and the buttons work on points and edges too.
- **Shapes**: Create > Rectangle / Circle / Arc with editable sizes, "Make it a line" to edit the
  points. "Smooth curve" makes any line pass through its points as a curve. New render **Lathe**: draw half the outline
  of a vase, a column or a lamp, it turns around an axis (segments, degrees), closed and facing outward.
- **Bones, Skin and weight paint**: Create > Bone (with a bone selected: continues the chain). Add a Skin modifier to
  the mesh, "Bind" makes the weights from the distance to the bones. Turning or moving a bone bends the mesh.
  "Paint weights" shows the chosen bone's weights in colour (blue 0 ... red 1); left button adds, Ctrl removes, Shift
  smooths, [ ] radius.

---

## 1.6.5

- **Fix: Polymorph closed at start-up on Windows set to Chinese, Japanese or Korean** with "gta_basic.hlsl(389,1):
  error X3000: syntax error: unexpected end of file". The shaders were handed to the compiler in the system's code
  page, and a non-ASCII character in a comment could swallow the line break after it. They are now passed as plain
  ASCII, and the shader files no longer contain any non-ASCII character.

---

## 1.6.4

- **Edges easier to click inside a room**: an edge whose far end is behind the camera (close up inside a room, most
  of the walls' long edges) was skipped and could not be clicked. It is now cut where it enters the view.
- **Edges of a room seen from outside**: with Backface Cull on, the near wall of a room (its faces turned inwards) is
  not drawn, but it still hid the far walls' edges from the click. Only what is drawn hides edges now.
- **The click stays on the mesh you edit**: when another piece of the room lies on the same wall, a click on an edge
  or vertex of the edited mesh takes it, instead of jumping to edit the other piece.

---

## 1.6.3

- **Scale box** (Scale tool, "Box" next to it in the toolbar, on by default): a box with handles is drawn around the
  selection. Drag a side to stretch only that way, an edge for two axes, a corner for all three; the opposite side
  stays where it is. Shift keeps the proportions, snapping rounds the size to the grid, and the size in metres shows
  next to the handle. Works on objects and on vertices / edges / polygons in edit mode.
---

## 1.6.2

- **QuickSlice** (Modify > Edit, under Knife, or search "QuickSlice"): click two points and the whole object is cut by
  that line, straight through, like a saw. With polygons selected only those are cut. It stays on for the next cut;
  right click or Esc to stop.
- **Wall Opening is much faster on big rooms**: the hole is now cut only in the part of the mesh around the opening,
  not on the whole room, and when the holes are already in the mesh the modifier costs nothing. On a 200k-polygon
  interior every edit redid the boolean and copied the whole room (0.1 to 0.8 s each), which made the scene lag.
- **No more seeing through the mesh when you zoom out**: the edit wires, edges and the red of selected polygons were
  pulled towards the camera by an amount that grew with the square of the distance (several metres on a big room), so
  the back walls showed through. The pull is now a small fixed part of the distance, like 3ds Max.
- **Edge and vertex clicks on big meshes**: a click picks the edge you see in front, not a hidden one behind it that
  was a few pixels closer to the mouse, and it no longer tests every triangle of the mesh (up to 1 s per click on a
  200k-polygon room, which also made some clicks miss).

---

## 1.6.1

**Fix: exported MLOs crashed the FiveM client** ([#2](https://github.com/molossen/polymorph/issues/2))
- The interior collision is now named after the MLO archetype, the archetype names it as its physics dictionary,
  and the export writes a `_manifest.ymf` that declares it as the interior's bounds. Before, the game crashed a few
  seconds after loading the interior.
- The `_milo_.ymap` is written like the game's own interiors (HD instance, LOD 200 m, interior content flags), the
  MLO archetype gets real bounds, and rooms / exit portals without settings get working defaults.
- New warning: a room with no portal path to the outside (it would be invisible in game).
- Tip: the interior goes where **Placement on the map > Position** says (GTA export settings, with Heading and "Paste coords"); left at 0, 0, 0
  the rooms keep their scene coordinates.

---

## 1.6.0

**Molo Paint, painter upgrade** (brushes, reliefs and tools taken from the open-source painter SubCraft)
- **80 brush alphas** in the shelf's Alphas tab, with thumbnails and a search: scratches, chipped edges, cracks,
  bullet impacts, grime streaks, rivets, vents, leaves, splatters... **Import alphas...** adds your own PNG / JPG
  (white = paint); they are kept in Documents\Polymorph\alphas.
- **80 relief textures** in a new **Relief** tab: bricks, cobblestone, diamond plate, corrugated metal, rivets, planks,
  peeling paint, rust pitting... One click adds a relief layer (height only, tiles every metre) with a tile size and a
  strength; give it a mask to keep it where you want.
- **Eyedropper**: **I** or the **Pick** button, then click the model: the brush takes its colour, roughness, metal and
  relief.
- **Radial symmetry**: up to 16 copies of each stroke around the object's vertical axis (wheels, lamps, columns).
- **Scatter**: each print lands at a random spot inside the brush.
- **New masks**: Dirt (blotches), Speckles, Pores.
- **New filters**: directional blur, highpass, threshold, clamp and outline on masks; threshold and clamp on colours.

The alphas and reliefs are CC0 (public domain), from the SubCraft starter pack by Aalzard; the normal maps were turned
into height maps for Polymorph.

---

## 1.5.3

**New on the Line tool**
- **Round the corners**: a radius and a number of segments in the line's panel (1 segment = a chamfer). Arched door
  frames, rounded counters, chamfered beams; the line itself shows the rounded path.
- **Sweep (your profile)**: draw the section of a moulding as another line, pick it in the panel, and it follows the
  path (its first point rides on the line), with a scale. Skirting, cornices, handrails, picture frames.

## 1.5.2

**New: Line tool** (Create > Shapes > Line), like the classic modeller's spline line.
- Click points one by one. Each one snaps to the vertices, edges and faces under the mouse, the selected object's too.
- Shift keeps the segment straight on X / Y / Z, Alt places a free point.
- Click the first point (or press C) to close; right click or Enter to finish; Backspace removes the last point; Esc cancels.
- The line stays a shape. Its panel turns it into a **door frame casing** (square, stepped, chamfer or round, with width
  and depth), a rectangular beam, a round pipe or an **extrusion**, rebuilt live. *Convert to Editable Poly* freezes it.
- A casing stands out of the wall toward you and grows away from the opening (*Flip side* for the other way).
- Move the line's points in Vertex mode; click the line to select it.

**Improved**
- Vertex snap: when two points sit on top of each other on screen (front and back corners in a side view), the nearer one wins.
- Vertex mode on huge meshes (a joined building of 800 000 points): points are shown all over the mesh, not only around the mouse.
- No more "> 60k, split it" warning in the stats and the import report.

## 1.5.1

**Fixed**
- Node material: a crash right after editing the graph (it was read by several cores at once).
- File > Open Recent: two scenes with the same name showed a red ImGui "conflicting ID" error; the folder now shows next to the name, the full path on hover.
- The node editor opens low on the screen, so the model stays visible.

## 1.5.0

**New: node materials in Molo Paint** — *Node material...* on a fill layer.
- Nodes: Colour, Value, Mesh (AO dirt, edges, cavities, top, height), Noise, Voronoi (cells, cracks), Bricks, Pattern,
  Warp, Colour ramp, Mix (every blend mode), Math, Levels, and the output (colour, roughness, height, metal).
- Drag an output onto an input to link, right click to add, Delete to remove.
- The graph is saved with the layer and in smart materials.

## 1.4.9

**Fixed**
- Molo Paint: *Pick the parts on the model* turns painting on (clicks did not reach the model when painting was off).
- Scripting / MCP: a C# script that only reads no longer marks the scene as modified; `app_quit` saves the scene to its file first, so no "Save changes?" dialog is left waiting.

## 1.4.8

**New in Molo Paint**
- **Picked parts** mask: click the parts of the model (face, connected piece or UV island) where the layer shows — a colour-ID mask without an ID map.
- **Drips** particle brush: drops run down the surface from your strokes (rust, paint, water), thinner as they go, with an adjustable length.

**Fixed**
- Wood pattern: rings a few centimetres apart (they were more than a metre).

## 1.4.7

**Fixed**
- The outliner's eye (and freeze, rename) stopped working after Undo / Redo: the list kept old copies of the objects.

**Improved**
- MAP: *My MODEL scene here* and *Import a 3D scene here* ask where to put it. If a .ymap (and its .ytyp) already sits next
  to the scene — exported resource, Sollumz / CodeWalker map, .ymap or .ymap.xml — it is offered, and the models go where
  that .ymap places them (an MLO by its interior entity). Otherwise: the scene's GTA position, or the ground in the middle of the view.

## 1.4.6

**New in Molo Paint** (ported from ArmorPaint, zlib licence)
- Procedural textures in a fill layer (*Pattern*): voronoi cells, cracks, bricks with mortar and per-brick tint, checker,
  waves, wood rings, magic, Perlin noise — with a 2nd colour, relief, own roughness and Warp.
- The same patterns as masks, plus Warp on every mask.
- 14 more blend modes: Darken, Lighten, Colour burn / dodge, Soft / Hard / Linear light, Difference, Subtract, Divide, Hue, Saturation, Colour, Value.
- Filters: Brightness / contrast, Gamma, Posterize, Colour ramp.
- Smart materials: Red brick wall, Cracked mud, Wood grain, Cobblestones; masks: Cracks, Brick joints, Clouds, Stains.

**Improved**
- Material panel: a thumbnail of each texture (Diffuse, Normal, Specular, Detail), big on hover.
- **Join (Ctrl+J)** with Drawables selected: meshes go into a mesh of the active Drawable, never into the Drawable node itself; hidden objects, collisions and lights stay apart.
- Walk mode (arrows) ends as soon as you orbit, pan or zoom; Ctrl / Alt + arrow never walks.
- Holding Ctrl to add to the selection no longer turns the snap on (it still flips it during a G / R / S move).

## 1.4.5

**New**
- **File > Export models**, GIMS style: a list of models with a checkbox each, .ydr / .yft, collisions on / off, one file
  per model with its own pivot and embedded textures. "_high_0" meshes take their model's name.

**Improved**
- Big selections stay fast (outlines were rebuilt every frame above 48 objects).
- Hide on a Drawable hides its meshes too.
- Right click no longer jumps the camera in the Top / Front / Left views.

## 1.4.4

**Improved**
- A click always lands where it is made, even with no mouse move just before.
- Vertex mode shows every visible point up to 150 000 points.
- Separate (Detach) works in Vertex and Edge mode, also from the right-click menu.

## 1.4.3

**New**
- **Loop Cut**: several loops at once (wheel, + / -), Tab turns the cut the other way.
- **Knife** options: angle steps (free, 15°, 45°, 90°; Shift = 45°), straight cuts on world axes, visible faces only. A cut can start in the middle of a face.
- **Ctrl + Up / Down** grows / shrinks the selection (arrows alone still move the camera).
- Add-on: GTA-style stairs generator (0.20 m risers, 0.30 m treads, ramp collision).

**Fixed**
- A quick 2nd click on a face adds that face instead of the whole loop.
- Shrink works on open meshes and in edge mode; UV Editor "conflicting ID" errors.
- Every reported bug is now a test that replays the user's gestures.

## 1.4.2

- GTA .dds textures (and normal maps) in .glb export and the photo render.

## 1.4.1

- Photo render progress bar follows the engine's live status.

## 1.4.0

**New**
- **Gaussian splat scans**: import .ply (3DGS or point cloud), .spz (v1–v4) and .splat. The scan shows in every view,
  mixed with your models, as a reference to model on. Simple settings; never exported to GTA; saved with the scene.
- **Photo render** (F12): a path-traced photo of the active view, computed in the background. Plain settings: quality,
  size, light (view, day, evening, night, studio), brightness, sky, lamp power, grain, soft shadows, transparent background.
  Also from Python / MCP (`render.photo`).

**Fixed**
- MLO archetype: no physics dictionary name (it crashed the game).

## 1.3.1

- Several doors and windows on the same wall (Room Builder, Wall Opening, Cut Doorway).

## 1.3.0

- **Python inside Polymorph**, like `bpy`: `import poly`, add-ons, a script editor.

## 1.2.0

- **C# scripting inside Polymorph** (`script.run`, MCP `script_run_cs`) for heavy loops.
- Terrain 4-layer painting fixed (brush sized to the mesh, no paint material over the terrain shader).

## 1.1.4

- Middle drag grabs the selected edges / points near the mouse.

## 1.1.3

- The update feed is read at every start and again right before downloading.

## 1.1.2

- Hide / Unhide All / End Isolate in the right-click menus.

## 1.1.1

- Move tool: middle button on the selection moves it (empty space still navigates).

## 1.1.0

**New**
- **One-click updates**: a green *Update* button when a new version is out; download, SHA-256 check, silent install,
  restart. Settings are kept. Help > Check for updates.
- **Animation**: timeline, keyframes (I / K), auto key, dope sheet, UV and camera keys, GTA .ycd import / export.
  UV animations play in game.
- **Wall Opening**: drag a rectangle on a wall, pull the sides, Enter cuts. Doors, windows, arches, gothic and round
  openings; stays editable as a modifier; works on thick walls and MLO rooms.
- **Clean booleans**: straight quads around doors and windows, no crash far from the origin, only the crossed faces change on dense meshes.
- **Texture library** built from your own GTA V (every interior, materials sorted, .dds written).
- **Asset browser**, Make Planar, crossing box select.
- MAP: DLC maps replace their base map, interiors shown near the camera, open a stream folder like a CodeWalker project,
  save one .ymap in place, send selected models to MODEL.
- Terrain 4-layer shaders: live blend in the view, paint the layers with the vertex colour brush.
- Open window with bookmarks and recent scenes; Search always fits the toolbar.
- Selection undo, UV editor gizmo, material copy / paste, camera lens, typed transforms, material pipette.
- Bevel segments, Connect segments; Subdivision is Simple by default.
- Outliner eye hides / shows the whole selection; drag down the eye column like Blender.

**Performance**
- Heavy scenes (490k-polygon rooms, thousands of objects) and MAP mode streaming without hitches.

**Quality**
- Chaos tests: thousands of random modelling operations and random clicks in the real window, with undo / save / export checks.

## 1.0.0

First public release. Free, by Molo Modding.

**Modelling**
- Edit Poly and Unwrap UVW as modifiers, anywhere in the stack (non-destructive).
- Chamfer, bridge, knife / slice, booleans, extrude with an arrow and an *Adjust Last Operation* panel.
- Vertex / edge / face snaps with visible targets; grab a point and drop it on the target.
- Shift + move copies faces / edges / points; on edges it extrudes along the drag.
- Sculpt (Smooth, Relax, Flatten, Draw, Inflate, Grab, Pinch, symmetry) to clean generated meshes.
- Instances (Alt+D), isolate, game-style fly camera (right button + ZQSD / WASD), draggable ViewCube.
- Scene Explorer that holds thousands of objects; crossing selection, *Visible only* (Alt+Z).

**Surface**
- Cube Projection (UV + material), Smart UV, UV editor, texel density; UV right-click menu (Unwrap, Lightmap, Cube, Cylinder, Sphere, From View, Pack…).
- Game shaders, Light tab (32 lights, sun shadows, filmic curve), face orientation view.
- Molo Paint: layered texture painting, smart masks, high-poly bake, DDS export.

**GTA V / FiveM**
- Import and export .ydr, .yft, .ydd, .ybn, .ytyp, .ymap — a ready FiveM resource (fxmanifest, stream, embedded textures).
- Automatic LODs and collision, vertex colours by the game's rules, DDS 1024 with mipmaps.
- GTA checker with one-click fixes (shell in limbo, closed rooms, normals…).
- **MLO Room Builder**: plan drawn or read from an image, automatic portals, materials per surface, skirting, dirt and worn corners, door and window frames (5 styles), connected rooms.
- Point / Spot / Capsule lights with every Sollumz property.
- Game files browser (models, textures, collisions by material); send to your FiveM test server.

**MAP mode**
- The whole GTA V map with its LOD / SLOD, like CodeWalker; HD loaded around the camera.
- Move, delete and place props stuck to surfaces; edit game interiors (MLO), the .ytyp is rewritten.
- Model right on the map: every modelling tool, the Room Builder on the ground, a game model opened as your own copy in place.
- Save as one FiveM resource.

**Forges**
- **Weapon Forge**: any 3D model becomes a GTA weapon (a game weapon's skeleton, auto fit, replace or add-on with cloned metas).
- **Vehicle Forge**: any car model becomes a FiveM vehicle (wheels detected, fitted on a game car's wheels, .yft + metas + textures).

**Scripting**
- Python API and MCP server: Claude can drive Polymorph (create, edit, UV, check, export, screenshots).

**Known limits**
- Game interiors are edited object by object in MAP mode; rooms and portals themselves are redrawn in MODEL mode (import the .ytyp).
- New archetypes of a resource started while you are connected show after a reconnect (FiveM behaviour).
