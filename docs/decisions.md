# 결정 로그 (decisions)

구현 중 내린 아키텍처 결정의 짧은 기록과 이유. 최신순. 이 기록들은
[design-review.md](design-review.md) 를 **보완**하지 대체하지 않는다.

---

## D11 — 2026-10-11 — 이득이 없는 교사는 "잠금"으로 라벨한다, 그리고 BC/DAgger 줄기를 닫는다

교사를 12 합성 주행으로 늘리자(F11) 학습 정책이 학습 주행에서조차 강체보다 나빴다.
원인 중 하나는 라벨이었다: 내보내기가 모든 틱의 해제 요청을 +1 로 고정했기 때문에,
천장이 "아무것도 안 하는 게 최선"인 주행에도 "해제 + 속도 0" 을 가르쳤다. 해제된
서보는 잠긴 탱크가 아니다(F5).

**결정:**

- **유휴 교사 규칙.** 천장이 강체를 max(0.5 L, 10 %) 이상 못 이기는 주행은 교사
  궤적을 정확히 0 으로 만들고(`MultiCornerRunner`), BC 내보내기는 0 궤적 교사에 잠금
  라벨(해제 요청 0)을 붙인다(`BcDatasetExporter`). 가드:
  `ExplicitLock_IdleTeacherLabelsLocked_ActiveTeacherLabelsRelease`.
- **합성 교사는 별도 매니페스트.** `datasets-f8-multi.tsv` — 소유자의 `datasets.tsv`
  분할(train / validation / final)과 섞지 않는다.
- **배포측 해제 보정은 실험 도구.** `serve_policy.py --release-bias / --release-ema`
  는 기본 꺼짐이고, 켜면 정책 식별 문자열에 기록된다(실행 증거에서 구분 가능).
- **BC / DAgger 줄기를 닫는다.** 근거: F11 (교사 늘리기 무효), F12 (DAgger 가 손실은
  줄이나 강체를 못 이기고 분산 큼), F13 (소유자 홀드아웃에서 ③ 이 강체보다 5~15 % 나쁨).
  재개 조건은 [experiment-plan.md](experiment-plan.md) "앞으로 할 수 있는 것" 표.

**기각한 대안:** 교사 주행을 더 늘리기(F11 의 구조적 원인이 그대로), DAgger 시드·반복
확대(분산이 크고 평균이 강체 아래로 내려가지 않음), 해제 임계 상향(편향이 크면 정확히
강체와 같아질 뿐 이득이 없음).

---

## D10 — 2026-09-11 — 잠금은 dead-band 가 아니라 정책이 요청한다 (D8 수정)

D8 은 lock-capable 액션으로 F5 의 구조적 핸디캡을 없앴지만, **잠금 여부를 합성된
rate 로부터 사후 판정**했다. finding F9 가 그 대가를 측정했다: 잔차 액션 공간이
**절벽**이 된다 — dead-band 안에서는 행동 효과가 정확히 0(학습 신호 없음), 넘으면
스필 2.4 배, 사이에 기울기가 없다. 가우시안 정책의 꼬리가 문턱을 넘나들어 절벽
아래로 착지한다.

**결정: 잠금을 정책의 명시적 선택으로 만든다.**

- **3번째 액션 = 해제 요청.** 학습 안 된 네트워크의 출력 ≈ 0 이 곧 잠김 = 강체다.
  우리가 가진 최선의 해석적 결과를 정책이 공짜로 받는 게 맞다.
- **시작을 잠긴 상태로.** 체결이 0.5° 이내에서만 가능하므로, 시작 후 서보가 관절을
  움직이면 다시는 잠글 수 없는 **일방통행**이었다 — 정책이 잠금을 못 배운 진짜 이유.
- **해제 요청을 저역통과** (gain 0.1 ≈ 10 틱, 문턱 0.5). 기계식 잠금을 100 Hz 로
  재결정하지 않는다. 이게 없으면 에피소드가 ~2500 번의 독립 추출이라, 평균이
  "잠금 유지" 인 정책도 즉시 해제를 뽑는다.
