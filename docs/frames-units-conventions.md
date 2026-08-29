# 프레임 · 단위 · 타이밍 규약

상태: **고정 (v1).** 2026-08-27 설계 리뷰 승인의 일부로 합의. 여기 날짜와 함께
기록된 명시적 결정으로만 변경.

## 단위 — 어디서나 SI

| 물리량 | 단위 |
|---|---|
| 길이 | 미터 (m) |
| 질량 | 킬로그램 (kg) |
| 시간 | 초 (s) |
| 각도 | 내부는 라디안 (rad); UI / 인스펙터 경계에서만 도 |
| 힘 | 뉴턴 (N) |
| 토크 | 뉴턴미터 (N·m) |
| 선가속 | m/s² |
| 각속도 | rad/s |

시뮬레이션 코어와 모든 메시지는 **오른손, SI, 라디안**. Unity 의 transform
(왼손, Y-up, 도)은 `FluidStabilizer.Sim` 경계에서 **만** 변환 — `Contracts` 나
`Controller` 로 절대 새지 않음.

## 좌표계 — 오른손

| 프레임 | 원점 | 축 | 메모 |
|---|---|---|---|
| **World (W)** | 에피소드 시작 시 지면의 고정점 | X = 동, Y = 북, Z = 위 (ENU) | 관성. 중력 `g_W = (0, 0, −9.80665)` m/s² |
| **Vehicle (V)** | 후축 중심, 지면 높이 | X = 전방, Y = 좌, Z = 위 | 스프렁매스에 강체 부착; 차체 heave/pitch/roll 과 함께 움직임 |
| **Tank (T)** | 짐벌 피벗점 (탱크의 명목 CoM 에 배치) | 짐벌이 0 일 때 X = 전방, Y = 좌, Z = 위 | 두 스태빌라이저 관절 각도로 V 대비 회전 |
| **Sensor 프레임 (S\*)** | 각 센서 마운트점 | 명목 = 부모 body 프레임; 각각 에피소드당 뽑는 작은 고정 **마운팅 오정렬**(회전 + 오프셋)을 가짐 | IMU 프레임 ≈ body 프레임 ∘ 마운팅 오차 |

### 짐벌 운동학

- 직렬 revolute 관절 2개: **V 의 Y 축 주변 pitch**, 그다음 **한 번 pitch 된
  X 축 주변 roll**. 회전 순서 고정: `R_VT = Rx(roll) · Ry(pitch)`.
- Travel 한계: `|pitch| ≤ 30°`, `|roll| ≤ 30°` (운동학적 특이점에서 충분히
  떨어짐; 정확한 값은 ≤ 30° 대역 안에서 랜덤화 — design-review §13).
- 양의 pitch 는 탱크의 +X(전방) 가장자리를 올림; 양의 roll 은 탱크의 +Y(좌)
  가장자리를 올림.

## 타이밍 / 갱신율 — 고정 기본값

| 루프 | 율 | 주기 | 메모 |
|---|---|---|---|
| 물리 + 슬로시 적분 | **600 Hz** | 1.667 ms | 고정 `dt`. 모델이 필요하면 스텝 안에서 서브스텝 허용 |
| 센서 샘플링 | **200 Hz** | 5 ms | 물리스텝 데시메이션 ×3. 모든 샘플이 sim-time 스탬프를 지님 |
| 컨트롤러 / 정책 | **100 Hz** | 10 ms | 물리스텝 데시메이션 ×6. 틱 사이 행동은 zero-order hold |
| 명령 전송 지연 | **10~50 ms** | — | 에피소드당 모델링 + 랜덤화 (design-review §9, §14) |
| 에피소드 길이 | **20~40 s** sim time | — | 기동 후 슬로시 ring-down 을 담을 만큼 김 |
| 렌더링 (수동 주행 모드만) | 디스플레이 율 | — | 물리 경로에 절대 없음 |

율은 데시메이션이 정수이고 컨트롤 틱이 항상 센서 틱과 일치하도록 선택:
물리:센서:컨트롤 = 6:2:1. **200 Hz 센서가 정수로 나눠지도록 500 대신 600 Hz 선택.**

매니지드 시뮬레이션 루프가 타이밍을 소유; 물리적인 것에 Unity 의
`FixedUpdate`/`Time` 을 **쓰지 않음** (decisions.md D1 — 기계 시뮬은 PhysX 가
아니라 매니지드 코드).

## 시간과 결정론

- 단조 시뮬레이션 클록 하나, `IClock.SimTimeSeconds` (`double`), 에피소드
  시작에 0.
- 모든 센서/명령 메시지가 `StampSeconds` (`double`) = 그것이 나타내는 sim
  time 을 지님.
- 컨트롤러가 메시지를 **타임스탬프로** 소비, 프레임 인덱스로 절대 안 함.
- Wall-clock 시간은 (미래) 실시간 / ROS 2 어댑터만 씀.
- 모든 확률 요소가 seeded `IRandomSource` 트리에서 뽑음 (에피소드당 루트 seed
  하나 → 명명된 서브스트림). sim 경로에 `UnityEngine.Random`, 명시적 seed 없는
  `System.Random` 없음.
- 슬로시 ODE 는 매니지드 코드에서 고정 스텝으로 적분 — PhysX 아님 — 그래서
  에피소드가 seed 로 비트 재현 가능.

## 메시지 DTO (v1 셋)

`FluidStabilizer.Contracts` 의 평범한 직렬화 가능 C# struct, `UnityEngine`
타입 없음, 오른손 SI, 각각 `double StampSeconds` 필드. 필드 레이아웃이 대응하는
ROS 2 메시지를 미러링해서 미래 어댑터가 straight copy.

| DTO | 미러링 | 담는 것 |
|---|---|---|
| `ImuSample` | `sensor_msgs/Imu` | 선가속(specific force, m/s²), 각속도(rad/s), 센서 프레임 |
| `JointStateSample` | `sensor_msgs/JointState` | pitch & roll 각도(rad), 각속도(rad/s) |
| `MotorStateSample` | — | 축당 추정 모터 전류(A), 추정 출력 토크(N·m) |
| `LoadCellSample` | — | 고정 마운트점의 스칼라 수직 힘 3~4개 (N) |
| `LevelSample` *(옵션)* | — | 고정 프로브 위치의 점 액체표면 높이 1~3개 (m) |
| `VehicleCanSample` *(옵션, 플래그 뒤)* | — | 속도(m/s), 조향각(rad), throttle [0,1], brake [0,1] |
| `StabilizerCommand` | — | 축당 명령: mode(`JointRate` / `JointAngle` / `Locked`) + pitch/roll 값(rad/s 또는 rad) + sequence 번호 + 계산된 stamp |

`SensorBundle` 이 한 컨트롤 틱의 동기화된 샘플 셋을 묶음.
