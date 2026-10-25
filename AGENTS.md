# AGENTS.md — 이 저장소를 처음 읽는 AI·사람을 위한 안내

**상태: 마무리된 아카이브.** 새 기능을 만들지 않는다. 읽고 응용하는 용도다. (README.md 참고)

## 한 줄 결론

Physical AI 시뮬레이션(사람이 운전하는 트럭 위 개방형 액체 탱크, 2축 짐벌). 처음 보는 7 주행에서 **① 강체(짐벌 잠금)가
② 규칙기반·③ 학습 정책·오라클 MPC 를 모두 이겼고**, 탱크 설계(충전율·림 높이·배플)가 능동 제어보다 훨씬 크게 스필을
줄였다. 모든 결과는 시뮬레이션 값이다.

## 읽는 순서

1. [README.md](README.md) — 왜 시작했나, 한 장 요약, 영어 요약, 한계
2. [docs/lessons.md](docs/lessons.md) — 날짜순 "질문 → 시도 → 답 → 틀렸던 점" 표와 교훈 15개
3. [docs/findings.md](docs/findings.md) — 숫자의 근거 F1~F17 (제목과 "한눈에" 표로 먼저 찾는다)
4. [docs/decisions.md](docs/decisions.md) — 설계 결정 D1~D14
5. [docs/experiment-plan.md](docs/experiment-plan.md) — 만든 것, 닫힌 줄기와 재개 조건
6. [docs/reproduce-from-scratch.md](docs/reproduce-from-scratch.md) — 공개본에서 재현되는 것과 안 되는 것

문서 색인과 용어(①②③④ 등): [docs/README.md](docs/README.md). 문서는 모두 한글이다.

## 건드리기 전에 알아둘 것

- **닫힌 줄기를 재개하지 않는다** (맨땅 PPO, PPO 미세조정, 교사 수 늘리기, DAgger, 인과 MPC 상태추정·학습, 미래 도로를
  관측에 넣는 프리뷰). 재개 조건: experiment-plan "닫힘" 표.
- 홀드아웃 `final` 7 주행은 평가에 소비됐다. 다시 튜닝에 쓰면 홀드아웃이 아니다.
- 컨트롤러 코드는 `UnityEngine` 을 참조하지 않는다. AI 관측에는 실제 센서로 얻을 수 있는 값만 쓴다.
- 한 슬로시 모델의 결과를 다른 모델 검증 없이 결정 근거로 쓰지 않는다 (F3, F15).
- 공개본에는 `train`/`validation` 주행과 학습된 모델이 없다. 숫자 재현 범위는 reproduce-from-scratch 의 표를 따른다.
- 설계 결정이 바뀌면 같은 변경에서 해당 `docs/*.md` 를 갱신하고, 같은 사실을 여러 문서에 쓰지 않는다.
- 저장소 작업 규칙 전체: [CLAUDE.md](CLAUDE.md).
