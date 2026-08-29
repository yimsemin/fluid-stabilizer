# Assets/FluidStabilizer — 코드 레이아웃

`docs/design-review.md` 의 아키텍처 경계가 **규율이 아니라 컴파일러로** 강제되도록
배치한 어셈블리들.

| 어셈블리 | 엔진 참조? | 의존 | 담는 것 |
|---|---|---|---|
| **FluidStabilizer.Contracts** | ❌ `noEngineReferences` | — | 메시지 DTO (`SensorBundle`, `StabilizerCommand`, …), 인터페이스 (`IClock`, `IRandomSource`, `IController`, `ISloshModel`, `IDriverInput`), 수학 (`Double3`, `MathD`). |
| **FluidStabilizer.Core** | ❌ `noEngineReferences` | Contracts | 엔진 의존 없는 결정론적 구현: `Pcg32`, `DeterministicRandom`, `ManualClock`, `SimulationLoop` 과 물리 서브시스템(`VehicleModel`, `RoadModel`, `GimbalActuator`, `TankKinematics`, `PendulumSloshModel`, `ShallowWaterSloshModel`), 드라이버, 평가기(`Evaluator`, `TraceEvaluator`, `HindsightOptimizer`), `ExperimentProfile`. |
| **FluidStabilizer.Controller** | ❌ `noEngineReferences` | Contracts, Core | 스태빌라이저 컨트롤러만: `HoldController`, `SpecificForceAlignmentController`, `PidController`, `PrivilegedFullStateController`, (보류) `LqrSloshController`. **설계의 하드 룰 — "컨트롤러는 `UnityEngine` 참조 0" — 이 `noEngineReferences: true` 로 여기서 보장됨.** |
| **FluidStabilizer.Rl** | ❌ `noEngineReferences` | Contracts, Core, Controller | RL 글루: `ObservationEncoder`, `PrivilegedEncoder`, `RewardFunction`, `RlEnvironment` (잔차 모드 포함), `TrainingServer` (TCP 브릿지), `RemotePolicyController` (정책 서빙 클라이언트), `BcDatasetExporter`. 엔진프리 — Python 학습기와 정책 서버가 Unity 설치 없이 소비. |
| **FluidStabilizer.Sim** | ✅ | Contracts, Core, Controller, Rl, Unity.InputSystem | 얇은 Unity 껍데기: `SimulationRunner`, `HumanDriverInput`, `SimView`, `Hud`, `PlayControls`, `Frames` (SI 오른손 ↔ Unity 왼손 변환이 일어나는 유일한 곳), `ComparisonRunner`, `RoadRibbon`, `TraceRecorder`, `ExperimentProfileStore`. 안쪽으로 Unity 타입을 절대 새지 않음. |
| **FluidStabilizer.Sim.Editor** | ✅ (Editor) | Contracts, Core, Controller, Rl, Sim | 편집기 러너: `SceneBuilder`, `RlServerLauncher`, `HindsightEvalRunner`, `BcExportRunner`, `PolicyEvalRunner`, `TraceEvalRunner`, `BuildRunner`, `SceneCapture`. |
| **FluidStabilizer.Tests.EditMode** | ✅ (Editor 전용) | Contracts, Core, Controller, Rl, Sim | 순수 C# 코드의 빠른 결정론적 유닛 테스트. Unity Test Runner 또는 헤드리스 (`-runTests -testPlatform EditMode`). |

분리가 왜 가치 있나 (그리고 과도한 추상화가 아닌가): 미래 ROS 2 브릿지나 외부
Python 학습기가 `Contracts` + `Controller` (+ `Rl`) 를 **Unity 설치 전혀 없이**
소비할 수 있게 하는 최소한이고, 컨트롤러에서 실수로 `GameObject` 를 참조하면
컴파일 에러가 된다.

## 코드 추가하기

- 새 메시지 타입이나 인터페이스 → `Contracts`.
- 새 엔진프리 헬퍼 (적분기, 필터, 물리, 평가) → `Core`.
- 새 컨트롤러 / 정책 어댑터 → `Controller` (또는 RL 이면 `Rl`).
- `MonoBehaviour`, `Transform`, 입력, 렌더링을 건드리는 것 → `Sim`.
- 편집기 메뉴 / `-executeMethod` 러너 → `Sim.Editor`.

물리 추가 전에 프레임·단위·갱신율은 `docs/frames-units-conventions.md` 참고.
