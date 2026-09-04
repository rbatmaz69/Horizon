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
- Post: tonemapping + colour grading always; bloom on Balanced/High only; **no** SSAO, no realtime
  reflections, no motion blur — see *The frame* for what this specified for years without existing
- **MSAA is off, and this line used to say 2×.** That was true when it was written and stopped being
  true when the renderer went to `RenderScale 0.8`: MSAA there is antialiasing an image that is about to
  be bilinearly upscaled, and on a tile GPU 2× halves the tile and doubles the bins in a world that is
  geometry-bound rather than fill-bound. They are alternatives, not companions — if edges are the
  complaint, `RenderScale 0.85` is the cheaper answer and it is one number. `ValidatePostStack` prints
  MSAA, render scale and grading mode for both pipeline assets every build, so this line and
  `Mobile_RPAsset.asset` cannot silently disagree again
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

**And there is deliberately no ambient world audio either, which is the second time that has been
decided here.** A full one was built and removed: wind rising with altitude, water at a shore, birds
in a wood by day and insects in the low country at night, driven by a canopy field baked from the
trees the world had actually planted, by the distance to the nearest body of water, and by the clock.
It was removed after being driven, for two reasons given from the car. Wind and water **together**
were simply irritating — two broadband beds under an engine, however carefully they were separated
from each other. And **birdsong with no bird anywhere in the frame did not read as a place; it read
as a notification arriving.** That second one is the more useful of the two, and it generalises: a
sound with no visible source in a game with a 600 m far plane and no animals in it has nothing to
attach itself to, so the ear files it under "the device", not "the world".

**The rule that comes out of it: this is a driving game and the car is the subject, so anything in
the mix that is not the car is competing with it.** The first wind layer was deleted for masking the
engine; this one was deleted for standing beside it as a second thing to listen to. What conveys the
world is what the car does *in* it — which is exactly why the one part of that work the player liked
was the surface under the tyres, and why that is where the effort went instead. If ambient sound ever
comes back it has to arrive through the car, or be attached to something the driver can see they are
passing.

**Every measurement in that work was correct, and not one of them was the question.** The build check
walked all 75 km of road and found three real faults — a canopy normalisation that put birds on 88 %
of every road, a water range that left the Meerenge corniche hearing the strait on 27 % of a road
whose entire character is running beside it, and a coast road at 0 % that turned out to be 399 m from
the nearest water and correct. A second check measured the synthesised clips and found a fourth: the
wind was a whistle, because a two-pole resonator's gain goes as 1/(1 − r²) and 0.9955 is a gain of a
hundred and eleven, so the moan drowned its own hiss and normalisation finished the job. All four
were found by measurement and by nothing else, exactly as intended — and the feature still had to go.
**A check tells you whether a thing is what you said it was. It can never tell you whether it should
exist.** The only instrument for that is driving it.

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

## The boost gauge

A third dial in the top-right cluster, above the fuel gauge, and it is not there at all on six of the
ten cars.

**The number it draws already existed, and it is not modelled twice.** `EngineAudio` has held plenum
pressure since the turbo arrived — exhaust energy above `TurboSpoolRevs` times throttle, built at
`TurboSpoolRate` and collapsing at four times it — and spent it on a whistle and a dump valve and
nothing else. It is `EngineAudio.Boost01` now. A boost model in the drivetrain or in `Horizon.Game`
would have been a second opinion about one thing, which `UpdateTurbo`'s own comment already forbids:
the needle and the whistle are the same number, so a dial claiming the turbo is on song cannot
disagree with an engine that sounds like it is not. It is **already smoothed**, so the gauge adds none
of its own — unlike the tacho, which needs it because `EngineRpm` steps at 50 Hz. A second chase here
would be lag the car does not have, and collapse is four times as fast as build precisely so that a
lift reads as a lift.

**`VehicleConfig.IsTurbocharged` is one test, because two would have hidden six instruments or shown
four.** `UpdateTurbo` decided it inline from `TurboWhistle` and `BlowOffLevel`, and the gauge asks the
same question to decide whether to exist at all. The Van and the Offroader are what make the obvious
spelling wrong — turbocharged diesels with no throttle plate to shut, so they carry a whistle and no
valve.

**Three states, and all three have to be tellable apart.** No turbocharger is the dial being gone; a
turbo fitted but off boost is the needle on the stop with a white compressor; on boost is the needle
out and the compressor lit in the accent orange. The middle one is what a gauge that only lit up would
lose, and the hole below the spool point is most of the character of a big single turbo — it is
something you should be able to watch.

**It is built active and hidden at run time, never the other way round.** `HudPreviewRenderer`
photographs a saved scene in which no `Update` has run, so anything the build leaves inactive is
invisible in every picture this project takes of its own HUD — and the default car is the naturally
aspirated Fastback. An instrument that no frame can show is exactly the failure the preview tools
exist to catch.

**The first size was 100 units, and the arithmetic was the trap.** 100 + 30 + 170 = 300 made the
column of small dials span the rev counter exactly, top edge to top edge and bottom to bottom — an
answer so tidy it looked like the answer. The picture came back with a compressor that was a smudge,
tick marks that had vanished, and a dial narrower than the one beneath it reading as a stray bauble
rather than as the top of a stack. **A layout can square up on paper and still not be an instrument.**
Both are 170 now and the column overhangs the tacho by 70 at the bottom, which is better: the
alignment that matters is the one at the top, where the eye starts, and equal widths are what make two
circles a column instead of two circles.

**The compressor is solid where the brake disc is a ring, and that is the whole reason the symbol
works.** Those two are the pair most at risk of collapsing into one shape — both are round things with
something off the side — and a wall thickness is not a difference anybody reads at forty units. A blob
with a pinhole against a donut with a hub are opposites at any size. The outlet duct is drawn well
*inside* the housing rather than tangent to its rim: three attempts had it touching, and each came
apart the same way when shrunk, into a spot in the bottom corner with no visible connection to
anything.

Three marks against the fuel dial's five, and it is the only thing on the two faces that differs.
Identically sized dials one above the other need something to tell them apart before the symbol is
read, and a count survives being glanced at. Five would say nothing here anyway: a boost scale has no
quarters worth naming.

**And the picture could not answer the question until it was taught to.** Both small dials place
their own marks on the first frame they get, and `HudPreviewRenderer` photographs a saved scene in
which no `Update` has ever run — so every mark sat stacked at the centre of its dial under the
needle's hub, and both faces came out as a bare ring with a needle across it. The fuel gauge had
been photographed that way for builds, unnoticed, because the dial is correct in the running game.
`FuelGauge.LayOutFace` and `BoostGauge.LayOutFace` are public for the tool to call, which is the
argument `MapGraphic.SetView` and `Minimap.ForwardBias` already make: the alternative is the tool
carrying its own copy of where a mark goes. **The rev counter is deliberately left bare** — its face
is built from the car and there is no car in that scene, so laying it out would mean choosing a
redline, and a picture that invents its subject is worse than one that admits it has none.

`Tools > Horizon > Render HUD Preview` is where every fault above was found. The build reported none
of them, and each build it said nothing about was otherwise clean.

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

## The Stadtfeld

`StadtfeldCourse` runs 3.7 km from Hochstadt's east gate back up to the Ebental, and it is the first
road here that exists for the shape of the network rather than for the ground it crosses. Before it
the world was a thirty-kilometre strand with two dead ends and one fork — the motorway interchange —
so the only decision a driver ever made was whether to turn round. It closes the loop
Talheim → pass → Ebental → Stadtfeld → Hochstadt → motorway, and everything east of the fork (the
Kalkgrat, the Meerenge, Yalıköy) becomes a branch off a ring instead of the far end of a corridor.

**The city's arterial is not a road, and a leg grafted onto its end would have started in a field.**
`HochstadtCourse` is never paved: `PrototypeSetup` builds it as `ArterialPath` and hands it straight
to `PrepareTown`, because a town's trunk road only has to be a coordinate frame and a height datum.
What is driven through Hochstadt is the boulevard in `HochstadtLayout`, which ends at a degree-one
node at `CityEnd` — 120 m short of where the arterial does, and those 120 m are bare datum for the
skirt rings. So the leg hangs off `HochstadtCourse.EastGatePoint` at `CityEnd`, and its first
instruction carries `HochstadtCourse.Grade` rather than a grade of its own, for the reason
`AutobahnCourse.MotorwayGradeAtJunction` gives. `HochstadtLayout`'s `BoulevardStart`/`BoulevardEnd`
were literal copies of `CityStart`/`CityEnd`; they now read the constants, because if those two ever
came apart the country road would arrive where the boulevard is not and nothing would say so.

**24 m over three kilometres is what decided the character.** Every other road here is defined by its
radii and each is the opposite of its neighbour — 20 m hairpins at 9.5 %, nothing under 150 m and
nothing over 3 %, 240–450 m sweepers at 6.6 %, nothing under 850 m. This one physically cannot be a
climb or a descent, so its interest is the profile instead: ordinary 260–320 m corners, and a road
that rises 25 m out of the city, drops 13, climbs 12, drops 10 and climbs 15 again, so a corner exit
is regularly over a rise. That is the Ebental's one crest — "the one genuinely demanding moment on an
otherwise open road" — stated for a whole leg, and it is the only character on the list that stands
being driven in both directions, which the closing side of a ring will be. **No distant set-piece:**
the first crest stands 25 m over Hochstadt and the city is still 2 km away, which is the
Kalkgrattunnel's mistake exactly. The city arrives in the last few hundred metres or not at all.

**Two branches of a fork must agree in *height* while they are close, not merely get apart.** They
have to be close — that is what a fork is — and `MountainField` gives every road a shelf 80 m wide
and a coarse grid reaching 250 m, so near the mouth the two shelves are one whatever the plan says.
The rule that matters is therefore the vertical: 1.1 m apart at 44 m of separation, under 3 m at
100 m. `AutobahnCourse.MergeOffset` records what the alternative looks like — a five-metre ridge
standing through a carriageway. `ForkDeflection` is 32° because below about 29° the two roads are
still inside each other's coarse grid after 500 m, and because the honest bearing to Hochstadt is
only 20° off the Ebental's own, at which angle the junction reads as a road that widens.

**`RoadCourseBuilder.ConnectTo` has a failure mode that reports success.** It takes the shortest of
four Dubins families and `TurnBy` deliberately goes the long way round rather than crossing zero, so
when the authored road above ends in a pose the target does not suit, the shortest family that exists
can be one that turns through 300°. That is a full loop of carriageway in the middle of a country
road: geometrically exact, arriving on the target to the millimetre, logging nothing, and validating
cleanly. It is 359 m here; at four degrees more or less on the corner out of the city it is 2200, and
no radius between 220 and 380 m rescues it. `StadtfeldCourse.ConnectLimit` is the guard, and the
corner is 122° because that sits in the middle of a four-degree plateau rather than on its edge.

The fork is marked on the **Ebental**, 200 m before its end, because `AddJunction` wants straight and
level track and the Kalkgrat's own first instruction climbs at 1.6 % and carries a forecourt. Only two
courses therefore have to agree about it, and `KalkgratCourse` is untouched. The `Ebenkopf` viewpoint
that stood at the Ebental's end is gone: it existed because the road stopped there, and it does not.

`TrunkForkBuilder` lays the mouth — the class three doc comments had been asserting the existence of
for some time. It is a flared throat in the **branch's** frame, because the only line through a fork
that is not already paved is the one turning off; the surface is projected onto the trunk's local
plane at the mouth and blended to the branch's own over one verge width, which is what makes
`AddJunction`'s straight-and-level rule load-bearing rather than advisory. Laid on at
`MotorwayMergeBuilder.Lift` for the reason recorded there. Unmarked, for the reason
`StreetJunctionBuilder.AppendTrunkMouth` gives.

**Traffic does not use it yet, and that is visible rather than hidden.** `TrafficNetworkBuilder`'s
`OnwardRoad` is a chain, not a graph; a car choosing at a fork needs a node with three edges. Until
then the ring is drawn, driveable and empty, and traffic still runs Ebental → Kalkgrat.

`TrafficRouteValidator`'s `trunkRoads` gained an entry, and the way it gained one matters: Yalıköy's
street network was reaching for `trunkRoads[trunkRoads.Length - 1]`, so appending would have repointed
the whole village onto a road eight kilometres away and reported every lane in it as a car on the
pavement — the same failure this file has already had twice. The path is a named local now.

`Tools > Horizon > Render Stadtfeld Preview` photographs the leg day and night. `_10_ForkPlan` is the
acceptance shot: it is the only frame anywhere that would show a ridge standing between the two
branches of the fork.

## The Weissjoch

`WeissjochCourse` leaves the motorway's western leg at median 1078 m and climbs thirteen kilometres to
a col at **906 m**. Before it the whole world topped out at 196 m and the tree line sat at 160, so only
the top thirty-six metres of anything was above the trees. It is the first winter here, the first
region that decides anything by altitude, and the only road built as a dead end on purpose — the
descent is the drive back.

**The road is the mountain, and that is not a figure of speech.** `MountainField` averages road samples
inside `CoarseReach` (250 m), takes the nearest sample's height verbatim beyond that, and clamps the
boundary value at the edge of its grid; terrain exists only inside a 200 m corridor. There is no ground
anywhere in this world that the roads did not put there. A high pass with long traverses out into open
country therefore does not make a mountain — it makes a **plateau in the sky** at its own height, with
no valley beneath it and no summit above it. Altitude is only visible where a *lower* leg runs within a
couple of hundred metres of a higher one. The climb is one compact stack for that reason and not a
tour, and the first plan for it — three stacks joined by long traverses — was wrong for exactly this.

