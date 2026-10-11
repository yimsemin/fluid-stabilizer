# 실험 계획 — "지금 어디에 있고, 다음은 무엇인가"

이 프로젝트의 **유일한 진행상황 추적기**이자 소유자용 지도. 의도와 로드맵은
[project-goals.md](project-goals.md), 밝혀진 사실은 [findings.md](findings.md),
설계 결정은 [decisions.md](decisions.md), 결과·교훈 요약은 [lessons.md](lessons.md)
에 있고 여기서는 링크만 건다.

## 현황 (2026-10-11)

phase-1 이 끝났다: 결정론적 시뮬레이션(물리 · 도로 · 액추에이터 · 슬로시 2종 · 센서)
· 베이스라인 · 평가기 · 재현성 도구 · 플레이 씬 · RL/BC/DAgger 경로 · 정책 서빙 ·
4-way 비교 · standalone 빌드가 모두 만들어졌고 EditMode 테스트 **153 통과 / 0 실패 /
16 스킵**이다.

**결과:** 학습·튜닝에 쓰지 않은 `final` 7 주행에서 ① 강체 114.4 L, ② 규칙기반
166~175 L, ③ 학습 정책 120~131 L — **① 강체가 이긴다** ([F13](findings.md)). ③ 은
합성 F8 조건의 주행 하나에서만 ② 를 앞섰고(F10), 교사를 늘리거나 DAgger 를 써도
강체를 못 이겼다(F11·F12).

