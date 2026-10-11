# 실험 계획 — "지금 어디에 있고, 다음은 무엇인가"

이 프로젝트의 **유일한 진행상황 추적기**이자 소유자용 지도. 의도와 로드맵은
[project-goals.md](project-goals.md), 밝혀진 사실은 [findings.md](findings.md),
설계 결정은 [decisions.md](decisions.md), 결과·교훈 요약은 [lessons.md](lessons.md)
에 있고 여기서는 링크만 건다.

## 현황 (2026-10-12)

phase-1 이 끝났다: 결정론적 시뮬레이션(물리 · 도로 · 액추에이터 · 슬로시 2종 · 센서)
· 베이스라인 · 평가기 · 재현성 도구 · 플레이 씬 · RL/BC/DAgger 경로 · 정책 서빙 ·
4-way 비교 · standalone 빌드가 모두 만들어졌고 EditMode 테스트 **155 통과 / 0 실패 /
16 스킵**이다.

**결과:** 학습·튜닝에 쓰지 않은 `final` 7 주행에서 ① 강체 114.4 L, ② 규칙기반
166~175 L, ③ 학습 정책 120~131 L — **① 강체가 이긴다** ([F13](findings.md)). ③ 은
합성 F8 조건의 주행 하나에서만 ② 를 앞섰고(F10), 교사를 늘리거나 DAgger 를 써도
강체를 못 이겼다(F11·F12).

**인과 MPC (F14):** 완전 상태 + 정확한 모델의 오라클 MPC 도 8 주행 합 **−5 ~ −8 %**
(강체 162.8 L)에 그쳐 게이트(−10 %)를 못 채웠다 ([F14](findings.md)). 이득은 사건 뒤 자유
진동이 지배하는 주행 하나에 집중되고 나머지는 강체와 구분되지 않는다. 인과 MPC 줄기는
종료했다 ([D13](decisions.md)).

