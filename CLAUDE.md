# CLAUDE.md — 이 저장소에서 Claude Code 를 위한 안내

## 이 프로젝트가 무엇인가

`fluid-stabilizer` — Physical AI 시뮬레이션. AI 가 **인간이 운전하는** 트럭에
실린 개방형 액체 탱크 아래의 2축(pitch/roll) 스태빌라이저를 제어해 스필을
최소화한다. AI 는 **운전하지 않는다.** 전체 맥락:
[docs/project-goals.md](docs/project-goals.md).

## 문서 규칙

- **문서는 모두 한글로 쓴다** (파일명은 도구·상호참조 호환을 위해 영어 유지,
  제목·본문은 한글). 새 문서도 한글로. 색인: [docs/README.md](docs/README.md).
- 설계 결정이 바뀌면 같은 변경에서 해당 `docs/*.md` 를 갱신한다. 문서는 소유자가
  프로젝트를 추적하는 수단이다.
- **문서마다 하나의 역할** — 같은 사실을 여러 곳에 쓰지 않는다 (중복 → 미래
  모순). 각 문서의 역할은 [docs/README.md](docs/README.md) 표 참고.

## 작업 전에 읽을 것

1. [docs/project-goals.md](docs/project-goals.md) — 목적 · 헤드라인 결과 · 최종
   아키텍처. **의도와 범위의 유일한 출처.**
2. [docs/experiment-plan.md](docs/experiment-plan.md) — 살아있는 현황 + 다음 단계.
3. [docs/design-review.md](docs/design-review.md) — 비판적 리뷰 + 권장 아키텍처.
   **설계 결정의 근거 출처.**
4. [docs/decisions.md](docs/decisions.md) — 결정 로그 D1~D6.
5. [docs/findings.md](docs/findings.md) — 실험으로 밝혀진 사실 F1~F5.
6. [docs/frames-units-conventions.md](docs/frames-units-conventions.md) — 좌표계,
   단위, 타이밍 (물리 코드 전에 합의됨, 고정).

## 소유자

전문 개발자가 아님. 소유자가 Claude Code 와 함께 이해하고 관리할 수 있는 구조를
우선한다. 단순하게; 트레이드오프를 설명; enterprise 급 추상화를 만들지 않는다.
더 무거운 접근을 조용히 채택하지 않는다 — 그렇게 하려면 말하고 이유를 댄다.

## 하드 룰 (설계 리뷰에서)

- **컨트롤러 코드는 `UnityEngine` 참조 0.** 메시지 in, 메시지 out. 지금 시뮬과
  나중 ROS 2 브릿지에서 변경 없이 돌아야 한다.
- **Sensor → Controller → Actuator 경계를 지킨다.** 인터페이스: `IController`,
  `ISloshModel`, `IDriverInput`, `IClock`, `IRandomSource`. (`ISensorBus` /
  `IActuatorBus` 는 아직 명시적 타입이 아니라 `IController` seam + DTO 가
  계약을 담음.)
- **AI actor 관측 = 현실적 센서 데이터만.** Ground truth (실제 액체 CoM, 슬로시
  진폭, 자세, 미래 도로/입력, 물리 내부)는 critic 과 로깅에만 — actor 에는 절대.
- **결정론:** 고정 timestep, 모든 확률 요소에 seeded RNG, 슬로시 ODE 는 PhysX
  밖에서 적분(매니지드 루프). 프레임레이트 의존 로직 없음.
- **지연, 센서 노이즈/바이어스, 액추에이터 비이상성을 1일차부터 모델링.**
- **베이스라인 컨트롤러(specific-force alignment, PID, 특권 MPC)를 RL
  전에/함께 만든다.** 모든 RL 결과는 그것들 상대로 보고.
- SI 단위, 오른손 프레임, 모든 메시지에 타임스탬프.

## 명시적 결정 없이 설치/채택 금지

ROS 2 · Unity ML-Agents · SPH/입자 유체 플러그인 (Obi, Zibra) · CFD ·
완전한 WheelCollider 차량 시뮬 · 클라우드/CI 인프라 · `com.unity.sentis`.
근거는 design-review §21, setup-status §4.

## 슬로시 모델

v1 = 커스텀 분석적 **등가역학 모델** (축당 진자 1개, 매니지드 루프에서 적분,
양방향 결합), `ISloshModel` 뒤에. 얕은물과 다중모드 모델은 phase-2 drop-in.
design-review §6. **브래킷 규율** (findings F3): 진자 ROM 으로 학습, 얕은물로 검증.

## 환경 사실

