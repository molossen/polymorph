# Changelog

All notable changes to Polymorph. Newest first.
Download the latest version from the [website](https://molossen.github.io/polymorph/); installed copies update themselves
(green **Update** button in the menu bar).

| Version | Highlights |
|---|---|
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
