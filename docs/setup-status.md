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
| Unity **license** | ❌ **not activated** — blocks project creation. See §4. |
| Git | 2.55.0 |
| Git identity | `YSM` (global) |
| Git LFS | ❌ was missing → **installed** (3.7.1), see §3 |
| GitHub CLI (`gh`) | 2.98.0, authenticated as **`yimsemin`** (scopes: repo, workflow, read:org, gist) |
| Homebrew | 6.0.20 |
| Python | 3.13.14 |
| Node.js | not installed (not needed) |
| .NET SDK | not installed (not needed — the Unity Editor bundles its compiler) |

### Verdict

The environment is sufficient to build and run the simulation **once the Unity
license is activated**. No missing tools block phase-1 work except the Unity
license.

- **Unity 6000.5.10f1 is a fine version to standardize on.** It is current, it is
  the same on macOS and Windows, and nothing in the design needs a different
  version. Recommend pinning the project to it (`ProjectSettings/ProjectVersion.txt`
  is committed) and both machines using exactly this version via Unity Hub.
- **No extra Unity modules needed now.** Mac in-Editor play mode does not require
  a build-support module. Add "Windows Build Support (Mono)" / "Mac Build Support"
  only if/when you want standalone builds. The WebGL module that is already
  installed is harmless; leave it.

## 2. Git repository — configured

- `git init` on branch **`main`**, in `<저장소 루트>`.
- `.gitignore` — Unity + Python + ROS ignores (standard Unity ignore set).
- `.gitattributes` — LF normalization for cross-platform (Mac/Windows), Unity
  YAML assets marked binary-safe with `unityyamlmerge`, Git LFS rules for binary
  asset types.
- `core.autocrlf=input` set locally.
- Git LFS hooks installed in the repo.
- **No GitHub remote created yet** — that needs your go-ahead (see §5).

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

## 5. Blocked / needs you

### 5.1 Activate the Unity Editor license (required to proceed)

The Editor is installed but no license is active, so it refuses to create or open
projects (`No valid Unity Editor license found`). This needs your Unity account
sign-in — it cannot be automated.

1. Open **Unity Hub**.
2. Sign in with a Unity account (create a free one at id.unity.com if you don't
   have one — account creation must be done by you).
3. Hub → the account menu (top-right) → **Manage licenses** → **Add license** →
   **Get a free personal license** (Unity Personal). Unity Personal is free for
   individuals / small revenue and is the right choice here.
4. Confirm it shows an active license.

Tell me when that's done and I will create the Unity project via the command
line with the settings from the design review (URP "3D cross-platform" template,
pinned to 6000.5.10f1, at the repo root), verify it opens and enters Play mode,
and make the initial commit.

Alternatively you can create it yourself in Unity Hub (**New project → Universal
3D → name `fluid-stabilizer`, location = this folder's parent so the project
lands at `<저장소 루트>`**) — but letting me do it via
CLI keeps the settings controlled.

### 5.2 GitHub remote (optional, your call)

`gh` is authenticated as `yimsemin`, so I can create the remote without any new
login. I have **not** done so. When you want it, confirm:

- repo name `fluid-stabilizer`, owner `yimsemin`
- **private** or public
- then I'll run `gh repo create` and push `main`.

## 6. Next step after unblocking

Create + verify the Unity project (§5.1), make the initial commit, then **stop
and wait for your decision on the design review** (`design-review.md`) before any
feature implementation.