**The stack advances north, so the face looks south at the motorway.** The ground is steep only *along*
the direction the stack advances; across it the field clamps to leg height and goes flat. That grain is
the mountain's one free decision, and pointing it at the road that leads there means the wall is
visible from the carriageway.

**Hairpins are 176° with a 4° leg sweep, and the two summing to 180 is the whole point.** The pass uses
170 and 14, which sums to 184: it over-rotates four degrees a corner and alternates so the error
cancels over a pair, which is what fans its switchbacks around the summit. Fanning costs advance —
measured over this table, 170/14 spreads the stack 2273 m for the same climb and gives a 39 % face,
while 176/4 spreads it 1850 m and gives 48 %. A mountain is the steeper of those. Face steepness is
`(leg × grade + hairpin arc × 4 %) / (2 × radius)` and it scales without limit, so the stack is
deliberately *wider* than the pass's: 26–36 m hairpins against its 20, because 927 m in the pass's own
40 m footprint would be vertical.

**Length is not a choice.** Legs stay at or under the world's steepest 9.5 %, which gives about 8.5 %
effective once the flattened hairpins are counted, so 938 m of climb costs thirteen kilometres. Four
stages — 420 m legs at 8 %, then 360 at 9 %, then 300 at 9.5 %, then 240 — and the **tree line at 460 m
lands on the top of stage B and the snow line at 650 m on the top of stage C**, so the stages are four
places rather than one corner told twenty-eight times.

**The tree line used to be a fraction of the Passhöhe, and that had to change.**
`VegetationContext.ClimbFraction` normalises elevation against `MountainPassCourse` alone and
`TreeLineHeight` is 0.82 of that span. A mountain four times higher elsewhere clamps to 1 and comes out
**bare rock from the valley floor up**. Stretching the axis to cover both is worse: it would move the
world's tree line past 700 m and wood the pass to its own summit. So `LandRegion` may now carry an
**absolute** `TreeLineElevation` and `SnowLineElevation`, and `VegetationBuilder.ClimbAt` maps a region
that has them onto the same 0..1 axis with its tree line landing exactly on `TreeLineHeight`. Every
constant tuned against that axis — the snag band, the shrink-towards-the-line ramp, `BroadleafBelow`,
the boulder test, the shrub thinning — keeps meaning what it meant; only the elevation it means it at
moves. **The build log must still say "Tree line around 160 m" after any change here.** If it moved,
the axis leaked and the vegetation of the entire world moved with it.

`ScatterShrubs` was given the region for the first time, and only so it can read that line — shrub
appearance is still nobody's business but the shape's.

**Snow is a fourth colour in a comparison the tile builder was already making**, on the one shared
vertex-tinted material, in the slot the shoreline's sand occupies: no material, no draw call, no
vertices, for nine hundred metres of mountain. Two things make it read as snow rather than as paint.
It is **skipped on steep faces**, so the flanks between stacked legs come out bare rock with snow lying
either side of them — uniform white above a height is a cake. And the line is **jittered** by one noise
lookup, because a snow line laid on flat is a contour drawn round a mountain, which is what a map does
and not what weather does. `Snow line: N terrain triangles tinted snow` is in the build log, and it
**warns when nothing comes out** — a winter region with no snow in it builds and validates exactly like
one that works.

**Two pumps and nothing between them, and `ValidateFuelStations` warns about it correctly.** A forecourt
has to be poured flat and plants a level shelf a whole verge width around itself; there is no such
ground on eleven kilometres of switchback, which is the same reason the pass's second station is at its
foot rather than its summit. The col's pad sits 240 m of run-out clear of the last hairpin, because the
pass records a summit platform dropping twenty metres of ground onto the carriageway below it.

**The viewpoint at the col looks down its own road.** Nine hundred metres of altitude buys nothing you
can see: the valley is far outside the 600 m far plane with the fog wall inside it. What is within half
a kilometre is the last four hairpins, directly underneath. This is the Kalkgrattunnel's lesson and
this was the likeliest place in the project to repeat it.

The bore and the avalanche gallery each get **a traverse of their own**, and the arithmetic is why: a
stage-C leg is 300 m of which the straight halves are 143 m each, while a 190 m bore with 60 m of
approach at either end needs 310. `TunnelBuilder` also sweeps its massif 40 m to each side, which the
26–36 m hairpins here clear comfortably where the pass's 20 m ones barely do.

**Traffic does not use the ramp.** `TrafficNetworkBuilder` is written for exactly one interchange —
scalar parameters, one `bool merging`, one node, and a lane cut that breaks the nearside westbound lane
into exactly two pieces. `BuildMotorwayMerge` now takes a name so a second wedge does not overwrite the
first's mesh asset, and the second call throws its out-params away. Cars stream past this exit and none
of them take it; with the Stadtfeld road also empty, that is two, and it is the argument for doing the
branching-traffic job as its own change.

**The mountain is not visible from the motorway, and the stack was turned for nothing.** Its grain was
aimed south so the wall would face the road that leads to it; the frame taken to check that came back
as flat forest, twice, once yawed straight at the massif. Nine hundred metres of altitude buys no
silhouette from six hundred metres away when the far plane is six hundred and the fog wall is inside
it: the base is at the limit and the summit is 1750 m of slant range. This is the third time the same
lesson has been paid for here — the Kalkgrattunnel's reveal, Köprü Manzarası, and now this — and it is
the first time it was written into a plan as a risk *and then walked into anyway*. The Weissjoch
reveals itself by being climbed. Nothing else was ever going to work, and the orientation is kept only
because it costs nothing.

`Tools > Horizon > Render Weissjoch Preview` photographs the climb day and night. `_2_Valley` and
`_10_Above` are the two that carry it: from the valley floor it reads as arriving at the foot of
something big, and from above the three bands — forest, rock, snow — are unmistakable.

**`_8_Face` does not work yet and is left in deliberately.** It exists to answer the one question the
build cannot — whether the ground between two stacked legs reads as a mountainside or as a staircase of
flat shelves, which `MountainField` produces just as quietly — and three attempts at the camera failed
to frame it: first into the uphill cutting, then over the top of the leg below, then at a downhill face
so deep in its own shadow that terrain and background are the same grey. **So that question is open.**
It is worth more as a frame that admits it than as one quietly deleted.

## The Weissjochring

A closed circuit on the shoulder below the col: **14.6 km**, 810 m down to 560 and back, and the first
road here that is driven for its own sake rather than to get somewhere. It is also the first closed
loop in the project and the longest single piece of road in the world.

**A circuit with a real enclosed area cannot exist here, and that decided the shape before anything
else.** `TerrainShape.CorridorWidth` is 200 m — ground exists only that far from a road — so an oval,
or anything shaped like a modern Grand Prix track, has a hole a kilometre across in the middle of it
and nothing in the build says so. So the lap folds: **six rungs running across the mountain, a
hairpin's own diameter apart, with a long straight down one side joining the two loose ends.** The
furthest point inside the whole loop is **192 m** from tarmac against that 200 m corridor, which
`ValidateInfieldCoverage` measures — and that check exists because a hole in an infield is near no road
at all, so every other check in this build is blind to it.

**A circuit is a footprint, not a line, and that is a placement problem nothing here had before.**
Every other road is positioned relative to the one it leaves, which is safe for a leg carrying on from
somewhere — a leg cannot double back over a world it has not reached yet. This one can. Placed 360 m
off the col it ran its rungs two kilometres south at 810 m, straight over `MountainPassCourse` a
hundred metres above sea level, and the build reported **terrain standing 674 m above the asphalt at
1709 points of the pass**. Note where that was reported: against the road that was there first.
`ValidateRoadClearance` on the circuit itself said nothing. `LineAcross` is now measured against every
other road's plan bounds, and it costs nothing — the footprint lands inside the world's existing
bounds in both axes, so the coarse height grid does not grow.

**A level pad reaches a quarter of a kilometre past its own rim.** The paddock apron went in at a 190 m
radius pushed 150 m towards the ladder, and level samples behave exactly like road samples: the coarse
field averages them out to `CoarseReach`. The build reported terrain **162 m above the asphalt at 699
points** on a rung that ran through the disc. It is 120 m now and centred on the main straight, where
it is levelling ground the carriageway had already levelled — the same rule the forecourts follow,
which is that a pad plants a whole verge width of level ground around itself and has to be given room
for it.

**A clearance breach reported on one road can be another road's fault, and the distance along says
nothing about it.** The access road's closing solve ended 340 m from the pit mouth facing 58° away
from it — two poses that close with a 260 m turning circle is exactly the case `Close` warns about, and
the shortest Dubins family that existed came out **1935 m long**. The road built at 3546 m instead of
1700, looped back through the circuit, and put a carriageway at 810 m within eighty metres of a rung at
649. What the build said was *terrain standing 183 m above the asphalt at 312 points of the
Weissjochring* — a complaint about the circuit, caused by the road that joins it, with nothing anywhere
naming the culprit. `ValidateRoadClearance` now prints the world position as well as the distance
along, because a distance says where to look on this road and a position is what lets the cause be
found on another.

**The fold is also what makes the altitude visible.** `MountainField` derives the ground from the
roads, so two rungs 340 m apart with 170 m of height between them build a real hillside between
themselves — the switchback stack's own mechanism (legs 40 m apart for a 65 % face) opened out by a
factor of eight. From the climb out you look across at the descent below you, which inside a 600 m far
plane is the only way height has ever been visible in this world.

**Consecutive rungs are translates of one another, not mirror images, and that is one line of code.**
Each rung snakes — a rung that did not would be a ruler — and a snake traversed backwards is a snake
mirrored, which pulls two neighbours together by twice the amplitude in the middle. The rung's whole
snake therefore flips sign with each hairpin. Without it the closest approach anywhere on the circuit
was **73 m**; with it, 246. The amplitudes vary (41–55 m) so the rungs differ in character, and what
two neighbours actually vary by is the *difference* of their amplitudes rather than the sum.

**The height table is pinned to the region's bands, not to taste.** `LandRegion.Weissjoch` puts the
tree line at 700 m and the snow line at 600. A lap running 810 → 560 → 810 crosses both twice: snowy
rock at the top, dark spruce on white ground through the middle, green forest on the floor of the
Kesselgrund. `PaddockElevation` is therefore an absolute number and the access road's grade is what
absorbs whatever the climb above it does — which is also why the paddock is **96 m below the col**
rather than level with it. Level with it, the last kilometre of the lap climbs back to 906 m at eleven
per cent, and a main straight that is really a hill climb.