- **관측에 잠금 상태 추가** (frame 39 → 40, `obsVersion` 2). 컨트롤러 자신의
  상태이지 특권 정보가 아니다.
- 물리 제약(0 각도 체결, 최소 유지 틱)은 **그대로** 둔다. 학습 편의로 기계를
  거짓말시키지 않는다.

**결과 (F10):** 절벽이 진짜 바닥이 됐다 — 해제를 요청하지 않는 한 잔차가 무엇이든
스필이 강체와 정확히 같다 (③ ⊇ ① 이 a = 0 뿐 아니라 전 구간에서 성립). 그리고
③ 이 처음으로 학습해 규칙기반을 51 % 앞섰다.
가드: `ExplicitLock_KeepsTheRigidFloor_ForAnyResidual`.

---

## D9 — 2026-09-10 — 정책 행동 계약을 한 곳에 (PolicyContract)

학습(`RlEnvironment`)은 `baseline + policy·0.35·maxRate` 를 만들고 lock gate 를
통과시켰는데, 배포(`RemotePolicyController`)는 `policy·maxRate` 만 보냈다.
baseline 도 잠금도 없었다 — **잔차 정책을 브릿지로 서빙하면 학습 때와 완전히 다른
의미로 실행**됐다. 관측도 1 틱 어긋나 있었다 (학습 `(s_t, a_t)`, 배포 `(s_t+1, a_t)`).

**결정: 행동 합성 규칙을 하나의 타입으로 묶고, 계약이 체크포인트를 따라다니게 한다.**

- `PolicyContract` = baseline · 행동 배율 · maxRate · 잠금 모드 · frame stack ·
  관측 버전. `ActionComposer` 하나가 합성을 담당하고 **학습과 배포가 같은 것을 쓴다.**
- 관측 정렬을 배포 쪽에 맞췄다. 이전 체크포인트는 재학습 대상이다.
- 전달 경로: 학습 서버 핸드셰이크 v2 → `train_ppo.py` / `bc_pretrain.py` 가
  체크포인트에 저장 → `serve_policy.py` 가 되돌려 보냄 → Unity 가 그대로 실행.
- **의미가 불명확한 정책을 조용히 실행하지 않는다.** 계약이 없으면 레거시
  절대rate 경로 + `policy-unverified`; 액추에이터 상한이나 frame stack 이
  다르면 재스케일하지 않고 **거부**하고 `hold-fallback` 으로 기록한다.
- BC 목표도 계약에 맞춰 변환한다 (잔차면 `(교사rate − baselineRate) / actionScale`,
  잘린 비율을 데이터셋 헤더에 기록).

**가드:** `PolicyContractParityTests` — 같은 초기조건·결정론적 정책에서 학습 환경과
브릿지 경로의 **스필·freeboard·명령rate·스텝수가 모두 일치**해야 한다.
인코더 클래스가 같다는 건 근거가 아니다.

---

## D8 — 2026-08-30 — lock-capable 하이브리드 액션 공간 (묶음 3, F5 전제 조정)

finding F5: 완전한 후행지식 짐벌 궤적조차 녹화 주행에서 기계적으로 잠근 탱크(①)를
못 이긴다. 근본 원인은 *능동* 컨트롤러의 "hold" 가 강체 관절이 아니라 슬로시
모멘트에 토크 제한되어 처지는 위치 서보라는 것 — 0 을 명령해도 이미 잠근 관절에
≈ 5~7 L 진다 (`무동작` 25 L vs `잠금` 19 L). F5 가 후보로 지목한 세 전제 조정 중
하나가 "능동 보정할 때만 아니면 기계적으로 잠글 수 있는 하이브리드 액션 공간".

**결정.** 합성된 관절속도 명령에 데드밴드 + 히스테리시스 게이트(`LockGate`,
엔진프리 결정론적 상태기계)를 둔다:

