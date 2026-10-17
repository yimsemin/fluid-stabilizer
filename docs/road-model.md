# 도로 모델

결정 D6 의 구현이다. design-review §4/§5 가 "grade, bank, 거칠기를 가진 도로"를 필수 모델링 대상으로 꼽았고,
이 문서가 그 도로 모델을 설명한다.

## 왜 중요한가

강체로 잡은 탱크(`HoldController`, D5)는 모든 도로 외란을 액체에 그대로 전달한다. 완벽하게 평평한 도로에서는
현실적인 고속도로 운전에서도 강체 탱크가 거의 넘치지 않는다(finding F4). 그러면 학습 정책이 이길 여지가
별로 없다. 실제 도로 — 굽이치는 grade, 커브 bank, 노면 거칠기, 간간이 나오는 포트홀 — 는 강체 탱크가
*걸러 낼 수 없고* 예측하는 정책은 걸러 낼 수 있으리라고 기대한 외란이다. 이것이 4종 비교(project-goals.md)를
의미 있는 조건으로 만든다. *(결과: 도로를 켜도 녹화 주행에서는 강체가 이긴다 — F5~F13.)*

## 무엇을 모델링하나 — `RoadModel` (엔진프리, `FluidStabilizer.Core.Sim`)

**이동 거리** `s` 의 결정론적 함수다. 주어진 seed 에 대해 코스가 고정되어, 두 번 달리면 같은 굽이와 범프가
같은 자리에 나온다. 아래 부분들이 겹쳐 있다.

| 부분 | 모델 | 트럭에 미치는 효과 |
|---|---|---|
| **커브** | `s` 에 대한 사인 2개, 파장 ~`CurvatureWavelengthM`(180 m), 피크 `CurvatureAmplitudeRadPerM`(~0.004 rad/m ≈ 반경 250 m) | 수평 **곡률**: 트럭의 heading 이 `curvature · speed` 를 스스로 따라간다. 운전자의 조향은 그 위의 *보정*(차선 변경, drift, 잘못 든 커브의 회복)이다. 코너링이 액체를 옆으로 던진다 |
| **언덕** | `s` 에 대한 사인 2개, 파장 ~`HillWavelengthM`(140 m), 피크 `GradeAmplitudeRad`(~3.5 %) | 천천히 변하는 **grade**: 도로를 따른 중력 성분 + 차체 pitch 트림 → 탱크가 중력에 대해 기울어진다 |
| **Bank** | superelevation `BankPerCurvature · curvature`(±0.08 rad 로 제한)가 커브 안쪽으로 기울고, 작은 독립 cross-slope 사인(`BankAmplitudeRad`)이 더해진다 | 커브 superelevation / cross-slope → 코너링 하중을 일부 상쇄하는 차체 roll 트림 |
| **거칠기** | 사인 6개, 파장은 기하급수로 0.6~9 m, red 스펙트럼(진폭 ∝ 1/k), RMS = `RoughnessRmsM`(6 mm)이 되도록 다시 스케일 | 연속적인 수직 표면 변위 → 승차 진동 |
| **이벤트** | 도로를 따른 Poisson 과정, `EventsPerKm`(18), 평균 높이 `EventHeightM`(±5 cm), 길이 `EventLengthM`(0.4 m)인 raised-cosine 범프/포트홀 | 이산적인 jolt — 신축 이음매, 포트홀, 돌, 연석 |

`Severity`(기본 1)가 거칠기와 이벤트를 스케일한다(grade/bank/곡률은 제외). 커리큘럼이나 도메인 랜덤화를 위한
유일한 손잡이다. `RoadConfig.Enabled` 가 false 면 모든 출력이 0 이고, 이것이 v1 의 기본값이므로 **기존의 모든
non-road 시나리오와 결정론 가드는 변함이 없다.** 곡률 위상은 `Reset` 에서 *마지막*에 뽑으므로 grade/bank/
거칠기/이벤트의 RNG 스트림에 영향을 주지 않는다(곡률 채널을 추가해도 그 스트림은 바이트 단위로 같다).

## 트럭이 도로를 운전한다 — 차선 모델

도로는 레일이 아니라 **운전하는 코스**다. `VehicleModel.Step`(도로가 켜져 있을 때만):

- **운전자가 조향 권한을 완전히 갖는다.** `Steer` 가 노면 바퀴 각도로 대응되고, 평범한 bicycle 모델처럼
  트럭을 yaw 시킨다.
- **곡률은 그 위에 자동으로 추종한다**(`roadYawRate = speed · road.Curvature`). 그래서 손을 놓은 트럭이 굽이
  바깥으로 벗어나지 않고 도로를 따라간다.
