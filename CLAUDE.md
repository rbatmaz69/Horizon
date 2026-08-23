# Horizon

An Android driving game about the *experience* of driving, not racing.

## Concept

The player steers a vehicle freely through a large, contiguous open world. There is no fixed
goal and no time limit — what matters is the feeling of freedom, exploration, and the visual
beauty of the world. The relaxed atmosphere of *Alto's Odyssey* or *Art of Rally*, applied to
free-roam driving.

## World & environments

One seamless world made of zones that flow into each other:

- **Mountain passes** — winding serpentines, elevation change, wide vistas, fog/clouds at altitude
- **Cities** — denser building, traffic, lights and intersections, streets with distinct character
  (old town, modern skyline, suburbs)
- **Country roads** — gently curving roads through fields, forests, small villages
- **Highway** — straight fast sections with traffic, bridges, tunnels

Transitions between zones must feel seamless, so the world reads as one whole.

## Gameplay

- Free driving with no forced objective
- Realistic but *accessible* handling — not arcade, not a hardcore simulation
- Day/night cycle and dynamic weather (sunsets, rain, fog) for mood
- Points of interest / viewpoints where the player can stop and enjoy the view
- Later: photo mode; gentle progression (new vehicles, new regions) without pressure

## Visual style

Warm, inviting low-poly / stylized. Must perform on mobile yet feel atmospheric and beautiful —
the reference points are *Alto's Odyssey*, *Monument Valley*, *Journey*. Flat-shaded geometry,
strong silhouettes, colour and light doing the heavy lifting rather than texture detail.

---

# Working conventions

## Tech stack

- **Unity 6 LTS** (`6000.3.x`), **URP**, C#, target **Android** (ARM64, IL2CPP)
- Packages: `com.unity.inputsystem` (Active Input Handling = *Input System Package*),
  `com.unity.splines`, later `com.unity.adaptiveperformance`
- Language for everything — docs, identifiers, commit messages — is **English**

## Guiding principle: feel before beauty

The vehicle and camera get tuned on a grey plane before scenery exists. If driving does not feel
good, no amount of art will fix it. When in doubt, spend the effort on handling and camera.

## Module layout

`Assets/_Project/Scripts/` — one assembly definition per module. **Dependencies point one way only:**

```
Horizon.Core           no dependencies (utilities, ChaseCamera)
Horizon.Input          -> Unity.InputSystem
Horizon.Vehicle        -> Horizon.Core, Horizon.Input (via IDriveInput only)
Horizon.World          -> Horizon.Core
Horizon.World.Splines  -> Horizon.World, Unity.Splines (optional, see below)
Horizon.Atmosphere     -> Horizon.Core
Horizon.Updates        no dependencies (GitHub release feed)
Horizon.Game           -> everything (leaf assembly: scene wiring, debug overlay)
Horizon.EditorTools    -> everything (Editor platform only)
```

Optional package integrations get their own assembly with a `defineConstraints` entry, so the
assembly is skipped entirely when the package is absent rather than erroring on an unresolvable
reference. `Horizon.World.Splines` is the pattern — copy it for any future optional dependency.
Never add an optional package's assembly to a core module's `references`.

Never add a reference that points back down this list. If a lower module needs something from a
higher one, that is a signal to introduce an interface in the lower module and wire the
implementation from `Horizon.Game`. `DriveInput.Current` in `Horizon.Input` is the existing
example of this pattern — follow it rather than inventing a new one.

## Content layout

`Assets/_Project/` holds everything we author (the underscore sorts it above imported packages):
`Art/`, `Audio/`, `Prefabs/`, `Scenes/`, `Settings/`, `Scripts/`.

## Authoring Unity assets

Scenes, prefabs, `ScriptableObject` instances and URP assets are GUID-linked YAML — do **not**
hand-write them. Everything the prototype needs is constructed from code by
`Tools > Horizon > Rebuild Prototype Scene` (`Horizon.EditorTools/PrototypeSetup.cs`).
When the prototype scene needs to change, change that tool and re-run it. Keeps setup
reproducible and reviewable in git.

## Input

The four control schemes read their devices directly (`Keyboard.current`, `GravitySensor.current`,
`Touchscreen.current`) rather than going through an `.inputactions` asset. There is no asset to wire
up, and nothing is generated. Introduce an `.inputactions` asset when — and only when — we need
rebinding or non-driving UI navigation; those files are plain JSON and safe to edit by hand.

