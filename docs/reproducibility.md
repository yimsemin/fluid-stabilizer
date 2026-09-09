# 재현성 · 데이터 셋 · 실행 기록

**이 문서의 역할:** 실험을 다시 돌릴 수 있게 만드는 규칙 한 곳 — 주행 파일이
무엇을 저장하는가, 어떤 주행을 무엇에 쓰는가, 각 실행이 무엇을 남기는가.
실험 *결과* 숫자는 [findings.md](findings.md), 진행상황은
[experiment-plan.md](experiment-plan.md) 담당.

## 왜 만들었나

F5/F6 게이트 결론은 "이 조건에서 hindsight 가 강체를 못 이긴다"는 주장이다.
그런데 `*.trace.csv` 는 seed·fill·도로 on/off 만 저장했고 탱크 높이, 접지력,
도로 곡률·경사, 센서 설정은 저장하지 않았다. **결론을 재현할 수 없다면 그
결론은 검증할 수 없다.** 그래서 세 가지를 고쳤다: 설정 전체 기록, 데이터 셋
명시 분리, 실행 증거 기록.

## 1. 주행 파일 (trace v2)

`Traces/drive-<시각>-<uuid>.trace.csv`.

- **v2 헤더는 `SimulationConfig` 전체**를 기록한다 (`ConfigSnapshot` — 모든
  public 필드를 정렬된 문자열 사전으로). 녹화 시점의 컨트롤러와 실제 로드된
  어셈블리 MVID 도 함께.
- 헤더는 **BuildLoop 이후의 실제 `Loop.Config`** 에서 캡처한다. Inspector 값이
  아니라 진짜로 돌아간 값이다.
- **v1(옛 파일)은 계속 읽힌다.** 없는 필드는 현재 기본값으로 채우되 `Assumptions`
  목록에 따로 남긴다 — **추정치를 "녹화 당시 사실"로 승격시키지 않는다.**
  레거시 파일을 다시 저장해도 추정값이 녹화 사실로 기록되지 않는다.
- `SaveToFile` 은 `CreateNew` 라 **기존 파일을 덮어쓰지 않는다.**
- 원본 파일 SHA-256 과 입력 시퀀스 SHA 를 따로 둔다. 이름·seed·환경을 바꿔도
  입력이 같으면 중복으로 잡힌다.

재생 경로(일반 재생 · `ComparisonRunner` · 배치 평가)는 모두 `trace.ToConfig()`
의 같은 유효 설정을 쓴다. `ReplayTrace` 는 현재 프로파일/Inspector 보다 우선한다.

## 2. 데이터 셋 분리 (`datasets.tsv`)

루트 `datasets.tsv` 가 **train / validation / final** 을 명시한다.

- **hindsight 파일이 있느냐로 멤버십을 결정하지 않는다.** (예전 `-rlTrace all`
  동작. 산출물 유무가 데이터 분할을 정하면 분할이 아니다.)
- 각 행에 원본 SHA-256 과 `history`. 해시가 다르면 실패한다.
- split 간 중복, 빈 split, `final` 에 `history != unseen` 은 전부 실패.
- BC · RL · hindsight · sweep 는 **`train` 만** 쓴다 (`-traceSplit`, 기본
  `-traceManifest datasets.tsv`).

**현재 상태 — 정직하게:**

| split | 주행 수 | history |
|---|---|---|
| train | 6 | `bc-and-repeated-condition-search` |
| validation | 0 | — |
| final | 0 | — |

기존 6 주행은 BC 데이터셋과 2c 조건 스윕에 반복 사용됐다. **홀드아웃이 아니다.**
`Traces/` 의 나머지 9 주행은 어디에도 배정하지 않았다 — 무엇에 쓰였는지 기록이
없어서 `unseen` 이라고 주장할 수 없다.

**따라서 지금 구조에서는 최종 평가가 불가능하다.** `final` 을 채우려면 소유자가
**새 주행을 녹화**해야 하고, 그 주행은 어떤 튜닝·탐색에도 쓰지 않아야 한다.
이건 제약이 아니라 F5/F6 같은 결론을 다시 내리기 위한 전제다.

## 3. 실행 기록 (provenance)

`Logs/episodes-v2.csv` (기존 `episodes.csv` 스키마에 append 하지 않는다).
실행마다: 유효 설정 전체, 어셈블리 MVID, trace/입력/manifest 해시, split ·
history · `Assumptions`, 요청 컨트롤러, 정책 식별 정보, fallback 여부.

평가·sweep·hindsight·BC 출력은 시각/UUID 를 포함한 새 경로에 쓴다. 기존 로그를
덮어쓰지 않는다.

## 4. 정책이 실제로 돌았는가

`IPolicyExecutionStatus` 가 실행 상태를 다섯으로 나눈다:

| 상태 | 뜻 |
|---|---|
| `policy-ok` | 정책이 전 구간 실행됨 |
| `policy-unverified` | 실행됐지만 체크포인트 식별 불가 (v1 handshake) |
| `hold-fallback` | 서버 없음 → 전 구간 Hold |
| `mixed-policy-and-hold` | 중간에 끊겨 일부만 정책 |
| `not-executed` | 실행 안 됨 |

**`policy-ok` 와 `analytic` 만 AI 성능 비교에 들어간다.** `ComparisonRunner` 의
판정 배너와 초록 하이라이트(`BestLanes()`) 둘 다 이 규칙을 쓴다 — fallback 레인은
강체와 스필이 같아서 "최소 스필 승리" 규칙으로는 AI 승리로 표시됐었다.

`serve_policy.py` 는 **실제 로드한 바이트의** 체크포인트 SHA-256 과 서버 소스
SHA, 라이브러리 버전을 handshake 에 보낸다.

## 5. RL 환경

`RlEnvironment` 는 **Reset 마다** 선택된 trace 의 설정·seed·모델·전체 길이를
적용한다. 예전 동작(첫 주행 설정을 전체에 적용, 가장 짧은 주행 길이로 자르기,
도로 severity 기본값 덮어쓰기)은 제거됐다. 합성 실행은 명시적 `-rlRoad` 가
없으면 severity 를 유지하고, trace 모드와 합성 override 플래그의 혼용은 거부한다.

## 알려진 한계

- 일반 `SimulationRunner` 재생은 아직 에피소드 기록을 남기지 않는다 (배치·비교·
  RL 만). 일반 재생 완료/재시작/정책 변경도 근거를 남겨야 한다.
- 녹화 중 R/도로/모델 변경이 `BuildLoop`/Reset 을 타면 캡처된 설정과 실제 샘플이
  어긋날 수 있다. 녹화 세션 설정 고정 또는 분할 저장 규칙이 필요하다.
- 코드 버전은 **실제 로드된 어셈블리 MVID** 다. git commit/dirty 는 아직 따로
  저장하지 않는다 (소스 checkout 과 실제 로드 바이너리는 다른 사실이다).
- 외부 학습 체크포인트의 가중치를 검증하지는 않는다 — 식별 정보만 기록한다.
- v1 파일 중 seed/step/fill 같은 **기존 필수 헤더까지** 빠진 파일의 검증은 아직
  약하다.
- `Assumptions` 는 누락 필드마다 반복 Capture 해서 비효율적이다 (정확성 문제는
  아님).
- 2026-09-09 Unity 배치 실행이 `Logs/AssetImportWorkerHW0.log`,
  `…-prev.log`, `Logs/upm.log` 세 개를 갱신했다. 실험 결과 로그는 아니지만
  원본은 복구되지 않았다. **완전 보존이라고 보고하면 안 된다.**