**`RoadPath.isLoop` already existed and had never once been passed `true`.** The arc-length table
closes on segment zero, `NormalizeDistance` repeats rather than clamps, and the Catmull-Rom neighbours
wrap — so a course marked closed is paved, railed, sampled for terrain and drawn on the map with no
seam at all. That is strictly better than butting two ends together under the line, which gives a
duplicate ring and a curve straightened at both ends by extrapolation. `RoadCourseBuilder.Close` is
the only way to set it, because three things have to happen together: the guard against the
three-hundred-degree Dubins family, the guard against a **self**-closure degenerating and emitting
nothing at all (the target is the walk's own start, so the two turning circles can be concentric), and
the trim of the final control point the solve lands on.

**Kerbs are the cheapest thing in this project that changes what a road *is*.** Sixteen metres of
asphalt with no centre line is a wide road; the same ribbon with kerbs is a race track and nobody has
to be told. Two vertex-tinted submeshes on the existing `RoadTint` material — one draw call, no new
material. Which side is the inside is asked per sample, not per corner, because the rungs snake and a
kerb laid on one hand throughout spends half the lap on the wrong side. No collider: a kerb is meant
to be driven over, and a `MeshCollider` on one would be the row of re-entrant corners
`GuardRailBuilder.BuildCollision` exists to avoid.

**No centre line costs nothing.** `RoadTextureBuilder.BuildSurface` paints `laneCount − 1` interior
lines, so the atlas is asked for with **one** lane. `RoadShape.Circuit`'s `MaxBankDegrees` is 3 and not
6: the camber drops the inner edge by `HalfWidth × sin(bank)` and that has to stay under
`TerrainShape.RoadShelfDrop`, and this carriageway is a quarter wider than the pass's, so every degree
costs a quarter more.

**Two checks that nothing else in the build can stand in for.** `ValidateCircuitClosure` asks whether
the lap meets itself, and whether it meets itself *facing the same way* — a closure that misses by a
metre still paves, still carries rails and kerbs, still passes the clearance sweep and still draws on
the map. `ValidateInfieldCoverage` samples the ground inside the loop against `DistanceToRoad`: every
other check here looks along a road or across it, and a hole in an infield is near no road at all,
which is exactly why they are all blind to it.

**`ValidateFuelStations` was taught what a loop is rather than switched off for one.** On an open road
the two ends count, because the start of a road is somewhere a driver can be; on a circuit there are no
ends and the gap wraps past the line. One pump, in the paddock: a lap is inside a tank driven hard, and
three filling stations round a race track to satisfy a rule written for a country road would be the
check wearing the costume of a feature.

**A null tint is not a mistake here, it is an instruction.** `VegetationMeshBuffer.MergeTinted` folds
*every* slot carrying a tint into the first one and bakes the colour into its vertices — which is why
the kerbs are one draw call carrying two colours, and it is exactly wrong for the two things in the
paddock that exist because they need a material of their own: the start/finish board, which must not
swap at dusk, and the road paint, which wants asphalt smoothness rather than a building's. Tinting
them merged the lot onto one material and the build said nothing louder than `1 of 4`. So the rule that
goes with this mechanism is not "never a null tint" — it is that a tint means "fold me in" and a null
means "keep me, I have my own material", and every slot has to mean one of the two on purpose.

**What it costs: +256 terrain tiles and +2.0 M world triangles, and the heaviest tile does not move.**
It is still `Terrain_-16_-6` at 27 052, which is a Weissjoch tile and was already the heaviest before
this existed. Growing the world costs streaming; growing a tile costs frame time, and only the second
is a budget. That is what `LandRegion.Weissjochring`'s lower density bought.

**The starting grid is data, not paint.** Twelve slots, staggered either side, six rows — and the
poses come out of the same `CircuitMeshes.GridSlot` table the boxes are painted from. Two copies of
that arithmetic would be twelve cars parked beside their boxes rather than on them: obvious in a
picture and impossible to attribute, because each half looks right on its own. Nothing races yet;
`StartingGrid` exists because deriving those places later, off a finished mesh, would be guesswork —
the argument `RoadCourse` already makes for carrying its features rather than re-reading them.

**The crown rises towards the middle of the road; it does not fall away from it.** `AppendRing`'s
section is +`Crown` on the centreline, three quarters of it at the quarter points and zero at the
asphalt edges — a shallow ridge, not a shallow dish. The start line and all twelve grid boxes were laid
with that sign inverted *and* at the merge's 2 cm lift rather than on top of the ribbon's own 8, so
they sat eleven centimetres inside the tarmac down the middle of the lane and broke the surface only at
the very edge. Built, counted correctly in the log at fifty triangles, and completely invisible.

This is the specific shape of the trap already recorded as "laid-on paving only sits flush where the
surface under it has no camber to follow", and getting the camber's *sign* wrong is worse than ignoring
it: ignoring it leaves the paint floating at the edges, where it can at least be seen. It also took
three passes to find, because the triangle count was right every time — the failure mode every laid-on
thing here has is that the number says it is there and only a picture says where.

**And getting the sign right was not enough: the shape has to be followed too.** The start line and all
six sector gates span the full width of the carriageway, and each was laid as a **single quad**. The
crown is a parabola across the road; a quad spanning both edges is a chord drawn under it, so the band
met the tarmac at the two kerbs and sat the whole `Crown` — eleven centimetres — *below* it down the
middle. Both circuits' start lines and all twelve gates were invisible, on the road, in daylight, with
every log line about them reading correctly and the grid boxes beside them showing perfectly (they are
0.16 m rails, too narrow to sag). `CircuitMeshes.AddStripe` lays a full-width band in eight spans now,
which leaves a third of a millimetre of sag. **The player's report was that none of the gates were
visible, and no build, check or preview frame had said a word.**

**The grid staggers by half a row, and the first version did not.** Advancing a row every two slots put
pole and second line abreast — six pairs of cars, not a grid. The build said it in one number and
nothing else would have: `0 m ahead of it`.

**The order along the main straight is the whole design of it: mouth, pumps, grid, line, turn one.**
The first version had the line at distance zero with the pit mouth 180 m after it, which put the grid on
the *far* side of the line — on the closure's climbing approach, and behind a car arriving from the col.
Reaching pole meant turning round, and the grid sat on a five per cent slope. A start you have to drive
backwards to is not a start. `LineDistance` is 530 m now and everything that has to agree about the line
reads it: the paint, the grid poses, the timing plane, the spawn point and the preview cameras. It is a
sum of the table above it rather than a number, so it cannot drift.

**The circuit's start place is on pole, not on the line.** Astride the timing line there is nothing to
say which way round the lap goes or where it begins. Sixteen metres back, in the first painted box,
with eleven more behind it, the road answers both. `BuildSpawnTable`'s `Add` now takes
`NormalizeDistance` rather than a clamp, because a grid slot can be at a negative distance and a clamp
put all twelve on the line.

**A lap only counts if it was driven.** Without gates the fastest possible time is: cross the line, turn
round, cross it again — four seconds, and it would sit at the top of the board forever. Six gates,
every 2.1 km, and they have to be passed **in order** — in order, not merely all of them, because any
weaker rule is satisfied by driving back and forth over one gate. Crossing the line always *restarts*
the lap whether or not it counted, which is the gentler of the two designs and the right one here:
nothing is refused, the clock simply starts again. They are **painted** — a pair of white bands, two of
them so a gate is not mistaken for the line — because a rule the player cannot see reads as the game
being broken, and the HUD carries a `GATES n/6` row so a lap that will not count says so while it is
still being driven rather than at the line.

**Whether a lap can pass them at all is now measured rather than assumed.** A gate is a plane with a
window in it, and every part of that can be true of the geometry and false of the drive: a circuit that
doubles back can leave the car already on a gate's positive side when the lap begins, and a gate laid
across a tight enough corner can be crossed outside its own window. The consequence is a readout stuck
on `0/6` with nothing anywhere saying why. `ValidateLapGates` walks the path from the line, once round,
running exactly the test the runtime runs. A driver does not follow the centreline — but a centreline
that cannot pass the gates is a circuit no line can. `LapTiming.SetGates` also flattens and normalises
the gate directions, which its own tooltip had claimed since the day it was written and which only
`SetCircuit` actually did for the line.

**No traffic, and that is a decision rather than an omission** — said in the comment beside it, the way
the Weissjoch ramp says it. `TrunkForkBuilder` gained a name for its mesh asset at the same time, for
exactly the reason `BuildMotorwayMerge` already had one: the second call was overwriting the first.

`Tools > Horizon > Render Weissjochring Preview` photographs the lap day and night. **`_2_Line` and
`_8_Infield` are the two that carry it** — the first contains the closure seam at the fastest point on
the circuit, the second looks square across the middle of the ladder, and neither fault appears in any
log. Both come back clean: unbroken tarmac under the gantry, and ground all the way across the infield.

**Two things the pictures say are not right yet, recorded rather than hidden.**

- **There is almost no snow on it, and the cause is the steepness test rather than the snow line.**
  `TerrainTileBuilder` skips snow on faces past `RockSlopeThreshold` — deliberately, because uniform
  white above a height is a cake and the flanks between stacked legs should come out bare. But the
  detail noise is 5 m of amplitude at a 33 m wavelength, so *open* ground is locally steep almost
  everywhere, and a circuit with 340 m between its legs is nearly all open ground. What is left gentle
  is the road's own shelf. The Weissjoch's stack does not have this problem because its legs are 52–72 m
  apart and the shelves are most of the mountain. So the ring at 810 m reads as rock where it should
  read as snowfield. The honest fix is a slope threshold that belongs to the region rather than to the
  world, and that is a change to make on purpose rather than in passing.
- **`_3_Fork` photographs the filling station instead of the fork.** The yaw sends the camera down the
  forecourt frontage and the pit mouth is off frame, so the one question it exists for — is there a
  ridge standing between the branch and the trunk — is still unanswered. It is worth more as a frame
  that admits it than as one quietly deleted, which is the same call `_8_Face` on the Weissjoch got.

What the pictures do say is right: the kerbs read as kerbs at any distance, the closure is invisible,
the tree line lands where 700 m says it does (bare rock and snags above it in `_4_Descent`, dense
spruce below it in `_6_Kessel`), and the infield is ground rather than a hole.

## The Bahçe Ring

A second closed circuit, five and a third kilometres, in the empty quadrant beyond the end of
Yalıköy — and the first road here whose shape came from somewhere else. It is **Istanbul Park**,
measured rather than remembered.

**The layout was traced, and the trace checked itself.** The reference plan was decoded to a
centreline, chained out of its twelve drawn segments, scaled so the lap comes to the real circuit's
5338 m, and reduced to fourteen corners. Two things then fell out that nobody put in: the net turn
came to **−360.0°**, and the corners split **eight left and six right**, which is exactly the count
the real circuit publishes. Neither was a target. That is the whole argument that the shape is the
place rather than an impression of it, and it is why the angles in `BahceRingCourse.Corners` are not
to be edited casually.

**An enclosed circuit was supposed to be impossible here, and measuring it is what said otherwise.**
`TerrainShape.CorridorWidth` is 200 m, which is why the Weissjochring is folded into a ladder instead
of being shaped like a race track. But Istanbul Park is not an oval — it doubles back on itself
twice — so at full scale the furthest point inside the loop is 281 m from tarmac and, far more to the
point, **every terrain tile the loop encloses is one `TerrainTileBuilder.ListTiles` already asks
for**: a tile is kept when its *centre* is within the corridor plus most of a tile, which reaches
319 m. There is no hole.

**Which exposed that `ValidateInfieldCoverage` was measuring a proxy.** It compared distance-to-road
against the corridor and errored past it — and would therefore have condemned a circuit that has
ground under all of it. It now builds the same tile list the builder is about to build and asks
whether the point lands on one, which is the rule this file states elsewhere: *a checker with an
opinion of its own agrees with the builder right up until one of them is wrong.* The corridor
distance is still measured and still printed, because how much of an infield is ground the roads
shaped and how much is ground the tile grid merely reached is worth knowing. It is a line in the log
now, not an error.

**The angles are the measurement; the radii and the straights are a fit.** A traced polyline carries
a few per cent of drift, and a few per cent over five kilometres is a lap that ends **431 m from
where it began, facing a hundred degrees wrong**. Every radius and every straight was solved as a
constrained least-squares against its measured value, so none of them moved far, until the walk ends
300 m short of the line. **Change an angle and the fit has to be redone**, and `CloseLimit` is what
stands there to catch it not having been.

**The table sums to exactly −360°, and the first version did not.** It stopped at the fourteenth
corner and left the last 36° for `Close` to lay. `TurnBy` goes the long way round rather than
crossing zero, so the shortest family that *existed* took the road most of the way round a 260 m
circle: the closure came out **1965 m**, the lap built at **6.99 km instead of 5.37**, and the spare
loop of carriageway ran through the access road's own corridor — which the build then reported as
*terrain standing 12 m above the asphalt of the Bahçe Ring*, a complaint about the circuit caused by
the shape of the circuit's own closure. `CloseLimit` was the only thing that named it. **A closure
asked to change the heading is a closure asked to gamble; asked only to cover ground, it is a
straight line.**

**Thirty metres of valley, and the main straight is level.** The real circuit falls about forty
metres into Turn 1 and climbs back over its last sector; here everything after Turn 1 drops to 30 m
by the exit of the eighth corner and the 730 m back straight climbs it all back. The straight itself
is 0 % over its whole authored length, and that is not taste: the fork mouth, the start line and
twelve grid boxes are all laid *on* it, and laid-on paving only sits flush where the surface under it
has no camber to follow.

**The access road comes in from outside, and that is not a free choice.** North of the start line
there is no circuit at all — the closure arrives from the south-east — so a road down the west side
reaches the pit mouth without crossing tarmac. The infield is on the other hand, and anything wanting
to be *in* it would have to go under the track.

Placement follows `WeissjochringCourse.LineAcross`'s rule to the letter: measured against every other
road's **plan bounds**, not against the pose of the road it hangs off. The footprint is
x 12090…13170, z −2250…−410 — inside the world's existing extent in both axes, so it costs streaming
and not a wider height grid; the nearest carriageway is the Yalıköy leg 1.8 km north and the nearest
water another two kilometres past that.

**Five builders were wired to one circuit, and it took a second one to find out.** `BuildPaddock`,
`BuildLapTiming`, `BuildSectorGates`, `BuildStartingGrid` and `AddPaddockSamples` all read
`WeissjochringCourse` directly and all wrote mesh assets under fixed names, so a second call would
have built one circuit's furniture over the other's and left one of them with no paddock, no kerbs
and no timing — silently, with a correct triangle count logged each time. `CircuitBuild` carries the
name, the label the assets are stemmed from, the line distance, the infield hand and the apron. It is
the same trap `BuildMotorwayMerge` and `TrunkForkBuilder` have each already been through.

**A corner too short to hold control points is worse than no corner at all, and closing a lap
cleanly is what produces one.** With the heading right, `Close`'s Dubins solve came out as two arcs
of a fifth of a degree either side of a 300 m straight — and `RoadCourseBuilder.Turn` has a floor of
two steps, so a one-metre arc emitted two control points half a metre apart between ten-metre
neighbours. A Catmull-Rom through that is not a road: across the short span the parameterisation
stops resembling arc length, the tangent swings, and every reader of the curve believes there is a
corner there. `GetRadiusAtDistance` read **1.6 m**, on the start line, on the fastest part of the
lap — and `RoadShape`'s banking would have rolled the carriageway over on the strength of it. `Turn`
now carries the pose across an arc shorter than 0.4 of a point's spacing and emits nothing. The only
thing that ever reported this was `ReportCourse`'s "tightest radius"; `ValidateCircuitClosure` said
0.3 m and 0.2°, which is to say it said the closure was excellent.

**`LapTimer` resolved its `LapTiming` with `FindFirstObjectByType`, which two circuits turn into a
coin toss** — half the time the readout would sit blank on the circuit being driven while faithfully
timing the other one four hundred kilometres away. It finds them all once and reads whichever reports
`OnCircuit`.

**A closed road has no start, so it has no entry fade.** `LandRegion.Weight` ramps a region in over
the first 400 m of its road, which is right where two regions share one — and wrong on a lap, where
`along` runs back to zero at the start line. It was thinning both circuits' regions over the main
straight and the paddock, the one stretch of either that anybody looks at closely. `RoadProximity`
now carries `IsLoop` and the fade is skipped; the Weissjochring's paddock gets it back too.

**The valley is in flower, and that is a region rather than a colour.** `LandRegion.Bahce` is farmed
— orchard rows, walled boundaries, cut meadows — with a fresh spring green under it and pale warm
stone rather than either mountain's rock. `BlossomChance` mirrors `SpireChance` exactly and does two
jobs from one number: it picks the wild trees and it puts the orchard rows into blossom instead of
the Ebental's rust, because a region with pink woods and rust orchards would be two places. The
fourth parcel — the slot the Ebental ploughs — is petal drift, which is the honest way to get blossom
on the ground: `LandRegion.Parcel` already does the work, and it costs no triangle, no draw call and
no new mechanism. It is counted in the build log for the reason the snow line is.

**Whatever is not in flower here is a broadleaf, and never a spruce.** Left to fall through to the
world's own coin, about one tree in five of the orchard valley came out alpine conifer:
`ClimbFraction` is normalised against the mountain pass, so down at 40 m it is nought, `coniferBias`
with it, and the spruce probability sits at its floor of 0.45. A dark five-tiered conifer standing in
a cherry orchard is the one thing in the frame that says this is the same mountain as everywhere
else, which is exactly what a region exists to deny. It is the argument `AutumnCanopy` already makes
one region along — and **the log was correct throughout; only `_5_Blossom` and `_7_Infield` showed
it.**

**The cherry is a mesh of its own where the cypress was only a repaint.** Tone sorts a wood at
distance and shape is what survives when the fog has taken the tone — the argument the spruce and the
fir already make against each other. `AddCherry` is squat, twice as wide as its stem is tall, and
lumpy on top: two blobs, because one wide one came out a mushroom (the jitter is a fraction of the
radius, so a wider crown is a smoother outline, and a cherry is the opposite of smooth). The two
blossom tints are the only pale cool colours anywhere in this world, which is most of why the place
reads as somewhere else.

**The blossom branch goes *after* `SpireChance` and `AutumnCanopy`, not before.** Every draw from a
plant's random stream shifts every draw after it, so a new `random.Next()` in front of those two
would have moved every tree on the far shore of the Meerenge by a species — a change to a region
nobody touched, reported nowhere.

**No altitude bands, deliberately.** The lap runs between 30 and 60 m and the world's tree line is at
160, so every metre of this region is below it by construction. Setting one anyway is the trap
`VegetationBuilder.ClimbAt` exists to make visible. **The build log must still read "Tree line around
160 m" after any change here.**

**The pit road meets the track at 18°, where every other fork in this world uses 32.** At 32 the
branch arrives pointing *across* the carriageway and its last twenty-five metres of paving lie over
the racing line — which is how it was reported: a road that does not meet the track flush but runs
straight into the middle of it. `StadtfeldCourse`'s 32° is not a rule about forks, it is a rule about
**height**: two branches close together share one shelf whatever the plan says, so a shallow angle
keeps them near each other for longer and a disagreement about elevation becomes a ridge. Here they
agree by construction — the main straight is level at `PaddockElevation` and the access road arrives on
it — so the reason for the wider angle is absent and the cost of it is not. It cannot go much below 18
either: `TrunkForkBuilder.ThroatLength` is 70 m and a branch at eighteen degrees crosses a 19 m
carriageway over 61 m of its own length.

**And the mouth was sized from the wrong road.** `TrunkForkBuilder` computed its widest half-width as
`branchShape.OuterHalfWidth + MouthOverlap` — the branch alone, with no reference to what it opens
onto. On this world's first two forks that is the same number written twice, so nothing showed; a
circuit is 6.5 m of asphalt inside a 9.5 m half-width, and the same expression still returned 6.8, a
bell **narrower at its widest than the road it opens onto**. The junction pinched shut exactly where it
should have been at its most open. `AppendFillets` had always taken its reach from the trunk's own
edge; this is the same fact and it belonged there too.

**The build had been reporting that number from its own second copy of the formula**, printing
`branchShape.OuterHalfWidth` and calling it "at the mouth". It was right by coincidence for as long as
both forks joined two roads of one class, and it went on being right-looking for the first build after
the fix — the one line anybody would have read to check. `TrunkForkBuilder.MouthHalfWidth` is the
number now, and the log asks for it.

**The one pump has no advance sign, and that is accepted rather than fixed.** `ValidateFuelStations`
wants 250–600 m of road behind a station clear of bores, spans and bends under 90 m; on a lap that
wraps into the closing corners there is no such stretch, and there does not need to be. Nobody is
looking for this forecourt from a distance — it is thirty metres past the pit mouth, on the way in.

**No traffic**, like the Weissjochring and the Stadtfeld, and said in the comment beside it rather
than hidden. That is now three roads without it, which is the argument for doing the branching-traffic
job as its own change.

`Tools > Horizon > Render Bahçe Ring Preview` photographs the lap day and night. **`_2_Line`,
`_7_Infield` and `_5_Blossom` are the three that carry it** — the closure seam at the fastest point
on the lap, the ground square across the middle of the loop, and the only frame anywhere that says
whether a valley meant to be in flower reads as one. `_3_Fork` is aimed at the throat rather than
along the road, which is the fault the Weissjochring's own `_3_Fork` still has.

## The woods

**A wood in one colour is a texture, not a wood.** Every conifer in the world shared one green, every
broadleaf another and every bush a third — so a hillside of four hundred trees was four hundred copies
of the same three tones. The trees already varied in height by nearly a factor of two and it made no
difference: what the eye sorts a forest by at distance is **tone before shape**, and there was one tone
per species.

There are now three greens for conifers, three for broadleaves and two for undergrowth, each drawn from
the plant's own seed. **It costs nothing.** A submesh with an entry in `PlantMeshes.FoliageTints` is
merged into the same draw call as the rest — `PlantMaterials` hands everything but `RockSubmesh` the
same tinted material — so the rebuild came back with the identical triangle count, the identical tile
count and the identical draw calls, and a different-looking world. The one rule is the one already
written down: never add a slot with a null tint.

**The tones are further apart than looks sensible written down.** On a flat-shaded low-poly tree under
one directional light the canopy is a handful of facets, so a subtle difference between two greens is
no difference at all by forty metres — which is where nearly every tree here is seen from.

**Two conifer silhouettes, because shape is what survives when tone has gone grey in the fog.** The
spruce is tall, narrow and five-tiered against the fir's broad three; a stand of both reads as a mixed
wood where one read as wallpaper. Same triangle order, chosen by seed.

**The autumn canopy and the orchard keep their single palettes on purpose.** Those two *are* a
signature — the Ebental reads as one country precisely because its gold does not vary — so
`AddBroadleaf`'s explicit-slot overload is left alone and only the wild scatter picks a tone.

**Two density changes, and they are the only part of this that costs anything.** `TreeClearance` 14 m →
11: fourteen left four metres of shoulder and then seven of nothing before the first trunk, which reads
as a mown verge on both sides of every road in the world — the wood stood back from the road instead of
the road being cut through the wood. And `ClumpThreshold` 0.42 → 0.34, which is the fraction of hillside
that is clearing rather than wood; at 0.42 the stands were real and everything between them was bare.

That pair is +20 % on the world total and **+3 % on the heaviest tile, which is the same tile it has
always been**. Growing the world costs streaming; growing a tile costs frame time, and only the second
is a budget. `ClumpThreshold` is the knob to pull back on if `MaxTrianglesPerTile` ever starts naming a
tile other than Terrain_8_6.

**A region may now ask for a forest rather than a scatter, and the Weissjoch does.** Three things were
keeping a nine-hundred-metre mountain bald and only the first was obvious:

- Its tree line was at 460 m of a 906 m summit, so three quarters of the climb had nothing growing on
  it by construction. It is 700 m now, and the **snow line at 600 sits below it on purpose** — the band
  where dark spruce stands on white ground is the whole picture a winter region is for.
- **`TreeMaxSlopeDegrees` was rejecting every tree on every face.** The world's 30° was chosen against a
  pass whose face is 63 % — but that is the *mean*, and `MountainField` blends between stacked legs with
  an inverse-fifth power, so the middle of a face is far steeper than its average. On a twenty-eight
  hairpin stack that kept the trees on the flat shelves and nowhere else, which is a hillside with
  stripes on it. The region carries 44°.
- Density and clearing share are global, so the mountain was as open as the farmland. `TreeDensity` 2.4
  and a `ClumpThreshold` of 0.10 are the region's own.

`TreeDensity` divides `TreeCellSize` by its square root, and it has to be applied **to the candidate
grid** — everything after that only ever removes candidates, so no amount of relaxing filters can plant
more trees than the grid offered.

**The undergrowth follows the region's clearings but deliberately not its density.** Multiplying the
shrub count too was the obvious next line: it cost 0.44 M triangles and five thousand off the heaviest
tile to say nothing, because a two-metre bush under a twelve-metre canopy is not visible. Density there
buys nothing and is paid for per tile.

**And `FarDensity` cannot help on a mountain like this.** It thins what stands more than 100 m from a
carriageway; a switchback stack puts its legs 52–72 m apart, so there is no ground on that mountain far
from a road and nothing for the falloff to thin. A stacked climb is all near field — which is exactly
why its tiles are the heaviest in the world, and why `LandRegion.TreeDensity` is the knob rather than
`FarDensity`.

## How wide a road is

Every carriageway in the world is a quarter wider than it was authored at, and the reason is not that
the roads were wrong. **The cars grew a quarter in plan** — the widest collider went from 2.26 m to
2.92 m, and the offroader is 3.00 m across its tyres — so the roads followed, because what a driver
reads is not the width of the road but **how much of it the car fills**. Hold the roads still and the
world quietly becomes a tighter game than it was. `RoadShape.Default`, `Autobahn` and `Circuit` all took
the same factor, shoulders and hard shoulder included; `TownStreetShape` took it on the carriageways and
not on the footways, because a pavement is sized for people.

**The numbers were written out, not scaled at a gate, and that is the opposite of what the cars got.**
`CarMeshBuilder.PlanScale` exists because the ten station tables are *measurements* of real cars and
multiplying them in place would destroy the only thing that makes them checkable. A road width is not a
measurement of anything — "two 5.25 m lanes, wider than a real pass, deliberately" is a decision — so a
permanent scale factor here would leave every comment in the file claiming a width the builder does not
produce, which is the stale-doc failure this file has already paid for four times.

**`RoadShape`'s own comment says widening is "close to free because everything that needs the number
takes it from here". That is true of about forty call sites and false of a dozen, and two of the dozen
break in silence.**

- **`TerrainShape.RoadShelfDrop`.** The camber lowers the inner edge of a carriageway by
  `HalfWidth × sin(bank)` and the terrain shelf has to sit below *that*. At the old 0.45 the widened
  motorway wanted 0.49 and the widened pass 0.46 — so the hillside comes up through the asphalt on the
  inside of every corner in the world, on a build that says nothing. It is 0.57 now. **Widen a road
  again and this moves with it.** `ValidateRoadClearance` is the only thing that reports it.
- **`AutobahnCourse.CarriagewayOffset`.** It was the literal `10.5`, which is a 7.5 m half-width plus a
  3 m median written down as a number with no reference to the shape it came from. At a 9.4 m
  half-width the two carriageways' asphalt *overlaps*, and nothing in the build asks whether a
  motorway's two halves are on top of each other because every check there walks one road at a time. It
  is derived, and `MergeOffset` — four widths in a trench coat — with it.

The rest are loud, or merely wrong-looking: `TrafficNetworkBuilder.HighwayLaneWidth` (four lanes bunched
down the middle of a carriageway, reported as correct by every check that exists),
`VegetationShape.TuftClearance` (grass in the shoulder — its own comment already stated the rule, which
is why it is read rather than restated now), `TunnelBuilder.MoundHalfWidthFor` (forty metres of massif
is sized against how far apart a switchback's legs are, and says nothing about the motorway's
fifty-metre bore), `TrunkForkBuilder.ThroatLength` (`RibbonTrim` is three widths over a sine),
`CircuitMeshes.GridBoxLength` (a painted box shorter than the car standing in it), and the two spawn
places that were typed as `4f` where `4` was half of a boulevard half-width.

**And one check had become narrower than its own subject.** `ValidateDriveableCorridor` sweeps a box
along every road asking whether anything solid stands in it, and that box *is the car* — 1.3 m of
half-width, chosen when the widest car was 2.26 m across. A check that cannot reach its subject finds
nothing wrong and is indistinguishable from a clean pass, which is the argument `ValidateSurfaces`
already makes about its own rays missing. It is `DriverBoxHalfWidth` now, and it is 1.5.

**What it cost, measured against a build of the same world at the old widths.** Everything else in
the log is unchanged, and these four are the whole of the difference:

- **Talheim's street graph stopped being planar**, and that is the failure this widening was always
  going to have somewhere. The clearance the check wants is the two streets' paved half-widths added
  together, so it grows with them: the avenue leaving the housing row and the market square's north
  edge went from 14.9 m of paving between their centrelines to 17.2, against 15.7 m of ground. The
  housing row already bulges around the square for exactly this reason, one widening ago; it bulges
  8 m further now. **The warning named the pair and no distance, which would have sent anybody reading
  the whole layout table** — it prints the measured separation and the clearance it needed now, and
  that turned it into one line of the table.
- **The interchange's seam was measured against a tyre nobody drives.** `ValidateMergeSeam`'s
  tolerance is "a tenth of the tyre's radius", and it was the literal `0.04` — a tenth of the 0.40 the
  hatchback's wheel was before `5bd7396` grew every wheel by 15 %. A rule about the car, spelt as a
  number, in a file the car does not pass through. The step itself went from 38 mm to 43 mm because
  the ramp stands further out; the frame there is banked 0.05°, so the bank explains none of it. It is
  read off the smallest wheel in the garage now.
- **Two twenty-metre stretches of the pass have no ground under the outside of their verge** — 1750
  and 2470 m along, both hairpin exteriors in the switchback stack, worst 3.71 m. That ground was
  always like that; the shoulder now reaches 1.75 m closer to it, and `MountainField` averages
  stacked legs, so a shelf just outside a 20 m hairpin is being pulled towards the leg forty metres
  below. A guard rail already stands at both, because the drop is past `GuardRailBuilder`'s 3 m. It is
  recorded rather than tuned away: the alternative is a check that agrees with whatever it is shown.
  `ValidateRoadSupport` reports stretches rather than a bare count now, because one number cannot tell
  a hole beside one hairpin from half a kilometre of road in the air.
- **Ten streets have their junction trims scaled back rather than five**, worst 0.79. Four of them are
  the market quarter's high street, where both ends sit at `StreetJunctionBuilder.MaximumTrimFactor`'s
  cap — 2.5 × a half-outer that grew 15 % against segments that did not. The scaling is the designed
  degradation and every network still validates as planar, convex and flush, so it is accepted; the
  fix, if it ever matters, is to lengthen the market quarter rather than to narrow the street.

**What did not scale, and why.** Guard-rail and parapet *heights*, kerb rise, tunnel headroom: those are
answerable to the car behind them rather than to the width of the road, and the cars grew 15 % in height
against 25 % in plan. Dash and gap lengths: a dash is read against the speed it goes past at, and no
speed changed — only the line *widths* moved, because a line is read against the road under it. Corner
radii, grades and course lengths: nothing about a centreline moved, so the world footprint, the terrain
tile list and the per-tile triangle counts are exactly what they were.

## Where roads meet

Fourteen courses, four towns, two circuits and one motorway, and until now **nothing in this project
measured or photographed a junction.** `ValidateMergeSeam` asked the question of the single motorway
on-ramp; eighteen other checks walk *along* a road or measure the ground under it, and a seam is the
gap between two of those. Every fault below built without a word, validated cleanly, and was reported
from the car.

**A branch's course ends on the centreline of the road it joins, and its ribbon must not.** `ConnectTo`
is aimed at the trunk's own published pose, which is right — that is what makes a fork one place rather
than two. But a cross-section is square to the branch, so a ribbon walked all the way there lays its
last one *across* the carriageway: 5.25 m of asphalt and 1.5 m of gravel shoulder, most of it past the
centre line, ending in a square cap with a 0.5 m drop off its edge. On the Weissjochring the throat then
reached **5.2 m past the centreline of a 6.5 m half-width** — nearly the whole of the far side of the
racing surface — on the fastest part of the lap. `RoadMeshBuilder.BuildRoad` now takes a trim and
`TrunkForkBuilder.RibbonTrim` says where — twenty-odd metres on the Stadtfeld and the Weissjochring
and roughly twice that on the Bahçe Ring, the difference being the fork angle. It is computed from the
two shapes rather than tabulated, and it is what `ThroatLength` has to stay ahead of: the trims grew
with the carriageways when the roads were widened for the cars, and the throat had to grow with them.

**The throat is clipped to the trunk's paved edge, and that line is not a taste.** `AppendRing` puts the
camber at exactly zero at the asphalt-to-shoulder edge, so a surface cut off there is flush with the
carriageway to the millimetre and offset from it only by the two centimetres of `MotorwayMergeBuilder.Lift`
every laid-on thing here carries. Anywhere else it would be a step. This also **removes** the throat's
original reason to exist — it was there to cover two ribbons fighting for the depth buffer, and with the
branch trimmed there is nothing left to cover — so what is left is a bell mouth that opens off the edge
of a road that keeps its own markings, its own camber and, on a circuit, its own kerbs. Laying a flat
plane over a cambered carriageway is the trap already recorded against the start line and the grid boxes,
and the fork had been walking into it.

**Where the throat touches the trunk it has to be at the trunk's height, and that is not the same as
where it was clipped.** The clip line runs *along* the carriageway for forty metres or so of branch,
and over that run the throat's own section is blending off the trunk's plane onto the branch's grade —
so carrying the row's height across the cut walked the seam open at the difference of the two grades:
19 cm on the Stadtfeld fork, 15 on the Weissjochring, **62 on the Bahçe Ring**, against a tolerance of
four. The cut end takes its height from the trunk and the blend stays on the far edge, which makes the
quad between them the ruled apron `AppendFillets` already builds one road class further out. The
overhang was zero on the first build and this was still wrong, which is the point: **two things had to
be right and only one of them was measurable by looking at the plan.**

**`AddJunction` has to be on both courses, and it was on one.** Guard rails, delineator posts and kerbs
all read `RoadCourse.IsJunction` off the course they are building, so a mark on the trunk protects the
trunk. `GuardRailBuilder`'s own comment describes the failure exactly — *"a rail there stands across the
road the branch exists to reach"* — and the branch is the road whose end is **at** the junction, which is
precisely where the drop test beside a mouth fires every time. Three roads, both pit lanes among them.
`AddJunction` also takes a `reach` now, because the motorway's two termini are junctions two hundred
metres long rather than points.

**The motorway ended in a wall, at both ends.** `AutobahnCourse` hands over to Hochstadt's boulevard in
the east and the coast road in the west, and both of them begin on the **median line** — that is the axis
the whole road is measured against. The carriageways are `OffsetRoadPath`s at ±`CarriagewayOffset`, so
each one finished a dozen metres to the side of the road it was handing to, with six metres of unpaved
median between them and `GuardRailBuilder.BuildMedian`, whose `present[step]` was the literal `true` with
a comment saying that not even a bore breaks the run, standing down the middle of it. **The last post
stood on the city gate.** There was no way out of Hochstadt and no way onto the coast road.

`MotorwayTerminusBuilder` is the two carriageways coming together over 200 m, and it **replaces** them
rather than covering them: the ribbons are trimmed and the terminus carries a cross-section that is
theirs at one end and the onward road's at the other — two crowns becoming one, `offset + HalfWidth`
becoming the onward half-width, the shoulder and its drop going with them. Laid over instead, the two
would disagree by a whole `Crown` at each carriageway's centreline, which is 12 cm against a 4 cm
tolerance. The east end reads its narrow width off `TownStreetShape.For(Boulevard)` rather than typing
16 m, for the reason this file gives about every second copy of a number.

**`ValidateRoadSupport` is the missing sign of `ValidateRoadClearance`.** That check measures terrain
standing *above* the asphalt and nothing measured the other way — which is a gap rather than an
oversight, because `MountainField` averages: wherever two roads at different heights come within reach
of one another the lower gets ground on its carriageway, *which is reported*, and the higher loses the
ground under it, which was not. Every breach that check has ever printed had a silent twin. It sampled
at the shoulder's outer edge, and on the first run it found the thing the player had reported and
nobody could locate.

**The Meerenge corniche was standing on a ledge for most of two kilometres.** `MountainField.UnderWater`
eases the ground up from a waterline over `BankEase`, and the Boğaz's bank is wider than the corniche's
distance from it — so the ease was dragging the ground down towards the water *underneath the
carriageway itself*, 54 m of it at the worst point. The road had no verge at all: its shoulder edge was
the top of a cliff. Nothing had ever asked, because `ValidateRoadClearance` only ever asked whether the
ground was too high. `MountainField.BankFloor` keeps a carriageway's own shelf out to its verge and lets
the bank take over past that, so there is still a fall to the water — it starts where a verge ends
rather than where the asphalt does. It is deliberately **carriageways only**: `DistanceToRoad` excludes
the level samples a town or a paddock lays, and a harbour basin and a bay are dug to meet their town on
purpose.

The support check skips bridged and covered stretches with `MountainField.BridgeCorridor` of margin,
which is the distance a deck's carve eases back over. Without it, it reported 128 points on each
motorway carriageway and 68 on the Kalkgrat — every one an abutment doing exactly what it is drawn to
do. `linkPath` and `coastPath` are on both lists now; they were on neither.

**`ValidateTownEntry` asks the question `CountUnreachable` is handed the answer to.** That walk is seeded
with the town's gateway node as a given — its own remarks say so — so it reported Hochstadt as reachable
because it assumed it, through a solid barrier, across ten and a half metres of grass, for as long as the
city has existed. This measures how far the nearest street node is from the *paving* of the road that
actually arrives, how far apart they are in height, and whether anything solid stands on the line between
them. For Hochstadt that road is the eastbound carriageway and not the arterial, which is a coordinate
axis with no asphalt on it; for Seeburg it is the coast road and not the town's own axis.

`Tools > Horizon > Render Junction Preview` photographs all thirteen joins day and night — the three
forks, the two motorway termini, the on-ramp, the six places one course hands to the next, and
Seeburg's gate. A fork gets four frames: down the branch at the mouth, the road it joins from both
directions, and straight down from a hundred and sixty metres. **The plan frame is the one that carries
it.** Asphalt laid across a carriageway, a ridge standing between two branches and a barrier across a
mouth are all things you look straight past at eye level and cannot miss from above — which is the same
lesson the `_ForkPlan` shot on the Stadtfeld already stands for, generalised to every junction in the
world.

**The eye-level frames stand at forty-five metres, and the first version stood at ninety.** At ninety a
mouth three metres wider than the road it opens off is a few pixels of dark asphalt against dark
asphalt, and every fork frame came back as a photograph of an ordinary road — which is exactly the
fault already recorded against the two `_3_Fork` shots these replace. A frame that cannot resolve its
subject is worse than no frame, because it looks like an answer.

## What the car is touching

Two things the world has never told the driver: that they hit something, and what they are standing
on. There was **no `OnCollisionEnter` anywhere in this project**. Guard rails, the median, viaduct
parapets and the crossing's have been solid since `f9c6c86`, ambient traffic carries a `BoxCollider`,
and hitting any of it at any speed produced no sound, no shake and no consequence whatever. Grip was
one scalar with one writer, so the verge, a ploughed field and the carriageway were the same road.

**A surface is asked of the wheel's own raycast, never of the road network.** `RoadRespawn.TryNearest`
is the search that answers "where is the nearest road", and its own remarks say why it runs on a
button press and not per frame: a lane can be a kilometre long and there are three hundred of them.
Meanwhile `UpdateWheel` already casts four rays a physics step and already knows exactly what it hit.
`GroundSurface` makes that answer readable, and the whole mechanism costs a reference comparison per
wheel — the collider is cached and only re-resolved when it changes.

**`GroundSurface` lives in `Horizon.Core`, and that is the module layout rather than a preference.**
`Horizon.Vehicle` is the reader and may not reference `Horizon.World`. Core is the assembly with no
dependencies, so it is the only place a type both of them can see is allowed to be.

**Untagged geometry drives like asphalt, and that is the safe direction to be wrong in.** Something
nobody remembered to tag then behaves as it always did, which is invisible; the other way round, one
forgotten call puts the car on grass in the middle of a carriageway and reads as the handling model
having broken. So the build counts what it tagged and **warns when either kind comes out empty** — a
world with no surfaces in it builds, validates and drives exactly like one that works, which is the
argument the snow line already makes.

**Three kinds, because three is what the world can actually tell apart.** A terrain tile carries
grass, rock, sand and snow as *vertex tints on one material in one mesh*, so no amount of asking the
collider separates a snowfield from a meadow — they are the same triangles. A carriageway genuinely
does carry its shoulders as a submesh of their own (`RoadMeshBuilder.ShoulderSubmesh`), which is why
that one distinction exists and the others do not. Adding `Gravel` or `Ice` here before giving them
geometry of their own would be a kind nothing could ever return.

**The runs are measured off the collision mesh, never the rendered one.** `RaycastHit.triangleIndex`
counts triangles across the whole mesh in submesh order, so a submesh is a contiguous run and the
boundaries are prefix sums — but the tunnels are deliberately built with what you can see and what
you can hit as different meshes, and taking the counts from the visible one puts the asphalt-to-gravel
boundary at a triangle number that means nothing in the mesh being asked.

**Two grip multipliers, and they mean different things.** `GripScale` is what the world has done to
the car and applies to all four wheels at once — `WaterHazard` is its one writer today and rain will
be its second. The surface multiplies *per wheel*, inside `UpdateWheel`. Folding them into one number
was the first version and it lost the thing worth having: dropping two wheels onto the verge pulls the
car towards it, where a single car-wide scalar just makes the whole car slippery. It also means the
two cannot fight — a car climbing out of a lake onto grass does not get full grip back because
`WaterHazard.Dry` set the number it owns to 1.

**Neither is a cliff.** A verge at half grip would be a wall the car bounces off, which is worse than
having no surfaces at all: the shoulder is under two metres wide and a driver clips it on nearly
every hairpin exit. 0.78 and 0.62 are tuned so that going wide is a moment the driver feels and
corrects, not a moment the car is taken away from them.

**Severity is the closing speed along the contact normal, not the speed of the car.** A car leaning
on the Meerenge parapet through a fast corner is doing 160 km/h and hitting nothing — almost all of
that velocity is *along* the wall. Taking the magnitude reports every graze as the hardest crash in
the game, which is the one reading that would make this feature worse than not having it. The wheels
are raycasts, so the body's collider does not touch the road in ordinary driving: a vertical contact
means the car has bottomed out or landed, and that thud is deliberately not filtered out.

**Placing the car is not a crash.** A car put down on a road is put down *inside* whatever it lands
touching, and PhysX reports the push-out as a contact — so every start, every `MoveTo`, every respawn
out of the water opened with a bang and a shaken camera. `Teleport` suppresses impacts for a third of
a second, which covers the settle and nothing else.

**A scrape is a state and an impact is an event, and they are delivered differently.** `Impacted` is
an event with a severity; `ScrapeSpeed` is a number. A car leaning on a barrier through a long corner
is one continuous noise, and delivering that as a stream of events would be a stream of bangs. It is
collected in `OnCollisionStay` and published once at the head of the next `FixedUpdate`, because that
callback runs after the step, may run several times, and may not run at all on a step where the
contact happened to lapse — read straight from it, the level drops to zero at 50 Hz and buzzes.

**The camera kick is pushed in, and it is a separate cue from the buffeting tremor.** `Horizon.Core`
has no references, so `ChaseCamera` cannot ask a car whether it has crashed; `Horizon.Vehicle` may not
know a camera exists. `ImpactEffects` in `Horizon.Game` is the join, which is `SpeedAtmosphere`'s
shape exactly. Folding the kick into `highSpeedShake` would have put a crash through
`shakeOnsetSpeedFraction` — so hitting a wall at 30 km/h, below the onset, would shake the camera by
nothing at all. **The impact offset rolls the horizon where the tremor deliberately does not**: that
comment says roll at speed reads as a crash rather than as velocity, which is exactly the reading
wanted when there has been one. The strongest hit wins rather than accumulating, or a long scrape
rings the camera harder the longer it goes on.

**`ContactAudio` is one component with three layers, and it is not in `EngineAudio`.** That class
re-synthesises its clips whenever the player changes car, because a diesel and a turbocharged six are
different notes. None of these three depend on the car at all — a wing hitting a barrier sounds like a
wing hitting a barrier — so putting them there would mean rebuilding three clips that cannot change
every time somebody opened the garage. They are one component rather than three because they read one
car and are shaped by one speed, and the thud and the scrape are two readings of a single contact.

**The scrape and the rumble are separated by register, not by level.** They are the pair most at risk
of collapsing into one noise — both are filtered white — and they occur together, so level could never
have told them apart. Metal on steel sits above a kilohertz through a resonator; tyres on loose ground
sit under two hundred hertz through a two-pole lowpass. A resonator on the rumble gives a hum, which
reads as engine and fights the one already playing.

**The thud is two resonators and pitches *down* with severity.** Two, because a body hitting a barrier
is a shell booming around 80 Hz and a panel ringing five times higher; one gives either a boom with no
edge or a clank with no weight. The high partial decays several times faster, and that difference is
the impression of mass. Down rather than up because a light tap is a panel and a heavy one is the
whole structure — pitched up with severity, a big crash sounds like a small one played loudly. The
loop rule applies to the other two and not to this one: it is a one-shot, so there is no loop point to
click. The scrape and the rumble get their tails crossfaded into their heads, as the squeal does.

**The rumble is roughness times speed, and it has to be the product.** Roughness alone rumbles at a
standstill on grass, which is a car that has broken; speed alone rumbles on the motorway.

**Gravel and grass were one clip played at two volumes, and that was the plainest fault this had.**
Two surfaces separated only by level is one surface at two distances — which is exactly the mistake
recorded a paragraph above about the scrape and the rumble, and again about the wind and the water,
and it was sitting inside this feature the whole time. There are two loops now, crossfaded by
`VehicleController.SurfaceGrit` on **one** level and **one** pitch, the way the engine's two voices
already are: two levels would be two sounds that happened to be playing, and they would disagree
every time the car had wheels on both, which is every verge exit. Loose stone is a scatter of
individual strikes through a band at 900 Hz — the rain's two-layer construction one register down,
because what the ear picks gravel out by is that it can very nearly count the stones. Soft ground
keeps the filtered boom, because earth and grass absorb the top and chippings do not.

**And the two volumes were the wrong way round.** `RoughnessOf` read 0.72 for the shoulder against 1
for open ground, on the reasoning that open ground is the rougher ride — which it is. But that number
is read by exactly one thing and that thing is the level of a sound, and a gravel verge is far the
louder of the two. Written as a ride-quality figure and spent as a volume.

**`SurfaceGrit` is weighted by roughness rather than by wheel count.** It is spent crossfading two
clips whose shared level is `SurfaceRoughness`, so the share that matters is the share of the *noise*
— by wheel count, a wheel still on the tarmac and contributing nothing to either clip would get a
vote on what the other three sound like. It is also held rather than reset when the car is back on
tarmac: at zero level the blend is inaudible, so moving it would be a decision nobody can hear, right
up until the car reaches the next verge and the blend snaps under a level that is already rising.

**This is the one feature here that a picture cannot check, and it gets a measurement instead.** Every
other system in this project is verified by photographing it, because what goes wrong is visible and
silent. A surface is the opposite — invisible and silent. A carriageway whose gravel run starts down
the middle of the road looks identical to a correct one in every frame this project can take, day or
night, and the only symptom is a car that is mysteriously slippery on the crown. So `ValidateSurfaces`
**casts rays at the finished scene** and asks what the wheel will be told, on all twelve carriageways.
It asks the scene rather than the data on purpose — three things have to line up before a wheel gets
the right answer, the submesh order, the triangle counts and the collider being the mesh that was
measured, and only a real raycast tests all three at once. It also warns when **its own rays miss**,
because a check that cannot reach its subject finds nothing wrong and is indistinguishable from a
clean pass. Off the road is deliberately not probed: at 25 m from a carriageway the honest answer is
sometimes a bridge deck, a forecourt or the next road over, and a check that called any of those a
fault is a check nobody reads.

**The crown is an error and the verge is a measurement, and that asymmetry is what the check found.**
The first version failed both the same way and reported all eleven roads as broken — 2 crown samples
out of 2268 and several hundred verge samples. The crown figure is a fault. The verge figure is the
world: **`ShoulderDrop` is 0.63 m against a `TerrainShape.RoadShelfDrop` of 0.57**, so the gravel
already hangs below the shelf on level ground, and the camber on the inside of a corner takes it a
further `sin(bank)` down — the hillside stands over the outer half of the verge there, and a wheel
running wide genuinely touches terrain rather than gravel. Reporting the terrain it touches is
correct. `RoadShape.ShoulderDrop`'s own comment states this arithmetic for the **asphalt edge** and
stops there; nothing had ever measured what becomes of the gravel behind it, because
`ValidateRoadClearance` asks about the carriageway and `ValidateRoadSupport` asks whether the ground
is too *low*. So the verge is one counted line in the build log, and only a **majority** failing is an
error — a majority is the shape of a submesh boundary in the wrong place, where a scattering is the
shape of banked corners.

**And the crown warning prints the world position, not only the distance along.** That is
`ValidateRoadClearance`'s own lesson: a distance says where to look on this road, and a position is
what lets a cause on another road be found. It is also what separated the two possible causes the
first time it fired — the motorway reported `Ground at 37 m along`, on both carriageways, which is
the **western terminus**: `MotorwayTerminusBuilder` replaces 200 m of both ribbons with one converging
surface, so a probe on a carriageway's own centreline drops through where the road used to be. The
check now skips junction spans, using `RoadCourse.IsJunction` — **the same predicate the guard rails
and the kerbs read**, rather than a rule of its own about where a ribbon ends. `BuildBranchRoad` trims
a branch back by twenty to forty metres at a fork for the same reason, and `AddJunction` is on both
courses, so one predicate covers both cases. Distances line up on a carriageway because
`OffsetRoadPath.Length` is deliberately the centreline's, not its own.

`DriveDebugOverlay` prints the surface grip, the roughness and the scrape speed, because a surface
moves no pixel and makes only a noise — driving one wheel onto the verge should take that number off
1.00, and nothing else in the game would say whether it did.

## Rain

`PlayerChoices.WeatherPreset` used to carry a comment saying there was no rain in this project — no
particles, no wet road, no audio, nothing the car knew about — and that **a "Rain" button which only
made the fog heavier would be the menu lying about the world, and the fix for that is a rain system
rather than a different word.** So the button arrived with the four things that make it true: water
falling past the camera, a noise that stops under a bridge, a darker sky, and tyres that let go
earlier.

**Appended to the enum, never inserted, and the clamp moves in two places.** The preset is written to
PlayerPrefs as a bare integer, so a value added in the middle silently changes what every returning
player chose. `PlayerChoices.Load` clamps and so does `PauseMenu.SetWeather` — the second one is the
easy one to miss, and missing it leaves the new button dead rather than broken.

**One owner, four consumers.** `WeatherDirector` reads the preset and pushes it to the drops, the
road, the tyres and the sound. Four separate reads of `PlayerChoices.Weather`, each with a ramp of its
own, would be four things able to disagree about whether it is raining — and what would show is a road
drying while the sound is still falling. It is the boost gauge's argument again: the needle and the
whistle are the same number.

**The sky is the one thing it does not push, and that is deliberate.** `StartScreen.ApplyConditions`
and `PauseMenu.SetWeather` have written `TimeOfDayController.Overcast` since long before there was
rain, and they have to — both call `Apply()` immediately so the player watches the light change behind
the open menu. A second writer ramping the same field would fight them, and what shows is the sky
snapping to the new weather and then sliding back off it. The rain's own ramp is short (under a
second) for the same reason: the light it arrives under changes in one frame, so a long fade would
have water still building after the scene had finished darkening.

