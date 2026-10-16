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
| [lessons.md](lessons.md) | **결과·교훈·후속 제언 요약** — 처음 보는 사람용 한 문서 (포트폴리오·후속 프로젝트 출발점) | 누구나 |
| [findings.md](findings.md) | **실험으로 밝혀진 사실 F1~F16** (숫자·근거) | 소유자 + Claude |
| [decisions.md](decisions.md) | **설계 결정 로그 D1~D13** (무엇을 정했고 왜) | Claude + 소유자 |
| [design-review.md](design-review.md) | 아키텍처 결정의 근거 (큰 참고 문서, 잘 안 바뀜) | 주로 Claude |
| [reproducibility.md](reproducibility.md) | **재현성 · 데이터 셋 분리 · 실행 기록 규칙** (trace v2, `datasets.tsv`, provenance, 정책 실행 상태) | Claude + 소유자 |
| [rl.md](rl.md) | RL 경로 세부 (브릿지·학습기·정책 서빙) | Claude |
| [road-model.md](road-model.md) | 도로 모델 (D6) 세부 | Claude |
| [human-drive.md](human-drive.md) | 수동 주행 플레이 씬 세부 | Claude |
| [frames-units-conventions.md](frames-units-conventions.md) | 좌표계 · 단위 · 타이밍 규약 (고정) | Claude |
| [setup-status.md](setup-status.md) | 개발 환경 사실 (버전·경로·도구) | Claude |

문서 변경 이력은 `git log -- docs/` 로 본다 (이 파일에는 현재 상태만 둔다).
