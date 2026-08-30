# 개발 환경 사실

버전·경로·도구. (프로젝트 진행상황은 [experiment-plan.md](experiment-plan.md).)

## 개발 환경 — 검증됨

| 항목 | 값 |
|---|---|
| OS | macOS 26.x (Apple Silicon) |
| CPU / 아키텍처 | Apple M4 Max, **arm64** |
| Unity Hub | `/Applications/Unity Hub.app` |
| Unity Editor | **6000.5.10f1** (Unity 6.2 라인), `/Applications/Unity/Hub/Editor/6000.5.10f1` |
| Unity 라이선스 | ✅ 활성 (Unity Personal) |
| Git | 2.55+, LFS 활성 (3.7+) |
| Git 신원 | `YSM` (global), `core.autocrlf=input` (local) |
| GitHub CLI (`gh`) | `yimsemin` 으로 인증 (scopes: repo, workflow, read:org, gist) |
| Python (시스템) | 3.13 |
| **Python (프로젝트, `python/.venv`)** | **3.9.6** (CommandLineTools) — torch 2.8.0 / gymnasium 1.1.1 / tensorboard, 전부 arm64, MPS 사용 가능 |

## Unity 프로젝트

- Editor CLI (`-createProject`)로 **저장소 루트** 에 생성 (`Assets/`,
  `Packages/`, `ProjectSettings/` 가 `docs/` 옆).
- **렌더 파이프라인: Built-in (BiRP).** 의존성 적고, 가볍고, 헤드리스 학습 중엔
  렌더링이 꺼져 있음. 단순한 트럭/탱크/액체 프록시에 URP 가 주는 게 필요 없음.
- **`Packages/manifest.json`** — Unity 빌트인 모듈 + `com.unity.test-framework`
  (결정론적 유닛 테스트) + `com.unity.inputsystem` (게임패드/키보드 입력). 둘
  다 phase 1 시작 때 추가됨. **`com.unity.sentis` 는 명시적 결정 없이 추가 금지**
  (project-goals 아키텍처: 정책은 별도 프로세스, ONNX 를 Unity 에 굽지 않음).
- **표준 빌드 지원:** `FluidStabilizer ▸ Build Standalone App` (`BuildRunner`)
  이 활성 타깃(이 Mac 에선 StandaloneOSX)으로 빌드 → `Builds/`. 다른 플랫폼용
  빌드가 필요하면 Unity Hub 에서 해당 Build Support 모듈 추가.
- **검증:** 헤드리스로 열림, 스크립트 0 에러 컴파일, EditMode 테스트
  131 통과 / 8 스킵 (2026-08-30), standalone 빌드 정상 실행.

## Git 저장소

- `git init` on `main`, `<저장소 루트>`.
- `.gitignore` — Unity + Python + ROS + `Builds/` ignore.
- `.gitattributes` — LF 정규화 (Mac/Windows), Unity YAML 자산 binary-safe +
  `unityyamlmerge`, Git LFS 규칙.
- GitHub 리모트 `origin` = `github.com/yimsemin/fluid-stabilizer` (존재).

## 이번 세션들에 설치한 도구

| 도구 | 이유 |
|---|---|
| **git-lfs** | Unity 저장소 표준. 바이너리 자산(모델, 텍스처, 오디오, 학습된 `.onnx` 정책)이 git 히스토리를 부풀리면 안 됨. 큰 파일 커밋 후 LFS retrofit 은 고통스러워서 지금 설정. `.gitattributes` 에 패턴 나열됨. |

## 의도적으로 설치 안 한 것과 이유

| 설치 안 함 | 이유 |
|---|---|
| **ROS 2** | 하드웨어 통합(phase 3) 전까지 불필요. 설계가 message-DTO + bus 인터페이스를 유지해서 나중에 재구조화 없이 추가 가능. |
| **Unity ML-Agents** | 설계가 의도적으로 학습을 Unity/ML-Agents 에 결합 안 함 (design-review §12). RL 학습기는 언어중립 env 경계 뒤에. |
| **유체 시뮬 플러그인 (Obi Fluid, Zibra 등)** | 권장 슬로시 모델은 커스텀 분석적 reduced-order 모델 (design-review §6). 입자 유체는 결정론과 RL 스텝 예산을 깸. |
| **`com.unity.sentis` (ONNX 런타임)** | 아키텍처 결정 (project-goals): 정책은 별도 Python 프로세스로 서빙, Unity 바이너리에 안 구움. 작은 MLP 에 과함. |
| **.NET SDK, Node.js** | Unity Editor 가 자체 C# 컴파일러 번들. Claude Code 가 생성된 `.csproj`/`.sln` 을 읽고 편집 가능. |
| **standalone IDE (Rider / VS Code C#)** | phase 1 에 불필요. Editor + Claude Code 로 충분. |

## 헤드리스 실행

```bash
UNITY="/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity"

# EditMode 테스트
"$UNITY" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/x.xml -logFile -

# 편집기 러너 (예: feasibility 천장)
"$UNITY" -batchmode -quit -nographics -projectPath . -executeMethod FluidStabilizer.SimEditor.HindsightEvalRunner.Run -logFile -
```

> ⚠️ Unity 는 프로젝트당 단일 인스턴스. `-executeMethod` / `-runTests` 전에
> Editor 를 닫고 `Temp/UnityLockfile` 이 없는지 확인.