**Rain's `Overcast` is 0.80 against the Overcast preset's 0.90, which looks backwards written down.**
The rain itself takes light out of the frame on top of the sky, and a rain preset that also asked for
the heaviest cloud came back as a grey wall with nothing readable in it. The sky is the setting; the
rain is the weather.

**Three grip factors now, each with exactly one owner.** `GripScale` is what the world has done to the
car (`WaterHazard`), `WeatherGrip` is what the sky is doing (`WeatherDirector`), and `SurfaceGrip` is
what one wheel is standing on — applied per wheel rather than car-wide. That is not new machinery, it
is the rule already written on `GripScale`: *whatever sets it owns putting it back*. Two owners cannot
both honour it, and the failure is concrete — `WaterHazard.Dry` writes 1 when the car leaves the
water, so a shared number would hand full grip back to a car climbing out of a lake in a downpour.

**0.82 in the wet, and it is gentler than the real number.** Wet asphalt takes more than that, but the
car is steered with a thumb on a phone: past about a fifth the pass stops being a road and becomes a
punishment, and the driver has no seat to feel the back stepping out from. What this is tuned for is a
corner taken at the dry speed running a little wide — something the player can learn.

**It does not rain inside the mountain, and that had to be reasoned about rather than seen.** The
emitter box hangs fourteen metres over the camera, which inside a bore is fourteen metres of solid
rock — so drops were being born in the massif and falling through it into the tunnel. The build would
never have mentioned it and neither would any existing frame. The drop rate is scaled by
`1 − VehicleCover.CoverAmount`, which is the probe that already answers this question for the sound
and for the engine's reverb rather than a second test with its own opinion; being eased, the rain
fades back in across a portal instead of switching.

