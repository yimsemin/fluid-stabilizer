# 도로 모델

2026-08-29 추가 (계획 리뷰, 결정 D6). design-review §4/§5 가 "grade, bank,
거칠기를 가진 도로" 를 v1 필수 모델링으로 꼽음; 스텁(`RoadInput`)만 있고 아무것도
안 채웠음. 이게 그 갭을 닫음.

## 왜 중요한가

강체로 잡은 탱크(`HoldController`, D5)는 모든 도로 disturbance 를 액체로 곧장
전달한다. 완벽히 평평한 도로에서는 현실적 고속도로 운전 하에 강체 탱크가 거의
안 넘친다 (finding F4), 그래서 학습 정책이 이길 게 별로 없다. 실제 도로 — 굽이치는
grade, 커브 bank, 노면 거칠기, 간간이 포트홀 — 는 강체 탱크가 *거부할 수 없고*
예측 정책은 할 수 있는 disturbance. 3-way 비교(project-goals.md)를 다시 흥미로운
조건으로 되돌린다.

## 무엇을 모델링하나 — `RoadModel` (엔진프리, `FluidStabilizer.Core.Sim`)

**이동 거리** `s` 의 결정론적 함수 — 주어진 seed 에 대한 고정 코스 (두 번 달리면
같은 굽이와 범프가 같은 자리에). 중첩된 부분:

| 부분 | 모델 | 트럭에 미치는 효과 |
|---|---|---|
| **커브** | `s` 대비 사인 2개, 파장 ~`CurvatureWavelengthM` (180 m), 피크 `CurvatureAmplitudeRadPerM` (~0.004 rad/m ≈ 반경 250 m) | 수평 **곡률**: 트럭 heading 이 스스로 `curvature · speed` 를 따름 — 운전자 조향은 그 위의 *보정* (차선 변경, drift, 잘못 든 커브 회복). 코너링이 액체를 옆으로 던짐 |
| **언덕** | `s` 대비 사인 2개, 파장 ~`HillWavelengthM` (140 m), 피크 `GradeAmplitudeRad` (~3.5%) | 천천히 변하는 **grade**: 도로를 따른 중력 성분 + 차체 pitch 트림 → 탱크가 중력 대비 기울어짐 |
| **Bank** | superelevation `BankPerCurvature · curvature` (±0.08 rad 클램프) 가 커브 안쪽으로 뱅킹, + 작은 독립 cross-slope 사인 (`BankAmplitudeRad`) | 커브 superelevation / cross-slope → 코너링 하중을 일부 상쇄하는 차체 roll 트림 |
| **거칠기** | 사인 6개, 파장 기하 0.6~9 m, red 스펙트럼 (amp ∝ 1/k), RMS = `RoughnessRmsM` (6 mm) 되게 재스케일 | 연속 수직 표면 변위 → 승차 진동 |
| **이벤트** | 도로를 따른 Poisson 과정, `EventsPerKm` (18), 평균 높이 `EventHeightM` (±5 cm), 길이 `EventLengthM` (0.4 m) 의 raised-cosine 범프/포트홀 | 이산 jolt — 신축 조인트, 포트홀, 돌, 연석 |

`Severity` (기본 1) 가 거칠기 + 이벤트를 스케일 (grade/bank/곡률 제외) — 커리큘럼
이나 도메인 랜덤화의 유일한 손잡이. `RoadConfig.Enabled` 이 false 면 모든 출력이
0, 이게 v1 기본 → **기존의 모든 non-road 시나리오와 결정론 가드가 변함 없음.**
곡률 위상은 `Reset` 에서 *마지막*으로 뽑혀서, grade/bank/거칠기/이벤트 RNG
스트림이 이 채널 존재 전과 바이트 동일 (opt-in 하는 RoughRoad* 시나리오만 새
동작을 봄 — 그 베이스라인 수는 재스윕 필요).

## 트럭이 도로를 운전한다 — 레인 모델 (2026-08-29)

도로는 레일이 아니라 **운전하는 코스**. `VehicleModel.Step` (도로 on 일 때만):

- **운전자가 완전한 조향 authority** — `Steer` 가 노면 바퀴 각으로 매핑되고
  평범한 bicycle 모델처럼 트럭을 yaw.
- **곡률은 그 위에 자동으로 추종** (`roadYawRate = speed · road.Curvature`),
  그래서 손 놓은 트럭이 굽이를 바깥으로 벗어나지 않고 추적.