Sensors are **disabled by default** in the Input System. `InputSystem.EnableDevice(...)` is required
or they read zero, which looks like a broken scheme rather than a missing call.

## Scenes

Two scenes, and the split is deliberate. `Bootstrap.unity` carries only what must survive a zone
change — input router, frame-rate policy, debug overlay — and therefore has **no camera and no
geometry**; opened on its own it looks empty, and that is correct. `World_MountainPass.unity` holds
the road, terrain, sun and car, and is loaded additively at runtime by `GameBootstrap`.

Adding the city or the highway later means another world scene loaded the same way, not restructuring
Bootstrap. `GameBootstrap` skips its additive load when the scene is already open in the editor, so
working with both scenes open behaves the same as a clean Play.

## Data-driven tuning

Tunables live in `ScriptableObject` configs (`VehicleConfig`, `TimeOfDayProfile`), not in
component fields. A new vehicle should be a new asset, not new code. Configs can be edited while
in Play mode and the changes persist — that is the intended tuning loop.

## Performance budget (mid-range Android, 60 fps)

- Forward renderer, SRP Batcher on, GPU instancing on, static batching for props
- Baked lightmaps + light probes; realtime shadows from the sun only, tight cascade distance
- Post: tonemapping + colour grading always; bloom on Mid/High only; **no** SSAO, no realtime
  reflections, no motion blur
- MSAA 2× (cheap on mobile tile GPUs, and low-poly edges benefit)
- **No per-frame GC allocation** in driving code. This is the usual cause of mobile stutter:
  cache arrays, avoid LINQ and `foreach` over interfaces in `Update`/`FixedUpdate`, never
  allocate in a physics step.
- Fog does double duty: atmosphere *and* draw-distance hiding. Cull distance should sit inside
  the fog wall.

## Audio

Sound is **synthesised in code**, not shipped as files — see `EngineAudio`. A generated harmonic stack
costs a few kilobytes of source instead of megabytes of APK, has no licensing question attached, and
maps directly onto revs. Keep it that way for engine and tyre noise. Recorded audio is worth it
only for things synthesis genuinely cannot do (music, ambience with real character).

There is deliberately **no wind layer**. One existed, driven by speed, and it put a whoosh over the
engine on every acceleration — see the note on `EngineAudio` for why the obvious variations on it are
worse rather than better.

Generated loops must contain a whole number of cycles at the sample rate, or the loop point clicks
once a second. The engine drone is 56 Hz over exactly one second for that reason; noise beds get their
tail crossfaded into the head instead.

## Physics

Vehicle uses a **custom raycast-wheel model** on a single `Rigidbody`, deliberately not
`WheelCollider`. All forces are applied in `FixedUpdate` via `AddForceAtPosition`. Note Unity 6
renamed `Rigidbody.velocity` → `linearVelocity` and `drag`/`angularDrag` →
`linearDamping`/`angularDamping`.

Anti-roll bars and speed-dependent downforce are **not optional** — without them the car flips
on the first hairpin.

## The rev counter

`InstrumentCluster` draws the dial in the top-right corner — the only screen corner nothing else
owns. **Its face is built from the car rather than printed on it.** The ten engines redline anywhere
between 4200 and 8000 rpm, so full scale is the car's own redline rounded up to the next thousand,
the tick labels are written from that, and the red zone starts at the real redline. It re-runs
whenever `VehicleController.Config` is a different object, because `SetConfig` raises no event and
the garage changes car while the game is running.

The needle reads **absolute rpm over full scale**, not the idle-rebased fraction `EngineAudio.Revs`
exposes. That fraction is zero at idle, which is right for a pitch curve and wrong for a dial: a real
tacho sits just above zero when the engine is running, and `EngineRpm` is held at idle whenever the
engine is running. The one case where it is not is an empty tank — see the fuel section — and there
the needle falling all the way to the stop is the point rather than an exception.

Text in a HUD allocates. `label.text = $"{speed:0}"` makes a string every frame the number changes,
which above walking pace is most of them — so the numbers come out of a prebuilt table and are
assigned only when the integer moves. Anything else added to this dial has to do the same.

## Fuel