**The rain stops under a bridge, and that detail is free.** `VehicleCover` is the single upward ray
that already fades the engine's reverb in a tunnel, and it is on the car, which is why `RainAudio` is
too. Not to silence, though — a tunnel silences the sky and not the tyres, and cutting the whole layer
at the portal reads as the sound breaking rather than as shelter. It goes quieter *and duller*: what a
roof takes away first is the top of the spectrum, and level alone reads as the rain having moved into
the distance.

**No speed term on the rain, deliberately.** The obvious next line is to make it louder as the car
goes faster, and the note against `EngineAudio`'s deleted wind layer is exactly why not: a broadband
noise that rises with the throttle sits over the engine on every acceleration. Rain sounds like rain
whether the car is moving or not.

**The rain clip is two layers, because rain is not a hiss.** The bed is the many-drops-at-a-distance
wash, which on its own is indistinguishable from static or from wind; what makes it read as water is a
sparse foreground of individual drops close enough to have an attack, laid down at about sixty a
second through a high resonator. Drop the second layer and it is a noise generator; drop the first and
it is a leaking tap. It sits above two kilohertz and `ContactAudio`'s rumble sits below two hundred
hertz, for the reason recorded there: the two play at once, so level could never separate them.

**The road is swapped, never repainted.** Darkening the asphalt by writing `_BaseColor` on the shared
material is one line — and Unity does not roll asset edits back when Play mode ends, so a player who
tried the rain once would leave `M_RoadSurface.mat` modified in the working tree. That is the trap
`QualityDirector` and `TownLights` both document. A `MaterialPropertyBlock` is the other obvious
answer and it breaks the SRP batcher across every carriageway in the world. So there are two finished
assets and the renderer is pointed at one or the other, exactly as `TownLights` does at dusk.