- **약한 return-to-centre** (`LaneReturnGain`) — 손 놓은 트럭이 ~10 s 에 걸쳐
  도로 중심 쪽으로 drift, 그러나 당김이 약해서 운전자가 다른 차선으로 옮겨
  거기 있을 수 있음.
- **확실한 도로 가장자리** — `RoadHalfWidthM` 를 넘으면 `RoadEdgeGain` yaw 보정
  (friction circle *밖*에 적용 — 타이어 힘이 아니라 가드레일) + `RoadHalfWidthM
  + 1.2 m` 에서 하드 위치 클램프.

`VehicleState` 가 `LateralOffsetFromRoadCentre`, `HeadingErrorFromRoad`,
`RoadHeadingRad` 를 노출; `SimulationLoop` 이 도로 heading 을 적분
(`_roadHeadingRad += curvature · Δdistance`). `RoadRibbon` 이 lateral offset 을
써서 트럭의 실제 차선 위치에 도로를 그림.

코너링은 lateral 슬로시 disturbance 도 더함. 도로가 off 면 `Curvature` 가 0,
레인 모델을 완전히 건너뛰고, 동작이 이전과 바이트 동일.

## 좌우 바퀴 트랙 비대칭 (2026-08-30)

소유자 피드백: "왼쪽 바퀴에 돌맹이가 있으면 왼쪽으로 들썩거릴 수 있잖아? 그런게
지금 적용 안 된 건가." 맞다 — v1 은 단일 트랙 프로파일이었다. 이제 좌/우 바퀴
트랙을 구분한다.

- `RoadInput.SurfaceHeightLeft` / `SurfaceHeightRight` — 각 바퀴 트랙 아래 노면
  높이. `SurfaceHeight` 는 둘의 **평균** (기존 heave 경로가 그대로 씀).
- **긴 undulation 은 공유**, **짧은 거칠기는 좌우 독립 위상** (약 2 m 떨어진
  노면은 짧은 파장에서 decorrelate), **이산 이벤트는 ~55% 가 한쪽 바퀴만**
  (돌, 가장자리 포트홀) — 나머지는 양쪽 (신축 조인트, 넓은 포트홀).
- `RoadConfig.WheelTrackRoughnessFrac` (기본 0.35) 가 차등 거칠기 진폭을
  `RoughnessRmsM` 의 비율로. **0 = 옛 대칭 단일 트랙 도로** (이벤트도 전부 양쪽).
- `VehicleModel` 이 `atan2(heightL − heightR, TrackWidthM)` 를 도로 유발 roll
  목표로 만들어 기존 roll 2차 서스펜션(1.6 Hz)으로 필터 → 한쪽 범프가
  일시적 roll 로 온다. `VehicleConfig.TrackWidthM` (2.0), `RoadRollGain` (1.0).
- **결정론:** 차등 채널은 `road.Fork("wheeltracks")` 전용 스트림에서 뽑는다 →
  도로 평균/grade/bank/이벤트위치/곡률은 이 채널 존재 전과 **바이트 동일**,
  새 roll 채널만 추가.
- **3분할 비교와의 정합성:** 세 트럭이 같은 트레이스(같은 조향)를 재생 → 같은
  거리에서 같은 lateral 위치 → 동일한 L/R 트랙을 샘플링. 시각적 오프셋은 무관.
  통제된 비교 유지.

## 미끄러운 노면 슬립 프록시 (2026-08-30)

소유자 피드백: "빗길/눈길 프리셋인데 별로 안 미끄러움." 원인: v1 은 마찰 한계만
낮춰서 급기동이 덜 먹혀 오히려 슬로시가 *줄었다*. 이제 lumped 슬립 프록시가
있다 (design-review §8/§18 은 완전한 타이어 모델을 defer — 이건 싼 대역).

- `VehicleModel._slip ∈ [0,1]`: 요구 가속이 마찰 한계를 넘으면 빠르게 상승
  (`SlipOnsetTau` 0.08 s), 천천히 회복 (`SlipRecoverTau` 0.35 s).
- 슬립 중: (1) 접지력 추가 감소 `SlipGripLoss` (kinetic < static), (2) 조향
  under-response — 요레이트 저역통과, 시정수가 `SlipYawLag · _slip` → grip
  복귀 시 시정수가 붕괴해 "홱 잡힘"(grab), (3) OU 요 wander `SlipYawWander`
  (fishtail). (2)+(3) 이 탱크를 출렁이게 한다.