- 합성 rate `max(|pitch|,|roll|)` 가 `LockRateThresholdRadPerSec` (기본 0.06)
  미만이고 두 관절이 `LockAngleThresholdRad` (기본 0.5°) 안이면 →
  `StabilizerCommand.Hold` (Locked 모드) 발행. 구조가 모멘트를 받고 토크 0,
  권선 냉각.
- 일단 잠기면 rate 요구가 `LockRateThreshold × UnlockRateFactor` (기본 2.5×) 를
  넘어야, 그리고 `MinLockDwellTicks` (기본 8) 이 지나야 능동 모드로 전환 —
  100 Hz 채터 방지.
- 잠금은 `|angle| < 0.5°` 일 때만 허용 → 액추에이터의 즉시 0 스냅
  (`GimbalActuator.LockAxis`) 이 무시할 수준. 하중에 눌려 서보가 0 으로 못
  돌아오면 그냥 능동 유지.

**적용 범위.** 액션 벡터 차원(2)·wire 포맷·Python 학습기 **변경 없음** — 게이트는
전부 C# 컨트롤러 쪽. `RlEnvironment(lockCapable: true)` 의 잔차 컨트롤러,
`HindsightOptimizer`(`opts.LockCapable`)의 hindsight 액터, (묶음 3 뒤) 서빙 시
`RemotePolicyController` 가 같은 `LockGate` 를 씀. 기본 off — 기존 결정론 가드·
테스트·초안 결과 불변.

**효과 (의도).** ③ 의 바닥이 처지는 서보(25 L)가 아니라 강체 탱크(19 L, =①) 가
된다 → **③ ⊇ ①**. "학습이 강체를 이긴다" 가 구조적으로 가능해지고, F1/F4 탐색
절벽도 완화된다 (0 근처 무작위 행동 = 여전히 잠김 = 페널티 없음). ③ 이 ① 을
포함하게 되어 두 레인의 구분이 흐려지는 트레이드오프는 받아들인다 — 이게 F5 가
명시적으로 제안한 조정이고, 능동 스태빌라이저가 "필요없을 때 강체로 있기" 를
선택하는 것은 실제 로봇으로도 더 현실적.

**리워드 불변.** Locked 일 때 `AppliedTorque = 0` 이라 effort 항이 자동으로 0.
잠금 보너스는 reward-hacking 표면만 넓혀서 넣지 않음.

세부: `LockGate.cs`, `GimbalConfig` 의 `Lock*` 필드. `-hsLockCapable` /
`-rlLockCapable` 로 켠다.

**결과 ([F6](findings.md), 2026-08-30):** lock-capable 이 F5 의 구조적 핸디캡은
없앴다 (do-nothing 바닥이 25 L → 잠근 Hold 19 L). 하지만 2c 스윕 11개 운영점에서
hindsight 는 여전히 강체를 못 이긴다 — 강체가 넘치는 모든 fill 에서 회복 1~2%.
잔차 RL 은 시작하지 않음. lock-capable 인프라는 유지 (전제가 바뀌면 재사용).

**가드:** `LockCapableActionTests` (8) — 게이트 잠금/해제/히스테리시스/최소 체류,
큰 각도에서 잠금 거부, lock-capable env 결정론, 잠금 시 액추에이터 토크 0,
hindsight `무동작` 바닥이 강체 Hold 와 일치, config 오버라이드 전파.

---

## D7 — 2026-08-30 — 좌우 바퀴 트랙 비대칭 + lumped 슬립 프록시 (묶음 2)

소유자가 초안을 만져본 뒤 두 가지 현실성 갭을 지적: (a) "왼쪽 바퀴에 돌맹이 →
왼쪽으로 들썩" 이 안 됨 (D6 은 단일 트랙 프로파일), (b) "빗길/눈길" 프리셋이
별로 안 미끄러움.

**결정:**