**Wet is dark first and shiny second, and the first version had that backwards.** Smoothness 0.80
turned every carriageway in the world into a mirror of the sky: the lane markings vanished under it
completely, the motorway came back looking like a canal, and — the frame that settled it — **the bore
of the Kehrtunnel had a blue river running through it**. There are no reflection probes in this world
by budget, so URP's environment reflection is the skybox itself, and past about half smoothness the
asphalt stops being asphalt and becomes whatever the dome above it is. 0.46 against a dry 0.34 is a
garnish on top of the darkening, which is what actually says "wet". The verge barely moves at all —
gravel holds water in it rather than on it, and a shoulder shining like the carriageway reads as a
second lane.

**And nothing in this project had ever changed the sky.** `TimeOfDayController` writes the sun, the
ambient and the fog; the skybox is a material on `RenderSettings` that only ever read the sun's
*direction*. So `Overcast` had been dimming the light and thickening the air under an unchanged blue
dome since the day it was written — for Hazy and for Overcast as much as for rain, and nobody had
noticed until there was a photograph of a downpour falling out of a clear blue afternoon.
`M_SkyOvercast` is a second procedural sky, grey and thick and dim, swapped in above an `Overcast` of
0.6 with hysteresis for the reason `TownLights` gives about dusk. **It is also half of the wet-road
fix**, and that connection is worth keeping in mind: with no probes, the sky is what every smooth
surface reflects, so greying it greys the reflection in the same stroke.

**`WetVariant` writes the tint *after* the asset is created and only when it creates it.**
`LoadOrCreateMaterial` forces `_BaseColor` to white whenever a base map is given, and for a dry road
that is right — the tint multiplies the marking atlas and anything but white darkens the paint. Here
darkening is the point. Only on creation, because that helper deliberately returns an existing asset
untouched so hand-retints survive a rebuild.