`FuelTank` lives in `Horizon.Vehicle` because an empty tank has to cut the throttle, and throttle is
consumed inside `VehicleController`. It burns from **work done**, not from a speed table: power is
`EngineTorqueNm × rpm`, so speed, revs and driving style all fall out of one expression instead of
three. `EngineTorqueNm` is published by the controller rather than recomputed — and is cleared on the
three paths that make no torque (mid-shift, on the limiter, off the throttle), or the tank bills the
driver for work they can hear the engine is not doing.

**The burn is scaled by 5, and the number is not a taste.** Honest physics gives a 55-litre tank
about 750 km against a world twenty-five kilometres wide, so at 1 the stations would be scenery. The
world's own clock looks like the obvious reference — `DayLengthMinutes` is 24, so the sun runs at
60 — but 60 does not work for fuel: burn rises with the *cube* of speed through the drag term, so a
compression that is brisk at a cruise is savage flat out, and a tank emptied in ninety seconds.

**20 was the first answer and it was also wrong, in the way worth remembering.** It was calibrated
against a steady 100 km/h, where it gave a comfortable twenty-two minutes — but holding 100 km/h asks
about eight per cent of the engine, and nobody plays this way. Half throttle at 3000 rpm burns five
times as much and emptied a tank in **four minutes**. Calibrate against the driving style the game
actually gets, not against the one that is easy to compute. At 5 that is about 17 minutes, 8 flat
out, and an hour and a half if genuinely cruised; the gentle-to-hard spread is left as the physics
has it, because that spread is the feature. Distance is not compressed, so any l/100 km figure
derived from this would read five times too high — which is why the dial shows a level and nothing on
screen prints that number.

**The tank is never saved, and does not need to be.** Every run begins by placing the car
(`StartScreen.Drive` → `ApplyPlace` → `PauseMenu.MoveTo`), and placing the car fills it. Respawn does
the same, because a respawn that leaves you unable to move is not a recovery.

**A station has to be findable, and three separate things had to be right before it was.** Its sign
face and its canopy luminaires are on a submesh of their own with a plain bright material and no
`TownLights` registration, because every `LitGroup` swaps between a day material and a night one and
these are the two things on a forecourt that look the same at noon as at midnight. Sharing the lit
slot painted them `M_Lane` — road asphalt — all day, since that is what `LitGroup.Lamps` uses so a
street lamp's pool of light can vanish into the carriageway. The board is also *broadside* to the
road: `AddBox`'s half-length runs along its `forward` argument, and getting that the wrong way round
presents a 16 cm edge to the only person who will ever read it. And an advance sign stands 250 m
back, because the totem at the entrance only helps somebody who has already arrived.

**The forecourt says what to do.** Painted aisles between the pump islands — laid-on geometry at the
motorway merge's 2 cm lift, legitimate here only because the slab has no camber, which is the trap
`08aba1f` had to unpick on the town streets. And a line at the top of the screen: pull up, stop,
refuelling, full. `FillingStations` tells them apart with a **rectangle** against the slab's own axes,
not a radius — a circle big enough to hold a 52 × 34 m slab reaches the carriageway, and would prompt
every car that drove past.

A station is a `RoadFeatureKind.FuelStation` on a course, beside the tunnels and the viewpoints —
never a scene object. It is the only feature kind with a `Side`, because the motorway's two
carriageways are one course. Its pad is fed into `MountainField` **before the field is built**; do
that after and the apron comes out perfectly flat, hovering over a hillside, with nothing complaining.
`ValidateFuelStations` measures the longest stretch without a pump and warns past 6 km — it caught a
6.2 km run on the eastbound carriageway on the first build, which is why the motorway services are
paired.

`Tools > Horizon > Render Fuel Station Preview` photographs all of them from the road, day and night,
on the approach past the advance sign, and from under the canopy. **Every fault this feature has had
was found in those pictures and by nothing else** — bushes growing through a forecourt, a lit soffit
that was 240 square metres of pure white over the driver's head, a canopy with no underside at all
(what looked like a dark ceiling was sky), a sign painted with asphalt, and a sign turned edge-on.
The build reported none of them. Look at the pictures.

## The Meerenge

