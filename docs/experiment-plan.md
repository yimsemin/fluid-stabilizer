# 실험 계획 — 현황과 지도

이 프로젝트의 **유일한 진행상황 추적기**이자 소유자용 지도. 무엇을 만들었는지, 무엇이 닫혔고 무엇이
열려 있는지만 쓴다. 의도와 범위는 [project-goals.md](project-goals.md), 밝혀진 사실은
[findings.md](findings.md), 설계 결정은 [decisions.md](decisions.md), 결과·교훈과 한계는
[lessons.md](lessons.md) 에 있고 여기서는 링크만 건다.

## 현황 (2026-10-16)

**프로젝트는 마무리 단계다 ([D14](decisions.md)).** 시뮬레이션·베이스라인·평가기·재현성 도구·플레이 씬·
RL/BC/DAgger 경로·정책 서빙·4종 비교·수동 해법(배플) 평가·standalone 빌드가 모두 만들어졌고
EditMode 테스트는 **158 통과 / 0 실패 / 16 스킵**이다.

**한 줄 결과:** 녹화 주행에서 ① 강체가 능동 제어(②·③·MPC)를 이기고, 탱크 설계(충전율·림·배플)가
능동 제어보다 훨씬 크게 스필을 줄인다. 숫자와 한계는 [lessons.md](lessons.md).

**다음 작업: 없음.** 새 실험 후보는 [아래](#앞으로-할-수-있는-것).

## 지금까지 만든 것

(상세 근거는 각 finding / decision 링크. 범위 기준은 [design-review.md](design-review.md) §20.)

| 층 | 상태 | 요약 |
|---|---|---|
| 결정론적 시뮬 코어 | ✅ | `ManualClock` · `DeterministicRandom`(PCG32) · `SimulationLoop` (600/200/100 Hz, 번들 지연, Gт 기록), 엔진프리 (D1) |
| 차량 + 도로 + 히브(heave) | ✅ | `VehicleModel` (3-DOF 스프렁 매스 + friction-circle), `RoadModel` (거리 함수 = grade/bank/거칠기/포트홀, 좌우 트랙 비대칭, 슬립 프록시) — D6·D7, [road-model.md](road-model.md) |
| 짐벌 액추에이터 | 🟡 | `GimbalActuator`: 전송지연, PD+I 위치 서보, 피크토크 포화, 마찰, rate/travel 한계, `Locked` 모드(강체 관절), I²t 권선열 모델 — D5. 백래시·기어박스 상세 보류 |
| 슬로시 모델 2종 | ✅ | `PendulumSloshModel` (등가역학 ROM, 축당 진자 1개) + `ShallowWaterSloshModel` (2-D Saint-Venant, ~48×28 격자, 스크린 배플 지원) — D4·D14. **브래킷 규율** (F3): ROM 으로 설계·학습, 얕은물로 검증 |
| 센서 층 | 🟡 | `SensorSuite`: 차량+탱크 IMU, 엔코더(양자화), 모터, 로드셀 4개, 옵션 레벨/CAN; 샘플당 노이즈 + 에피소드당 바이어스, 번들 지연 |
| 베이스라인 컨트롤러 | 🟡 | ① `Hold`(잠금), ② `SpecificForceAlignment` / `Pid`, 특권 `PrivilegedFullStateController`. `AxisLqr`/`LqrSloshController` 는 **보류**: 설계 모델이 실제 서보·지연 플랜트와 달라 공진 (F1). **D8·D10** lock-capable 액션(`LockGate`, 명시적 해제 요청). `OracleMpcController`(특권 MPPI, F14) 는 상한 시험용이며 배포 불가 |
| 평가 하네스 | ✅ | `Evaluator` + `ScenarioSet`(8개), `TraceEvaluator` (녹화 주행 × 베이스라인 × 슬로시 2종, 주행별 리터), `HindsightOptimizer` (저대역 천장, F5), `SweepRunner`, `DrivingPremiseSweepRunner`, `SloshSweepRunner` (탱크 값 스윕 · 감쇠 보정, F15·F16), `ActiveVsBaffleRunner` (능동 vs 배플, F17) |
| 플레이 씬 · 조작 | ✅ | `SimulationRunner` · `HumanDriverInput` · `SimView` · `Hud` · `PlayControls`. 컨트롤 패널(백틱), 녹화/재생(`P`/`L`), 3분할 비교 + 판정 배너(`C`), 라이브 학습 패널(`K`) — [operating-guide.md](operating-guide.md) |
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

**수동 해법 실험 도구** (F15~F17, 메뉴 없이 `-executeMethod` 로 실행. 결과는 `Logs/` 에 새 파일로 쓴다):

```
UNITY="/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity"
# 탱크 값 스윕 (충전율·림·감쇠·배플). 얕은물: -slShallow, 강체만: -slRigidOnly, 배플 감쇠 보정: -slCalibrate
"$UNITY" -batchmode -quit -nographics -projectPath . -executeMethod FluidStabilizer.SimEditor.SloshSweepRunner.Run \
  -slVariants "base:|fill 0.56:FillRatio=0.56|X2Y1 K20:BaffleCountX=2;BaffleCountY=1;BaffleLossCoeff=20" -slShallow -slRigidOnly -logFile -
# 능동 제어 vs 배플 (강체·규칙기반·오라클 MPC, 주행 병렬 실행)
"$UNITY" -batchmode -quit -nographics -projectPath . -executeMethod FluidStabilizer.SimEditor.ActiveVsBaffleRunner.Run -logFile -
```

얕은물 스윕은 변형 하나당 수 분이 걸린다 (`-slRigidOnly` 로 절반). 인자와 기본 변형 목록은 각 러너의 클래스 주석.

## MPC 를 다시 열 때 참고 (확인된 사실)

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
| 인과 MPC (상태 추정 · 학습) | F14 — 오라클 −5 ~ −8 % < −10 % ([D13](decisions.md)) | 외란 예측이 개선되거나(프리뷰 외 센서로 얻은 정보) 액추에이터 권한·잠금 방식이 바뀌어 오라클 상한이 −10 % 를 넘을 근거가 생길 때 |
| 능동 제어로 배플 탱크의 강체를 이기기 | F17 — 배플 유무와 관계없이 강체가 이김. 단 MPC 내부 모델이 진자 ([D14](decisions.md)) | 얕은물을 내부 모델로 쓰는 MPC 가 현실적인 비용으로 가능해질 때 (현재 에피소드당 약 4.2 s) |

### 열림 — 소유자가 선택한다

| 후보 | 하는 일 | 결정 필요 |
|---|---|---|
| 수동 해법 — 배플 실물 대응 | F16 의 K 가 실제 배플(구멍 비율·판 두께)에 해당하는지 문헌 또는 실험으로 확인, 부분 높이·링 배플 모델링, 새 홀드아웃에서 재측정 | 어떤 배플 종류까지 모델링할지 |
| 활성 구간 감지기 | 저주파 지속 외란 구간만 능동 제어, 그 밖에서는 항상 강체 | 합성 주행 40 개 이상 생성. F14 가 근거를 보탰다: MPC 이득이 자유 진동 구간에 집중되고 시간의 약 55 % 를 잠금으로 보냈다 |

### 보류 — 필요가 생길 때

도메인 랜덤화(활성 학습 재개 시) · 액추에이터·센서 정밀도 상향(백래시·기어박스, 데이터시트
보정) · 학습기 GUI · 실제 하드웨어 / ROS 2 (열림 후보가 새 홀드아웃에서
강체를 이긴 뒤에 판단. 아키텍처는 이 분리를 이미 전제한다 — [project-goals.md](project-goals.md)).
