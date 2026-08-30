# 실험 계획 — "지금 어디에 있고, 다음은 무엇인가"

이 프로젝트의 **유일한 진행상황 추적기**이자 소유자용 지도. 로드맵의 큰 그림은
[project-goals.md](project-goals.md), 밝혀진 사실은 [findings.md](findings.md),
설계 결정은 [decisions.md](decisions.md) 에 있고 여기서는 링크만 건다.

## 한 문단 현황 (2026-08-29)

결정론적 시뮬레이션 전체가 만들어지고 테스트됨(물리, 도로, 액추에이터, 슬로시
모델 2종, 센서, 베이스라인, 평가기, 플레이 씬, RL 브릿지). findings **F1~F4** 로
평평한 도로에서 **강체로 잠근 탱크는 반응형 틸트 제어로 거의 이길 수 없다**는 걸
안다 — 이기려면 *예측(anticipation)* 이 필요하고, 그게 학습 정책 ③ 이 존재하는
이유. 첫 RL 실행이 반대편을 확인해줬다: 맨땅 PPO 는 예측 정책을 못 찾는다 (무작위
탐색 짐벌 동작이 슬로시를 펌핑해 스필 — "F1/F4 절벽").

그다음 **녹화 주행**과 **feasibility 천장**을 만들어 돌렸고, **결정 게이트는
통과하지 못했다** (finding **F5**): 완전한 후행지식 + 실제 액추에이터 제약
하에서도 최적 짐벌 궤적이 소유자의 실주행에서 기계적으로 잠근 탱크보다 *더* 스필.

**소유자 결정 (2026-08-29) — 초안 먼저, 판단은 나중.** 지금 전제를 다시
튜닝하려고 멈추는 대신, **처음부터 끝까지 완성된 초안**을 먼저 만든다 —
녹화 → 잔차 RL(BC 웜스타트) → 정책 서빙 → 앱 내 3-way 비교 → standalone 빌드 —
*강체/규칙기반이 더 나은 걸로 밝혀지더라도.* 프로젝트의 요점은 파이프라인 전체를
만들고 이해하는 것이지 ③ 이 이긴다는 걸 증명하는 게 아니다. ③ 이 이기는지는
*발견*이다. F5 전제 조정은 초안이 존재한 *다음*, 돌아가는 전체를 다듬는 작업으로
한다. 모든 숫자는 이제 편집 가능한 파일 하나(`experiment.txt` /
[`ExperimentProfile`](../Assets/FluidStabilizer/Core/Sim/ExperimentProfile.cs))
에 있어서 운영점(operating point)을 나중에 싸게 바꿀 수 있다.

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
| 베이스라인 컨트롤러 | 🟡 | ① `Hold`(잠금), ② `SpecificForceAlignment` / `Pid` / `PrivilegedFullState`. `AxisLqr`/`LqrSloshController` 는 만들었으나 **보류**(공진). **F4**: 강체 Hold 를 반응형 틸트로 못 이김 |
| 평가 하네스 | ✅ | `Evaluator` + `ScenarioSet`(8개) + 텍스트 리포트. `TraceEvaluator` (녹화 주행 × 베이스라인 × 슬로시 2종, 주행별 리터). `HindsightOptimizer` (feasibility 천장, F5) |
| 플레이 씬 (수동 주행) | ✅ | `FluidStabilizer.Sim`: `SimulationRunner` · `HumanDriverInput`(키보드+게임패드) · `SimView` · `Hud` · `PlayControls` · `Frames`. `SceneBuilder` → `PlayScene.unity`. 트레이스 녹화/재생(`P`/`L`), `ComparisonRunner`(`C`), `RoadRibbon` — [human-drive.md](human-drive.md) |
| RL 경로 | 🟡 | C# 쪽 완료+테스트: `FluidStabilizer.Rl` (`ObservationEncoder` 프레임스택, `PrivilegedEncoder` 비대칭 크리틱, `RewardFunction`, `RlEnvironment`, `TrainingServer` TCP). Python 쪽(`python/`) end-to-end 실행됨. **맨땅 PPO 는 학습 못 함** (F1/F4 절벽) — [rl.md](rl.md) |
| 도메인 랜덤화 + 에피소드 로깅 | ⬜ | config 구조체는 있음, 랜덤화기+로깅 미구현 |
| 녹화 주행 (녹화 + 재생) | ✅ | `DriverTrace` — 실제 disturbance 데이터 + 홀드아웃 평가 조건 |
| feasibility 천장 | ✅ | `HindsightOptimizer` — **게이트 미통과** (F5) |

**어셈블리** ([Assets/FluidStabilizer/README.md](../Assets/FluidStabilizer/README.md)):
`Contracts` / `Core` / `Controller` / `Rl` (모두 엔진프리) · `Sim` / `Sim.Editor` (Unity) · `Tests.EditMode`.

**검증:** EditMode 테스트 **113 통과 / 0 실패 / 8 스킵** (스킵 = `[Explicit]` 진단).
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

### 묶음 2 — 물리 현실성 (진행 중)
- **2a ✅** 좌우 바퀴 트랙 비대칭 ("왼쪽 돌맹이 → 들썩") — `RoadInput.SurfaceHeightLeft/Right`,
  `(L−R)` → body roll. `RoadConfig.WheelTrackRoughnessFrac`. [road-model.md](road-model.md), D7
- **2b ✅** 미끄러운 노면 lumped 슬립 프록시 — 접지력 감소 + under-response/grab + fishtail.
  `VehicleState.TyreSlip` HUD 표시. D7
- **2c ⬜** "어떤 조건에서 학습 모델이 ①②를 이기나" 탐색 — F5 전제 조정의 확장판.
  후보: freeboard 낮추기 (잠근 Hold 가 스필 문턱 근처에), 도로/운전 강도,
  하이브리드 "보정할 때만 풀림" 액션 (잔차 RL 베이스라인이 19 L 잠근 탱크가 되도록).
  Preview 단독은 해결책 아님 (hindsight 는 이미 완벽한 look-ahead).

### 묶음 3 — 학습 정확성 ⬜
잔차 RL 제대로 배선(B 완료) + BC 웜스타트(C 완료) + **긴 학습**. 2c 와 짝 —
조건을 바꿔가며 ③ 이 이기는 지점을 찾는다. 그다음 도메인 랜덤화 + 에피소드 로깅.

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