The road on from the Ebental is two courses — `KalkgratCourse` climbs a ridge, bores through it and
drops down the Steilufer; `MeerengeCourse` runs the coast, crosses a strait on a suspension bridge and
carries on up the far shore. Twelve kilometres, which is half again what the world had.

**Nothing in this world is revealed from more than about half a kilometre, and the leg was designed as
if it were.** The Kalkgrattunnel was built as a shutter: 280 m of rock, and then the strait, the far
shore and the towers arriving in one frame. The crossing is five kilometres from that portal, the
camera's far plane is 600 m and the fog wall stands inside it. The set-piece was impossible and the
build reported nothing — `WorldPreview_Strait_1_Portal` reported it. Any moment that depends on
distance has this problem. The sea arrives on the corniche and the bridge on the last corner before it.

**The coast road had no coast, and three separate things were hiding it.** This is the one worth
reading before touching any of it again.

- *Distance.* `WaterPlanner` lays a river's spine square to the deck of the bridge it is named for, so
  the strait's axis is fixed by the crossing. A coast road that wanders relative to that axis opens and
  closes its distance to the water by more than a kilometre over its length. Every heading on the
  corniche is held within about fifteen degrees of the channel's own.
- *Height.* Everything here takes its height from the roads, the strait's surface included, so a coast
  road at sea level gets a sea at road level. The descent stops on a shelf about fifty metres up
  instead of running down to the water, and `ChannelFreeboard` is 24 m rather than the 6 a river in a
  valley needs.
- *The bank, twice.* It is a smoothstep and therefore convex in its middle: with the road 300 m back
  and the bank 240 m wide, the line of sight to the waterline passed *below* the bank's own crown and
  the ground hid the water. The bank now reaches past the road. And nothing in this project had ever
  kept a plant off a **shore** — only out of the water — so every bank in the world came up wooded to
  the waterline. `VegetationShape.ShoreTreeClearing` is clamped per body by `MountainField.IsShore`,
  which is why one number reads as "no trees on a bank" for a 1200 m strait and a 70 m tarn alike.

**How steep a hillside comes out is set by the legs, not by the corners.** `MountainField` derives the
mountain from the road, so the slope between two stacked switchback legs is decided by how far apart
they are in plan. The pass turns at 20 m with 250 m legs and stacks them forty metres apart, which is
its 65 % face. The Steilufer keeps open corners — 38 to 70 m — and got its fall from shortening the
legs to 90–140 m. Open corners and a cliff are not the same knob.

`RoadFeatureKind.Suspension` is a kind of its own and `IsBridged` reports it alongside `Bridge`, because
every caller of that predicate is asking one question — is the ground under this stretch the road's
business — and for both the answer is no. The two differ in exactly one place: `BridgeBuilder` plants a
pier pair every forty metres, and a pier pair across a shipping channel is what the other kind exists to
avoid. `SuspensionBridgeBuilder` shares that one's girder and parapet rather than copying them; a deck
is a deck whatever is holding it up.

**The main span is 950 m and not the 1074 of the bridge it is named after, and that number comes from
the renderer.** Past about a kilometre between the towers there is no frame anywhere on the deck that
holds both of them, and a suspension bridge whose shape you cannot see is a road with railings.

The structure's dimensions live on `MeerengeCourse` and are passed to the builder as a
`SuspensionShape`, because three things have to agree about where a tower stands: the course, which puts
the anchorages on land by choosing the structure's length; the channel, whose half-width decides whether
the towers rise out of water or out of a field; and the builder. A constant in the builder would be a
fourth opinion, and the other three are the ones that go wrong silently.

The beacons and the cable beads are on a submesh with a plain bright material and **no `TownLights`
registration**, for the reason already recorded against the filling station signs. A bridge lit along
its cables is most of why anybody photographs one.

`ValidateSuspensionBridges` asks the four questions `ValidateBridges` cannot: is there water under it,
is there enough air over that water to be a shipping channel, does the cable stay above the parapet it
is holding up, and is the deck level. Each failure builds without complaint and shows only in a picture.

Beyond the water is `LandRegion.Anadolu` — warm ground, red earth, and half the trees cypresses (the
avenue's poplar mesh, scattered instead of planted in a row; a spire and a crown are still telling apart
at four pixels, which no ground colour can claim). It hangs off the **same road** as the corniche and
begins at the eastern anchorage, because what separates the two countries is 1250 m of bridge rather
than a different piece of tarmac — that is what `LandRegion.StartAlong` is for.