1. **좌우 바퀴 트랙을 구분한다.** `RoadInput.SurfaceHeightLeft/Right`, 평균이
   `SurfaceHeight`. 긴 파동 공유, 짧은 거칠기 좌우 독립, 이산 이벤트의 절반쯤은
   한쪽 바퀴만. `(L−R)` 이 body roll 을 유발 (기존 roll 서스펜션으로 필터).
   `RoadConfig.WheelTrackRoughnessFrac` (0 = 옛 대칭 모델). 차등 채널은 전용
   RNG 스트림 → 도로 평균/grade/bank/이벤트/곡률은 바이트 동일.
2. **lumped 타이어-슬립 프록시.** design-review §8/§18 이 완전한 타이어 모델을
   defer 했지만, "미끄러움" 이 그냥 낮은 마찰 한계면 급기동이 덜 먹혀 오히려
   슬로시가 준다 — 비현실적. 그래서 `_slip ∈ [0,1]` 상태: 요구가 한계를 넘으면
   빠르게 상승·천천히 회복, 슬립 중 접지력 추가 감소 + 조향 under-response 후
   grip 복귀 시 "홱 잡힘" + OU fishtail. 온셋 1.02 (한계 초과 시에만) →
   정상 하드 주행 불변, adversarial/저접지만 슬립. 전용 RNG fork.

상세: [road-model.md](road-model.md). 결과: 도로-on 시나리오 수치가 다시
조금 이동 (roll 채널 추가). `EvaluatorTests` 가드 하나(`RigidHold_NotBeaten...`)
는 10× → 유지 (온셋 1.02 로 정상 주행 슬립 없앰).

---

## D6 — 2026-08-29 — 도로 모델 + 서스펜션 heave DOF

계획 리뷰([project-goals.md](project-goals.md)) 결과. 도로에는 스텁(`RoadInput`)만
있고 아무것도 채우지 않아서 시뮬이 완벽하게 평평한 도로에서 돌았다 — design-review
§4/§5 가 v1 필수 모델링 갭으로 지적한 부분이고, 강체 Hold 베이스라인을 인위적으로
이기기 어렵게 만든다 (finding F4).

**결정:**

1. **도로 = 이동 거리의 결정론적 함수** (시간이나 미리 구운 heightmap 이 아님):
   언덕(grade), 커브 bank, red-spectrum 거칠기(사인 합), 이산 raised-cosine
   이벤트(포트홀/조인트/돌)를 `s` 를 따라 Poisson 과정으로. 엔진프리 `RoadModel`,
   `SimulationConfig.Road` 뒤에; **기본 비활성** — 기존 시나리오와 결정론 가드가
   바이트 단위로 동일하게 유지되도록. `Severity` 손잡이 하나가 거칠기+이벤트를
   스케일 (미래 커리큘럼용). 상세: [road-model.md](road-model.md).
2. **단일 트랙 도로 프로파일** (좌/우 바퀴 트랙 분리 없음), **grade/bank 는
   차체 트림으로** (도로 유발 yaw 없음) — v1. 액체로 가는 지배적 disturbance
   경로는 수직 + grade 이고, 이렇게 하면 모델이 싸고 상태가 작다.
3. **`VehicleModel` 에 heave DOF 추가** (pitch+roll 만 있었음; design-review §8
   은 셋 다 요구). 차체가 ~1.5 Hz 트럭 승차 주파수의 spring-mass-damper 로 노면을
   추종하므로, 범프가 탱크에 도달할 때 원시 가속 스파이크가 아니라 서스펜션이
   필터링한 jolt 로 온다 (design-review §4). `RoadInput.VerticalAccel` →
   `RoadInput.SurfaceHeight` (이제 서스펜션이 가속을 만듦).

결과: 거친 도로 시나리오가 학습정책 vs 강체탱크 비교의 의도된 홈그라운드 —
강체 탱크는 도로 입력을 필터링 못 하고, 예측 정책은 할 수 있다.

---

## D5 — 2026-08-28 — `Hold` 는 잠근 관절; 액추에이터 리사이즈, 열 한계 강제