- `SlipOnsetFraction` = 1.02 — 한계 초과 시에만 (한계에선 아직 접지). 정상
  하드 주행은 깨끗, adversarial 운전자나 저접지 노면만 슬립.
- 전용 RNG: `vehicle.Fork("slip")` — 도로/센서/슬로시 스트림 무영향. Gaussian
  을 매 스텝 뽑되 `_slip > 0` 일 때만 적용 → 슬립 궤적과 RNG 소비 분리 (결정론 안정).
- `VehicleState.TyreSlip` 노출 → HUD 에 "⚠ 타이어 미끄러짐" 표시.

**격렬함 조절 (`slipIntensity`, 2026-08-30).** 소유자 피드백: "너무 미끌려서
운전 중이라는 느낌이 안 든다." 코드 기본값(YawWander 0.35 등)이 데모에 과했다.
`ExperimentProfile.SlipIntensity` 가 슬립의 *결과* 세 가지 —
`SlipYawWander`(fishtail) · `SlipGripLoss`(추가 접지 손실) · `SlipYawLag`(조향
under-response + grab) — 를 한 번에 스케일. `Grip` 과 독립: `Grip` 은 *언제*
미끄러지기 시작하나, `SlipIntensity` 는 미끄러진 뒤 *얼마나* 요동치나.
  - `0` = 슬립 dynamics 완전 제거 (슬립 프록시 도입 이전 느낌, 마찰한계만 유지).
  - `0.5` = 순하고 제어 가능 — **기본값**.
  - `1` = 코드 기본값 그대로.
  - `2~3` (클램프) = 의도적으로 격렬한 슬라이드.
- 프리셋: 일반/굽은/급경사 0.5, 비포장 0.6, 빗길 0.7 (grip 0.6), 눈길 0.9
  (grip 0.38). 예전의 거센 느낌은 `experiment.txt` 에서 `slipIntensity: 1.5`
  이상으로.

## 서스펜션 heave

`VehicleModel` 이 3번째 스프렁매스 DOF 를 얻음 (pitch + roll 만 있었음;
design-review §8 은 "heave, pitch, roll" 요구). 차체가 spring-mass-damper 로
노면을 추종 — `HeaveNaturalFreqHz` 1.6, `HeaveDampingRatio` 0.30, ~1.5 Hz
트럭 승차 모드. 그래서 날카로운 범프가 순간 가속 스파이크가 아니라 서스펜션
자체 공진을 가진 *필터링된* jolt 로 탱크에 도달 (design-review §4). 차체 수직
가속이 종/횡 항과 똑같이 탱크 프레임 운동학에 들어감; 수직 변위가
`VehicleState.OriginWorld.Z` 에 들어가 트럭이 눈에 띄게 튐.

## 결정론

`RoadModel` 이 `Reset(seed)` 에서 fork 한 `IRandomSource` 하나로 모든 파라미터를
뽑음. 이산 이벤트는 `s` 가 진행하며 엄격한 전진 순서로 lazy 하게 생성 → 거리
축이 어떻게 청크되든 시퀀스가 독립
(`RoadModelTests.Sampling_IsIndependentOfStepSize`). 도로 on 인 전체 루프
에피소드는 비트 재현 가능.

## 어디에 배선됐나

- `SimulationConfig.Road` (`RoadConfig`) — 모든 파라미터.
- `SimulationLoop` — 매 물리 스텝 진행 거리에서 도로 샘플, `VehicleModel.Step`
  에 먹임, grade/bank/표면 높이를 `GroundTruthFrame` 에 기록 (HUD + 로깅).
- 시나리오: `RoughRoadHighway`, `RoughRoadLowFreeboard` (기존 6개는 안 건드림).
- RL 런처: `-rlRoad {none|mild|rough}`.
- 플레이 씬: `SimulationRunner.Road` / `RoadSeverity` 인스펙터 필드; **`G`** 가
  도로를 실시간 토글; HUD 가 grade %, bank, 표면 높이 표시.

## 모델링 안 함 (보류)

도로 유발 yaw (crown steer), 짧은 범프의 타이어 envelopment, 공간 상관된 좌/우
트랙 (v1 은 단일 트랙 프로파일), 차체 roll 트림 너머의 뱅크드 커브 lateral 하중
결합. 어느 것도 액체에 도달하는 지배적 disturbance 경로(수직 + grade)에 영향 없음.