Traffic reaches it through `TrafficNetworkBuilder.OnwardRoad`: the trunk road runs into the country
road runs into the Kalkgrat runs into the Meerenge, four courses that are one drive, each joining the
previous at a node they share. Past two, chaining them as their own pairs of arguments stopped scaling.

**`CheckLanesFollowTheTrunkRoad` measured every trunk lane against the pass alone, which made it a
check that always failed** — thousands of samples "outside the carriageway" by kilometres, on roads
that were correct — and therefore one nobody would read when it caught the fault it exists for. It now
takes the nearest of every paved road.

The deck is lit: a lamp standard every 48 m and a bead on the cables every 26. Not decoration — it is
the one place in the world with no verge, no hedge and no horizon to judge position against, and at
the first spacing the night shot came back with four lights on a kilometre of cable.

**The side spans were a hundred and fifty metres of carriageway over an open hole, each.**
`BridgeBuilder` takes only `RoadFeatureKind.Bridge`, so a suspension span never got a pier; `AddHangers`
hung off the main cable, so it never reached outside the towers; and `MountainField` had meanwhile carved
its nine metres of headroom under the *whole* structure, both kinds being `IsBridged`. Two correct halves
and nothing between them. They now take piers from `BridgeBuilder.AddPiers` — which is why that takes a
stretch of course rather than a deck somebody already sampled — and hangers off the back-stay, and
`ValidateBridgeSupport` measures the longest bay of deck with nothing under or over it. That check walks
the list the builders fill rather than working out for itself where a pier belongs: a checker with its
own opinion agrees with the builder right up until one of them is wrong.

`AddPiers` also emitted **no pier at all** for any span under about sixty metres — `Max(1, span/40)` with a
loop over the interior. None of the three authored viaducts is short enough to have shown it.

**One lateral offset was doing the work of three, and it was sized for a cable.** Towers, cables,
hangers and anchor blocks all stood at `OuterHalfWidth + CableOffset` = 7.65 m: right for something half
a metre thick, and for a 4.5 m anchor block it put a seven-metre concrete wall two metres inside the
lane, on both sides, at the entrance and the exit. Six point three metres of clear width on a road that
is thirteen and a half wide. The deck is now `DeckOverhang` wider than the road on it — which is what a
suspension deck is — the towers stand in that margin, the anchor blocks have an axis of their own and sit
behind the abutments, and `AddFootways` lays the slab the widening opened up, without which the gap
between asphalt and parapet is a hole through the bridge. `ValidateSuspensionBridges` asks a fifth
question now: does any of this stand in the road it is carrying. Every other check in the build looks up,
down or along, and none of them looked across.

**Nothing at the edge of any road in this world used to be solid.** Guard rails, delineators, median
barrier, viaduct parapets and the crossing's — all built with `addCollider: false`, and a doc comment
above `BuildBridges` had been asserting the opposite for some time. A doc comment is not a test. They are
solid now, but **not against the mesh you can see**: a `MeshCollider` taken from a rail as drawn is a row
of re-entrant corners every four metres and the car catches on each of them, which is what the original
decision was really objecting to. `GuardRailBuilder.BuildCollision` walks the same `Plan` a second time
and sweeps a smooth wall along it; `BridgeBuilder.AddBarrier` does the same for a parapet. Both go in
through `CreateMeshObject`'s `collisionMesh`, which the tunnels have used since what you can see and what
you can hit first became different questions. Delineators stay soft — a post is a marker.

`Tools > Horizon > Render Strait Preview` photographs all of it, day and night. Every fault above was
found there and by nothing else — including the three above, which needed two shots that did not exist:
`_Entrance` and `_Exit` frame the gap between the anchor blocks, and `_SideSpan` looks under the deck.

## Anadolu

`YalikoyCourse` carries on from the Meerenge: the eastern cape, the bay behind it, the fishing village of
Yalıköy on its shore, and the climb into the dry hills over it. Six kilometres. The bridge now leads
somewhere; before this it ended eleven hundred metres later on a falling straight in the middle of a
hillside, and a threshold with nothing behind it is a long piece of road.