F3 부록(`Hold` 위치 서보가 공격적 기동 중 실제 슬로시 모멘트에 ~30° 포화·처짐)
이후의 연결된 두 결정.

**1. `Hold` = 0 을 명령하는 서보가 아니라 기계적으로 잠근 관절.**
새 `StabilizerCommandMode.Locked`: 두 관절 모두 0 에 고정, 슬로시 모멘트는 차체
구조(v1 에서 무한 질량, D1)가 받고, 액추에이터는 토크를 안 쓰고 포화·처짐·발열이
불가능. `HoldController` 가 이걸 발행. 이게 진짜 "능동 안정화 없음" 바닥 —
모든 컨트롤러와 RL 정책이 진짜 강체 탱크를 상대로 채점된다. 이유: 예전 "0도
각도 홀드" `Hold` 는 "스태빌라이저 없음" 과 "약한 스태빌라이저" 를 혼동했고,
F2 의 헤드라인 결과가 그 산물이었다 (F4 참고).

**2. 현실성을 위해 액추에이터 리사이즈; under-actuation 은 의도된 도전으로 유지.**
소유자 프레이밍: 제한된 authority 가 요점 — Physical AI 는 실제 로봇 제약을
극복해야 한다. 하지만 값이 *그럴듯*해야 하고, quasi-static 봉투조차 못 버티는
스태빌라이저는 그렇지 않다.
- `MaxTorqueNm` 650 → **900**. 0.45 g friction-circle 한계에서 최악
  quasi-static 슬로시 모멘트 ≈ 740 N·m; 900 은 ~20% 마진으로 커버하지만
  공격적 *동적* 틸트 추종에는 여유가 없다.
- `ContinuousTorqueNm` 280 → **420**, 이제 **강제됨**: `GimbalActuator` 의
  I²t 권선열 모델 (상태 `Thermal ∈ [0,1]`, `ThermalTimeConstantSeconds` = 15 s).
  연속 이상의 토크는 권선을 데우고 사용가능 피크가 900 에서 420 쪽으로 디레이트;
  연속 이하면 냉각. 버스트는 무료, 지속적 하드 기동은 아님 — 정책이 관리할
  명시적 authority 예산. `Thermal` 은 `GimbalAxisState` 와
  `PrivilegedObservation` 에 노출.

결과 (F4): 강체 `Hold` 상대로 어떤 분석적 반응형 틸트 컨트롤러도 어느 시나리오에서도
못 이긴다. 강체를 이기려면 예측/학습이 필요하다.

---

## D4 — 2026-08-28 — 얕은물 슬로시 모델: 스킴과 커플링 선택

`ShallowWaterSloshModel` (phase-2 모델 A, design-review §6) 은 바뀌지 않은
`ISloshModel` 인터페이스 뒤의 2-D 깊이평균 Saint-Venant 솔버. 내린 선택:

- **스킴.** 1차 유한체적, **Rusanov (local Lax–Friedrichs)** flux, forward
  Euler, ~48×28 격자(명목 탱크, ~25 mm 목표 셀 크기). 반사 벽(정수압만 통과),
  벽 셀이 rim 을 넘으면 weir overflow. 정확도보다 견고성과 양수성(positivity)
  보장을 택함. 수치적으로 확산성이지만 1차 모드가 ~90 셀/파장으로 해상되어
  주파수는 여전히 진자 모델과 같은 ±15% 대역 안. MUSCL/제한 재구성은 나중
  결과가 덜한 감쇠를 요구하면 drop-in 업그레이드.
- **결정론.** 600 Hz 틱마다 서브스텝 수 = `ceil(1.5·dt·s_max/CFL)` (CFL 0.40),
  필드의 결정론적 함수 — wall-clock 없음, 스레딩 없음. 하드 클램프
  (h ≥ 1e-6, |u| ≤ 8 m/s) 는 진자 모델의 anti-NaN 가드를 미러링.
