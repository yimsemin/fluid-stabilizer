# 실험 재현성 수정 인계 (2026-09-09)

## 인계 시점

사용자가 토큰 사용량을 줄이기 위해 작업 중단·정리를 요청했다. **구현 초안은 작성됐지만 완료 검증과 문서 정리는 남아 있다.** 추가 기능이나 장시간 RL·물리 튜닝 없이 아래 항목만 마무리한다. 현재 `main`의 미커밋 변경이며 커밋·push하지 않았다. `experiment.txt`는 사용자가 작업 시작 전에 수정한 파일이므로 절대 포함·수정하지 않는다.

최초 요청: 인간 주행 조건의 정확한 재생, train/validation/final 분리, 실제 실행 정책과 fallback을 구별하는 비교 결과. 큰 의존성·별도 인프라 금지. CLAUDE.md, docs/project-goals.md, docs/experiment-plan.md 및 관련 설계 문서를 읽고 진행했다. 새로운 의존성은 추가하지 않았다.

## 작성한 구현

- `Core/Sim/ConfigSnapshot.cs`: SimulationConfig의 모든 public 필드를 정렬된 문자열 사전으로 캡처/복원. Double3 별도 처리, SHA-256 유틸. Unity 참조 없음.
- `Core/Sim/Drivers/DriverTrace.cs`: v2 헤더에 실제 전체 설정과 녹화 컨트롤러·코드 MVID 기록. v1 읽기 유지, 누락 설정은 `Assumptions`로 구분. 레거시 재저장에도 누락 값을 녹화 사실로 저장하지 않음. 원본 파일 SHA, 입력 SHA, split 메타데이터 추가. SaveToFile은 CreateNew로 덮어쓰기 거부.
- `Sim/TraceRecorder.cs`: BuildLoop 뒤 실제 Loop.Config를 캡처. 녹화명 UUID 포함. 일반 재생은 Runner.ReplayTrace 사용, 누락 설정 경고 표시.
- `Sim/SimulationRunner.cs`: ReplayTrace가 현재 프로파일/Inspector보다 우선. trace의 timestep·길이·seed·모델 적용.
- `Core/Sim/Drivers/TraceDataset.cs`, 루트 `datasets.tsv`: 명시적 train/validation/final TSV 목록. 원본 파일 해시 검증, 이름·seed·환경을 바꿔도 같은 입력이면 중복 검출. 전체 manifest의 split 간 중복도 검출. final은 history=unseen만 허용. 빈 split은 실패.
- 기존 BC 데이터 헤더에서 확인한 6개 주행만 train으로 등록. history=bc-and-repeated-condition-search. validation/final은 비어 있음. 다른 기존 9개 주행은 미배정. 원본 trace에 쓰기 없음.
- `Sim/TraceStore.cs`: `-traceManifest`(기본 datasets.tsv), `-traceSplit`(평가 기본 train). BC/RL/hindsight/sweep는 train만 허용. hindsight 파일 존재로 멤버십을 결정하지 않음.
- `Rl/RlEnvironment.cs`, `Sim/Editor/RlServerLauncher.cs`: 매 Reset 때 선택 trace의 설정·seed·모델·전체 길이를 함께 적용. 첫 trace 일괄 적용/최단 길이 자르기 제거. 합성 실행은 명시적 -rlRoad가 없으면 severity 유지; none은 도로 끔. trace와 합성 환경 override 플래그의 혼용 거부. RL 에피소드 시작 메타데이터와 종료 결과 기록 추가.
- `Contracts/IPolicyExecutionStatus.cs`, `Rl/RemotePolicyController.cs`: 정책 틱/fallback 틱 및 이유 누적. policy-ok / policy-unverified / hold-fallback / mixed-policy-and-hold / not-executed. v2 정책 handshake에 체크포인트 식별 정보 추가, 기존 v1은 사용 가능하나 미확인 정책으로 표시. NaN/Inf 응답 거부, 재연결 때 reset handshake.
- `python/serve_policy.py`: 실제 로드한 동일 바이트의 체크포인트 SHA-256, 서버 소스 SHA와 라이브러리 버전 전송. 행동 의미는 기존과 같은 absolute-joint-rate.
- `Core/Sim/Eval/RunProvenance.cs`, `EpisodeLog.cs`: 실제 로드된 어셈블리 MVID, 전체 유효 설정, trace/input/manifest 해시, split/history/가정, 요청 컨트롤러, 정책 식별·fallback 기록. 새 CSV는 Logs/episodes-v2.csv. 기존 스키마 파일에 무턱대고 append하지 않음.
- TraceEvaluator/ComparisonRunner: 누락 설정과 정책 실행 증거 기록, fallback·미확인 정책을 정상 AI 판정에서 제외. 배치 기본 모델은 각 trace 모델; TraceEvalRunner의 명시적 양 모델 검증은 유지.
- 평가·sweep·hindsight·BC 출력은 시간/UUID를 포함한 새 경로. hindsight는 실행별 디렉터리 아래 궤적 이름 유지. BC는 -bcTrajDir로 선택, 궤적의 입력/설정/모델 해시 일치 검사. 기존 메타데이터 없는 궤적은 -bcAllowLegacyTrajectory 1을 명시해야 탐색용 사용 가능. lock-capable 궤적은 현재 absolute-rate BC와 의미가 달라 거부.
- 새 ReproducibilityTests: 2종 비기본 설정(탱크/접지/곡률/경사/센서/시간/슬로시 모델)의 녹화→일반 재생→비교→배치 일치, 레거시 가정, 중복, 덮어쓰기 금지, RL 주행별 설정/길이. RemotePolicyControllerTests에 단절·레거시·NaN 테스트 추가. ComparisonRunnerTests에 fallback 판정 확인 추가.