**The bay is not the strait, and they are kept a cape apart.** A `Sea` *sets* the ground under it while
the Boğaz, a corridor river, only caps it — two of them over the same water fight, and the loser leaves a
step across the middle of it. The tunnel through the cape is the only place on this road with water on
neither hand, which is what it is for.

**The seafront is dead straight, and that is structural rather than styling.** Yalıköy hangs off the
driving road, the way Talheim hangs off the pass, so town-local space folds wherever that road bends
towards the town more tightly than the town is deep — `LimitAcross` caps a town at 0.65·R, and 300 m of
`Inland` needs 462 m of radius. The bend *leaving* the village is 520 m and has 120 m of straight in front
of it for exactly that reason; at 240 m `ValidateTownMapping` reported the along-axis squeezed to 0.29.

**A fishing village has its quay against its road.** The first attempt put the waterline 40 m out and the
basin further again, which came back in the picture as a lane through a heath with a lighthouse on the
horizon: two hundred metres of flat dry scrub between the driver and the harbour. `ShoreOffset` and
`BasinAcross` came in together, and they are bound — the basin's landward rim has to clear the
carriageway, and `|BasinAcross| − ShoreOffset` has to stay under `BasinRadius` or the moles spring from
open water instead of from the beach.

**Water only exists where a terrain tile does, and the corridor is 200 m wide.** Without `bayBand` the
bay ran out a couple of hundred metres off the quay — inside the fog and inside the far plane, so it read
as a lagoon with an edge on it. Same fix the Westmeer and the strait already have, same reason.

**The bay has to arrive on a corner.** Running parallel to the front four hundred metres short of the
village keeps the road 270 m from a waterline that one ordinary rise then hides completely — which is what
the viewpoint's first picture was. The road comes in at an angle now and turns onto the front, so the
water arrives with the corner.

**And the layby may not look at the bridge.** It was called *Köprü Manzarası* and placed at the top of the
climb, four kilometres from the crossing against a 600 m far plane — the Kalkgrattunnel's lesson, on
course to be repeated. Moved to the end of the seafront and renamed, it looks back down the village, which
is 560 m and therefore actually there. A viewpoint on the climb has a second problem besides distance: the
mountain is derived from the roads, so the ground between a seafront and the track climbing away from it
is a shoulder, and a viewpoint behind that shoulder is a viewpoint of it.

`HarbourMeshes` was already world-agnostic — a `HarbourSite` is a centre, a radius and a landward vector —
so Yalıköy's harbour is Seeburg's, one climate over. What had to be generalised was `PrototypeSetup`'s
`BuildHarbour`, which had Seeburg's constants written into it.

**`CheckLanesFollowTheirStreets` was measuring Seeburg against Talheim.** The validator's town list had
two entries and the world had three; every Seeburg lane was being held to streets eight kilometres away
and reported as a car on the pavement — 3730 samples of correct road. Exactly the failure its own class
remarks describe, one town later. It now takes all four.

`Tools > Horizon > Render Anadolu Preview` photographs the leg, day and night. Every fault above came out
of those pictures.

## Updating

The game is sideloaded, so nothing tells a player that a release happened. `Horizon.Updates` asks
`api.github.com/.../releases/latest` once per app start and `UpdateScreen` puts the answer on the
start screen: a version row, and behind it a page with the release notes and a download button.

It only ever **offers**. The button hands the APK URL to `Application.OpenURL`, and the browser plus
Android's own installer do the rest. An in-app download would need `REQUEST_INSTALL_PACKAGES`, a
FileProvider in a `.androidlib` manifest and an install intent through `AndroidJavaObject` — every
failed attempt at which costs a twenty-minute IL2CPP build to observe.

`ReleaseVersion.TryParse` encodes a version the same way `AndroidBuild.TryVersionCode` does
(`major*10000 + minor*100 + patch`) **on purpose**: that is the `bundleVersionCode` Android compares
when deciding whether an APK may install over the running one, so a release the game calls newer is
one the installer will accept. The copy exists because `AndroidBuild` is Editor-only; change one and
change the other.

`AndroidBuild.Configure` sets `forceInternetPermission`. Unity's "Auto" infers INTERNET from what
survives IL2CPP stripping, and when it guesses wrong the symptom is a transport error on the phone
against a check that works in the editor.

## Commits

Imperative mood, English, one logical change per commit. Do not commit `Library/`, builds, or
`UserSettings/`.