- **프레임.** 내부 격자 원점 = **탱크 바닥 중심**, `PendulumSloshModel` 의 유효
  규약과 일치 (진자도 바닥 중심 기준 토크를 반환). 실제 짐벌 피벗 높이가 토크
  기준이 되어야 하면 두 모델이 함께 바뀐다.
- **반응 (D3 적용).** 솔버는 탱크 각가속(Euler) 항을 포함한 *실제* 운동을
  적분한다. 탱크로 되먹이는 반응은 **quasi-steady**:
  Σ mₖ·(g − a_O − ω×(ω×r) − 2ω×v_rel) 셀당, 즉 액체 질량분포 × body force
  에서 Euler 항 생략. 그 항은 진자 모델이 쓰는 *같은* 분석적 등가역학 추정에서
  계산한 상수 **reflected inertia** 로 전달 — 그래서 액추에이터는 두 모델에서
  동일한 회전 하중을 느끼고 파동 동역학만 다르다. 파동 자체의 momentum flux 대
  벽(2차 반응 보정)은 v1 에서 되먹이지 않음.
- **파동 breaking + rim run-up** (finding F3 스테이지 1 이후 추가). NLSW 는 파동을
  깰 수 없어 벽 run-up 을 과대예측. 두 추가: (1) 파동-breaking eddy viscosity —
  ~31° 보다 가파른 front 를 가로질러 확산성 momentum flux `ν = C·Δ·√(g·h̄)·ramp`
  (Kennedy et al. 2000, slope-triggered), `C = 0.6`; (2) weir overtopping head
  는 셀당 low-pass 경계면(`τ = 0.2 s`)에서 측정 → 물러나는 일시적 벽 slap 은
  거의 스필 안 함. 상수는 문헌 범위 안; `HighwayBriskLowFreeboard` 판정이 여기
  약간 민감 (finding F3).
- **`SloshParameters` 에서 안 쓰는 것:** `ParticipatingMassScale` (필드에
  내재, reflected-inertia 추정 제외), `SurfaceSlopePerPendulumAngle` (실제
  표면이 있음). `NaturalFrequencyScale` 은 gravity-wave 항만 스케일.
- **`ModeState`** 는 평균 자유표면 기울기에서 *등가* pitch/roll 슬로시 각+rate
  로 보고 (특권 베이스라인이 계속 작동하도록).

진자 ROM 이 대량 RL 의 기본으로 유지(더 쌈); 얕은물은 정밀도 체크이자 옵션
고정밀 평가/학습 모델.

---

## D3 — 2026-08-28 — 슬로시 ↔ 액추에이터 커플링: 관성 vs 하중 분리

슬로시 모델과 짐벌 액추에이터는 양방향 결합. 전체 슬로시 반응 토크를 액추에이터에
외부 하중으로 먹이면 불안정한 명시적 대수 루프가 생김 (반응의 각가속 항이 음의
관성처럼 작용). 해결:

1. **반응을 분리.** 슬로시 모델이 축당 대략 상수인 `ReflectedInertia*` 를 보고
   (`IReflectedInertiaSource`); 시뮬이 그걸 액추에이터 축 관성에 더함. 그러면
   모델이 매 스텝 반환하는 반응 토크는 탱크 각가속 항을 생략하고, 진짜
   상태의존(θ, θ̇, base 운동) disturbance 만 남아서 한 스텝 지연으로 외부 하중
   되먹임 — 600 Hz 에서 안정.
2. **짐벌 운동 하에서 피벗이 평행이동하지 않음.** `TankKinematics` 는 피벗의
   선가속을 차체 운동만으로 계산. 짐벌 피벗은 회전 중심이라 관절 rate/accel 이
   그걸 안 움직임; 포함하면 큰 lever-arm(≈2.6 m) 되먹임을 슬로시 모델에 주입.
3. **액추에이터는 순수 rate 루프가 아니라 위치 서보.** 명령된 rate 가 위치
   setpoint 로 적분되고 PD+I 루프가 추종 → 정상 외부 하중이 drift 가 아니라
   유계 sag 를 만듦.

