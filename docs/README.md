# 문서 안내 (docs 색인)

이 폴더의 문서는 **한글**로 관리한다 (파일명은 도구·상호참조 호환을 위해 영어
유지, 제목·본문은 한글). 새 문서도 한글로 쓴다.

## 문서마다 "하나의 역할"

중복을 줄이려고 각 문서에 역할을 하나씩만 준다. 같은 내용을 여러 곳에 쓰지
않는다 — 어떤 사실은 아래 표의 "담당 문서" 한 곳에만 있고, 나머지는 링크만 건다.

| 문서 | 하나의 역할 | 누가 보나 |
|---|---|---|
| [project-goals.md](project-goals.md) | **무엇을 · 왜 · 헤드라인 결과 · 최종 아키텍처.** 의도와 범위의 유일한 출처. | 소유자 |
| [experiment-plan.md](experiment-plan.md) | **살아있는 현황 + 다음 단계 지도.** 유일한 진행상황 추적기. | 소유자 |
| [operating-guide.md](operating-guide.md) | **조작 가이드** (비개발자용) — 설정 패널·운전·서버·비교·빌드 | 소유자 |
| [demo-guide.md](demo-guide.md) | **시연용 참고 문서** — 개요·구조·학습 과정·비교·결과 해석·남은 과제 (비개발자 청중) | 소유자 (시연 시) |
| [vision-check.md](vision-check.md) | 초안 단계별 O/X 체크리스트 (2026-08-30 초안 종료 — 기록으로 보존) | 소유자 |
| [findings.md](findings.md) | **실험으로 밝혀진 사실 F1~F9** (숫자·근거) | 소유자 + Claude |
| [decisions.md](decisions.md) | **설계 결정 로그 D1~D8** (무엇을 정했고 왜) | Claude + 소유자 |
| [design-review.md](design-review.md) | 아키텍처 결정의 근거 (큰 참고 문서, 잘 안 바뀜) | 주로 Claude |
| [reproducibility.md](reproducibility.md) | **재현성 · 데이터 셋 분리 · 실행 기록 규칙** (trace v2, `datasets.tsv`, provenance, 정책 실행 상태) | Claude + 소유자 |
| [rl.md](rl.md) | RL 경로 세부 (브릿지·학습기·정책 서빙) | Claude |
| [road-model.md](road-model.md) | 도로 모델 (D6) 세부 | Claude |
| [human-drive.md](human-drive.md) | 수동 주행 플레이 씬 세부 | Claude |
| [frames-units-conventions.md](frames-units-conventions.md) | 좌표계 · 단위 · 타이밍 규약 (고정) | Claude |
| [setup-status.md](setup-status.md) | 개발 환경 사실 (버전·경로·도구) | Claude |

## 정비 이력

### 2026-09-10 — 재현성 정비 + F7·F8

- **F8 추가** — 운전 축을 훑는 `DrivingPremiseSweepRunner` 로 **프로젝트 최초의
  게이트 PASS** (매끄러운 노면의 지속 코너링). 동시에 규칙기반 ②는 그 이득을
  못 가져간다는 것도 측정. `demo-guide.md` 에 시연 절차, 키 **5** = ④ 천장 레인.
- 헤드라인은 유지: 소유자의 실제 녹화 주행에서는 ① 강체가 이긴다.
- **F9 추가** — F8 조건 잔차 RL 실패 + 원인(잔차 액션 공간이 절벽). `rl.md` 에
  "행동 계약" 절 신설 (PolicyContract / ActionComposer / 관측 정렬 수정).


- **`reproducibility.md` 신설** — 주행 파일 형식·데이터 셋 분리·실행 기록·정책
  실행 상태를 한 곳에. `reproducibility-handoff.md`(중단 세션 인계문서)를 흡수·폐기.
- **용어 정정:** 기존 6 주행을 "홀드아웃" 이라 부르던 서술을 고쳤다. BC 데이터셋과
  2c 조건 스윕에 반복 사용됐으므로 홀드아웃이 아니다. **F1~F6 의 관측 숫자는
  그대로 두었다** — 라벨이 틀렸던 것이지 측정이 틀린 게 아니다.
- `rl.md` 의 "hindsight 파일 유무로 학습 셋 결정 / 첫 주행 설정 일괄 적용 /
  가장 짧은 주행 길이로 자르기" 서술을 실제 코드로 교체.
- **F7 추가** (findings) — F5/F6 재검증. `project-goals.md` 의 "어느 운영점에서도"
  단정을 F7 범위로 좁혔다. 소유자의 2026-08-30 결정은 바꾸지 않았다.

### 2026-08-30 — 묶음 2c/3 이후 정비

- **`demo-guide.md` 신설** — 시연용(비개발자 청중) 참고 문서.
- **`vision-check.md`** — 초안 단계 종료 표시, 기록으로 보존 (삭제 안 함, 소유자 관리).
- 진행상황 숫자 갱신: EditMode 113 → **131 통과**, findings **F1~F6**, decisions **D1~D8**.
- `experiment-plan.md` "한 문단 현황" 을 F6 결과 + 소유자 결정(정직한 헤드라인
  수용)으로 재작성. 묶음 2 ✅, 묶음 3 ⏸ (전제 조정 대기).

### 2026-08-29 한글화

- **`plan-next.md` 폐기** — 2026-08-28 시점의 계획(얕은물 모델·운전자·플레이 씬).
  전부 완료됨. `experiment-plan.md` 가 대체.
- **`phase1-progress.md` 폐기** — 진행상황 추적이 `experiment-plan.md` 와 중복.
  "지금까지 만든 것" 목록만 `experiment-plan.md` 로 흡수.
- **`project-brief.md` → `project-goals.md` 로 병합** — "원래 요청"은
  `project-goals.md` 안의 한 절로 보존. 의도 문서는 하나만 둔다.
- **모순 수정:**
  - `decisions.md` D1 이 "500 Hz" 라고 적혀 있었음 → 실제는 **600 Hz**
    (`frames-units-conventions.md` 가 근거: 200 Hz 센서가 정수로 나눠지도록
    500 대신 600 선택).
  - 루트 `README.md` 가 "코드 없음 / GitHub 리모트 없음 / 설계 리뷰 대기중"
    이라고 되어 있었음 → 전부 옛말. 갱신.
  - `plan-next.md` 의 "키 3 = LQR, S = 스태빌라이저 토글" → 실제는 LQR 폐기,
    `Space` 토글, 키 `0~4`. (폐기 문서라 그대로 두고 삭제)
