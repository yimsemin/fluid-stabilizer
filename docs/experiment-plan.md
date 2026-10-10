# 실험 계획 — "지금 어디에 있고, 다음은 무엇인가"

이 프로젝트의 **유일한 진행상황 추적기**이자 소유자용 지도. 로드맵의 큰 그림은
[project-goals.md](project-goals.md), 밝혀진 사실은 [findings.md](findings.md),
설계 결정은 [decisions.md](decisions.md) 에 있고 여기서는 링크만 건다.

## 한 문단 현황 (2026-09-11 — phase-1 종료)

결정론적 시뮬레이션 전체가 만들어지고 테스트됐다: 물리 · 도로(좌우 트랙 + 슬립) ·
액추에이터 · 슬로시 2종 · 센서 · 베이스라인 · 평가기 · 재현성/데이터셋 · 플레이 씬 ·
RL 브릿지 · 정책 계약 · 3분할 비교 · 에피소드 로깅 · standalone 빌드.
**모든 층이 만들어졌고, 작동하고, 왜 그렇게 작동하는지 설명된다.**

**헤드라인 — ① 강체 탱크가 이긴다.** 소유자의 녹화 주행에서는 반응형 틸트(F1·F4)도,
완전한 후행지식(F5)도, lock-capable 후행지식(F6)도 기계적으로 잠근 탱크를 못 이긴다.
인과적 센서전용 정책(③)이 비인과적 천장(④)을 넘을 수 없으므로 그 결론은 방어된다.
**이건 실패가 아니라 결과다.**

**③ 은 학습한다 (F10).** 합성 F8 운영점(매끄러운 노면의 지속 코너링)에서 교사 →
BC → 배포 파이프라인이 끝까지 돌고, ③ 이 규칙기반을 **51 % 앞선다**
(12.4 vs 25.2 L). 강체(7.9 L)에는 여전히 진다. 이것으로 "배관은 있으나 아무것도
학습한 적 없던" 마지막 공백이 닫혔다.

### 이번 라운드에서 밝힌 것 (F7~F10) — 요약

| | 무엇 | 왜 중요한가 |
|---|---|---|
| **재현성** | trace v2(유효 설정 전체) · `datasets.tsv` split · 실행 증거 · 정책 실행 상태 | F5 표가 **재현되지 않는다**는 걸 이걸로 알았다 (옛 주행이 설정을 기록 안 함 + 그 사이 D7 물리 추가) |
| **F7** | 탐색기는 정상 (알려진 개선을 81~102 % 회수) | F5/F6 는 탐색 실패가 아니다 |
| **F8** | 프로젝트 최초의 게이트 PASS (지속 코너링 · 도로 off, 두 모델) | 이길 수 있는 조건이 존재한다. 노면 **광대역성**이 진짜 전제 |
| **F9** | 잔차 액션 공간이 **절벽**이라 학습 신호가 0 | 실패 원인이 하이퍼파라미터가 아님을 특정 |
| **F10** | 명시적 잠금 액션으로 수정 → ③ 이 학습 | **액션 공간 설계가 학습 가능성을 결정한다** |

### 남은 한계 (정직하게)

- **홀드아웃은 등록됐지만 아직 평가하지 않았다.** `final` 7 주행(2026-10-10 녹화)은
  봉인 상태이고, 어떤 결론도 아직 거기서 확인되지 않았다
  ([reproducibility.md](reproducibility.md)).
- **F5 표의 절대 수치는 재현 불가.** 원 수치는 보존하되 그렇게 표시했다.
- **③ 의 일반화 미검증.** 한 주행짜리 BC 클론이다.
- **BC > PPO.** 미세조정이 스필을 두 배로 악화시켰다. 보상은 스필과 잘 정렬돼
  있으므로(항별 분해 확인) PPO 가 클론을 무너뜨린 것이다.

### phase-1 종료 판정

