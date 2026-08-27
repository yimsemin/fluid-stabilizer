# Setup status

Last updated: 2026-08-27

## 1. Development environment — verified

| Item | Result |
|---|---|
| OS | macOS 26.6.2 (build 25G83) |
| CPU / architecture | Apple M4 Max, **arm64** (Apple Silicon) |
| Unity Hub | 3.21.0, installed at `/Applications/Unity Hub.app` |
| Unity Editor | **6000.5.10f1** (Unity 6.2 line), at `/Applications/Unity/Hub/Editor/6000.5.10f1` |
| Unity Editor modules installed | WebGL Build Support, Documentation |
| Unity **license** | ✅ activated (Unity Personal, entitlement license present) |
| Git | 2.55.0 |
| Git identity | `YSM` (global) |
| Git LFS | ❌ was missing → **installed** (3.7.1), see §3 |
| GitHub CLI (`gh`) | 2.98.0, authenticated as **`yimsemin`** (scopes: repo, workflow, read:org, gist) |
| Homebrew | 6.0.20 |
| Python | 3.13.14 |
| Node.js | not installed (not needed) |
| .NET SDK | not installed (not needed — the Unity Editor bundles its compiler) |

### Verdict

The environment is sufficient to build and run the simulation. No missing tools
block phase-1 work.

- **Unity 6000.5.10f1 is a fine version to standardize on.** It is current, it is
  the same on macOS and Windows, and nothing in the design needs a different
  version. The project is pinned to it (`ProjectSettings/ProjectVersion.txt` is
  committed); both machines must use exactly this version via Unity Hub.
- **No extra Unity modules needed now.** Mac in-Editor play mode does not require
  a build-support module. Add "Windows Build Support (Mono)" / "Mac Build Support"
  only if/when you want standalone builds. The WebGL module that is already
  installed is harmless; leave it.

## 1b. Unity project — created and verified (2026-08-27)

- Created via the Editor CLI (`-createProject`) at the **repo root**
  (`<저장소 루트>`), so `Assets/`, `Packages/`,
  `ProjectSettings/` sit alongside `docs/`.
- **Render pipeline: Built-in (BiRP).** The CLI-created project is a bare 3D
  project on the Built-in pipeline — this supersedes the earlier note about the
  URP "3D cross-platform" template. Built-in is the better fit here: fewer
  dependencies, lighter, and rendering is off during headless training anyway.
  A simple visual truck/tank/liquid proxy needs nothing URP provides. Revisit
  only if visualization needs outgrow it.
- `Packages/manifest.json` is minimal: Unity built-in modules + one default
  package (`com.unity.multiplayer.center`, harmless, can be removed later).
  **Not yet added** (recommended when phase 1 starts, not before):
  `com.unity.test-framework` (deterministic unit tests for the controller/slosh
  math) and `com.unity.inputsystem` (gamepad/keyboard driver input). Held back
  per "no feature work before the design review is approved."
- **Verified:** opened headless twice — 37 packages resolved, scripts compiled
  with 0 errors, Asset Pipeline refresh clean, batchmode quit successfully.
  (The `Curl error 7 … config.uca.cloud.unity3d.com` and initial licensing
  handshake-retry lines in the log are the offline analytics/config endpoint and
  are harmless.)
- Committed: `Packages/manifest.json`, `Packages/packages-lock.json`, and
  `ProjectSettings/*` (24 small text files). `Library/`, `Logs/`, `Temp/`,
  `UserSettings/` are correctly gitignored.

## 2. Git repository — configured

- `git init` on branch **`main`**, in `<저장소 루트>`.
- `.gitignore` — Unity + Python + ROS ignores (standard Unity ignore set).
- `.gitattributes` — LF normalization for cross-platform (Mac/Windows), Unity
  YAML assets marked binary-safe with `unityyamlmerge`, Git LFS rules for binary
  asset types.
- `core.autocrlf=input` set locally.
- Git LFS hooks installed in the repo.
- Initial commits: repo scaffold + design docs, then the verified Unity project.
- **No GitHub remote created yet** — that needs your go-ahead (see §5.2).

## 3. Tools installed this session, and why

| Tool | Version | Why |
|---|---|---|
| **git-lfs** | 3.7.1 (Homebrew) | Standard for Unity repos. Binary assets (models, textures, audio, trained `.onnx` policies) must not bloat git history. Retrofitting LFS after large files are committed is painful, so it is set up now even though the repo currently has no binaries. `.gitattributes` already lists the patterns. |

Nothing else was installed.

## 4. Intentionally NOT installed, and why

| Not installed | Why |
|---|---|
| **ROS 2** | Not needed until hardware integration (phase 3). The design keeps a message-DTO + bus interface so ROS 2 can be added later without restructuring. Installing it now adds a large dependency with zero present use. |
| **Unity ML-Agents** | The design deliberately avoids coupling training to Unity/ML-Agents (design-review §12). The RL trainer will sit behind a language-agnostic env boundary. ML-Agents would only be reconsidered later, behind an adapter. |
| **Fluid-simulation plugin (Obi Fluid, Zibra, etc.)** | The recommended slosh model is a custom analytic reduced-order model (design-review §6). Particle fluids break determinism and the RL step budget. |
| **.NET SDK, Node.js** | The Unity Editor bundles its own C# compiler; Claude Code can read/edit the generated `.csproj`/`.sln` without a standalone SDK. No JS tooling is in the design. |
| **A standalone IDE (Rider / VS Code C#)** | Not required for phase 1. The Editor + Claude Code is enough. Can be added later purely as a convenience. |
| **Extra Unity build-support modules** | Not needed for in-Editor play or headless training on this Mac. Add per-platform build support only when standalone builds are actually wanted. |

## 5. Needs you

### 5.1 Unity license — done ✅

Unity Personal license activated. Project created and verified (see §1b).

### 5.2 GitHub remote (optional, your call)

`gh` is authenticated as `yimsemin`, so the remote can be created without any new
login. It has **not** been created. When you want it, confirm:

- repo name `fluid-stabilizer`, owner `yimsemin`
- **private** or public
- then: `gh repo create` + push `main`.

## 6. Current state / next step

Environment verified, Git repo configured, Unity project created and verified,
all committed locally. **Now waiting for your decision on the design review**
(`design-review.md`) before any feature implementation. When phase 1 is approved,
the first setup task is adding `com.unity.test-framework` and
`com.unity.inputsystem` and agreeing `frames-units-conventions.md`.
