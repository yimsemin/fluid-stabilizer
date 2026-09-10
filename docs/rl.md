# RL 경로

학습 정책 통합. design-review §11~§12: **언어 중립 Gymnasium 스타일 경계**,
Unity ML-Agents 아님 — 학습기는 Unity 의존성 없는 평범한 Python 프로세스이고,
같은 정책 계약이 나중에 ROS 2 브릿지에도 통한다.

## 왜 여기에 RL 인가

findings F1~F4: 강체(잠금 관절) `Hold` 상대로 **어떤 분석적 반응형 틸트
컨트롤러도 어느 시나리오에서도 못 이긴다** — 탱크를 회전시키는 반응형 피드백이
슬로시 에너지를 더한다. 그러나 완벽한(비인과적) 탱크-틸트 추종은 벽 파도를
0.40× 로 줄인다 (`IdealTiltTracking_…`). 격차는 *예측* — 노이즈 센서 이력에서
숨겨진 슬로시 상태를 추론하고, 하드 기동이 열적으로 소진시킬 수 있는 토크 예산
안에서 파도보다 앞서 행동하는 것. 그게 RL 타깃.

## 구성 요소

| 요소 | 위치 | 상태 |
|---|---|---|
| `ObservationEncoder` | `Assets/FluidStabilizer/Rl/` | ✅ actor obs: 센서 유래, ~단위 스케일, N-프레임 스택(기본 6) + 허용된 유도 felt-tilt |
| `PrivilegedEncoder` | `Rl/` | ✅ 비대칭 크리틱용 float[21] (실제 슬로시 상태, CoM, 액추에이터 + 열) |
| `RewardFunction` | `Rl/` | ✅ §11 연속 reward, 가중치 설정 가능, 항목별 breakdown |
| `RlEnvironment` | `Rl/` | ✅ 결정론적 `Reset(seed)→obs`, `Step(action)→(obs, reward, terminated, truncated, privileged)`. 잔차 모드 지원 (아래 참고) |
| `TrainingServer` | `Rl/` | ✅ length-prefixed 바이너리 TCP 브릿지 (클라이언트 1); in-process env 와 비트 동일하게 루프백 테스트됨 |
| `RemotePolicyController` | `Rl/` | ✅ 학습된 정책의 브릿지 *클라이언트* — `serve_policy.py` 에 연결 (step D) |
| `BcDatasetExporter` | `Rl/` | ✅ hindsight 궤적 → BC 지도학습 데이터셋 (step C) |
| `RlServerLauncher` / `BcExportRunner` / `PolicyEvalRunner` | `Sim/Editor/` | ✅ `-executeMethod` / 메뉴 진입점 (헤드리스 Unity 호스트) |
| Python env + PPO | `python/` | 🟡 비대칭 크리틱 CleanRL 스타일 PPO; end-to-end 실행됨, obs/return 정규화까지 올림. **맨땅 PPO 는 여전히 학습 못 함** — 튜닝이 아니라 잔차 RL / 커리큘럼 / 웜스타트가 필요. "첫 실행 결과" 참고 |
| `bc_pretrain.py` / `serve_policy.py` | `python/` | ✅ BC 사전학습 + 정책 추론 프로세스 (step C/D) |
| 도메인 랜덤화 | — | ⬜ (보류 — 활성 RL 학습 재개 시) |
| 체크포인트 재개 | `train_ppo.py --resume` | ✅ (2026-08-30, 묶음 3) — `state.pt` 로 이어서 학습. 커리큘럼 자동화는 아직 수동 |
| 에피소드 로깅 | `EpisodeLog` → `Logs/episodes.csv` | ✅ (2026-08-30) — 평가/비교 러너가 에피소드당 한 줄 append. RL 학습 자체는 아직 미배선 (필요 시 `RlEnvironment` 에서) |

## 행동 / 관측 / reward

- **행동:** 축당 관절 **각속도** 명령 `[-1, 1]`, `GimbalConfig.MaxRateRadPerSec`
  로 스케일. 각도가 아니라 rate 모드 (design-review §11; F1).
- **Actor 관측:** `ObservationEncoder` — 차량+탱크 IMU, 관절 상태, 모터 상태,
  로드셀 4개, 옵션 레벨/CAN, 마지막 행동, 탱크 프레임에서의 low-pass felt-tilt
  방향 + rate. 프레임 스택. **절대** ground truth 없음.