- **약한 return-to-centre**(`LaneReturnGain`) — 손을 놓은 트럭이 약 10 s 에 걸쳐 도로 중심 쪽으로 drift 한다.
  당기는 힘이 약해서 운전자가 다른 차선으로 옮겨 머무를 수 있다.
- **확실한 도로 가장자리** — `RoadHalfWidthM` 를 넘으면 `RoadEdgeGain` 만큼 yaw 를 보정하고(friction circle
  *밖*에서 적용 — 타이어 힘이 아니라 가드레일이다), `RoadHalfWidthM + 1.2 m` 에서는 위치를 강제로 제한한다.

`VehicleState` 가 `LateralOffsetFromRoadCentre`, `HeadingErrorFromRoad`, `RoadHeadingRad` 를 노출한다.
`SimulationLoop` 이 도로 heading 을 적분한다(`_roadHeadingRad += curvature · Δdistance`). `RoadRibbon` 은
lateral offset 을 써서 트럭의 실제 차선 위치에 맞춰 도로를 그린다.

코너링은 횡방향 슬로시 외란도 더한다. 도로가 꺼져 있으면 `Curvature` 가 0 이고 차선 모델을 완전히 건너뛰므로,
동작은 이전과 바이트 단위로 같다.

## 좌우 바퀴 트랙 비대칭

왼쪽 바퀴만 돌에 올라가면 차체가 왼쪽으로 들썩인다. 이를 표현하려고, 단일 트랙 프로파일이 아니라 좌/우 바퀴
트랙을 구분한다.

- `RoadInput.SurfaceHeightLeft` / `SurfaceHeightRight` — 각 바퀴 트랙 아래의 노면 높이다. `SurfaceHeight` 는
  둘의 **평균**이며 기존 heave 경로가 그대로 쓴다.
- **긴 undulation 은 공유하고**, **짧은 거칠기는 좌우의 위상이 독립이며**(약 2 m 떨어진 노면은 짧은 파장에서
  상관이 사라진다), **이산 이벤트는 약 55 % 가 한쪽 바퀴에만** 생긴다(돌, 가장자리 포트홀). 나머지는 양쪽에
  생긴다(신축 이음매, 넓은 포트홀).
- `RoadConfig.WheelTrackRoughnessFrac`(기본 0.35)가 좌우 차이 거칠기의 진폭을 `RoughnessRmsM` 에 대한 비율로
  정한다. **0 이면 대칭 단일 트랙 도로**(이벤트도 전부 양쪽)다.
- `VehicleModel` 이 `atan2(heightL − heightR, TrackWidthM)` 를 도로가 유발하는 roll 목표로 만들고 기존 roll
  2차 서스펜션(1.6 Hz)으로 걸러 낸다. 그래서 한쪽 범프가 일시적인 roll 로 나타난다.
  `VehicleConfig.TrackWidthM`(2.0), `RoadRollGain`(1.0).
- **결정론:** 좌우 차이 채널은 `road.Fork("wheeltracks")` 전용 스트림에서 뽑는다. 따라서 도로 평균/grade/bank/
  이벤트 위치/곡률은 이 채널이 생기기 전과 **바이트 단위로 같고**, 새 roll 채널만 추가된다.
- **3분할 비교와의 정합성:** 세 트럭이 같은 트레이스(같은 조향)를 재생하므로 같은 거리에서 같은 lateral 위치가
  되고 같은 좌/우 트랙을 샘플링한다. 화면상의 오프셋은 무관하다. 통제된 비교가 유지된다.

## 미끄러운 노면 슬립 프록시

마찰 한계만 낮추면 급기동이 덜 먹혀서 오히려 슬로시가 *줄어든다.* 그래서 lumped 슬립 프록시를 둔다
(design-review §8/§18 은 완전한 타이어 모델을 미뤘고, 이것은 그 값싼 대용품이다).

- `VehicleModel._slip ∈ [0,1]`: 요구 가속이 마찰 한계를 넘으면 빠르게 오르고(`SlipOnsetTau` 0.08 s) 천천히
  회복한다(`SlipRecoverTau` 0.35 s).
- 슬립 중에는 (1) 접지력이 추가로 줄고(`SlipGripLoss`, kinetic < static), (2) 조향이 덜 반응하다가(요레이트를
  저역통과하며 시정수가 `SlipYawLag · _slip`) grip 이 돌아오면 시정수가 급격히 줄어 "홱 잡히고"(grab), (3) OU
  요 wander(`SlipYawWander`, fishtail)가 더해진다. (2)와 (3)이 탱크를 출렁이게 한다.
