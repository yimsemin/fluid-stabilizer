# RL 경로

학습 정책의 통합 방식. design-review §11~§12: **언어 중립적인 Gymnasium 스타일 경계**를 쓰고 Unity
ML-Agents 는 쓰지 않는다. 학습기는 Unity 에 의존하지 않는 평범한 Python 프로세스이고, 같은 정책 계약을
나중에 ROS 2 브릿지에도 그대로 쓸 수 있다.

## 왜 여기에 RL 인가

findings F1~F4: 강체(잠금 관절) `Hold` 를 상대로 **어떤 분석적 반응형 틸트 컨트롤러도 어느 시나리오에서도
이기지 못한다.** 탱크를 돌리는 반응형 피드백이 슬로시 에너지를 더하기 때문이다. 그러나 완벽한(비인과적) 탱크
틸트 추종은 벽 파도를 0.40 배로 줄인다(`IdealTiltTracking_…`). 격차는 *예측*에 있다. 노이즈가 있는 센서 이력에서
숨겨진 슬로시 상태를 추론하고, 강한 기동이 열적으로 소진시킬 수 있는 토크 예산 안에서 파도보다 앞서
행동해야 한다. 이것이 RL 의 목표였다.

## 구성 요소

| 요소 | 위치 | 상태 |
|---|---|---|
| `ObservationEncoder` | `Assets/FluidStabilizer/Rl/` | ✅ actor 관측: 센서에서 유도, 단위 스케일 정규화, N 프레임 스택(기본 6) + 허용된 유도값(felt-tilt) |
| `PrivilegedEncoder` | `Rl/` | ✅ 비대칭 critic 용 float[21] (실제 슬로시 상태, CoM, 액추에이터 + 열) |
| `RewardFunction` | `Rl/` | ✅ design-review §11 의 연속 reward. 가중치 설정 가능, 항목별 분해 제공 |
| `RlEnvironment` | `Rl/` | ✅ 결정론적 `Reset(seed)→obs`, `Step(action)→(obs, reward, terminated, truncated, privileged)`. 잔차 모드 지원(아래 참고) |
| `TrainingServer` | `Rl/` | ✅ length-prefixed 바이너리 TCP 브릿지(클라이언트 1개). in-process env 와 비트 단위로 같게 루프백 테스트됨 |
| `RemotePolicyController` | `Rl/` | ✅ 학습된 정책의 브릿지 *클라이언트* — `serve_policy.py` 에 연결 (step D) |
| `BcDatasetExporter` | `Rl/` | ✅ hindsight 궤적 → BC 지도학습 데이터셋 (step C) |
| `RlServerLauncher` / `BcExportRunner` / `PolicyEvalRunner` | `Sim/Editor/` | ✅ `-executeMethod` / 메뉴 진입점 (헤드리스 Unity 호스트) |
| Python env + PPO | `python/` | 🟡 비대칭 critic 을 쓰는 CleanRL 스타일 PPO. 끝까지 실행됨. obs/return 정규화 적용. **맨땅 PPO 는 학습하지 못한다**(F1·F4·F9·F10). BC 클론이 PPO 미세조정보다 낫다(F10). 아래 "맨땅 PPO 가 안 되는 이유" 참고 |
| `bc_pretrain.py` / `serve_policy.py` | `python/` | ✅ BC 사전학습 + 정책 추론 프로세스 (step C/D). `serve_policy.py --release-bias/--release-ema` 는 해제 요청 보정 실험 옵션(기본 꺼짐, F12) |
| `DaggerRolloutRunner` / `dagger.py` | `Sim/Editor/` · `python/` | ✅ 학습자 상태를 교사 행동으로 다시 라벨링 + 누적 재학습 루프 (F12). **줄기는 닫힘** (D11) |
| `MultiCornerRunner` | `Sim/Editor/` | ✅ F8 계열 합성 코너링 주행 + 주행별 천장 + `datasets-f8-multi.tsv` (F11). 천장이 이득을 못 주는 주행은 0 궤적(유휴) |
| 도메인 랜덤화 | — | ⬜ (보류 — 활성 RL 학습을 재개할 때) |
| 체크포인트 재개 | `train_ppo.py --resume` | ✅ `state.pt` 로 이어서 학습. 커리큘럼 자동화는 아직 수동 |
| 에피소드 로깅 | `EpisodeLog` → `Logs/episodes-v2.csv` | ✅ 평가/비교 러너가 에피소드당 한 줄을 덧붙인다. RL 학습 자체는 아직 연결하지 않았다(필요하면 `RlEnvironment` 에서) |