- **Critic 관측:** actor obs 를 (Python 쪽에서) `PrivilegedEncoder` 출력과 연결.
- **Reward** (`RewardWeights.Default()`, C# 에서 튜닝): `−k1·overflow
  −k2·near-rim² −k3·슬로시 KE −k4·|Δaction| −k5·액추에이터 노력
  −k6·관절한계근접² −k7·탱크틸트² (+k8 생존) − 큰 스필 종료 시 K_term`.

## 실행 방법

**2026-08-29 브릿지 end-to-end 검증** (macOS arm64, M4 Max): 아래 명령이 전체
루프를 돌림 — ~1400 steps/s, 체크포인트 + TensorBoard 로그 기록. **PPO 는 아직
학습 못 함** ("첫 실행 결과" 참고) — 학습기 문제이지 브릿지 문제 아님.

> ⚠️ **헤드리스 서버 시작 전에 Unity Editor 를 닫아라.** Unity 는 프로젝트당
> 단일 인스턴스 — Editor(또는 죽은 `-batchmode` Unity, `AssetImportWorker`)가
> 이미 프로젝트를 잡고 있으면 `-executeMethod` 실행이 조용히 블록되고 포트를
> 안 연다. `pgrep -fl "Unity.app/Contents/MacOS/Unity"` 로 확인하고
> `Temp/UnityLockfile` 이 사라졌는지 확인.

```bash
# 1. 학습기 설치 (한 번). CPython 3.9~3.13 아무거나. 커밋된 python/.venv 는 3.9.6.
cd python && python3 -m venv .venv && . .venv/bin/activate && pip install -r requirements.txt

# 2. 시뮬 서버 시작 (헤드리스 Unity; 계속 켜둠 — Serve() 가 accept 루프에서
#    영원히 블록, 정상, -quit 주지 말 것). ~5 s (웜) ~ 1 분 (콜드).
UNITY="/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity"
"$UNITY" -batchmode -nographics -projectPath . \
  -executeMethod FluidStabilizer.SimEditor.RlServerLauncher.Serve \
  -rlPort 5555 -rlTrace all -rlStack 6 -rlBaseline hold \
  -logFile Logs/rlserver.log &
# 연결 전에 이 줄을 기다림:  [rl] TrainingServer listening on 127.0.0.1:5555
#   ("[rl] launching TrainingServer" 직후 UnityEngine.Debug:ExtractStackTrace...
#    블록은 에러가 아님 — batchmode 는 모든 Debug.Log 에 스택트레이스를 붙임.)

# 3. BC 웜스타트 후 학습
cd python
python bc_pretrain.py --dataset ../Logs/bc/dataset.csv --out runs/bc/actor.pt   # (먼저 메뉴 ▸ Export BC Dataset)
python train_ppo.py --port 5555 --init-actor runs/bc/actor.pt --total-steps 300000 --run-name bc-run
tensorboard --logdir python/runs

# 4. 정책 서빙 (별도 프로세스) + 앱에서 확인
python serve_policy.py --checkpoint runs/bc-run/actor.pt --port 5601
#   그다음 Unity 에서: 메뉴 ▸ FluidStabilizer ▸ Evaluate Learned Policy
#   또는 플레이 씬에서 키 4
```

서버는 한 번에 클라이언트 하나를 서빙하고 학습기가 끊으면 `accept` 로 돌아온다 —
같은 서버 프로세스 상대로 `train_ppo.py` 를 재실행 가능. `-rl*` 시나리오
플래그를 바꿀 때만 서버 재시작.

**런처 플래그:** `-rlPort -rlFill -rlSeconds -rlStack -rlDriver {highway|aggressive|scripted}
-rlSlosh {pendulum|shallow} -rlRoad {none|mild|rough} -rlBriskness -rlAggression`.
`-rlPort/-rlStack` 빼고 전부 이제 **`experiment.txt` 를 기본값으로** 하고,
플래그가 실행별로 override.

**트레이스 기반 학습 (step C0):** 학습에 쓰는 주행은 루트 `datasets.tsv` 의
`train` split 이 정한다 — **hindsight 산출물 유무로 결정하지 않는다.**
`-traceSplit` 로 split, `-rlTrace <substr>` 로 파일명 필터.
**Reset 마다** 해당 주행의 설정·seed·슬로시 모델·전체 길이를 적용한다 (예전의
"첫 주행 설정 일괄 적용 + 가장 짧은 주행 길이로 자르기" 는 제거됐다).
규칙 전체는 [reproducibility.md](reproducibility.md).

**BC 웜스타트 (step C):** `FluidStabilizer ▸ Export BC Dataset` (또는
`-executeMethod …BcExportRunner.Run`)가 각 주행을 실제 obs 파이프라인으로 재생하며
hindsight 행동을 먹여 → `Logs/bc/dataset.csv`. 그다음 `python bc_pretrain.py`
가 `actor_mean` + whitener 를 맞춰 → `runs/bc/actor.pt`, `python train_ppo.py
--init-actor runs/bc/actor.pt` 가 PPO 웜스타트. F5 hindsight 최적해가 거의
무동작이라 BC 가 가르치는 건 저진폭 매끄러운 프라이어(탐색을 절벽에서 떼어놓음)
이지 유능한 정책이 아니다.

## 행동 계약 (PolicyContract) — 2026-09-10

**문제였던 것:** 학습(`RlEnvironment`)은 `rate = baseline + policy·0.35·maxRate`
를 만들고 lock gate 를 통과시켰는데, 배포(`RemotePolicyController`)는
`rate = policy·maxRate` 만 보냈다. baseline 도 잠금도 없었다. 즉 **잔차 정책을
브릿지로 서빙하면 학습 때와 완전히 다른 의미로 실행**됐다. 관측도 1 틱
어긋나 있었다 — 학습은 `(s_t, a_t)`, 배포는 `(s_{t+1}, a_t)` 를 인코딩했다.

**지금:**

- `PolicyContract` 하나가 baseline · 행동 배율 · maxRate · lock 여부 ·
  frame stack · 관측 버전을 담는다.
- `ActionComposer` 하나가 행동 → 명령 합성을 담당하고 **학습과 배포가 같은 것을
  쓴다.**
- 관측 정렬을 배포 쪽에 맞췄다 (`_loop.LatestSensorBundle`). **이전 체크포인트는
  다른 정렬로 학습됐다** — 재학습 대상.
- 계약이 체크포인트를 따라다닌다: 학습 서버 핸드셰이크(v2) → `train_ppo.py` /
  `bc_pretrain.py` 가 체크포인트에 저장 → `serve_policy.py` 가 핸드셰이크로
  되돌려 보냄 → Unity 가 그대로 실행.
- **계약이 없거나 맞지 않으면 조용히 실행하지 않는다.** 없으면 레거시 절대rate
  경로 + `policy-unverified`; 액추에이터 상한이나 frame stack 이 다르면 **거부**
  하고 `hold-fallback` 으로 기록한다 (재스케일 안 함).
- BC 목표도 계약에 맞춘다. 잔차 계약이면
  `target = (교사rate − baselineRate) / actionScale` 로 변환하고, 한계에 걸린
  비율(`clampedFraction`)을 데이터셋 헤더에 기록한다. 절대 목표를 잔차 정책에
  복제하면 "교사 rate + baseline rate" 를 명령하는 다른 궤적을 배운다.

가드: `PolicyContractParityTests` — 같은 초기조건·결정론적 정책으로 학습 환경과
브릿지 경로의 **스필·freeboard·명령 rate·스텝 수가 모두 일치**해야 한다
(plain / hold-잔차 / sfa-잔차 + 잠금). 인코더 클래스가 같다는 건 근거가 아니다.

**정책 서빙 (step D — 배포 분리):** 학습된 정책이 자기 프로세스로 실행,
`python serve_policy.py --checkpoint runs/<run>/actor.pt --port 5601`
(`actor_mean` + whitener 로드, 결정론적 mean 반환, 클라이언트별 스레드). Unity
의 `RemotePolicyController` (엔진프리, `FluidStabilizer.Rl`)가 브릿지 클라이언트:
`ObservationEncoder` 를 로컬로 돌리고 벡터를 보내고, 응답을 관절속도 명령으로
바꿈 — 서버 없으면 잠근 Hold 로 격하 (~2 s 마다 재탐지). wire 포맷은 클래스
doc-comment. 이건 Unity 바이너리에 구운 ONNX 가 *아님* — project-goals.md 참고.

**잔차 모드 (step B):** `-rlBaseline {none|hold|sfa|pid}` `-rlResidualFrac <0..1>`
(기본 0.35). 베이스라인이 있으면 매 제어틱에 env 가 관절속도 공간에서
`baselineRate + policy·(residualFrac·MaxRate)` 를 발행 (베이스라인 각도/잠금
명령은 등가 rate 로 변환 — 각도는 P 추종, 잠금은 0 으로 PD 유지), 둘 다 max
rate 로 clamp. 정책은 멀쩡한 동작점 주변에서 작은 보정만 학습 — F1/F4 절벽의
해법. 행동 공간과 Python 학습기는 변경 없음; 합성은 서버 쪽. `RlEnvironment` 에
`baselineFactory` / `residualRateScaleRadPerSec` ctor 인자 + `ActorName` /
`GimbalState` getter 추가. `ResidualRlTests` (4).

**lock-capable 하이브리드 액션 (D8, `-rlLockCapable`, 기본 baseline≠none 이면 on):**
합성 rate 가 데드밴드(`GimbalConfig.LockRateThresholdRadPerSec`) 안이고 관절이
0 근처면 env 가 `Locked` 명령을 발행 — 처지는 서보가 아니라 강체 관절.
`LockGate` (engine-free 결정론 상태기계, 히스테리시스+최소 체류). ③ 의 바닥이
①(잠근 탱크)이 되어 F5 의 구조적 핸디캡 제거. **결과 (F6): 그래도 후행지식이
강체를 못 이김** — 긴 학습 보류. `LockCapableActionTests` (8), [decisions.md](decisions.md) D8.
**메모:** `serve_policy.py` 는 raw actor 출력(잔차)만 반환하므로, lock-capable
잔차 정책을 실제로 서빙하려면 `RemotePolicyController` 가 같은 baseline 합성 +
`LockGate` 를 로컬로 돌려야 함 (아직 미구현 — 학습 재개 시).

**병렬 롤아웃:** 서버는 단일 클라이언트. N 포트에 N 서버를 띄우고 N 개
`FluidStabEnv` 를 Gymnasium `AsyncVectorEnv` 로 감쌈 (v1 PPO 스크립트는
단일 env — 처리량이 중요해지면 확장).

## 첫 실행 결과 (2026-08-29): 배관은 작동; 맨땅 PPO 는 이 과제를 학습 **못 함**

세 번 실행 (600 k~1 M 스텝): 학습된 정책이 항상 *zero-action 정책보다 나쁨*,
return 곡선이 완전 평평. 별개의 두 문제; 첫째는 고침, 둘째는 과제 설계 결정.

### (1) 학습기가 CleanRL 참조 아래였음 — **고침**

`train_ppo.py` 가 원시 234차원 프레임스택 obs(물리 단위, 스케일 제각각)를
`tanh` MLP 에 바로 먹였고, 스케일 안 된 reward 타깃과 `--ent-coef 0`. 첫 Linear
층이 포화하고 아무것도 학습 안 됨. 이번 세션에 추가: actor obs **와** 특권 크리틱
obs 둘 다에 `RunningMeanStd` whitening (±10 클립); CleanRL 스타일 return 정규화
(reward ÷ 할인 return 의 running std); `--ent-coef` 기본 `0 → 1e-3`; obs
정규화기 통계를 체크포인트에 저장하고 `play.py` 가 재적용; 콘솔이 이제
`return(mean50)` (원시) 를 flush 출력. `v_loss` 가 스파이크 → ~1e-2. 하지만
정책은 (2) 때문에 여전히 개선 안 됨.

### (2) 과제는 예측 문제 — 반응형 탐색이 절벽에서 떨어진다

`-rlDriver aggressive -rlAggression 0.7 -rlFill 0.72` (진자 ROM, 30 s
에피소드)에서 측정한 평균 에피소드 return:

| 정책 | return | 넘치나? |
|---|---|---|
| zero action / 가만히 | **≈ −1800** | 아니오, 에피소드 생존 |
| felt-tilt obs 에 대한 **어떤** 반응형 P 또는 D 컨트롤러, **어떤 gain, 어느 부호든** | ≈ −3200 | 예, 3~4 / 4 |
| 1 M 스텝 후 PPO | ≈ −3250 | 예 |

이건 **정확히 finding F1/F4**: 짐벌이 파도에 반응해 움직이는 순간 슬로시
에너지를 펌핑하고 탱크가 넘친다. 유일한 좋은 정책은 좁은 "hold / 예측" 능선;
무작위 init 주변의 Gaussian 행동 노이즈는 *항상* 절벽 위, 모든 롤아웃이
≈ −3200 에서 조기 종료, 크리틱이 "전부 −3200" 을 학습, gradient 가 사라지고
log-std 가 붕괴. 맨땅 on-policy 학습기는 여기서 예측 정책을 발견 못 한다.

**이건 스텝이나 튜닝이 아니라 과제 설계 변경이 필요.** → [experiment-plan.md](experiment-plan.md)
의 결정으로 이어짐: 잔차 RL (design-review 의 명시된 접근), 쉬운 것부터
커리큘럼, 웜스타트/BC, 훨씬 무거운 action-rate 패널티.

### 결정 (step-back 세션, 2026-08-29) — [experiment-plan.md](experiment-plan.md)

두 가지가 존재하기 전까지 RL 실행 없음: **녹화 인간 주행** (완료 — `DriverTrace`)
과 **feasibility 천장** (고정 녹화 주행에 대한 오프라인 hindsight 궤적 최적화).
천장은 하드 게이트: 특권/hindsight 해가 녹화 주행에서 잠근 Hold 베이스라인을
못 이기면, RL 전에 전제를 조정.

**2026-08-29 갱신 — 천장을 만들고 돌렸고(`HindsightOptimizer`), 게이트 미통과.
finding F5 참고.** 녹화 6 주행(BC·반복 조건 탐색에 쓴 `train` 셋 — 홀드아웃이
아니다, [reproducibility.md](reproducibility.md))에서 hindsight-최적 짐벌-rate 궤적이 잠근
탱크보다 2~6 L *더* 스필 (진자 ROM; 얕은물에서 훨씬 나쁨). 0 을 명령하는
액추에이터 서보 자체가 이미 기계적으로 잠근 관절에 ≈ 5~7 L 진다. **잔차 RL 은
전제 조정(freeboard, 도로 강도, 또는 잔차 베이스라인이 25 L 처지는 서보가 아니라
19 L 잠근 탱크가 되도록 하는 하이브리드 lock-capable 액션 공간) + step-3
재실행이 hindsight 가 잠근 Hold 를 이기는 걸 보이기 전까지 보류.** RL 이 재개되면
여전히 **잔차 RL** — 정책 = 베이스라인 + 작은 보정, `Logs/hindsight/*.traj.csv`
궤적 behaviour-cloning 으로 웜스타트 — 이는 `RlEnvironment` 합성(완료)과
`train_ppo.py` 체크포인트 재개(미구현)가 필요.

## 메모 / 리스크

- **wire 로 스필 지표 없음.** `play.py` 는 shaped reward 만 합산. F4 표는 명명된
  6 시나리오의 리터 스필. `RlEnvironment.Summary()` 가 C# 에서 그 roll-up 을
  만들지만 TCP 프로토콜(RESET/STEP/CLOSE)이 노출 안 함. 정책을 F4 와 비교하려면
  `SUMMARY` opcode 나 `PolicyEvalRunner` 경로(step D, `Logs/policy-eval.txt`)
  필요.
- **체크포인트 재개 (2026-08-30, 묶음 3):** `train_ppo.py --resume runs/<name>/state.pt`
  가 agent + optimizer + obs/priv/return 정규화기 + `global_step` 을 복원해
  이어서 학습한다 (`--init-actor` 보다 우선). `state.pt` 는 `--save-every` (기본
  20) 업데이트마다 + 종료 시 기록 (`actor.pt` 는 매 업데이트 — serve 호환 슬림).
  크래시 복구 + 커리큘럼("어려운 플래그로 서버 재시작 후 --resume")에 씀.
  `runs/` 는 gitignore.
- **브래킷 모델 규율 (F3):** 진자 ROM 으로 학습(쌈), 승리를 신뢰하기 전에
  *항상* `-rlSlosh shallow` 로 재평가. ROM 하에서만 강체를 이기는 정책은
  아마 ROM 아티팩트를 학습한 것.
- **Reward hacking 감시:** 고전적 exploit 은 액체를 하드 end-stop 에 가두는 것.
  항목별 breakdown 의 `joint-limit-proximity` 와 `actuator-effort` 감시;
  발생하면 `k6`/`k5` 올림.
- **Reward 가중치 조정 2026-08-29:** `Overflow` 500 → 5e4, `TerminalSpill`
  5 → 500. 예전 값으로는 50 L 스필이 ≈ −25 만 비용이고 탱크를 쏟아 에피소드를
  끝내는 게 버티는 것보다 *더 나은* 점수 — fail-fast exploit. `RlEnvironmentTests`
  / `RewardFunction` 테스트 여전히 통과.
- **처리량:** 바이너리 브릿지는 v1 에 충분. 병목이면 스텝 배치하거나 호스트를
  standalone .NET 프로세스로 (env 코드는 엔진프리 — 런처만 Unity).