**The registry is found by material identity, not by threading a flag through a dozen builders.** The
ribbons, the town streets, the forecourt aprons, the fork throats, the motorway merges and termini and
the bridge decks are all painted by different code, and a "you are a road" argument on each is a dozen
places to forget one — with no symptom but a stretch of tarmac that stays dry. `BuildWetSurfaces`
sweeps the finished world once and records every renderer slot holding a known dry road material. That
is not a checker forming an opinion of its own: the test is the exact asset the builder assigned.

**Town streets cannot be wetted, and that is recorded rather than hidden.** They are painted
`M_TerrainTint` — the one vertex-tinted material that also carries grass, rock, sand and snow — so
wetting them would wet every hillside in the world. Giving the streets a material of their own is the
honest fix, and it is a change to make on purpose rather than in passing, which is the same call the
Weissjochring's missing snow got. Until then a shower darkens the carriageways and leaves the towns
dry.

**The drops hang off the camera and are simulated in world space.** The grit in `SpeedAtmosphere` is
placed ahead of the *car* because the whole point of it is the car passing it; rain only has to be
everywhere the frame is, so the emitter box travels with the rig while the drops, once emitted, belong
to the world and fall straight down. Simulated in local space they would lean into every corner, which
reads as rain stuck to the windscreen.

**The streak comes from velocity, not from a fixed length**, because a drop is a streak *because it is
moving* — and a fixed length makes standing rain look like falling rain in a still frame, which is
exactly the frame every preview tool here takes.

**Quality thins the rain rather than switching it off**, which is the opposite of what it does to the
grit, the tailpipe smoke and the tyre smoke. Those three are decoration. Rain is a state the world is
in — the road is slippery and there is a noise on the roof — so a phone that showed none of it while
both were true would be telling the player something false. Low draws a third of the drops; the sky,
the sound and the grip never scale.

**`WeatherDirector` held a reference to the atmosphere for a while — wired, asserted, never read.**
Once the sky went back to the menus there was nothing left for it to do, and a `[SerializeField]` with
an `AssertReferenceAssigned` on it looks exactly like a dependency while being a decoration. It is
gone. The same pass moved the sweep's log line onto `WetSurfaces.GroupCount` instead of counting the
builder's own local list, which is `TrunkForkBuilder.MouthHalfWidth`'s lesson: the build reported a
fork's width from its own second copy of the formula and went on looking right after the formula had
been fixed.

`Tools > Horizon > Render Weather Preview` photographs three places dry, in the rain, and in the rain
at night. **Three questions have no other answer.** Are the drops visible at all — they are stretched
billboards whose length comes from *velocity* rather than from a constant, precisely so that standing
rain does not look like falling rain, and a still frame is exactly where that decision can come back
empty. Does the carriageway actually darken — the swap is counted in the build log, and a count says
the material was assigned, never that it looks any different. And does it stay out of the tunnels.
`_2_Motorway_Rain` is the one that has to carry the sheen, because it is the only frame with a low sun
down a long straight and smoothness with nothing to reflect looks like nothing at all.

The tool drives the weather itself, because `WeatherDirector` does not run at edit time — the same
reason `HudPreviewRenderer` has to call `LayOutFace` on the gauges. What it must not do is carry its
own idea of what "wet" means, so it calls `WetSurfaces.SetWet`, reads the drop rate off
`WeatherDirector.MaxDropsPerSecond` rather than typing one, and asks `VehicleCover.RoofedAt` — the
same probe the running game uses — whether the camera is under a roof. **The first version wrote a
flat rate instead**, which meant `_3_Portal_Rain` showed rain falling through a mountain and would
have gone on showing it after the fix, because the tool was bypassing the very thing under test. A
frame that cannot fail is not a check. `VehicleCover.RoofedAt` is static for exactly this caller: a
frame claiming to show that rain stops under a roof has to be produced by the code that stops it. It also **simulates** the emitter before each shot: nothing ticks outside Play mode, so
an unsimulated system is an empty one and every rain frame would come back looking exactly like rain
that does not draw. The rain lives under the chase rig and is reparented to the preview camera and put
back afterwards, or it would rain wherever the saved scene happens to have parked the car.

## The map

A minimap in the top-left corner, and the whole world behind a tap on it (`MenuPage.Map`). Both are one
`MapGraphic`, drawing a `WorldMap` — an asset baked by `Rebuild Prototype Scene` holding every paved road,
every body of water, every town outline and every name, as flat arrays of world XZ.

**Not a second camera, and the reason is not performance.** `WorldStreamer` disables chunks by distance, so
an orthographic camera over the world photographs a few hundred metres of loaded terrain surrounded by
nothing — which is what the player can already see out of the windscreen. A second full render pass would
also not fit the budget, but the streaming settles it on its own.

**Not the three baked sources that already existed either.** `TrafficNetwork` holds every drivable road as
world-space polylines, `WaterHazard` every water body's spine, `FillingStations` every forecourt — and none
can be drawn as a map without undoing what it is for. The routes carry *two lanes per street* plus a
connector for every legal turn through every junction: drawn directly, doubled roads and a spider at each
crossroads, with no names, no water and no features. Meanwhile the forty-odd `RoadFeature`s the courses
carry are baked nowhere at all, so something had to be.

**`WorldMapBuilder` is handed what it draws.** The world scene holds 199 `RoadPath` components and nine of
them are roads. `MotorwayPath` is the median the carriageways are offset from; `SeeburgAxis` and
`ArterialPath` are the frames `TownShape.ToWorld` maps a town against. Nothing about a path says which it
is, and a builder that enumerated the scene would put a road down the middle of two towns and a third
carriageway down the motorway. Town outlines are the convex hull of the street junctions rather than
`StreetNetwork.Footprint`, because no town here is square to the world axes and a box reads as a town twice
the size of the streets inside it.

**`[RequireComponent(typeof(CanvasRenderer))]` on `MapGraphic` is not boilerplate.** `Graphic` declares one
and it did not carry down: built by `AddComponent`, the object came up with no `CanvasRenderer`, and
`Graphic.Rebuild` opens with `if (canvasRenderer == null || canvasRenderer.cull) return;`. Every rebuild
returned on its first line — no error, no warning, a map that drew nothing, and labels that sat in exactly
the right places because they are separate objects. `MapGraphic.LastVertexCount` starts at −1 so that "drew
nothing" is tellable from "never ran"; the preview prints it.

**Segments are mitred, not extended.** Closing the notch on the outside of a corner by extending every
segment by its own half-width works on a straight and fails on a hairpin: the pass turns at 20 m with
samples 12 m apart, so each joint swings some thirty-four degrees and the rectangle's corner lands well
clear of the road. The stack came back serrated, one tooth per sample, on the one thing the driver reads at
speed. `half · tan(θ/2)` is exact, costs no vertices, and needs no trigonometry — `|cross| / (1 + dot)`.

**The minimap is round and clips its own geometry.** No `Mask`, no `RectMask2D`: `MapGraphic` clips every
polygon it emits against a 32-sided approximation of the frame, which costs a distance test per shape
because almost everything is wholly inside. A stencil `Mask` is the ordinary way to do this and was the
first way it was done — it would not clip in any frame this project can take, so what it did in a running
game was going to be a matter of trust, and geometry the tool can photograph is worth more than a
component that cannot be checked. It also costs no extra pass on a tile GPU, which is the argument
`TouchUiSetup.ScrollList` already makes against stencils.

**The clip stops at the rim's inner edge, not the rect's.** `UI_Dial.png` has its hole at 0.8 of its
radius, so a map clipped to the full half-width spends its last thirty units under a ring drawn at 30 %
alpha — and shows through it. That reads exactly like a clip that is not working. It cost three rounds to
tell apart from one, through two real but unrelated faults in the preview itself (a render target with no
stencil, then one with no sRGB, which had been miscolouring every frame). **Twice the picture was wrong in
a way that impersonated the fault it was being used to diagnose.** Fix the instrument before trusting the
reading.

Heading-up on the minimap, because the only question asked of it at speed is which way the next corner
goes; the full-screen map is north-up, because it answers a different one and a world that spins under the
reader answers it badly.

**And the car does not sit in the middle of it.** A heading-up map centred on the car spends half of
itself on road already driven, which is worth almost nothing — the mirror is not the instrument for
that. The widget is 300 units across and clips to the inner 80 %, so at the original 340 m span a
driver could see **136 m** of road ahead: five seconds at a hundred kilometres an hour, which is not
enough to read a corner off a map. The span is 440 m now and `Minimap.ForwardBias` slides the car 40 %
of the half-height down the disc while the view is pushed the same distance forward, which buys another
half again of forward reach at no zoom cost — about 260 m ahead, still inside the 600 m far plane, so
it is not telling the player about ground they could not otherwise know. That constant is `public` for
one reason: this component shifts the view and `TouchUiSetup` places the marker, and two copies of it
would agree until the first retune and then put the car beside the road it is drawn over.

**The full-screen map carries a key**, and every swatch in it is read off the `MapGraphic` beside it
through `ColourOf`. A palette typed out a second time in `MenuUiSetup` would agree until the first time
somebody retuned one of them, and a key that quietly lies is worse than no key.

**Street lines and feature marks are dropped past a zoom.** Not tidiness: one canvas mesh holds 65 535
vertices, and the four towns are 189 street lines that at a zoom where a town is forty units wide are not
streets but hatching.

**A new `MapLineKind` broke the entire map, and the way it broke is the lesson.** `MapGraphic` bins the
segments it is about to draw into one bucket per kind, and those buckets were `new int[4][]` — a
literal. Adding `Circuit` made that an index out of range on the first segment of the new kind, thrown
inside `OnPopulateMesh`, which Unity catches per frame. So there was no broken map and no error the
player could see: every road, every town, both views simply drew **nothing**, including everything that
had worked the day before. The only place it was visible was `MapPreview_World.png: 0 vertices, 4305
segments` — which is exactly the distinction `LastVertexCount` starting at −1 was put there to make.
`MapLine.KindCount` lives beside the enum now, and anything sizing an array by kind reads it. A kind
also has to be added to `OnPopulateMesh`'s emit list or it is collected and never drawn, which is the
quieter half of the same mistake.

**And a colour has to be chosen against the palette, not in isolation.** The circuit's first one was
0.88/0.55/0.28 against the motorway's 0.96/0.55/0.28 — so the one closed loop in the world came back
reading as another stretch of motorway, which is the exact impression a kind of its own exists to
avoid. It is red now. The key beside the full-screen map reads its swatch off `MapGraphic.ColourOf`, so
that half looked after itself.

**Marks are silhouettes, not four colours of the same diamond.** A square is a filling station, a
triangle a viewpoint, a hollow diamond a start place, a solid one a tunnel or a bridge — one `AddNgon`
covers all of them, and the rotation is what turns a diamond into a square without a second case. A
shape needs no legend to be told apart, it survives being four pixels across (which is the size a mark
is read at on the minimap), and it does not fail for anyone who cannot separate the green from the
orange. Colour still carries the meaning; the shape is what makes it legible. Every mark also sits on a
dark backing, because it stands over roads, water, town blocks and bare ground by turns and a flat
silhouette disappears against whichever is under it.

**The car marker is its own sprite, and the shape of it is the whole point.** It was the arrows' glyph
rotated ninety degrees — a near equilateral triangle, so at 34 units across which way it points is a
guess, on a heading-up minimap whose only job is to answer that. It is 0.62 wide against 1.8 long now,
with a notched tail and a dark rim.

**Whatever the map draws, the key has to draw too.** `LegendMark` gained `Square` and `Triangle` the
same day `AddMarker` did, and `MapMarkerKind.Place` is a hollow *diamond* rather than the ring it wanted
to be for exactly that reason: a shape family the key cannot show is a key that quietly lies, which this
file already says is worse than no key.

**A place that shares a town's name gets no mark.** The picture came back with "Seeburg" printed twice over
itself — the start place at the waterfront and the town at its centroid, a hundred metres apart.

`Tools > Horizon > Render Map Preview` photographs the world, each town, and a minimap-sized crop, and
reports what each frame actually drew. `Tools > Horizon > Render HUD Preview` photographs the canvas
itself — the first thing in this project ever to do so, and it found both the unclipped map and the fact
that a saved scene has every control scheme active at once. Both run at the end of `Rebuild`. **Every fault
this feature has had was found in those two pictures.** The build reported none of them.

## The frame

There was no post-processing at all, for the life of the project, while the budget above specified a
tone map and a colour grade from the beginning. `DefaultVolumeProfile` was Unity's stock file with
every override neutral, neither scene held a `Volume`, and the `ChaseCamera` object carried no
`UniversalAdditionalCameraData` — so `renderPostProcessing` sat at its default of `false`. Meanwhile
`VehicleLights` drives its lens colours to 2.4 and 3.2 with a comment saying that *"reads as a lit
lamp and blooms"*, and the forecourt signs, the tower beacons and the circuit boards are all
deliberately bright unlit materials. Every one of those values was clipping flat to white.

**Both pipeline assets were also pointed at Unity's leftover `SampleSceneProfile`**, which carries an
active bloom at 0.25 and an active vignette at 0.2 that nobody here authored. That is the
*quality-default* layer, underneath every scene volume, so the Low tier would have gone on blooming
after the tier switch was built and the switch would have looked broken rather than overridden.

**Two volumes rather than one, because a `VolumeProfile` is an asset.** Switching bloom by writing
`active` on a component inside a shared profile edits that asset, and Unity does not roll asset edits
back when Play mode ends — the hazard `TownLights` and `WetSurfaces` both document. Bloom gets a
volume of its own and `PostProcessing` turns its `weight` down, which is exactly what
`QualityDirector`'s own remarks call for: *everything there is a runtime value on a component*.

**Neutral rather than ACES.** ACES skews saturated hues towards yellow as they brighten, and this
world's identity is in the Ebental's unvarying gold, the Bahçe's two blossom tints — *"the only pale
cool colours anywhere in this world"* — and Anadolu's red earth. Neutral moves mid grey by about one
per cent, which on a flat-shaded world that is mostly mid tones is the tone mapper that keeps the art.