## 검사 상태와 실행 환경

`git diff --check`는 통과했다. Unity 세 번째 실행에서 C# 컴파일을 마치고 EditMode 스위트가 시작된 것을 확인했다. 최종 결과는 아래 XML을 확인할 것.

- 결과: `Logs/repro-editmode-20260909-third.xml`
- 로그: `Logs/repro-editmode-20260909-third.log`
- 실행 도구 세션 ID: 53732 (이미 종료됐을 수도 있음)
- 첫 실행: 샌드박스가 로컬 UPM 소켓을 거부. 두 번째 실행은 첫 실행의 고아 라이선스 프로세스가 mutex를 점유해 대기. 이번 작업이 만든 PID 60393, 60579만 확인 후 종료하고 세 번째 실행.
- Unity 실행에는 sandbox_permissions=require_escalated가 필요하다. 자동 승인 검토는 허용했다. 사용자 에디터나 다른 프로세스는 종료하지 않았다.
- 재실행할 때 XML/log 이름을 새로 지정한다. 기존 기록 덮어쓰기 금지.

명령 예:

```sh
"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath <저장소 루트> -runTests -testPlatform EditMode -testResults Logs/repro-editmode-새고유이름.xml -logFile Logs/repro-editmode-새고유이름.log
```

## 보존 확인과 사고 기록

작업 전 188개 파일의 SHA-256 목록을 `/tmp/fluid-repro-preservation.json`에 저장했다. 범위는 experiment.txt, Traces, Logs, python/runs 및 추가 .pt 파일이다. **이것은 해시 목록이며 원본 백업은 아니다.**

중간 확인에서 experiment.txt, 모든 녹화·체크포인트·실험 결과 로그의 해시는 그대로였다. 다만 Unity 자체가 시작하면서 기존 `Logs/AssetImportWorkerHW0.log`, `Logs/AssetImportWorkerHW0-prev.log`, `Logs/upm.log` 세 개를 갱신했다. 자동 생성 진단 로그도 보존 요구에 포함되는데 사전에 내용 백업을 하지 못했다. 원본 완전 보존이라고 보고하면 안 된다. 해당 파일을 추정 복원하거나 숨기지 말 것. 후속 Unity 실행 전에는 현재 로그 폴더를 별도 디렉터리에 백업해 추가 손실을 막아야 한다.

## 다음 작업 — 우선순위대로, 범위를 늘리지 말 것

