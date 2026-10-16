# 문서 안내 (docs 색인)

이 폴더의 문서는 **한글**로 관리한다 (파일명은 도구·상호참조 호환을 위해 영어
유지, 제목·본문은 한글). 새 문서도 한글로 쓴다.

**처음 읽는다면:** [루트 README](../README.md) 의 "한 장 요약" → [lessons.md](lessons.md).

## 용어 (모든 문서에서 같은 뜻)

| 용어 | 뜻 |
|---|---|
| ① 강체 (Hold) | 짐벌을 기계적으로 잠가 탱크를 차량에 고정. 아무것도 안 하는 기준선 |
| ② 규칙기반 | 공식으로 탱크를 기울이는 제어 (SpecificForceAlignment, PID) |
| ③ 학습 정책 | 사람 주행으로 학습한 신경망 제어 (센서로 얻을 수 있는 값만 봄) |
| ④ 천장 | 주행 전체를 미리 아는 오프라인 최선의 저대역 궤적. 실제로는 못 쓰는 비교 기준선 |
| 4종 비교 | ①②③④ 전체. 결과 문서(`lessons.md`, `findings.md`)가 쓰는 범위 |
| 3분할 비교 | 앱에서 `C` 로 보는 ①②③ 세 레인. ④ 는 키 `5` 로 따로 본다 |
| 스필 | 탱크 가장자리를 넘어 흘러나간 액체량 [L]. 이 프로젝트의 점수 (작을수록 좋음) |
| 슬로시 (slosh) | 탱크 안 액체가 출렁이는 현상. 기동이 끝나도 계속되는 *시간 동역학*이 문제의 핵심 |
| freeboard | 액체 표면에서 탱크 가장자리(rim)까지 남은 높이. 0 이하면 넘친다 |
| MPC (예측 제어) | 모델로 앞날을 예측하며 매 순간 최적의 틸트를 계산하는 제어. 이 프로젝트에서는 정답 상태를 아는 "오라클" 판을 상한 시험용으로 썼다 |
| 홀드아웃 | 학습·튜닝에 한 번도 쓰지 않고 마지막 평가에만 쓰는 주행 (`final` split) |
| 진자 / 얕은물 | 슬로시 모델 2종. 진자는 빠른 근사(학습·반복용), 얕은물은 정밀(검증용) |
| F번호 / D번호 | [findings.md](findings.md) 의 발견 / [decisions.md](decisions.md) 의 결정 |

## 문서마다 "하나의 역할"

중복을 줄이려고 각 문서에 역할을 하나씩만 준다. 같은 내용을 여러 곳에 쓰지
않는다 — 어떤 사실은 아래 표의 "담당 문서" 한 곳에만 있고, 나머지는 링크만 건다.
**결과(숫자)는 `findings.md`(근거)와 `lessons.md`(요약)에만 쓰고, 다른 문서는 한 줄 헤드라인과
링크만 둔다.** 조작(키·버튼·서버)은 `operating-guide.md` 에만 쓴다.

| 문서 | 하나의 역할 | 누가 보나 |
|---|---|---|
| [lessons.md](lessons.md) | **결과·교훈·후속 제언 요약** — 질문 → 시도 → 답의 흐름 포함. 처음 보는 사람용 | 누구나 |
| [findings.md](findings.md) | **실험으로 밝혀진 사실 F1~F17** (숫자·근거) | 소유자 + Claude |
| [project-goals.md](project-goals.md) | **무엇을 · 왜 · 최종 아키텍처 · 설계 원칙.** 의도와 범위의 유일한 출처 | 소유자 |
| [experiment-plan.md](experiment-plan.md) | **현황 + 만든 것 + 닫힘/열림 지도.** 유일한 진행상황 추적기 | 소유자 |
| [decisions.md](decisions.md) | **설계 결정 로그 D1~D14** (무엇을 정했고 왜, 상태 표 포함) | Claude + 소유자 |
| [design-review.md](design-review.md) | 설계 시점의 근거 (큰 참고 문서. 상단 표가 현재 유효성을 알려줌) | 주로 Claude |
| [operating-guide.md](operating-guide.md) | **조작 가이드** (비개발자용) — 키·설정 패널·운전·서버·비교·학습·빌드 | 소유자 |
| [demo-guide.md](demo-guide.md) | **시연 대본** — 청중에게 무엇을 어떤 순서로 보여주고 어떻게 말할지 (비개발자 청중) | 소유자 (시연 시) |
| [reproducibility.md](reproducibility.md) | **재현성 · 데이터 셋 분리 · 실행 기록 규칙** (trace v2, `datasets.tsv`, provenance) | Claude + 소유자 |
| [rl.md](rl.md) | RL 경로 세부 (브릿지·학습기·정책 서빙·행동 계약) | Claude |
| [road-model.md](road-model.md) | 도로 모델 (D6·D7) 세부 | Claude |
| [frames-units-conventions.md](frames-units-conventions.md) | 좌표계 · 단위 · 타이밍 규약 (고정) | Claude |
| [setup-status.md](setup-status.md) | 개발 환경 사실 (버전·경로·도구) | Claude |

문서 변경 이력은 `git log -- docs/` 로 본다 (이 파일에는 현재 상태만 둔다). 프로젝트가 걸어온
과정은 `lessons.md` 의 "질문 → 시도 → 답" 표와 `git log` 가 담당하며 별도 로드맵 문서는 두지 않는다.
