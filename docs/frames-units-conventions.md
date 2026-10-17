# 프레임 · 단위 · 타이밍 규약

상태: **고정 (v1).** 2026-08-27 설계 리뷰 승인의 일부로 합의했다. 날짜와 함께 명시적 결정으로 기록할 때만
바꾼다.

## 단위 — 어디서나 SI

| 물리량 | 단위 |
|---|---|
| 길이 | 미터 (m) |
| 질량 | 킬로그램 (kg) |
| 시간 | 초 (s) |
| 각도 | 내부는 라디안 (rad). UI / 인스펙터 경계에서만 도(°) |
| 힘 | 뉴턴 (N) |
| 토크 | 뉴턴미터 (N·m) |
| 선가속 | m/s² |
| 각속도 | rad/s |

시뮬레이션 코어와 모든 메시지는 **오른손 좌표계, SI, 라디안**을 쓴다. Unity 의 transform(왼손, Y-up, 도)은
`FluidStabilizer.Sim` 경계에서 **만** 변환하고, `Contracts` 나 `Controller` 로 절대 새어 나가지 않는다.

## 좌표계 — 오른손

| 프레임 | 원점 | 축 | 메모 |
|---|---|---|---|
| **World (W)** | 에피소드 시작 시 지면의 고정점 | X = 동, Y = 북, Z = 위 (ENU) | 관성 프레임. 중력 `g_W = (0, 0, −9.80665)` m/s² |
| **Vehicle (V)** | 후축 중심, 지면 높이 | X = 전방, Y = 좌, Z = 위 | 스프렁매스에 강체로 붙어 있고, 차체의 heave/pitch/roll 과 함께 움직인다 |
| **Tank (T)** | 짐벌 피벗점(탱크의 명목 CoM 에 둔다) | 짐벌이 0 일 때 X = 전방, Y = 좌, Z = 위 | 두 스태빌라이저 관절 각도로 V 에 대해 회전한다 |
| **Sensor 프레임 (S\*)** | 각 센서의 마운트점 | 명목상 부모 body 프레임과 같다. 각각 에피소드마다 뽑는 작은 고정 **장착 오정렬**(회전 + 오프셋)을 가진다 | IMU 프레임 ≈ body 프레임 ∘ 장착 오차 |

### 짐벌 운동학

- 직렬 revolute 관절 2개: **V 의 Y 축 주위 pitch**, 이어서 **pitch 가 적용된 X 축 주위 roll**. 회전 순서는
  고정이며 `R_VT = Rx(roll) · Ry(pitch)` 이다.
- Travel 한계: `|pitch| ≤ 30°`, `|roll| ≤ 30°` (운동학적 특이점에서 충분히 떨어져 있다. 정확한 값은 ≤ 30°
  범위 안에서 랜덤화한다 — design-review §13).
- 양의 pitch 는 탱크의 +X(전방) 가장자리를 올리고, 양의 roll 은 탱크의 +Y(좌) 가장자리를 올린다.

## 타이밍 / 갱신 주기 — 고정 기본값

| 루프 | 주기 | 간격 | 메모 |
|---|---|---|---|
| 물리 + 슬로시 적분 | **600 Hz** | 1.667 ms | 고정 `dt`. 모델에 필요하면 스텝 안에서 서브스텝을 쓸 수 있다 |
| 센서 샘플링 | **200 Hz** | 5 ms | 물리 스텝을 ×3 으로 솎는다. 모든 샘플이 sim-time 타임스탬프를 갖는다 |
| 컨트롤러 / 정책 | **100 Hz** | 10 ms | 물리 스텝을 ×6 으로 솎는다. 틱 사이의 행동은 zero-order hold |
| 명령 전송 지연 | **10~50 ms** | — | 에피소드마다 모델링하고 랜덤화한다 (design-review §9, §14) |
| 에피소드 길이 | 기본 **30 s** sim time(`EpisodeSeconds`). 녹화 주행은 주행 길이(약 30~60 s)를 따른다 | — | 기동 뒤의 슬로시 ring-down 을 담을 만큼 길다 |
| 렌더링 (수동 주행 모드만) | 디스플레이 주기 | — | 물리 경로에는 절대 들어가지 않는다 |

주기는 솎는 비율이 정수이고 컨트롤 틱이 항상 센서 틱과 일치하도록 골랐다. 물리 : 센서 : 컨트롤 = 6 : 2 : 1
이다. **200 Hz 센서가 정수로 나누어떨어지도록 500 Hz 대신 600 Hz 를 택했다.**

타이밍은 매니지드 시뮬레이션 루프가 소유한다. 물리에 관련된 것에는 Unity 의 `FixedUpdate`/`Time` 을 **쓰지
않는다**(decisions.md D1 — 기계 시뮬은 PhysX 가 아니라 매니지드 코드).

## 시간과 결정론

- 단조 증가하는 시뮬레이션 클록 하나, `IClock.SimTimeSeconds`(`double`)를 쓰며 에피소드 시작에서 0 이다.
- 모든 센서/명령 메시지는 `StampSeconds`(`double`) = 그 메시지가 나타내는 sim time 을 가진다.
- 컨트롤러는 메시지를 **타임스탬프로** 소비하고, 프레임 인덱스로는 절대 소비하지 않는다.
- Wall-clock 시간은 (미래의) 실시간 / ROS 2 어댑터에서만 쓴다.
- 모든 확률 요소는 seeded `IRandomSource` 트리에서 뽑는다(에피소드마다 루트 seed 하나 → 이름 붙은 하위
  스트림). 시뮬 경로에 `UnityEngine.Random` 이나, seed 를 명시하지 않은 `System.Random` 은 없다.
- 슬로시 ODE 는 매니지드 코드에서 고정 스텝으로 적분한다(PhysX 가 아니다). 그래서 에피소드가 seed 만으로
  비트 단위 재현된다.

## 메시지 DTO (v1 세트)

`FluidStabilizer.Contracts` 에 있는, 직렬화할 수 있는 평범한 C# struct 들이다. `UnityEngine` 타입을 쓰지
않고, 오른손 SI 이며, 각각 `double StampSeconds` 필드를 가진다. 필드 구성이 대응하는 ROS 2 메시지를 본떠서,
나중의 어댑터가 그대로 복사하면 된다.

| DTO | 대응 | 담는 것 |
|---|---|---|
| `ImuSample` | `sensor_msgs/Imu` | 선가속(specific force, m/s²), 각속도(rad/s), 센서 프레임 |
| `JointStateSample` | `sensor_msgs/JointState` | pitch & roll 각도(rad), 각속도(rad/s) |
| `MotorStateSample` | — | 축별 추정 모터 전류(A), 추정 출력 토크(N·m) |
| `LoadCellSample` | — | 고정 마운트점에서 잰 수직 힘 스칼라 3~4개 (N) |
| `LevelSample` *(선택)* | — | 고정 프로브 위치에서 잰 점 액체 표면 높이 1~3개 (m) |
| `VehicleCanSample` *(선택, 플래그 뒤)* | — | 속도(m/s), 조향각(rad), throttle [0,1], brake [0,1] |
| `StabilizerCommand` | — | 축별 명령: mode(`JointRate` / `JointAngle` / `Locked`) + pitch/roll 값(rad/s 또는 rad) + sequence 번호 + 계산된 stamp |

`SensorBundle` 은 한 컨트롤 틱의 동기화된 샘플 세트를 묶는다.