[project-goals.md](project-goals.md) 의 목적은 "파이프라인 전체를 직접 만들며
이해하는 것" 이고, **그 기준으로 완료됐다.** 더 이어갈 경우의 선택지는
[아래 "다음에 할 수 있는 것"](#다음에-할-수-있는-것) 참고.


## 지금까지 만든 것

(phase-1 범위는 [design-review.md](design-review.md) §20 기준. 상세 근거는
각 finding / decision 링크.)

| 층 | 상태 | 요약 |
|---|---|---|
| 결정론적 시뮬 코어 | ✅ | `ManualClock` · `DeterministicRandom`(PCG32) · `SimulationLoop` (600/200/100 Hz, 번들 지연, Gт 기록), 엔진프리 (D1) |
| 차량 + 도로 + 히브(heave) | ✅ | `VehicleModel` (3-DOF 스프렁 매스 + friction-circle), `RoadModel` (거리 함수 = grade/bank/거칠기/포트홀, 기본 off, `Severity` 손잡이) — D6, [road-model.md](road-model.md) |
| 짐벌 액추에이터 | 🟡 | `GimbalActuator`: 전송지연, PD+I 위치 서보, 피크토크 포화, 마찰, rate/travel 한계, `Locked` 모드(강체 관절), I²t 권선열 모델(피크→연속 토크 디레이트) — D5. 백래시·기어박스 상세 보류 |
| 슬로시 모델 2종 | ✅ | `PendulumSloshModel` (등가역학 ROM, 축당 진자 1개) + `ShallowWaterSloshModel` (2-D Saint-Venant, ~48×28 격자, Rusanov FV) — D4. **브래킷 규율** (F3): ROM 으로 학습, 얕은물로 검증 |
| 센서 층 | 🟡 | `SensorSuite`: 차량+탱크 IMU, 엔코더(양자화), 모터, 로드셀 4개, 옵션 레벨/CAN; 샘플당 노이즈 + 에피소드당 바이어스, 번들 지연 |
| 베이스라인 컨트롤러 | 🟡 | ① `Hold`(잠금), ② `SpecificForceAlignment` / `Pid` / `PrivilegedFullState`. `AxisLqr`/`LqrSloshController` 는 만들었으나 **보류**(공진). **F4**: 강체 Hold 를 반응형 틸트로 못 이김. **D8** lock-capable 하이브리드 액션(`LockGate`) — 능동 컨트롤러가 필요없을 때 기계적으로 잠김 |
| 평가 하네스 | ✅ | `Evaluator` + `ScenarioSet`(8개) + 텍스트 리포트. `TraceEvaluator` (녹화 주행 × 베이스라인 × 슬로시 2종, 주행별 리터). `HindsightOptimizer` (feasibility 천장, F5) |
| 플레이 씬 (수동 주행) | ✅ | `FluidStabilizer.Sim`: `SimulationRunner` · `HumanDriverInput`(키보드+게임패드) · `SimView` · `Hud` · `PlayControls` · `Frames`. `SceneBuilder` → `PlayScene.unity`. 트레이스 녹화/재생(`P`/`L`), `ComparisonRunner`(`C`), `RoadRibbon` — [human-drive.md](human-drive.md) |
| RL 경로 | 🟡 | C# 쪽 완료+테스트: `FluidStabilizer.Rl` (`ObservationEncoder`, `PrivilegedEncoder`, `RewardFunction`, `RlEnvironment` +잔차 +lock-capable, `TrainingServer` TCP). Python: 비대칭 크리틱 PPO + BC 웜스타트 + `serve_policy.py` + `--resume` 체크포인트 재개. **맨땅 PPO 는 학습 못 함** (F1/F4 절벽); **긴 잔차 RL 은 F6 게이트 때문에 보류** — [rl.md](rl.md) |
| 에피소드 로깅 | ✅ (2026-08-30) | `EpisodeLog` (engine-free) → `Logs/episodes.csv` 에 에피소드당 한 줄(운영점 + 결과 지표). `TraceEvaluator`·`Evaluator`·`ComparisonRunner` 배선 |
| 도메인 랜덤화 | ⬜ (보류) | config 구조체는 있음, 랜덤화기 미구현. 활성 RL 학습 없이는 가치 낮아 보류 |
| 녹화 주행 (녹화 + 재생) | ✅ | `DriverTrace` v2 — 실제 disturbance 데이터 + 유효 설정 전체 기록 ([reproducibility.md](reproducibility.md)) |
| feasibility 천장 | ✅ | `HindsightOptimizer` — **게이트 미통과** (F5), lock-capable + 11 운영점 재실행도 미통과 (F6). `SweepRunner` |
| 3분할 비교 + 판정 | ✅ | `ComparisonRunner` (`C`) — 같은 녹화 주행 ①②③ 나란히, 레인별 리터, 완료 시 한 줄 판정 |

**어셈블리** ([Assets/FluidStabilizer/README.md](../Assets/FluidStabilizer/README.md)):
`Contracts` / `Core` / `Controller` / `Rl` (모두 엔진프리) · `Sim` / `Sim.Editor` (Unity) · `Tests.EditMode`.

**검증:** EditMode 테스트 **152 통과 / 0 실패 / 16 스킵** (스킵 = `[Explicit]` 진단).
```
"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile -
```

## 초안 (수직 슬라이스) — ✅ 완료 2026-08-29

파이프라인 전체를 *한 번* 대충 만든다 (어느 한 층을 완벽하게 만들기 전에).
성공 = "처음부터 끝까지 돌아가고, 셋을 나란히 볼 수 있다", **"③ 이 이긴다"가
아님.** 각 단계는 자체 완결 커밋 하나 + 짧은 데모 + [vision-check.md](vision-check.md) 항목.

| # | 단계 | 상태 | 메모 |
|---|---|---|---|
| A | **실험 프로파일** — fill/도로/운전자/에피소드/슬로시를 담은 `experiment.txt` 하나를 모든 러너가 읽음 | ✅ | `ExperimentProfile` + `ExperimentProfileStore`. 플레이 씬 + RL 서버에 배선. 나중 컨트롤 패널의 데이터 모델 |
| B | **잔차 RL 배관** — `RlEnvironment` 가 `베이스라인 행동 + 정책 보정` 합성 (베이스라인 = 잠금 Hold 또는 SFA) | ✅ | `baselineFactory` + `residualRateScaleRadPerSec` ctor 인자; 관절속도 공간에서 합성; `-rlBaseline {none\|hold\|sfa\|pid} -rlResidualFrac`. Python 학습기 변경 없음. `ResidualRlTests` (4) |
| C | **BC 웜스타트** — hindsight 궤적으로 정책망 사전학습 후 짧은 PPO | ✅ | `BcExportRunner` → `Logs/bc/dataset.csv` (23.8k 틱, 6 주행); `python/bc_pretrain.py` → `runs/bc/actor.pt`; `train_ppo.py --init-actor`. **메모:** F5 hindsight 최적해가 거의 무동작이라 BC 가 배우는 건 "얌전히" — 진짜 anti-cliff 프라이어이지 이기는 전략 아님. `-rlTrace all` 이 같은 6 주행으로 학습 |
| D | **정책을 별도 프로세스로** — `python/serve_policy.py` 가 브릿지로 추론 서빙; Unity `RemotePolicyController : IController` 가 클라이언트 | ✅ | "ONNX 를 유니티에 굽기"에서 수정 (project-goals.md). `StabilizerChoice.LearnedPolicy` / 키 `4`; `PolicyEvalRunner`(메뉴). end-to-end 검증: 6 주행에서 BC 정책이 24–30 L vs Hold 19–22 — **① 강체가 이김** (F5 예측대로). `RemotePolicyControllerTests` (3) |
| E | **앱 안의 3-way 비교** — `ComparisonRunner` 가 ①②③ 나란히; 플레이 씬 HUD 가 현재 `experiment.txt` 프로파일 + 레인별 리터 표시 | ✅ | 기본 레인 `Off / PID / LearnedPolicy`; ③ 레인은 `RemotePolicyController` (서버 없으면 "(offline → Hold)"). `C` 토글. `ComparisonRunnerTests` (2) |
| F | **standalone 빌드** — 메뉴 항목 하나 → 자기완결 앱 | ✅ | `BuildRunner` (메뉴 + `-executeMethod`): 플레이 씬 재생성 후 `BuildPipeline.BuildPlayer` → `Builds/`. 검증: 104 MB 유니버설 `fluid-stabilizer.app` |

**초안 완성** — 파이프라인 전체가 처음부터 끝까지 돈다: 녹화 → hindsight 천장 →
BC 데이터셋 → BC 사전학습 → PPO 미세조정 → `serve_policy.py` → Unity ①②③ 비교
→ standalone 앱.

## 초안 이후 — 3묶음 계획 (소유자 중간 점검 2026-08-29~30)

소유자 열린질문 답: (1) 정직한 초안 받아들임 + "어떤 조건에서 학습 모델이
이기나" 찾기도 학습. **주 목적 = 학습·적용이 정확히 된 뒤 가상세계 조건을
현실에 밀접하게.** (2) **껍데기 다듬기가 급함** (자립 운영). (3) **시연이 1순위.**

### 묶음 1 — 조작 껍데기 ✅ (2026-08-30)
- 1a HUD 가독성 (`SimGui` 패널+그림자)
- 1b **인게임 컨트롤 패널** (백틱 키) — 도로 프리셋 7종 + 슬라이더 + [적용]→씬 재시작
- 1c **정책 서버 시작/중지/상태** 버튼
- 1d **3분할 화면 비교** (`C`) — 트럭마다 자기 뷰·자기 도로 (동일 seed)
- 1e `docs/operating-guide.md` (한글 조작 가이드)

### 묶음 2 — 물리 현실성 ✅ (2026-08-30)
- **2a ✅** 좌우 바퀴 트랙 비대칭 ("왼쪽 돌맹이 → 들썩") — `RoadInput.SurfaceHeightLeft/Right`,
  `(L−R)` → body roll. `RoadConfig.WheelTrackRoughnessFrac`. [road-model.md](road-model.md), D7
- **2b ✅** 미끄러운 노면 lumped 슬립 프록시 — 접지력 감소 + under-response/grab + fishtail.
  `VehicleState.TyreSlip` HUD 표시. D7
- **2c ✅ (2026-08-30) — 결과: 이기는 조건 없음 (finding [F6](findings.md)).**
  하이브리드 "보정할 때만 풀림" 액션을 만들고 (**D8** lock-capable, `LockGate`),
  F5 게이트를 11개 운영점(fill 0.50~0.62 × 도로강도 × grip)에서 재실행.
  lock-capable 이 F5 의 구조적 핸디캡(처지는 서보 25 L vs 잠금 19 L)은 없앴지만
  — 강체가 조금이라도 넘치는 모든 fill 에서 hindsight 가 회복하는 건 스필의
  1~2% 뿐. 승리 가능 구간이 없다. 도구: `SweepRunner`.

### 묶음 3 — 학습 정확성 ✅ (2026-09-10 마무리; 아래는 당시 기록)

> **2026-09-10 갱신:** 2c 게이트는 여전히 소유자의 녹화 주행에서 통과하지 못한다.
> 그러나 합성 F8 운영점에서는 통과했고([F8](findings.md)), 액션 공간을 고친 뒤
> ③ 이 실제로 학습했다([F10](findings.md)). 아래 "긴 RL 을 시작하지 않았다" 는
> 2026-08-30 시점의 기록이다.
잔차 RL 배선(B)·BC 웜스타트(C)·lock-capable 액션(D8)·`train_ppo.py --resume`
모두 준비됨. 그러나 **2c 게이트가 통과하지 못했다** ([F6](findings.md)) — 긴
잔차 RL 학습은 시작하지 않았다 (CLAUDE.md 하드룰: hindsight 가 강체를 이기는 걸
보이기 전까지 RL 없음).

**소유자 결정 (2026-08-30): (a) 정직한 헤드라인 수용.** 완료:
- **에피소드 로깅 ✅** — `EpisodeLog` → `Logs/episodes.csv` (평가·비교 러너 배선).
- **데모 다듬기 ✅** — 3분할 판정 배너, `docs/demo-guide.md` (시연용 참고 문서).
- **도메인 랜덤화 — 보류** (활성 RL 학습 없이는 가치 낮음).

긴 잔차 RL 을 다시 하려면 소유자가 전제를 바꿔야 함: (b) 탱크 형상 (더 깊게/넓게),
또는 (c) 강체가 1~5 L 만 스필하는 얌전한 홀드아웃 주행 녹화 후 `SweepRunner`
/ `HindsightEvalRunner` 재실행. F6 "게이트 판정" 절 참고.

## step-back 세션의 3가지 우려 — 해결됨

### 1. "주행 코스" / 경로가 없다
**결정: 명시적 route/waypoint/맵 시스템 없음.** AI 는 운전하지 않는다. 경로추종
서브시스템은 이해에 보탬 없이 복잡도만 늘린다. "한 번의 주행" 단위 =
**녹화된 인간 트레이스 + 고정 도로 seed** — 완전 결정론·재현가능.
도로 곡률 ↔ 조향 ↔ 편경사(superelevation) 물리 결합은 첫 3-way 비교 *이후*,
도로 disturbance 가 충분히 흥미롭지 않을 때만 고려할 정밀도 업그레이드.

### 2. "합성 시나리오 6개 × seed 6개 평균 스필"이 맞는 벤치마크인가?
**결정: 아니다 — 헤드라인 벤치마크는 소유자 자신의 녹화 주행.** 홀드아웃 실주행
3~5개, 슬로시 2종, 주행별 리터. 1차 지표: 스필 리터. 2차: rim 근접 시간, 슬로시
운동에너지 피크, 액추에이터 노력/열부하. 합성 시나리오는 도메인 랜덤화 +
빠른 회귀 신호용으로 유지 (헤드라인 아님). design-review §17 과 일치.

### 3. F4 이후에도 RL 이 맞는 접근인가?
**결정: 천장을 먼저 만들고, 잔차 RL 로 전환.** 첫 RL 실행이 이미 주된 교훈(절벽)을
줬다. 맨땅 PPO 재실행은 새로 배울 게 없다. 대신: (a) 천장이 과제가 이길 수 있는지
+ 얼마나 이길 수 있는지 확립; (b) 잔차 RL — 베이스라인 위 정책 — 은 design-review
가 명시한 방법이자 절벽의 직접 해법, hindsight 최적 궤적으로 BC 웜스타트.
커리큘럼(체크포인트 재개 필요)은 보조 전술이지 주 계획 아님.

## 다음에 할 수 있는 것

phase-1 은 닫혔다. 이어서 한다면 셋 중 하나이고, **아무것도 안 하는 것도 정당한
선택**이다 — 목적은 이미 달성됐다.

### A. ③ 을 더 밀어올리기 (BC 쪽, 수익 체감 구간)

> **2026-10-11 갱신 — DAgger·해제 안정화도 실행됨 ([F12](findings.md)).** 평균 손실은
> 줄었지만 강체를 못 이겼고 분산이 크다. 남은 후보는 주행 수 확대뿐이다.
>
> **2026-10-10 갱신 — A1 교사 다양화 실행됨 ([F11](findings.md)).** 12 합성 주행으로
> 늘리자 ③ 은 학습 주행에서조차 강체보다 나빴다(65.9 vs 39.7 L). 라벨 버그 1건은
> 고쳤다. 아래 "교사 다양화" 는 더 늘려도 효과가 없을 가능성이 크다; 남은 후보는
> DAgger 와 해제 결정의 안정화다.

F10 이 가리키는 유효한 방향은 PPO 가 아니라 BC 다:
- **교사 다양화** — 지금은 주행 하나의 클론이라 일반화가 검증되지 않았다.
  F8 조건에서 주행 4~8 개를 만들고 각각의 천장 궤적으로 학습.
- **잘림 줄이기** — 교사 잔차의 29.5 % 가 전권 밴드에서도 액션 한계에 잘린다.
  액션 파라미터화를 바꾸면 클론 품질이 올라갈 여지가 있다.
- **DAgger 류 온-폴리시 교정** — 클론의 오차 누적이 천장과의 격차의 주원인이다.

**하지 않을 것: 맨땅 PPO, 현재 형태의 미세조정.** F10 에서 둘 다 근거가 없다는
게 측정됐다.

### B. 홀드아웃 최종 평가 (녹화 완료, 평가 대기)

`final` 7 주행이 봉인돼 있다. **어떤 결론도 아직 미사용 주행에서 확인되지 않았다.**
A 를 마친 뒤 한 번만 평가한다. validation 2 주행은 튜닝에 써도 된다.

### C. 실제 하드웨어 / ROS 2

별개의 큰 결정. [project-goals.md](project-goals.md) 의 최종 아키텍처가 이미
이 분리를 전제로 설계돼 있다(Unity = plant, 정책 = 별도 프로세스). 지금 판단할
일은 아니다.