**`postExposure` is +0.5 and leaving it at zero would have made the whole change a regression.**
Neutral maps linear 1.0 to 0.63 and 2.4 to 0.89, so a tone map with no lift takes every value in the
world *down* — and the lamps, which clip to flat white today, would have come back dimmer than they
are. The comment on `VehicleLights` is only made true by the tone map and the bloom together.

**A tone mapper is one curve over the whole world, so the answer to it is one number.** Dozens of
tints in this file were chosen against an untone-mapped frame and every one of them moves. The
compensation is `postExposure`, `contrast` and `saturation`, and **not** a pass over the world's
colours: fifty changes each of which looks right alone and none of which can be attributed is the
failure shape already recorded against the paddock's null tints.

`ValidatePostStack` prints the stack and both pipeline assets by name every build and fails if post is
off or a quality-default profile returns. Its first version reported `render scale 1.00` against a
mobile asset that says 0.80, because `UniversalRenderPipeline.asset` is whichever level the *editor*
is on — which is `PC_RPAsset`. It names what it read now.

**And every preview frame this project takes had to be fixed before any of it could be trusted.** All
five capture paths built their camera with a bare `AddComponent<Camera>()`, so post was off in every
picture; turning it on for the game without them would have left four hundred PNGs quietly showing a
world the player never sees. `PreviewCapture` is the one place a frame is taken now — which also
carried the map preview's two hard-won fixes to the other four, neither of which had ever reached
them.

**The HUD and map previews keep post off on purpose, and so does the car thumbnail.** The game's canvas
is `ScreenSpaceOverlay`, which URP composites *after* the post stack, so a tone-mapped HUD preview
would show a HUD that does not exist. The thumbnail clears to alpha zero for the garage sprite and post
does not preserve that alpha.

**The world shots keep the plain `GetTemporary` target they have always used.** Moving them onto the
descriptor blew `M_SkyOvercast` from (139,152,132) to pure white in every overcast and rain frame,
day and night, while leaving the procedural clear sky and the road pixel-identical. Three quarters of
the frames unchanged is the shape of a regression that ships.

**One thing the pictures said and is not fixed: the overcast sky does not dim.** `T_SkyOvercast.png`
is a fixed grey gradient with `_Exposure: 1` and a white tint, so the rain sky reads the same at
midnight as at noon — measured, day and night frames identical. That is a `TimeOfDayController`
change and it is the one place a per-asset fix is honest here, because the sky is one material rather
than fifty tints. Recorded rather than hidden.

## What the road feels like

The terrain has `TerrainShape.DetailAmplitude` and the asphalt had nothing. A suspension with four
raycasts, a spring, a damper, two anti-roll bars and a load-dependent grip curve had nothing whatever
to work against until the car reached a verge — the whole model stood still on the one surface the
game is played on.

**`SurfaceRelief` changes no mesh.** It is a world-space height field sampled at the contact point and
subtracted from what the wheel measures. Displacing the geometry instead would cost vertices on the
heaviest tiles in the world, would break every piece of laid-on paving this file has already paid four
times to get flush, and would put bumps in the shadow map.

In `Horizon.Core` beside `GroundSurface` for the reason that class already gives: `Horizon.Vehicle`
reads it, `Horizon.EditorTools` checks it, and Core is the only assembly both can reach. It is a pure
function of a position — no state, no seed, no time — so a parked car is perfectly still.

**The noise is hand-rolled rather than `Mathf.PerlinNoise`, and this is the one place in the project
where that choice reverses.** `MountainField`, `TerrainTileBuilder` and `VegetationBuilder` are right
to use Unity's: they bake a mesh once, so a changed implementation would move the world and nothing
else. Here the function runs at 50 Hz on the device and its *derivative* is spent as a damper force,
so a quintic fade being C2 — continuous in the second derivative — is load-bearing rather than tidy.

**The short octave is 5.8 m, and it is chosen against the car rather than against taste.** All ten
cars share a 3.375 m wheelbase and a 2.475 m track. 4 m is what the load budget alone would have
picked, and it sits between the wheelbase and twice the track, which locks the wheels into a fixed
pattern. 5.8 clears twice the track by 17 % and stays under twice the wheelbase by 14 %.

**The per-surface gain is eased, never read raw.** `GainOf` is a step function in space, and a step in
a height field is a step in the distance a wheel measures — which is exactly the kerb
`MaxDamperSpeed` exists to survive, arriving on every verge exit.

**The binding limit is the wheel load, not the damper clamp.** `MaxDamperSpeed` is 4 m/s, which at
3800 N·s/m is over six times the Hatchback's static wheel load; the field reaches 1.3 % of it. Sizing
the amplitude against the clamp means sizing against the wrong number and arriving at a car whose
wheels leave the ground on a straight.

`ValidateSurfaceRelief` measures it, because **this is the one feature here a picture cannot check at
all**: the road is pixel-identical with the field and without it. Peak 4.0–4.4 mm, shaft speed
1.2–1.4 % of the clamp, peak load 10.2–11.8 % of static, and a differential of about half a millimetre
of pitch and a third of roll.

**That check reported two faults of its own before it reported none, and both are the lesson.** It
measured a front-to-rear correlation and got exactly −1.00 on every road, because subtracting the
four-wheel mean makes the rear pair identically minus the front. Fixed to a real Pearson coefficient it
got +0.74 to +0.95 — also wrong to warn about, since two of the three octaves are deliberately longer
than the wheelbase and a car riding a swell *should* have its wheels in phase, and the roll figure is
always the higher of the two because a car is narrower than it is long. It measures differential travel
in millimetres now, which is a length rather than an opinion.

**What it will not do is move a wheel visibly in its arch.** 2.7 mm of travel against 57 mm of static
sag is five per cent. Visible wheel motion needs about four times the amplitude, which is 45 % load
swing, which reopens ten commits of grip work. If a drive reports that it cannot be felt, the answer is
**one scale factor on the three amplitudes** and then reading what the check prints — not a fourth
octave and not a special case.

## What the rig knows about the car

The camera knew about speed and about being hit. It knew nothing about a corner or a crest.

**Cornering and pitch are measured in `ChaseCamera` itself rather than pushed in**, which is that
class's own stated design: it takes a bare `Transform` and `Rigidbody` so it can follow anything, and
it already differentiates the velocity it reads. The sideways component is one more subtraction, where
importing `VehicleController.LateralG` would need an interface, a push and a component to own it.
Measured across the *direction of travel* and not across the car's own right, so a car sideways in a
drift is not reported as cornering hard — a slide is the path going straight while the nose does not.

**The roll argues with something already written down, so it is the smallest number here.** The
buffeting tremor deliberately has no roll because roll at speed reads as a crash, and the impact kick
rolls for exactly that reason. The claim that a corner is different — sub-hertz, smooth, caused by the
driver and of a sign they know — is an argument rather than a measurement. `corneringRoll` is 0.6° and
it is the first thing to set to zero if a bend starts reading as an accident.

**Nothing in this project read `GroundedWheelCount`.** It has been published since the wheel model was
written and every consumer ignored it, while the Stadtfeld leg was designed around crests, the pass has
its own and the Weissjoch's stack is nothing but them. A landing is also *not* an impact: the wheels
are raycasts, so a clean one never touches the body collider and the existing thud only catches a car
putting its floor on the road. `DriveFeel` watches the wheel count and reuses `ChaseCamera.Shake`,
because a landing and a knock are the same thing happening to the rig.

One wheel may still be down and the car still count as flying — a car leaving a crest lifts its nose
first and trails a rear wheel for a good part of the jump, which is the half the driver is looking at.

## What the phone feels

This is played on a phone and the phone was never used as an output: no `Handheld.Vibrate`, no gamepad
rumble, nothing, while `Impacted`, `IsShifting` and `SurfaceRoughness` were all published and read only
by sound.

**Not `Handheld.Vibrate`.** It is a fixed half-second buzz with no amplitude — long enough to still be
running when the next corner arrives, and identical for a kerb and for a parapet at speed. The whole
value here is that a graze and a crash feel different, which needs a duration and an amplitude, so it
goes through `VibrationEffect` over JNI.

**Events only, never a continuous rumble.** That is `ContactAudio`'s distinction and it is right twice
over: a scrape is a state and belongs to sound, which can hold a level, where a motor asked to hold one
drains the battery and arrives as a buzz that swamps the moments worth feeling. Three of them — hitting
something, the next gear taking the load, and the tyres finding the verge.

**Wheelspin is deliberately not one, and it is the interesting omission.** It was on the list and it is
a state: a wheel lit up for two seconds out of a hairpin is either one tick that says nothing about the
two seconds, or a stream of them, which is the buzz again. It already has a voice, and the tyre squeal
is driven off exactly that number.

**There is no `PlayerSettings` property for VIBRATE** the way there is `forceInternetPermission`. Unity
infers it from whether `Handheld.Vibrate` survives stripping, and this calls the vibrator through JNI,
so the inference has nothing to find. `AndroidVibratePermission` edits the *generated* manifest rather
than dropping one into `Assets/Plugins/Android` — that file **replaces** Unity's main manifest rather
than merging with it, so a file holding one permission is a file holding no activity, and the app
installs and will not launch.

**None of it is observable outside a device**, which is the cost structure this file already records
for `REQUEST_INSTALL_PACKAGES`. So `HapticsDirector` publishes what the last pulse asked for and
`DriveDebugOverlay` prints it: the tuning happens at a desk and only the last step needs a phone.

## The wind

Nothing in this world moved on its own. The complete list was the sun, the traffic, four material swaps
every sixteen seconds at the lights, two a day at the windows, and two particle systems — park at noon
in clear weather with no traffic in frame and the picture was perfectly still.

**The sway rides on the vertex colour's alpha, which was free.** This project has exactly one custom
shader and all of the vegetation and all of the water go through it, and it had only ever read
`colour.rgb`. So a wind mask costs no extra vertex attribute, no second material, no draw call and no
triangle, and it reaches every plant in the world at once. The rebuild comes back with the identical
triangle count and the identical heaviest tile.

**The channel is inverted: the shader reads `1 - alpha`.** Everything here writes 255, which under that
reading is rigid, so terrain, buildings, roads and anything anybody forgets to mark stay still. The
other way round, one missed call sets a hillside swaying. That is `GroundSurface`'s rule about untagged
geometry — being wrong has to be invisible rather than catastrophic.

**`MergeTinted` had to stop writing the whole `Color32`.** It assigned the tint over all four channels,
so every tree in the world would have been flattened back to rigid at the moment it was given its
green — and the count in the log would have said so while the picture said nothing.

**The sway is in all three passes.** Left out of `ShadowCaster` the wind moves a canopy and leaves its
shadow standing, which is worse than no wind; left out of `DepthOnly` the depth disagrees with what was
drawn.

**Shrubs and grass move less than trees, and not for the reason it looks.** A real bush moves more than
a spruce — but a bush here is two metres of four facets, and the same absolute push that reads as a
canopy breathing reads on a shrub as the whole plant sliding along the ground.

**`WindDirector` is the one wind.** The moment more than one thing sways they have to agree, and trees
leaning north-east over a lake rippling south is two weathers in one frame. Global rather than per
material because the vegetation is merged into terrain tiles by the thousand and shares a material with
the ground it stands on.

**The water's swell fades out in the shallows.** This world's water meets its land as a mesh edge with
no foam behind it, so every centimetre the surface moves horizontally is a centimetre the waterline
moves across the beach.

**Both are counted in the build log and warned about at zero**, because this is the one thing here a
picture cannot check at all: a still frame of a swaying wood and a still frame of a dead one are the
same photograph. **The swell count came back zero twice, and the feature was fine both times.** First
the report sat beside `Water: N bodies`, which counts *plans* — the tiles are built thousands of lines
of work later, so it ran before a single water vertex existed. Then the fix for that silently failed to
apply because the anchor it matched spanned two lines. The instrument was wrong twice and the thing it
measures never was, which is why the counter is worth more than the swell.

**Clouds and the windmills are not done.** The clear sky is Unity's procedural skybox with no cloud
layer, so clouds mean authoring a sky rather than rotating one; `MillMeshes.AddWindmill` lofts its
sails into the shared tile mesh as static geometry, so turning them needs a transform of their own.
Both are their own change.

## Traffic that says what it is doing

The traffic has braked for the player since `GapAhead` learned to treat them as an obstacle, and it
showed it with nothing at all. The tail lamps were a single binary day/night material swap on a
`TownLights` group, so a car stopping dead at a signal looked exactly like one cruising past it, at
every hour.

**The lamps left the group, because that mechanism cannot answer this question.** A `LitGroup` swaps a
whole set at once between a day material and a night one, which is right for a window and wrong for a
lamp that also has to know about a brake pedal — and two writers on one material slot is the failure
this file keeps naming.

**The braking flag is taken at the line that already decides between the acceleration and the braking
rate.** Anywhere downstream would be a second opinion about whether the car is slowing, and the lamps
could disagree with the speed being integrated on the next line.

**Not any deceleration at all.** These agents ease off constantly — for a limit, for a corner, for a
gap closing slightly — and lighting up for all of it is a motorway where every car brakes forever,
which reads as broken rather than as busy.

**The material array is cached per car and assigned only on a change.** `Renderer.sharedMaterials`
allocates a fresh array on every read, so the obvious spelling is ninety-six allocations a frame in
driving code.

**Night is on `VehicleLights`' thresholds and not the group's**, which differed by a quarter of a stop:
the traffic used to light up a full minute of game time before the player's car did, which is a road
that looks like it knows something you do not.

Indicators are still nowhere, and the player's car still has no reversing light.

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