진자 각/rate 는 ROM 유효 범위(~75°, 25 rad/s)로 클램프 — 나쁜 일시현상이
NaN 으로 번지지 않게. 정상 사용 중 클램프에 닿으면 봉투 밖 시나리오 신호.

---

## D2 — 2026-08-27 — 옵션 센서는 `Nullable<T>` 가 아니라 `Has*` 플래그

`Nullable<struct>` 는 Unity 직렬화를 못 견디고 wire/ROS 표현에 깔끔히 매핑 안 됨.
`SensorBundle` 은 대신 `HasLevels` / `HasVehicleCan` 불리언을 담는다.

---

## D1 — 2026-08-27 — 기계 시뮬레이션은 PhysX 가 아니라 매니지드 코드로

**결정.** 문제의 전체 강체 동역학 — 차량 스프렁매스 운동, 2축 짐벌, 슬로시
모델 — 을 `FluidStabilizer.Core` 의 평범한 C# 로 고정 **600 Hz** 스텝에서
적분한다. Unity PhysX 는 어떤 시뮬레이션 동역학에도 **쓰지 않는다**.
`FluidStabilizer.Sim` 은 시각화, 입력, 씬 배선에만 Unity 를 쓴다.

> (2026-08-29 정정: 이 문서 예전 버전에 "500 Hz" 라고 적혀 있었으나 실제 고정
> 스텝은 **600 Hz** 다 — 200 Hz 센서가 정수로 나눠지도록 500 대신 600 선택,
> [frames-units-conventions.md](frames-units-conventions.md) 참고.)

**이유.**

- **Mac 과 Windows 간 결정론.** Unity 의 PhysX 는 같은 머신/빌드에서만 재현
  가능; 크로스플랫폼 비트 재현성은 보장 안 됨. 설계 리뷰가 결정론을 top-5
  리스크로 꼽고 소유자가 두 플랫폼에서 개발하려 함. seeded RNG 를 가진 매니지드
  고정스텝 적분기는 어디서나 비트 동일.
- **시스템이 작다.** 차량 heave/pitch/roll + 종/횡 운동학(~10 상태), 짐벌
  관절 2개 + 액추에이터 내부 상태(~8), 슬로시 진자 2개(~4). 총 ~20 상태 변수 —
  커스텀 semi-implicit / RK4 적분기가 600 Hz 에서 쉽게 처리.
- **설계가 이미 이쪽을 가리킨다.** 커스텀 3-DOF 스프렁매스 차량(WheelColliders
  아님)과 슬로시 ODE 를 PhysX 밖에서 적분하기를 요구. "커스텀" 을 전체 기계
  코어로 확장하면 PhysX 바디와 매니지드 슬로시 ODE 사이의 seam 이 없어짐 —
  거기가 솔버 노이즈와 커플링 버그가 살 곳.
- **속도와 테스트 가능성.** RL 스텝 루프에 물리엔진 없음 → 초당 더 많은
  시뮬레이션 스텝, 전체 시뮬레이션이 Unity 프로세스 없이 유닛 테스트 가능
  (엔진프리 어셈블리에 산다).

**비용 / 리스크.**

- 자체 contact/도로 모델과 짐벌 관절 동역학을 작성. 정밀도를 의도적으로 modest
  하게 유지(설계 리뷰 §7–§9)하고 테스트에서 분석 케이스로 검증해 완화.
- 탱크가 짐벌을 통해 차량에 kinematic 하게 붙어서 v1 에선 슬로시 힘이 차량을
  안 움직임(트럭 질량 ≫ 탱크+액체). 슬로시는 짐벌 액추에이터와 로드셀에는
  하중을 준다. 이 근사는 문서화되고 재검토 가능.

**레이아웃 결과.** `FluidStabilizer.Core` 가 시뮬레이션 루프와 물리 서브시스템도
담는다. `Sim` 은 얇은 Unity 껍데기로 축소.
