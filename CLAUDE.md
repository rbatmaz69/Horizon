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