- Unity **6000.5.10f1**, 커밋된 `ProjectSettings/ProjectVersion.txt` 로 고정.
  Mac 과 Windows 같은 버전. **Built-in 렌더 파이프라인** (URP 아님).
- Unity 프로젝트가 **저장소 루트** 에 (`Assets/`, `Packages/`,
  `ProjectSettings/` 가 `docs/` 옆).
- macOS arm64 (Apple M4 Max). Windows 나중에 쓸 수도 — config 를 플랫폼 중립으로.
- Git `main`, Git LFS 활성. `gh` 는 `yimsemin` 으로 인증. 리모트 `origin` =
  `github.com/yimsemin/fluid-stabilizer` (존재).
- 생성 파일 (`Library/`, `*.sln`, `*.csproj`, `Logs/`, `UserSettings/`,
  `Temp/`, `Builds/`)은 gitignore.
- Python: `python/.venv` = **Python 3.9.6** (CommandLineTools), torch 2.8.0 /
  gymnasium / tensorboard 모두 arm64 에서 작동.
- 헤드리스 검증: `"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/x.xml -logFile -`

## 현재 상태

**초안(수직 슬라이스) 완성** (2026-08-29). 파이프라인 전체가 처음부터 끝까지 돈다:
녹화 → hindsight 천장 → BC 데이터셋 → BC 사전학습 → PPO 미세조정 →
`serve_policy.py` → Unity 앱 내 ①②③ 비교 → standalone 빌드. EditMode 테스트
**113 통과 / 0 실패 / 8 스킵**. 만든 것의 전체 목록은
[docs/experiment-plan.md](docs/experiment-plan.md) "지금까지 만든 것".

**게이트 결과 (finding F5):** 완전한 후행지식 + 실제 액추에이터 제약 하에서도
최적 짐벌 궤적이 소유자의 녹화 주행에서 기계적으로 잠근 탱크보다 더 스필한다.
**제대로 된 잔차 RL 은 전제 조정(탱크 freeboard, 도로 강도, 또는 하이브리드
lock-capable 액션 공간) + step-3 재실행 전까지 보류.** 그 게이트가 통과하기
전까지 **잔차 RL 을 시작하거나 맨땅 PPO 를 재실행하지 말 것.**

세션 커밋은 바로 `main` 으로 (혼자 쓰는 로컬 저장소 — feature 브랜치 없음).

## Git & 커밋 (이 저장소의 모든 세션에 적용)

- **GitHub 리모트(`origin`)를 가진 혼자 쓰는 로컬 저장소.** `main` 에서 작업,
  바로 커밋, **feature 브랜치 없음**.
- **상시 승인:** Claude 는 각 논리적 단계가 끝나면 매번 묻지 않고 `main` 에
  커밋 **하고 `origin/main` 으로 push** 한다. 단계별로 커밋 (자체 완결 변경
  하나 = 기능 + 그 테스트 + 그 문서 갱신), 세션 끝 거대 커밋 하나가 아님 —
  소유자가 히스토리로 진행을 추적한다. 코드 커밋 전 EditMode 스위트 실행,
  메시지에 통과/스킵 수 기록.
- **여전히 먼저 물을 것:** 히스토리를 재작성/폐기하는 것 (force-push, push 된
  커밋의 rebase, 커밋 넘는 `reset --hard`, 브랜치/태그 삭제).
- **커밋 메시지:** 간결한 요약 줄, 그다음 명백하지 않으면 *왜* 를 설명하는 짧은
  본문. **AI 생성 표식·co-author 트레일러 금지** — `Co-Authored-By: Claude …`,
  `🤖 Generated with …` 등을 커밋 메시지에 넣지 않는다. `.githooks/commit-msg`
  가 하드 게이트로 제거하며, `.claude/settings.json` 이 자동 추가를 끈다
  (`git config core.hooksPath .githooks` 로 훅 활성화 — 새 클론마다 1회).
- **생성/로컬 파일 커밋 금지:** `Library/ Temp/ Logs/ UserSettings/ Builds/`,
  `Assets/FluidStabilizer/Scenes/PlayScene.unity` (SceneBuilder 가 재생성),
  `Traces/` (녹화 주행 — 로컬 데이터; 안정되면 curated 홀드아웃 셋을 의도적으로
  force-add), `python/.venv/ python/runs/`, `*.pt *.onnx`. 추적 자산의
  `.meta` 파일은 **커밋함**.
- `.gitignore` 가 이미 이것들을 커버; 새 도구가 생성 출력을 쓰면 같은 변경에서
  `.gitignore` 에 추가.
