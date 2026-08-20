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