## 행동 / 관측 / reward

- **행동:** 3차원. 축별 관절 **각속도** 명령 2개 `[-1, 1]`(`GimbalConfig.MaxRateRadPerSec` 로 스케일.
  각도가 아니라 rate 모드이며 design-review §11, F1 참고. 베이스라인이 있으면 그 위에 얹는 잔차)와, 세 번째인
  **잠금 해제 요청**(D10 — 0 근처면 잠금 = 강체)이다. 합성 규칙은 "행동 계약" 절에 있다.
- **Actor 관측:** `ObservationEncoder` — 차량 + 탱크 IMU, 관절 상태, 모터 상태, 로드셀 4개, 선택적 레벨/CAN,
  마지막 행동, 탱크 프레임에서 저역통과한 felt-tilt 방향과 rate. 프레임 스택. ground truth 는 **절대** 들어가지
  않는다.
- **Critic 관측:** actor 관측에 (Python 쪽에서) `PrivilegedEncoder` 출력을 이어 붙인다.
- **Reward** (`RewardWeights.Default()`, C# 에서 튜닝): `−k1·overflow −k2·near-rim² −k3·슬로시 KE
  −k4·|Δaction| −k5·액추에이터 노력 −k6·관절한계근접² −k7·탱크틸트² (+k8 생존) − 큰 스필 종료 시 K_term`.

## 실행 방법

**브릿지가 끝까지 동작함을 확인했다**(macOS arm64, M4 Max). 아래 명령이 전체 루프를 돌린다. 약 1400
steps/s 이며 체크포인트와 TensorBoard 로그를 기록한다. 맨땅 PPO 가 학습하지 못하는 이유는 학습기나 브릿지가
아니라 과제 설계(액션 공간)에 있다. "맨땅 PPO 가 안 되는 이유" 절을 보라.

> ⚠️ **헤드리스 서버를 시작하기 전에 Unity Editor 를 닫아라.** Unity 는 프로젝트당 인스턴스 하나만 열 수
> 있다. Editor(또는 죽은 `-batchmode` Unity, `AssetImportWorker`)가 이미 프로젝트를 잡고 있으면
> `-executeMethod` 실행이 조용히 멈추고 포트를 열지 않는다. `pgrep -fl "Unity.app/Contents/MacOS/Unity"`
> 로 확인하고 `Temp/UnityLockfile` 이 없는지 확인한다.

```bash
# 1. 학습기 설치 (한 번). Python 3.12 는 uv 가 내려받는다. 자세히: python/README.md
cd python && uv sync

# 2. 시뮬 서버 시작 (헤드리스 Unity; 계속 켜 둔다 — Serve() 가 accept 루프에서
#    영원히 대기하는 것이 정상이므로 -quit 를 주지 말 것). 따뜻한 시작 ~5 s ~ 콜드 ~1 분.
UNITY="/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity"
"$UNITY" -batchmode -nographics -projectPath . \
  -executeMethod FluidStabilizer.SimEditor.RlServerLauncher.Serve \
  -rlPort 5555 -rlTrace all -rlStack 6 -rlBaseline hold \
  -logFile Logs/rlserver.log &
# 연결하기 전에 이 줄이 나올 때까지 기다린다:  [rl] TrainingServer listening on 127.0.0.1:5555
#   ("[rl] launching TrainingServer" 직후에 나오는 UnityEngine.Debug:ExtractStackTrace...
#    블록은 에러가 아니다 — batchmode 는 모든 Debug.Log 에 스택트레이스를 붙인다.)

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

서버는 한 번에 클라이언트 하나를 서빙하고, 학습기가 연결을 끊으면 `accept` 로 돌아온다. 그래서 같은 서버
프로세스에 `train_ppo.py` 를 다시 실행할 수 있다. `-rl*` 시나리오 플래그를 바꿀 때만 서버를 다시 시작한다.

**런처 플래그:** `-rlPort -rlFill -rlSeconds -rlStack -rlDriver {highway|aggressive|scripted|steady}
-rlSlosh {pendulum|shallow} -rlRoad {none|mild|rough} -rlBriskness -rlAggression`.
`-rlPort/-rlStack` 를 뺀 나머지는 모두 **`experiment.txt` 를 기본값으로** 하고, 플래그가 실행마다 이를
덮어쓴다.

**트레이스 기반 학습 (step C0):** 학습에 쓰는 주행은 루트 `datasets.tsv` 의 `train` split 이 정한다 —
**hindsight 산출물이 있느냐로 정하지 않는다.** `-traceSplit` 으로 split 을, `-rlTrace <substr>` 로 파일명
필터를 지정한다. **Reset 할 때마다** 해당 주행의 설정·seed·슬로시 모델·전체 길이를 적용한다. 규칙 전체는
[reproducibility.md](reproducibility.md).

**BC 웜스타트 (step C):** `FluidStabilizer ▸ Export BC Dataset`(또는 `-executeMethod …BcExportRunner.Run`)이
각 주행을 실제 관측 파이프라인으로 재생하면서 hindsight 행동을 먹여 `Logs/bc/dataset.csv` 를 만든다. 이어서
`python bc_pretrain.py` 가 `actor_mean` 과 whitener 를 맞춰 `runs/bc/actor.pt` 를 만들고,
`python train_ppo.py --init-actor runs/bc/actor.pt` 가 이를 PPO 웜스타트에 쓴다. F5 의 hindsight 최적해가
거의 무동작이어서, BC 가 가르치는 것은 유능한 정책이 아니라 저진폭의 매끄러운 prior(탐색을 절벽에서 떼어
놓는다)다.

**교사 다양화 · DAgger (F11·F12 — 줄기는 닫힘, D11):**
`MultiCornerRunner.Run` 이 합성 주행을 만들고, `-traceManifest datasets-f8-multi.tsv` 로 내보내기와 평가에
쓴다. `python/dagger.py --dataset … --manifest …` 는 학습자를 굴려 방문한 상태를 다시 라벨링하고 누적 재학습한다.
천장이 이득을 주지 못하는(유휴) 교사는 내보내기에서 **잠금 라벨**이 된다. 결과는 강체를 이기지 못했다. 재개
조건은 [experiment-plan.md](experiment-plan.md).

## 행동 계약 (PolicyContract)

**왜 계약이 필요한가:** 학습(`RlEnvironment`)은 `rate = baseline + policy·0.35·maxRate` 를 만들어 lock gate 를
통과시키는데, 배포(`RemotePolicyController`)가 `rate = policy·maxRate` 만 보내면 **잔차 정책을 브릿지로 서빙할
때 학습 때와 전혀 다른 의미로 실행된다.** 학습과 배포의 관측 정렬이 1 틱 어긋나도 마찬가지다. 그래서 합성
규칙을 하나의 타입으로 묶는다.

**현재:**

- `PolicyContract` 하나가 baseline · 행동 배율 · maxRate · lock 여부 · frame stack · 관측 버전을 담는다.
- `ActionComposer` 하나가 행동 → 명령 합성을 담당하고, **학습과 배포가 같은 것을 쓴다.**
- 관측 정렬은 학습과 배포가 같다(`_loop.LatestSensorBundle`). 이 정렬을 맞추기 전에 만든 체크포인트는 다시
  학습해야 한다.
- 계약이 체크포인트를 따라다닌다. 학습 서버 핸드셰이크(v2) → `train_ppo.py` / `bc_pretrain.py` 가 체크포인트에
  저장 → `serve_policy.py` 가 핸드셰이크로 되돌려 보냄 → Unity 가 그대로 실행.
- **계약이 없거나 맞지 않으면 조용히 실행하지 않는다.** 계약이 없으면 레거시 절대 rate 경로 +
  `policy-unverified` 로 기록한다. 액추에이터 상한이나 frame stack 이 다르면 **거부**하고 `hold-fallback` 으로
  기록한다(재스케일하지 않는다).
- BC 목표도 계약에 맞춘다. 잔차 계약이면 `target = (교사rate − baselineRate) / actionScale` 로 변환하고, 한계에
  걸린 비율(`clampedFraction`)을 데이터셋 헤더에 기록한다. 절대 목표를 잔차 정책에 그대로 복제하면 "교사 rate
  + baseline rate" 를 명령하는, 다른 궤적을 배우게 된다.

가드: `PolicyContractParityTests` — 같은 초기조건과 결정론적 정책에서 학습 환경과 브릿지 경로의 **스필·
freeboard·명령 rate·스텝 수가 모두 일치**해야 한다(plain / hold-잔차 / sfa-잔차 + 잠금). 인코더 클래스가
같다는 것은 근거가 되지 않는다.

**정책 서빙 (step D — 배포 분리):** 학습된 정책이 자기 프로세스에서 실행된다.
`python serve_policy.py --checkpoint runs/<run>/actor.pt --port 5601`(`actor_mean` + whitener 를 로드해
결정론적 평균을 반환하고, 클라이언트마다 스레드를 쓴다). Unity 의 `RemotePolicyController`(엔진프리,
`FluidStabilizer.Rl`)가 브릿지 클라이언트다. `ObservationEncoder` 를 로컬에서 돌려 벡터를 보내고, 응답을 관절
속도 명령으로 바꾼다. 서버가 없으면 잠근 Hold 로 내려가고 약 2 초마다 다시 탐지한다. wire 포맷은 클래스의
doc-comment 에 있다. 이것은 Unity 바이너리에 구운 ONNX 가 *아니다* — project-goals.md 참고.

**잔차 모드 (step B):** `-rlBaseline {none|hold|sfa|pid}` `-rlResidualFrac <0..1>`(기본 0.35). 베이스라인이
있으면 env 가 매 제어 틱마다 관절 속도 공간에서 `baselineRate + policy·(residualFrac·MaxRate)` 를 발행한다
(베이스라인의 각도/잠금 명령은 등가 rate 로 바꾼다. 각도는 P 추종, 잠금은 0 으로 PD 유지). 둘 다 max rate 로
자른다. 정책은 멀쩡한 동작점 주변의 작은 보정만 학습하므로 F1/F4 의 절벽을 피하는 해법이다. 합성은 서버 쪽에서
하므로 행동 공간과 Python 학습기는 그대로다. 다만 잔차 액션 공간만으로는 dead-band 때문에 학습 신호가 없는
절벽이 되므로(F9), 아래의 lock-capable 합성에 명시적 해제 요청(D10)을 더해 쓴다. `RlEnvironment` 에
`baselineFactory` / `residualRateScaleRadPerSec` 생성자 인자와 `ActorName` / `GimbalState` getter 를
추가했다. `ResidualRlTests` (4).

**lock-capable 하이브리드 액션 (D8, `-rlLockCapable`, baseline≠none 이면 기본 켜짐):** 합성 rate 가 데드밴드
(`GimbalConfig.LockRateThresholdRadPerSec`) 안이고 관절이 0 근처면 env 가 `Locked` 명령을 발행한다. 처지는
서보가 아니라 강체 관절이 된다. `LockGate`(엔진프리 결정론적 상태기계, 히스테리시스 + 최소 체류)가 이를 담당한다.
③ 의 바닥이 ①(잠근 탱크)이 되어 F5 의 구조적 핸디캡이 제거된다. **결과(F6): 그래도 후행지식은 강체를
이기지 못했다**(녹화 주행 기준). `LockCapableActionTests` (8), [decisions.md](decisions.md) D8. 서빙할 때는
`RemotePolicyController` 가 정책 계약(D9)대로 같은 baseline 합성과 `LockGate` 를 로컬에서 돌린다. **D10 이후**
잠금 판정은 rate 문턱에 따른 사후 판정이 아니라 정책의 세 번째 액션(해제 요청, 저역통과)이며, 시작은 잠긴
상태다.

**병렬 롤아웃:** 서버는 클라이언트 하나만 받는다. N 개 포트에 N 개 서버를 띄우고 N 개의 `FluidStabEnv` 를
Gymnasium `AsyncVectorEnv` 로 감싼다(v1 PPO 스크립트는 env 하나만 쓴다 — 처리량이 중요해지면 확장).

## 맨땅 PPO 가 안 되는 이유 (F9·F10)

학습기는 CleanRL 참조 수준이다(actor·critic 관측에 `RunningMeanStd` whitening 과 ±10 클립, return 정규화,
`--ent-coef` 기본 `1e-3`, 정규화기 통계를 체크포인트에 저장해 `play.py` 가 다시 적용). 그래도 처음부터 전부
배우는 PPO 는 이 과제를 학습하지 못한다. 원인은 **과제가 예측 문제**라는 점이다.

`-rlDriver aggressive -rlAggression 0.7 -rlFill 0.72`(진자 ROM, 30 s 에피소드)에서 잰 평균 return:

| 정책 | return | 넘치나? |
|---|---|---|
| zero action / 가만히 | **≈ −1800** | 아니오, 에피소드를 끝까지 버팀 |
| felt-tilt 관측에 대한 **어떤** 반응형 P 또는 D 컨트롤러, **어떤 gain, 어느 부호든** | ≈ −3200 | 예, 3~4 / 4 |
| 1 M 스텝 후 PPO | ≈ −3250 | 예 |

짐벌이 파도에 반응해 움직이는 순간 슬로시 에너지가 펌핑되고 탱크가 넘친다(F1·F4). 좋은 정책은 좁은
"hold / 예측" 능선 위에만 있다. 그래서 무작위 초기값 주변의 가우시안 행동 노이즈는 항상 절벽 위에 서게 되고,
모든 롤아웃이 ≈ −3200 에서 일찍 끝나며, critic 이 "전부 −3200" 을 학습해 gradient 가 사라지고 log-std 가
붕괴한다.

해결은 스텝 수나 튜닝이 아니라 **과제 설계**였다. 녹화한 사람 주행 + feasibility 천장(F5·F6) → 잔차 +
lock-capable 액션(D8) → 잔차 액션 공간이 절벽임을 특정하고 **명시적 잠금 액션**으로 교체(F9 → F10, D10).
그 뒤로 ③ 이 학습한다. 교사 다양화와 DAgger 로도 강체를 이기지 못했고 줄기는 닫혔다(F11·F12, D11). 현재
상태와 재개 조건은 [experiment-plan.md](experiment-plan.md).

## 메모 / 리스크

- **wire 로 스필 지표를 보내지 않는다.** `play.py` 는 shaped reward 만 합산한다. F4 의 표는 이름 붙은 6
  시나리오의 리터 단위 스필이다. `RlEnvironment.Summary()` 가 C# 에서 그 집계를 만들지만 TCP 프로토콜
  (RESET/STEP/CLOSE)이 노출하지 않는다. 정책을 F4 와 비교하려면 `SUMMARY` opcode 나 `PolicyEvalRunner` 경로
  (step D, `Logs/policy-eval.txt`)가 필요하다.
- **체크포인트 재개:** `train_ppo.py --resume runs/<name>/state.pt` 가 agent + optimizer + obs/priv/return
  정규화기 + `global_step` 을 복원해 이어서 학습한다(`--init-actor` 보다 우선). `state.pt` 는 `--save-every`
  (기본 20) 업데이트마다, 그리고 종료할 때 기록한다(`actor.pt` 는 매 업데이트 — serve 호환용 슬림 파일).
  크래시 복구와 커리큘럼("어려운 플래그로 서버를 다시 시작한 뒤 --resume")에 쓴다. `runs/` 는 gitignore 대상이다.
- **브래킷 모델 규율 (F3):** 진자 ROM 으로 학습하고(싸다), 승리를 믿기 전에 *항상* `-rlSlosh shallow` 로 다시
  평가한다. ROM 에서만 강체를 이기는 정책은 ROM 의 아티팩트를 학습했을 가능성이 높다.
- **Reward hacking 감시:** 고전적인 악용은 액체를 하드 end-stop 에 가두는 것이다. 항목별 분해에서
  `joint-limit-proximity` 와 `actuator-effort` 를 감시하고, 발생하면 `k6`/`k5` 를 올린다.
- **Reward 가중치:** `Overflow` 5e4, `TerminalSpill` 500. 낮은 값(500 / 5)에서는 50 L 스필의 비용이 ≈ −25
  에 불과하고, 탱크를 쏟아 에피소드를 끝내는 것이 버티는 것보다 *더 나은* 점수가 된다 — fail-fast 악용이다.
  `RlEnvironmentTests` / `RewardFunction` 테스트는 여전히 통과한다.
- **처리량:** 바이너리 브릿지는 v1 에는 충분하다. 병목이 되면 스텝을 배치하거나 호스트를 standalone .NET
  프로세스로 바꾼다(env 코드는 엔진프리이고 런처만 Unity 에 의존한다).