**다음 작업: 없음 (소유자 선택 대기).** 열린 후보는 [아래](#앞으로-할-수-있는-것).

### 남은 한계

- **홀드아웃 `final` 7 주행은 소비됐다.** 추가 평가는 새 주행 녹화가 필요하다
  ([reproducibility.md](reproducibility.md)).
- **F5 표의 절대 수치는 재현 불가.** 원 수치는 보존하되 그렇게 표시했다.
- **④ 천장은 저대역 궤적이다.** 인과 제어기 전체의 상한이 아니다 (D12). 슬로시 주기 단위
  능동 감쇠는 오라클 MPC 로 따로 시험했다 (F14).
- **MPC 는 진자 모델·8 주행에서만 평가했다.** 설정을 같은 8 주행으로 골랐고(낙관 편향),
  얕은물과 새 홀드아웃 평가는 하지 않았다 — 1단계 미달로 해당 조건에 이르지 않았다.
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
| 베이스라인 컨트롤러 | 🟡 | ① `Hold`(잠금), ② `SpecificForceAlignment` / `Pid`, 특권 `PrivilegedFullStateController`. `AxisLqr`/`LqrSloshController` 는 **보류**: 설계 모델이 실제 서보·지연 플랜트와 달라 공진 (F1). **D8·D10** lock-capable 액션(`LockGate`, 명시적 해제 요청). `OracleMpcController`(특권 MPPI, F14) 는 상한 시험용이며 배포 불가 |
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

## 인과 MPC 검증 — 종료 (F14, D13)

**질문:** 현재 상태와 모델·액추에이터 한계만 아는 예측 제어(MPC)가 슬로시 주기 단위의
능동 감쇠로 강체(①)를 이길 수 있는가?

**답:** 완전 상태와 시뮬레이션과 같은 내부 모델이라는 가장 유리한 조건(오라클)에서도
강체 대비 −5 ~ −8 % 였다. 계속 기준(−10 %)에 못 미쳐 단계 2~4 는 해당되지 않는다.

| 단계 | 내용 | 결과 |
|---|---|---|
| 0. 진단 | 강체 스필이 사건 순간인가 사건 뒤 잔여 진동인가 | ✅ 사건 뒤 잔여 진동 비중이 상당 (아래) |
| 1. 오라클 MPC | 완전 슬로시 상태, 내부 모델 = 시뮬레이션의 서보·진자, 현재 외란 유지 가정, 잠금 후보 포함. 기준: 합계 ① 대비 −10 % 이상 | ⛔ −4.9 ~ −7.6 % ([F14](findings.md)) |
| 2. 상태 추정 | IMU · 로드셀 · 엔코더로 슬로시 상태 추정 | 해당 없음 — 완전 상태에서 미달 |
| 3. 얕은물 검증 | 진자로 설계한 MPC 를 얕은물에서 채점 | 해당 없음 |
| 4. 학습 | MPC 증류 · 잔차 학습 | 해당 없음 |

### 단계 0 결과

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

### 단계 1 결과

`OracleMpcController` (MPPI, 지평 1.5 s, 200 Hz 내부 모델, 시뮬레이션과 같은 서보·진자
클래스) — 표와 해석은 [F14](findings.md). 요약: 강체 162.8 L 대비 −4.9 ~ −7.6 %
(시드 3 + 고품질 샘플링), 이득은 자유 진동 주행 1 개에 집중, 나머지는 강체와 구분 불가.
도구: 메뉴 `FluidStabilizer ▸ Evaluate MPC`, `MpcSweepRunner`(`-mpcVariants`,
`-mpcSeconds`, `-mpcDrives`). 전체 평가 ≈ 16 분 (MPC 1 시뮬레이션 초당 CPU ≈ 1~2.5 s).

### 재개 시 참고 (확인된 사실)

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
- 최적화 방식은 MPPI (스필 비용이 불연속에 가까워 미분 불필요, 잠금 후보를 같은 틀로 비교).
- **개입 억제가 이득의 원천이었다:** 틸트 페널티 0.05 → 15, 잠금 여유 0.01 → 1 L 로 올리자
  +12 % → −7 % 가 됐다. 같은 8 주행으로 고른 값이라 낙관 편향이 있다.
- 재개 조건: 아래 "닫힘" 표.


## 앞으로 할 수 있는 것

### 닫힘 — 재개 조건을 만족할 때만 다시 연다

| 줄기 | 닫은 근거 | 재개 조건 |
|---|---|---|
| 맨땅 PPO · PPO 미세조정 | F9·F10 | 액션 공간·보상이 바뀔 때 |
| BC 교사 다양화 | F11 | 활성/유휴를 관측으로 예측할 수 있다고 확인될 때 |
| DAgger · 해제 편향 | F12 | 위와 같음 |
| 프리뷰(미래 도로 정보) | D12 — 원칙상 제외 | 소유자가 원칙을 바꿀 때 |
| 홀드아웃 `final` 7 주행 | F13 으로 소비됨 | 새 주행을 녹화해 새 `final` 로 등록 |
| 인과 MPC (상태 추정 · 얕은물 · 학습) | F14 — 오라클 −5 ~ −8 % < −10 % ([D13](decisions.md)) | 외란 예측이 개선되거나(프리뷰 외 센서로 얻은 정보) 액추에이터 권한·잠금 방식이 바뀌어 오라클 상한이 −10 % 를 넘을 근거가 생길 때 |

### 열림 — 소유자가 선택한다

| 후보 | 하는 일 | 결정 필요 |
|---|---|---|
| 전제 변경 — 수동 해법 기준선 | 탱크 내부 배플·깊이를 슬로시 모델에 넣고 강체 대비 스필 감소를 측정 | 슬로시 모델 확장 범위 |
| 활성 구간 감지기 | 저주파 지속 외란 구간만 능동 제어, 그 밖에서는 항상 강체 | 합성 주행 40 개 이상 생성. F14 가 근거를 보탰다: MPC 이득이 자유 진동 구간에 집중되고 시간의 약 55 % 를 잠금으로 보냈다 |

### 보류 — 필요가 생길 때

도메인 랜덤화(활성 학습 재개 시) · 액추에이터·센서 정밀도 상향(백래시·기어박스, 데이터시트
보정) · 학습기 GUI · 실제 하드웨어 / ROS 2 (열림 후보가 새 홀드아웃에서
강체를 이긴 뒤에 판단. 아키텍처는 이 분리를 이미 전제한다 — [project-goals.md](project-goals.md)).
