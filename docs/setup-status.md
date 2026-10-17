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
| **Python (프로젝트, `python/.venv`)** | **3.12** (uv 관리, `python/.python-version`) — 정확한 버전은 `python/uv.lock`. torch / gymnasium / tensorboard 모두 arm64 에서 동작하고 MPS 를 쓸 수 있다 |

## Unity 프로젝트

- Editor CLI(`-createProject`)로 **저장소 루트**에 만들었다(`Assets/`, `Packages/`, `ProjectSettings/` 가
  `docs/` 옆에 있다).
- **렌더 파이프라인: Built-in (BiRP).** 의존성이 적고 가볍고, 헤드리스 학습 중에는 렌더링이 꺼진다. 단순한
  트럭/탱크/액체 프록시에는 URP 가 주는 이점이 필요 없다.
- **`Packages/manifest.json`** — Unity 빌트인 모듈 + `com.unity.test-framework`(결정론적 단위 테스트) +
  `com.unity.inputsystem`(게임패드/키보드 입력). 둘 다 프로젝트를 시작할 때 추가했다.
  **`com.unity.sentis` 는 명시적 결정 없이 추가하지 않는다**(project-goals 아키텍처: 정책은 별도 프로세스,
  ONNX 를 Unity 에 굽지 않는다).
- **Standalone 빌드:** `FluidStabilizer ▸ Build Standalone App`(`BuildRunner`)이 활성 타깃(이 Mac 에서는
  StandaloneOSX)으로 빌드해 `Builds/` 에 둔다. 다른 플랫폼용 빌드가 필요하면 Unity Hub 에서 해당 Build Support
  모듈을 추가한다.
- **검증:** 헤드리스로 열림, 스크립트 컴파일 에러 0, EditMode 테스트 통과(개수는
  [experiment-plan.md](experiment-plan.md)), standalone 빌드 정상 실행.

## Git 저장소

- `main` 브랜치, 저장소 루트에서 `git init`.
- `.gitignore` — Unity + Python + ROS + `Builds/` 를 무시한다.
- `.gitattributes` — LF 정규화(Mac/Windows), Unity YAML 자산을 바이너리로 안전하게 다루고
  `unityyamlmerge` 를 쓰며, Git LFS 규칙을 담는다.
- GitHub 리모트 `origin` = `github.com/yimsemin/fluid-stabilizer` (존재).

## 설치한 도구

| 도구 | 이유 |
|---|---|
| **git-lfs** | Unity 저장소의 표준이다. 바이너리 자산(모델, 텍스처, 오디오, 학습된 `.onnx` 정책)이 git 히스토리를 부풀리면 안 된다. 큰 파일을 커밋한 뒤에 LFS 를 끼워 넣기는 고통스러워서 처음부터 설정했다. 패턴은 `.gitattributes` 에 있다. |

## 의도적으로 설치하지 않은 것과 이유

| 설치 안 함 | 이유 |
|---|---|
| **ROS 2** | 하드웨어를 통합하기 전에는 필요 없다. 설계가 message-DTO + bus 인터페이스를 유지하므로 나중에 재구조화 없이 추가할 수 있다. |
| **Unity ML-Agents** | 설계가 일부러 학습을 Unity/ML-Agents 에 결합하지 않는다(design-review §12). RL 학습기는 언어 중립적인 env 경계 뒤에 둔다. |
| **유체 시뮬 플러그인 (Obi Fluid, Zibra 등)** | 권장하는 슬로시 모델은 직접 만든 분석적 reduced-order 모델이다(design-review §6). 입자 유체는 결정론과 RL 스텝 예산을 깬다. |
| **`com.unity.sentis` (ONNX 런타임)** | 아키텍처 결정(project-goals): 정책은 별도 Python 프로세스로 서빙하고 Unity 바이너리에 굽지 않는다. 작은 MLP 에는 과하다. |
| **.NET SDK, Node.js** | Unity Editor 가 자체 C# 컴파일러를 포함한다. 생성된 `.csproj`/`.sln` 은 별도 도구 없이 읽고 편집할 수 있다. |
| **독립 IDE (Rider / VS Code C#)** | 필요 없다. Editor 만으로 충분하다. |

## 헤드리스 실행

```bash
UNITY="/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity"

# EditMode 테스트
"$UNITY" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/x.xml -logFile -

# 편집기 러너 (예: feasibility 천장)
"$UNITY" -batchmode -quit -nographics -projectPath . -executeMethod FluidStabilizer.SimEditor.HindsightEvalRunner.Run -logFile -
```

> ⚠️ Unity 는 프로젝트당 인스턴스 하나만 열 수 있다. `-executeMethod` / `-runTests` 를 실행하기 전에 Editor 를
> 닫고 `Temp/UnityLockfile` 이 없는지 확인한다.
