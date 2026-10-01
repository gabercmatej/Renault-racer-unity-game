# Renault Racer

Renault Racer is a three-day Unity proof of concept exploring a racing giveaway experience for Renault’s new electric Renault 5 Turbo. The proposed campaign would invite participants to compete for the best lap time, with a reward for the best result.

## [▶ Play the browser demo](https://renaultracer.netlify.app/)

> **Work in progress:** the driving and drifting mechanics are unfinished and still need tuning and optimization. This demo showcases the current visual direction and gameplay prototype.

[![Renault Racer gameplay: the yellow Renault 5 Turbo on the circuit, with lap timer, minimap and speedometer](docs/gameplay.png)](https://renaultracer.netlify.app/)

<sub>Unedited frame from the production Web build. The in-game interface is in Slovenian. The giveaway is a campaign concept, not a live competition.</sub>

## What’s implemented

| Area | What is in the build |
| --- | --- |
| **Driving model** | A custom arcade simulation, not Unity physics. The car owns its world heading, so it carries momentum straight on unless steered. It has smoothed steering response, steering authority that drops with speed, surface-dependent grip on asphalt, rumble-strip curbs and grass, and braking. The handbrake is the only way into a drift, and the model detects counter-steering and uses it to speed up the recovery. It runs on a fixed timestep, so the handling is the same at 30 and 60 FPS. → [`CarSimulation.cs`](Assets/Scripts/Vehicle/CarSimulation.cs), [`HandlingProfile.cs`](Assets/Scripts/Vehicle/HandlingProfile.cs) |
| **Car presentation** | The Renault 5 Turbo 3E is drawn from 11 perspective views (five left, straight, five right), each with a brake-light overlay. The view follows the steering input: a tap shows the first angle, holding walks through to the fifth, and releasing walks back to straight. → [`CarFrameSelector.cs`](Assets/Scripts/Vehicle/CarFrameSelector.cs) |
| **Feedback** | Tyre smoke from the drawn tyres, impact sparks on the struck side, skid marks laid in road space, speed lines at the frame edges, synthesised motor and tyre sounds, and a camera that widens its field of view with speed, leans into corners, follows a slide and shakes on impacts. → [`SkidMarks.cs`](Assets/Scripts/Effects/SkidMarks.cs), [`CameraRig.cs`](Assets/Scripts/Effects/CameraRig.cs) |
| **Road and circuit** | A pseudo-3D road with hills, rebuilt each frame as one procedural mesh with textured grass, asphalt, curbs, line markings and a finish strip. It runs on a 15-turn circuit whose numbered corners drive the signage, minimap and sectors. Around it sit roadside crowds, tyre walls, chevrons and braking boards, placed from the track data, in front of an Alpine panorama. → [`RoadRenderer.cs`](Assets/Scripts/Track/RoadRenderer.cs), [`TrackLayout.cs`](Assets/Scripts/Track/TrackLayout.cs) |
| **Race flow** | Start-light countdown, lap clock, three sector splits compared with the player’s best, a minimap computed from the same track data the car drives on, and a finish screen. Laps are checked for checkpoints and plausible times, and invalid ones are rejected. Restart works without reloading the page. Personal bests are saved locally in the browser only. → [`MinimapView.cs`](Assets/Scripts/UI/MinimapView.cs), [`RaceValidator.cs`](Assets/Scripts/Race/RaceValidator.cs), [`PersonalBests.cs`](Assets/Scripts/Race/PersonalBests.cs) |
| **Controls and layout** | Keyboard, plus a touch scheme with full-height left and right steering halves and a brake button, tracking each finger separately. Portrait screens get a “rotate your phone” screen that pauses the race. The HUD is inset to the phone’s safe area for notches and home indicators. → [`TouchDriveInput.cs`](Assets/Scripts/Input/TouchDriveInput.cs), [`RotateGate.cs`](Assets/Scripts/UI/RotateGate.cs), [`SafeAreaFitter.cs`](Assets/Scripts/UI/SafeAreaFitter.cs) |
| **Artwork** | Car, environment, effects, UI and minimap sprite atlases, plus road textures. → [`Assets/Art/Resources`](Assets/Art/Resources) |

## Built for the browser

- **Small, compressed download.** The production Web build uses Brotli compression, hashed file names and browser caching, with engine code stripping and high managed-code stripping. The shipped player is about 13.4 MB compressed.
- **Few textures, few draw calls.** Art is packed into sprite atlases. The road is one mesh, the skid marks are one mesh, and the background is one quad.
- **No allocation spikes during play.** Roadside props and effects are pooled and hard-capped, nothing is created or destroyed during a race, and the road and skid-mark meshes reuse pre-allocated buffers.
- **Adaptive quality.** The game guesses a starting tier from the device’s reported memory and CPU cores, then measures frame time during the first seconds of racing. If needed, it steps down once to shorter draw distance, fewer props and less smoke, or turns off skid marks and speed lines, and caps resolution. → [`QualityDirector.cs`](Assets/Scripts/Core/QualityDirector.cs)
- **Bounded memory.** The WebAssembly heap starts at 128 MB with a 384 MB ceiling.

**Mobile, intended vs. tested:** these measures are aimed at lower-memory phones, but no real-device performance tests have been run. Mobile checks so far used browser emulation only. Frame rate, memory use and device compatibility are not yet measured.

## Controls

| Action | Keyboard | Touch |
| --- | --- | --- |
| Start | Enter, or click **ZAČNI** | Tap **ZAČNI** |
| Steer | ← → or A / D | Left / right half of the screen |
| Brake | ↓, S or Space | Brake button |
| Handbrake | Shift | — |
| Restart | R | Tap after the finish |

The car accelerates automatically during a race.

## Open it in Unity

1. Install **Unity 6000.6.0f1** with **Web Build Support**, via Unity Hub.
2. Clone this repository and add the folder in Unity Hub (**Add → Add project from disk**). Git must be installed, because one editor package is fetched from GitHub.
3. Open `Assets/Scenes/Main.unity` and press **Play**.

For a production Web build, use **Renault Racer → Build Web — PRODUCTION (Brotli) + size audit**, which writes to `Builds/WebCurrent`, or run:

```
Unity -batchmode -quit -projectPath . -executeMethod RenaultRacer.EditorTools.ProductionBuild.RunFromCommandLine
```

`com.coplaydev.unity-mcp` in `Packages/manifest.json` is an editor-only development tool. It is not part of the game and can be removed.

## Repository layout

| Folder | Contents |
| --- | --- |
| [`Assets/Scripts`](Assets/Scripts) | Game code: [`Vehicle`](Assets/Scripts/Vehicle), [`Track`](Assets/Scripts/Track), [`Race`](Assets/Scripts/Race), [`UI`](Assets/Scripts/UI), [`Input`](Assets/Scripts/Input), [`Effects`](Assets/Scripts/Effects), [`Audio`](Assets/Scripts/Audio), [`Core`](Assets/Scripts/Core), [`Web`](Assets/Scripts/Web) |
| [`Assets/Art/Resources`](Assets/Art/Resources) | Artwork: car, track, environment, effects and UI |
| [`Assets/ScriptableObjects`](Assets/ScriptableObjects) | Game configuration and handling profiles |
| [`site/`](site) | The showcase page and the prebuilt Web player (`site/demo`), served with Brotli headers from [`site/_headers`](site/_headers) |

## Credits and notices

- **Montserrat** font, © 2024 The Montserrat.Git Project Authors, licensed under the SIL Open Font License 1.1. The files in `Assets/Art/Resources/Fonts` are modified (instanced, subset and renamed) versions. See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) and [`Assets/Art/Fonts/Montserrat-OFL.txt`](Assets/Art/Fonts/Montserrat-OFL.txt).
- Built with **Unity** 6000.6.0f1. Unity is a trademark of Unity Technologies.
- Renault, the Renault logo and Renault 5 Turbo are trademarks of Renault. This prototype is a campaign proposal, not an official Renault product.

No license is granted for the code or artwork in this repository unless stated above.