1. 위 XML에서 실제 통과/실패/스킵과 실패 메시지 확인. 실패만 수정 후 필요한 검사 재실행. 새 테스트는 작성했으나 아직 결과 검토 전이다.
2. 현재 초안의 누락을 작게 보완하거나 명확한 한계로 보고:
   - 일반 SimulationRunner의 에피소드 기록은 아직 배선되지 않음(배치·비교·RL 기록은 추가). 일반 재생 완료/재시작/정책 변경에서도 실행 근거를 남기는 경로 필요.
   - 녹화 도중 R/도로/모델 변경·ControlPanel 적용이 BuildLoop/Reset으로 RecordingDriver의 샘플을 지우거나 최초 캡처와 다른 설정을 만들 수 있음. 녹화 세션 설정 고정 또는 분할 저장 규칙 필요. 원본·진행 중 샘플 보존을 우선.
   - PolicyEvalRunner의 rateScale은 아직 new SimulationConfig 기본값으로 계산함. trace의 비기본 Gimbal.MaxRateRadPerSec와 일치시키는 최소 수정 필요.
   - ComparisonRunner의 VerdictText는 미확인/fallback을 제외하지만 HUD `best` 하이라이트는 아직 모든 레인의 최솟값으로 계산함. 잘못 강조하지 않게 수정.
   - RL 학습 결과와 시작 메타데이터는 별도 고유 파일이지만 같은 episode ID로 연결되지 않음. 외부 훈련 체크포인트 식별 정보는 unavailable로 표시했고 실제 가중치를 검증하지는 않음.
   - 코드 버전은 실제 어셈블리 MVID. git commit/dirty 상태는 아직 별도 저장하지 않음. 필요하면 작게 추가하되 소스 checkout과 실제 로드 바이너리를 혼동하지 말 것.
   - DriverTrace는 일반 v1의 존재하는 헤더를 전제. v1에서 seed/step/fill 등 기존 필수 헤더까지 빠졌거나 잘못된 파일의 검증은 미흡. Assumptions는 누락된 전체 config 필드에 대해 반복 Capture하여 비효율적임.
   - 새 BC 출력의 정상 실제 파일 경로·메타데이터와 Python v2 서버를 연결한 짧은 검증은 미실행. 장시간 학습 불필요. 기존 잔차 정책의 행동 해석은 이번에 새로 구현하지 않았으며 absolute-rate로 서빙한 사실만 기록함.
3. 빠진 검증: manifest 파일 원본 변경/빈 final/잘못된 split 검증, RlServerLauncher의 -rlRoad 미지정/none 및 잘못된 selector 검증, 실제 recorder API로 비기본 설정 캡처(현재 새 통합 테스트는 core RecordingDriver+ReplayTrace를 사용). 위험도에 맞는 최소 검사만 추가.
4. 문서 동기화가 아직 전혀 안 됨. `docs/experiment-plan.md`에 현재 구현/검증 상태, 기존 6개는 BC·반복 탐색 사용으로 final holdout 아님을 명시. 재현/데이터/로그 규칙을 한 문서에 모으고 `docs/rl.md`의 hindsight 존재 기반 선택·첫 trace/최단 길이 설명을 교체. `docs/human-drive.md`, `docs/operating-guide.md`, `docs/demo-guide.md`의 경로/판정 설명도 실제 코드와 맞춰 링크 중심으로 최소 수정. 과거 결과 숫자는 재실험 없이 바꾸지 말고 옛 설정 불확실성/탐색 결과라는 한계를 붙일 것.
5. 최종 보존 해시 비교. 기존 실험 로그 손상은 없고 자동 진단 로그 3개는 갱신됐음을 구분해서 보고.
6. 완료 검증 뒤에만 저장소 지침에 따라 코드·테스트·문서를 묶어 커밋(신규 .meta 포함). `experiment.txt`, Traces/Logs/checkpoints는 커밋에서 제외. 현재 중간 상태를 완성된 수정으로 커밋하지 않았다. 이 인계 문서는 완료 후 정리하거나 현재 기준 문서로 통합.

## 후속 모델에게 보낼 짧은 요청

`docs/reproducibility-handoff.md`를 먼저 읽고 현재 미커밋 수정의 검증·누락 보완·문서 정리만 완료해라. experiment.txt와 기존 주행·체크포인트·실험 로그를 보존하고, 자동 Unity 진단 로그 3개 갱신 사실은 숨기지 마라. 먼저 Logs/repro-editmode-20260909-third.xml 결과를 확인하고 실패/남은 항목만 처리해라. 장시간 RL·튜닝·큰 의존성 없이 끝내고 한국어로 결과와 한계를 보고해라.

## 인계 직전 최종 확인

세 번째 EditMode 결과: {'result': 'Passed', 'total': '148', 'passed': '140', 'failed': '0', 'skipped': '8', 'duration': '109.2807496'}
