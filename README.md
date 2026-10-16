# fluid-stabilizer

Physical AI 시뮬레이션 프로젝트: **인간이 운전하는** 트럭에 실린 개방형 액체
탱크가 넘치지 않도록, AI 가 2축 스태빌라이저를 제어한다.

- **인간**이 트럭을 운전한다 (가속, 제동, 코너링, 언덕, 범프).
- **AI 는 운전하지 않는다.** 트럭 베드와 탱크 사이의 2축(pitch/roll)
  스태빌라이저를, 실제 센서가 낼 수 있는 신호만 써서 제어해 스필을 최소화한다.
- 운전자의 예측 불가능한 입력은 AI 가 거부해야 할 외부 disturbance 로 취급한다.

어려운 부분은 "가속에 맞춰 탱크를 기울이기" 가 아니다. **슬로시의 시간
동역학**이다: 기동이 끝나도 액체는 계속 움직이고, 움직이는 액체가 탱크의 무게
중심과 액추에이터 하중을 바꾸며, 스태빌라이저의 현재 행동이 뒤이어 올 액체 운동과
제어 상태를 만든다.

## 한 장 요약

| | |
|---|---|
| **무엇을 하려 했나** | 액체 탱크 아래 2축 짐벌을 AI 가 제어해 스필(넘친 양, 리터)을 줄일 수 있는가. 물리 → 센서 → 액추에이터 → 컨트롤러 → 학습 → 평가 파이프라인을 직접 끝까지 만들며 이해하는 것이 진짜 목적이다. |
| **무엇을 했나** | 결정론적 시뮬레이션(차량·도로·짐벌·슬로시 2종·센서)과 평가 하네스를 만들고, 같은 주행을 네 가지로 비교했다: **① 강체**(짐벌을 잠가 탱크를 고정, 아무것도 안 함) · **② 규칙기반**(공식으로 기울임) · **③ 학습 정책**(사람 주행으로 학습한 AI) · **④ 천장**(주행 전체를 미리 아는 오프라인 최선). 이어서 예측 제어(MPC)와 탱크 설계(충전율·림 높이·배플)도 시험했다. |
| **결과** | ① 강체가 이겼다. 처음 보는 7 주행에서 ① 114 L, ② 166~175 L, ③ 120~131 L. 오라클 MPC 도 강체 대비 −5 ~ −8 % 뿐. 반면 **컨트롤러 없이 탱크만 바꾸면** 훨씬 크게 줄었다(얕은물 모델에서 내부 배플 −20 ~ −75 %). |
| **인사이트** | ① 아무것도 안 하는 기준선이 이미 강하면 먼저 그것부터 재라. ② 학습이 안 되면 하이퍼파라미터보다 **액션 공간**을 의심하라. ③ 제어기를 만들기 전에 **문제(탱크)를 바꿀 수 있는지** 먼저 보라. ④ 모델에 따라 크기가 갈리는 결과는 보수적인 쪽을 믿어라. |

**이어서 읽기:** 질문 → 시도 → 답의 흐름과 15개 교훈은 [docs/lessons.md](docs/lessons.md)
(처음 읽는다면 이것부터). 숫자의 근거는 [docs/findings.md](docs/findings.md).

## 어디서부터 읽나

**문서는 모두 한글.** [docs/README.md](docs/README.md) 가 색인(용어, 문서별 역할)이다.

- [docs/lessons.md](docs/lessons.md) — **결과·교훈 한 문서 요약 (처음 읽는다면 이것부터)**
- [docs/project-goals.md](docs/project-goals.md) — 무엇을 · 왜 · 최종 아키텍처
- [docs/experiment-plan.md](docs/experiment-plan.md) — 현황 · 만든 것 · 닫힘/열림
- [docs/findings.md](docs/findings.md) — 실험으로 밝혀진 사실 F1~F17 (숫자·근거)
- [docs/decisions.md](docs/decisions.md) — 설계 결정 로그 D1~D14
- [docs/operating-guide.md](docs/operating-guide.md) — 조작 가이드 (키·패널·서버)
- [docs/demo-guide.md](docs/demo-guide.md) — 시연 대본 (비개발자 청중)
- [docs/design-review.md](docs/design-review.md) — 설계 시점의 근거 (큰 참고 문서)

## 저장소 구조

```
fluid-stabilizer/
├── Assets/FluidStabilizer/   엔진프리 시뮬 코어 + 컨트롤러 + RL + Unity 껍데기
│                             (어셈블리 레이아웃: Assets/FluidStabilizer/README.md)
├── python/                   RL 학습기 + 정책 서빙 (serve_policy.py) — 별도 프로세스
├── docs/                     설계 문서 (한글) — 먼저 읽을 것
├── experiment.txt            운영점 손잡이 (fill / 도로 / 운전자 / 에피소드 / 슬로시 / seed)
├── Packages/  ProjectSettings/   Unity 6000.5.10f1, Built-in 렌더 파이프라인
├── Traces/                   녹화 주행 (로컬 데이터, 대부분 gitignore)
└── Builds/                   standalone 빌드 (gitignore)
```

## 현재 상태

**마무리 단계** (2026-10-16, [D14](docs/decisions.md)). 녹화 → hindsight 천장 → BC 데이터셋 → BC 사전학습
→ DAgger/PPO → `serve_policy.py` → Unity 앱 내 ①②③④ 비교 → standalone 앱까지 처음부터 끝까지
돈다. 재현성(trace v2 · 데이터셋 split · 실행 증거)과 정책 행동 계약도 갖춰졌다. EditMode
테스트 **158 통과 / 0 실패 / 16 스킵**. 리모트 `origin` = `github.com/yimsemin/fluid-stabilizer`.
결과는 위 "한 장 요약", 현황과 열린 후보는 [docs/experiment-plan.md](docs/experiment-plan.md).

## 프로젝트 열기

Unity Hub → **Add → Add project from disk** → 이 폴더 선택. Unity
**6000.5.10f1** 정확히 (Mac 과 Windows 같은 버전).

플레이 씬: 메뉴 ▸ FluidStabilizer ▸ Rebuild Play Scene 후 `PlayScene.unity` 열기. 키와 패널은
[docs/operating-guide.md](docs/operating-guide.md), 시연 흐름은 [docs/demo-guide.md](docs/demo-guide.md).
