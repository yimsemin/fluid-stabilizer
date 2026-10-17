# 재현성 · 데이터 셋 · 실행 기록

**이 문서의 역할:** 실험을 다시 돌릴 수 있게 만드는 규칙 한 곳 — 주행 파일이
무엇을 저장하는가, 어떤 주행을 무엇에 쓰는가, 각 실행이 무엇을 남기는가.
실험 *결과* 숫자는 [findings.md](findings.md), 진행상황은
[experiment-plan.md](experiment-plan.md) 담당.

## 왜 필요한가

F5/F6 같은 게이트 결론은 "이 조건에서 hindsight 가 강체를 못 이긴다"는 주장이다. 주행 파일이 seed·fill·
도로 on/off 만 저장하면 탱크 높이, 접지력, 도로 곡률·경사, 센서 설정이 빠져 **결론을 재현할 수 없고,
재현할 수 없는 결론은 검증할 수 없다**(그 실제 사례가 [F7](findings.md) 이다). 그래서 세 가지를 지킨다:
설정 전체 기록, 데이터 셋 명시 분리, 실행 증거 기록.

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

- **hindsight 파일이 있느냐로 멤버십을 결정하지 않는다.** (산출물 유무가 데이터 분할을 정하면
  분할이 아니다.)
- 각 행에 원본 SHA-256 과 `history`. 해시가 다르면 실패한다.
- split 간 중복, 빈 split, `final` 에 `history != unseen` 은 전부 실패.
- BC · RL · hindsight · sweep 는 **`train` 만** 쓴다 (`-traceSplit`, 기본
  `-traceManifest datasets.tsv`).

**현재 상태 — 정직하게:**

| split | 주행 수 | history |
|---|---|---|
| train | 6 | `bc-and-repeated-condition-search` |
| validation | 2 | `unseen-at-registration` |
| final | 7 | `unseen` |

기존 6 주행은 BC 데이터셋과 2c 조건 스윕에 반복 사용됐다. **홀드아웃이 아니다.**
2026-10-10 에 새로 녹화한 9 주행(운영점 fill 0.62 · 도로 on · severity 1.1, 컨트롤러 Off,
각 약 60 s)을 입력 통계만 보고 나눴다: validation 2(공격적 1 + 꾸준한 1, 튜닝·탐색에
사용 가능), final 7(최종 보고 때 한 번만). 녹화 후 재생·평가는 하지 않았다 — 단, 로컬
녹화 도구의 사용 이력은 기록이 없어 소유자의 "전 주행 재생해 결과를 보지 않았다"는 진술(2026-10-10)에 의존한다.
그 밖의 옛 녹화 9 주행(로컬 전용, 비공개)은 어디에도 배정하지 않았다 — 무엇에 쓰였는지 기록이 없어서
`unseen` 이라고 주장할 수 없다.
**final 7 개 중 2 개(`232319`, `232734`)는 대부분 무가속 코스팅**이라 외란이 약하다.
제외하지 않은 것은 선택 편향을 피하려는 것이다 — 결과 해석 시 감안한다.

**홀드아웃 등록 절차** (새 주행을 녹화한 뒤):

1. 녹화 **전에** 운영점을 확정한다 (`experiment.txt` / 컨트롤 패널). 녹화 후에는
   그 주행의 설정이 trace 헤더에 고정되므로 바꿀 수 없다.
2. `P` 로 주행 3~5 개 녹화 (도로 on, fill ≥ 0.55). 녹화 뒤에는 **재생(`L`/`C`)으로
   스태빌라이저 결과를 미리 보지 않는다** — 보고 나면 `unseen` 이 아니다.
3. `shasum -a 256 Traces/<파일>` 로 해시를 구해 `datasets.tsv` 에
   `final  <파일>  <해시>  unseen` 행으로 추가하고 커밋한다 (트레이스 파일 자체는
   gitignore 이므로 의도적으로 force-add).
4. 이후 BC · RL · hindsight · sweep 는 `final` 을 읽지 않는다 (`-traceSplit train`
   기본값). 평가는 최종 보고 때 한 번만.

`final` 은 어떤 튜닝·탐색에도 쓰지 않는다. **7 주행은 2026-10-11 에 한 번 평가되어
소비됐다** ([F13](findings.md)); 매니페스트의 `unseen` 은 "튜닝에 쓰지 않았다"는 뜻이며
그 뒤로 다시 튜닝에 쓰면 홀드아웃이 아니다. 이건 제약이 아니라 F5/F6 같은 결론을
다시 내리기 위한 전제다. 추가 홀드아웃이 필요하면 위 절차를 반복한다.

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
강체와 스필이 같아서 "최소 스필 승리" 규칙으로는 AI 승리로 잘못 표시될 수 있기 때문이다.

`serve_policy.py` 는 **실제 로드한 바이트의** 체크포인트 SHA-256 과 서버 소스
SHA, 라이브러리 버전을 handshake 에 보낸다.

## 5. RL 환경

`RlEnvironment` 는 **Reset 마다** 선택된 trace 의 설정·seed·모델·전체 길이를 적용한다(첫 주행 설정을
전체에 쓰거나 가장 짧은 주행 길이로 자르지 않는다). 합성 실행은 명시적 `-rlRoad` 가 없으면 severity 를
유지하고, trace 모드와 합성 override 플래그의 혼용은 거부한다.

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