- `SlipOnsetFraction` = 1.02 — 한계를 넘을 때만 시작한다(한계 위에서는 아직 접지). 정상적인 강한 주행은
  깨끗하고, 적대적인 운전자나 저접지 노면에서만 슬립이 생긴다.
- 전용 RNG: `vehicle.Fork("slip")` — 도로/센서/슬로시 스트림에 영향이 없다. Gaussian 은 매 스텝 뽑되
  `_slip > 0` 일 때만 적용한다. 슬립 궤적과 RNG 소비를 분리해 결정론을 안정시킨다.
- `VehicleState.TyreSlip` 을 노출해 HUD 에 "⚠ 타이어 미끄러짐"을 표시한다.

**격렬함 조절 (`slipIntensity`).** 코드의 기본값(YawWander 0.35 등)은 데모에는 지나치다.
`ExperimentProfile.SlipIntensity` 가 슬립의 *결과* 세 가지 — `SlipYawWander`(fishtail) ·
`SlipGripLoss`(추가 접지 손실) · `SlipYawLag`(조향 둔화 + grab) — 를 한 번에 스케일한다. `Grip` 과는 독립이다.
`Grip` 은 *언제* 미끄러지기 시작하는지를, `SlipIntensity` 는 미끄러진 뒤 *얼마나* 요동치는지를 정한다.
  - `0` = 슬립 동역학 완전 제거(슬립 프록시 도입 이전의 느낌, 마찰 한계만 유지).
  - `0.5` = 순하고 제어할 수 있음 — **기본값**.
  - `1` = 코드 기본값 그대로.
  - `2~3`(상한) = 일부러 격렬한 슬라이드.
- 프리셋: 일반/굽은 길/급경사 0.5, 비포장 0.6, 빗길 0.7(grip 0.6), 눈길 0.9(grip 0.38). 거센 슬립이 필요하면
  `experiment.txt` 에서 `slipIntensity: 1.5` 이상으로 한다.

## 서스펜션 heave

`VehicleModel` 은 heave, pitch, roll 의 스프렁매스 DOF 3개를 갖는다(design-review §8 의 요구). 차체가
spring-mass-damper 로 노면을 따라간다 — `HeaveNaturalFreqHz` 1.6, `HeaveDampingRatio` 0.30, 약 1.5 Hz 의
트럭 승차 모드다. 그래서 날카로운 범프가 순간적인 가속도 스파이크가 아니라, 서스펜션 자체의 공진을 거친
*걸러진* jolt 로 탱크에 닿는다(design-review §4). 차체의 수직 가속은 종/횡 항과 똑같이 탱크 프레임 운동학에
들어간다. 수직 변위는 `VehicleState.OriginWorld.Z` 로 들어가서 트럭이 눈에 띄게 튄다.

## 결정론

`RoadModel` 은 `Reset(seed)` 에서 fork 한 `IRandomSource` 하나로 모든 파라미터를 뽑는다. 이산 이벤트는 `s` 가
진행하면서 엄격한 전진 순서로 필요할 때 생성되므로, 거리 축을 어떻게 나눠 처리하든 시퀀스가 같다
(`RoadModelTests.Sampling_IsIndependentOfStepSize`). 도로가 켜진 전체 루프 에피소드는 비트 단위로 재현된다.

## 어디에 연결되어 있나

- `SimulationConfig.Road`(`RoadConfig`) — 모든 파라미터.
- `SimulationLoop` — 물리 스텝마다 진행 거리에서 도로를 샘플해 `VehicleModel.Step` 에 넣고, grade/bank/표면
  높이를 `GroundTruthFrame` 에 기록한다(HUD + 로깅).
- 시나리오: `RoughRoadHighway`, `RoughRoadLowFreeboard`(기존 6개는 건드리지 않았다).
- RL 런처: `-rlRoad {none|mild|rough}`.
- 플레이 씬: `SimulationRunner.Road` / `RoadSeverity` 인스펙터 필드. **`G`** 가 도로를 실시간으로 켜고 끈다.
  HUD 에 grade %, bank, 표면 높이가 표시된다.

## 모델링하지 않은 것 (보류)

도로가 유발하는 yaw(crown steer), 짧은 범프에 대한 타이어 envelopment, 좌/우 트랙 사이의 정밀한 공간 상관
(현재는 긴 undulation 은 공유하고 짧은 거칠기는 독립으로 보는 근사), 차체 roll 트림을 넘어서는 뱅크드 커브의
횡하중 결합. 어느 것도 액체에 닿는 지배적 외란 경로(수직 + grade)에는 영향을 주지 않는다.