**다음 작업:** 인과 MPC 검증 ([아래](#다음-작업--인과-mpc-검증), [D12](decisions.md)).

### 남은 한계

- **홀드아웃 `final` 7 주행은 소비됐다.** 추가 평가는 새 주행 녹화가 필요하다
  ([reproducibility.md](reproducibility.md)).
- **F5 표의 절대 수치는 재현 불가.** 원 수치는 보존하되 그렇게 표시했다.
- **④ 천장은 저대역 궤적이다.** 인과 제어기 전체의 상한이 아니다 (D12).
- **③ 을 얕은물에서 평가하지 않았다.** 진자에서 이미 강체를 못 이겼다.
- **BC > PPO.** PPO 미세조정이 클론을 무너뜨려 스필이 두 배가 됐다 (F10).

## 지금까지 만든 것

(상세 근거는 각 finding / decision 링크. 범위 기준은 [design-review.md](design-review.md) §20.)

| 층 | 상태 | 요약 |
|---|---|---|
| 결정론적 시뮬 코어 | ✅ | `ManualClock` · `DeterministicRandom`(PCG32) · `SimulationLoop` (600/200/100 Hz, 번들 지연, Gт 기록), 엔진프리 (D1) |
| 차량 + 도로 + 히브(heave) | ✅ | `VehicleModel` (3-DOF 스프렁 매스 + friction-circle), `RoadModel` (거리 함수 = grade/bank/거칠기/포트홀, 좌우 트랙 비대칭, 슬립 프록시) — D6·D7, [road-model.md](road-model.md) |
| 짐벌 액추에이터 | 🟡 | `GimbalActuator`: 전송지연, PD+I 위치 서보, 피크토크 포화, 마찰, rate/travel 한계, `Locked` 모드(강체 관절), I²t 권선열 모델 — D5. 백래시·기어박스 상세 보류 |
| 슬로시 모델 2종 | ✅ | `PendulumSloshModel` (등가역학 ROM, 축당 진자 1개) + `ShallowWaterSloshModel` (2-D Saint-Venant, ~48×28 격자) — D4. **브래킷 규율** (F3): ROM 으로 설계·학습, 얕은물로 검증 |
| 센서 층 | 🟡 | `SensorSuite`: 차량+탱크 IMU, 엔코더(양자화), 모터, 로드셀 4개, 옵션 레벨/CAN; 샘플당 노이즈 + 에피소드당 바이어스, 번들 지연 |
| 베이스라인 컨트롤러 | 🟡 | ① `Hold`(잠금), ② `SpecificForceAlignment` / `Pid`, 특권 `PrivilegedFullStateController`. `AxisLqr`/`LqrSloshController` 는 **보류**: 설계 모델이 실제 서보·지연 플랜트와 달라 공진 (F1). **D8·D10** lock-capable 액션(`LockGate`, 명시적 해제 요청). **MPC 는 아직 없다** |
| 평가 하네스 | ✅ | `Evaluator` + `ScenarioSet`(8개), `TraceEvaluator` (녹화 주행 × 베이스라인 × 슬로시 2종, 주행별 리터), `HindsightOptimizer` (저대역 천장, F5), `SweepRunner`, `DrivingPremiseSweepRunner` |
| 플레이 씬 · 조작 | ✅ | `SimulationRunner` · `HumanDriverInput` · `SimView` · `Hud` · `PlayControls`. 컨트롤 패널(백틱), 녹화/재생(`P`/`L`), 3분할 비교 + 판정 배너(`C`) — [operating-guide.md](operating-guide.md), [human-drive.md](human-drive.md) |
| RL / BC / DAgger | ✅ (줄기는 닫힘) | `FluidStabilizer.Rl` (`ObservationEncoder`, `RewardFunction`, `RlEnvironment`, `PolicyContract`, `ActionComposer`, `TrainingServer`), Python PPO · `bc_pretrain.py` · `dagger.py` · `serve_policy.py`, `MultiCornerRunner`, `DaggerRolloutRunner` — D9–D11, [rl.md](rl.md) |
| 재현성 · 데이터 | ✅ | `DriverTrace` v2(유효 설정 전체) · `datasets.tsv` split · 실행 증거 · 정책 실행 상태 · `EpisodeLog` — [reproducibility.md](reproducibility.md) |
| 도메인 랜덤화 | ⬜ (보류) | config 구조체만 있고 랜덤화기 미구현 |

**어셈블리** ([Assets/FluidStabilizer/README.md](../Assets/FluidStabilizer/README.md)):
`Contracts` / `Core` / `Controller` / `Rl` (모두 엔진프리) · `Sim` / `Sim.Editor` (Unity) · `Tests.EditMode`.

**검증 명령:**
```
"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile -
```
(Unity 에디터가 같은 프로젝트를 열고 있으면 실행되지 않는다. 배치는 한 번에 하나.)

## 다음 작업 — 인과 MPC 검증

**질문:** 현재 상태와 모델·액추에이터 한계만 아는 예측 제어(MPC)가 슬로시 주기 단위의
능동 감쇠로 강체(①)를 이길 수 있는가? ④ 천장은 0.1~0.3 Hz 궤적이라 이 능력을 대표하지
못하고, 기존 능동 제어기는 반응형이거나 모델 불일치(LQR)로 실패했으므로 **이 질문은
아직 답이 없다** ([D12](decisions.md)).

### 지킬 제약

- **미래 도로·운전자 입력을 쓰지 않는다.** 외란은 현재 측정값 유지(또는 감쇠)로 가정한다.
- 컨트롤러 코드는 `UnityEngine` 참조 0, `IController`. 완전 상태를 쓰는 1단계는
  `IPrivilegedController` 로 표시해 배포 불가임을 드러낸다.
- 브래킷 규율: 진자 ROM 으로 설계하고 얕은물로 검증한다 (F3). 한 모델의 결과를 다른
  모델로 추론하지 않는다.
- 평가 주행은 `datasets.tsv` 의 `train`/`validation` 뿐이다 (`final` 은 소비됨). 합성
  주행이 필요하면 별도 매니페스트를 만든다 (D11 관례).
- 모든 비교는 같은 주행에서 ① 강체·② 규칙기반·(저대역 천장)과 나란히, 주행별 리터와
  합계로 보고한다. 단일 실행을 결론으로 쓰지 않는다 (F12: 실행 간 분산이 컸다).

### 단계와 중단 기준

| 단계 | 내용 | 계속 조건 / 중단 |
|---|---|---|
| 0. 진단 ✅ | 기존 주행에서 강체 스필이 언제 나는가 — 사건 순간 vs 사건 뒤 잔여 슬로시 진동 | 사건 뒤 잔여 진동에서 스필이 상당 비중이면 계속. 사건 순간에 집중되면 능동 감쇠 여지가 작다 |
| 1. 오라클 MPC | 완전한 슬로시 상태, 내부 모델 = 시뮬레이션 모델(진자), 현재 가속 유지 가정, `GimbalActuator` 의 서보·지연·rate/torque/travel 포함, "잠금" 후보 포함 | train/validation 합계가 ① 대비 **10 % 이상 감소**하면 계속. 미만이거나 어느 주행에서 크게 나빠지면 중단 |
| 2. 상태 추정 | IMU · 로드셀 · 엔코더 (노이즈·바이어스·지연 포함)로 슬로시 상태 추정 | 1단계 이득의 절반 미만이면 중단 |
| 3. 얕은물 검증 | 진자로 설계한 MPC 를 얕은물에서 채점. 통과하면 **새 홀드아웃 주행을 녹화**해 최종 확인한다 | 진자에서만 이기면 결론에 그렇게 명시하고 멈춘다 |
| 4. (선택) 학습 | MPC 증류 · 잔차 학습 — 3단계를 통과했을 때만 | — |

### 단계 0 결과 (2026-10-11)

도구: `SpillDiagnostics` / 메뉴 `FluidStabilizer ▸ MPC step 0 — spill diagnostics`.
강체(Hold)로 train 6 + validation 2 주행(합 162.8 L)을 재생해, 스필을 "탱크 좌표계
수평 비력(specific force) ≥ 임계값인 순간(사건 중)"과 "마지막 사건 뒤 ≤1 s / >1 s"로
나눴다.

| 임계값 | 사건 중 | 뒤 ≤1 s | 뒤 >1 s | 사건 중인 시간 |
|---|---|---|---|---|
| 2 m/s² | 93 % | 7 % | 0 % | 84 % |
| 4 m/s² | 77 % | 18 % | 5 % | 60 % |
| 6 m/s² | 63 % | 27 % | 10 % | 49 % |
| 8 m/s² | 51 % | 38 % | 11 % | 44 % |

- 해석(추정): 스필의 절반 이상이 강한 사건 *중*에 난다. 다만 강한 사건(≥6 m/s²) 뒤
  잔여 진동에서도 27~49 % 가 나므로 계속 조건은 충족한다. 사건 *중* 스필은 감쇠가 아니라
  기울여 정렬해야 줄어든다 — 1단계의 비용 함수가 틸트-슬로시 결합을 포함해야 하는 이유.
- 주행 편차가 크다: validation `…232628` 은 6 m/s² 에서 89 % 가 사건 뒤, 60 % 가 1 s 뒤
  (자유 진동이 지배). 나머지 7 주행은 사건 중이 60~80 %.
- 한계: 임계값 분할은 조잡하다. 사건 뒤 ≤1 s 는 슬로시 주기(0.98~1.43 s) 안이라 같은
  사건의 연장일 수 있다.

### 설계 메모 (이미 확인된 사실만)

- **슬로시 주파수·감쇠** (ROM 공식 계산값, 탱크 1.2×0.7 m, fill 0.62): 피치 0.70 Hz
  (주기 1.43 s), 롤 1.02 Hz (0.98 s), 감쇠비 0.08. 짐벌 대역폭 4 Hz, 지연 15 ms +
  센서 5 ms, 속도 한계 3.5 rad/s, 이동 ±30°.
- **틸트는 슬로시를 여기한다** (중력벡터 회전 · Euler 항, F1). 비용 함수에 이 결합이
  들어가야 하고 "아무것도 안 함(잠금)" 후보가 항상 있어야 한다.
- **LQR 이 실패한 이유는 모델 불일치다** — 설계 모델(이상 속도 추종기)이 실제 서보 +
  지연 + 각도 명령 플랜트와 달랐다. MPC 내부 모델에 액추에이터 동역학을 넣는다.
- **액추에이터 권한 증가는 도움이 되지 않았다** (F1: 이동 범위·속도·토크·대역폭 2배에도
  36~42 L). 병목은 권한이 아니다.
- **스필 비용은 불연속에 가깝다** (가장자리 넘기 전 0). 여유 높이(freeboard) 소프트
  제약 등 비용 설계가 핵심이다.
- **잠금은 이산 결정이다** (0.5° 이내에서만 체결, 최소 유지 틱). 후보 비교로 처리한다.
- 얕은물은 에피소드당 약 4.2 s 라 내부 모델로 쓸 수 없다.
- 재사용: `PendulumSloshModel` · `GimbalActuator` · `SimulationLoop` ·
  `TraceEvaluator` · `ComparisonRunner` 레인 · `PrivilegedObservation`
  (`SloshPitchAngle`, `SloshPitchRate`, `SpecificForceTankFrame` …).
- 해결되지 않은 선택: 최적화 방식(샘플링 기반 MPPI vs iLQR)은 1단계 시작 전에 정한다.

결과는 [findings.md](findings.md) 에 F14 로 기록하고, 문서는 같은 변경에서 갱신한다.

## 앞으로 할 수 있는 것

### 닫힘 — 재개 조건을 만족할 때만 다시 연다

| 줄기 | 닫은 근거 | 재개 조건 |
|---|---|---|
| 맨땅 PPO · PPO 미세조정 | F9·F10 | 액션 공간·보상이 바뀔 때 |
| BC 교사 다양화 | F11 | 활성/유휴를 관측으로 예측할 수 있다고 확인될 때 |
| DAgger · 해제 편향 | F12 | 위와 같음 |
| 프리뷰(미래 도로 정보) | D12 — 원칙상 제외 | 소유자가 원칙을 바꿀 때 |
| 홀드아웃 `final` 7 주행 | F13 으로 소비됨 | 새 주행을 녹화해 새 `final` 로 등록 |

### 열림 — 소유자가 선택한다

| 후보 | 하는 일 | 결정 필요 |
|---|---|---|
| 전제 변경 — 수동 해법 기준선 | 탱크 내부 배플·깊이를 슬로시 모델에 넣고 강체 대비 스필 감소를 측정 | 슬로시 모델 확장 범위 |
| 활성 구간 감지기 | 저주파 지속 외란 구간만 능동 제어, 그 밖에서는 항상 강체 | 합성 주행 40 개 이상 생성 |

### 보류 — 필요가 생길 때

도메인 랜덤화(활성 학습 재개 시) · 액추에이터·센서 정밀도 상향(백래시·기어박스, 데이터시트
보정) · 학습기 GUI · 실제 하드웨어 / ROS 2 (MPC 또는 열림 후보가 새 홀드아웃에서
강체를 이긴 뒤에 판단. 아키텍처는 이 분리를 이미 전제한다 — [project-goals.md](project-goals.md)).
