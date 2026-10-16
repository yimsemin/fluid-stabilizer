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

## 어디서부터 읽나

**문서는 모두 한글.** [docs/README.md](docs/README.md) — 문서 색인, "문서마다
하나의 역할", 정비 이력.

- [docs/lessons.md](docs/lessons.md) — **결과·교훈 한 문서 요약 (처음 읽는다면 이것부터)**
- [docs/project-goals.md](docs/project-goals.md) — 무엇을 · 왜 · 헤드라인 결과 · 최종 아키텍처
- [docs/experiment-plan.md](docs/experiment-plan.md) — 살아있는 현황 + 다음 단계
- [docs/demo-guide.md](docs/demo-guide.md) — **시연용 참고 문서** (비개발자 청중)
- [docs/findings.md](docs/findings.md) — 실험으로 밝혀진 사실 F1~F17
- [docs/decisions.md](docs/decisions.md) — 설계 결정 로그 D1~D14
- [docs/design-review.md](docs/design-review.md) — 아키텍처 결정의 근거 (큰 참고 문서)

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

**마무리 단계** (2026-10-16). 녹화 → hindsight 천장 → BC 데이터셋 → BC 사전학습 → DAgger/PPO
→ `serve_policy.py` → Unity 앱 내 ①②③④ 비교 → standalone 앱까지 처음부터 끝까지 돈다.
재현성(trace v2 · 데이터셋 split · 실행 증거)과 정책 행동 계약도 갖춰졌다. EditMode 테스트
**158 통과 / 0 실패 / 16 스킵**. 리모트 `origin` = `github.com/yimsemin/fluid-stabilizer`.

**정직한 헤드라인: ① 강체 탱크가 이긴다.** 학습·튜닝에 쓰지 않은 홀드아웃 7 주행에서도
① 114.4 L, ② 규칙기반 166~175 L, ③ 학습 정책 120~131 L ([F13](docs/findings.md)).
완전 상태와 정확한 모델을 가진 오라클 MPC 도 −5 ~ −8 % 에 그쳤다 (F14).

**그리고 탱크 설계가 먼저다.** 컨트롤러 없이 충전율·림 높이·내부 배플만 바꿔도 스필이
능동 제어보다 훨씬 크게 줄었다 — 얕은물 모델에서 배플만으로 −20 ~ −75 % (F15·F16).
배플 탱크에서도 강체가 능동 제어를 이겼다 (F17). 정량은 슬로시 모델에 따라 갈려서
보수적인 얕은물 값을 쓴다.

③ 학습 정책은 합성 조건(매끄러운 노면의 지속 코너링)에서 규칙기반을 **51 %** 앞서며
실제로 학습하지만(F10) 강체는 못 이긴다. 학습이 막혔던 원인은 하이퍼파라미터가 아니라
**액션 공간**이었다 (F9 → F10).

결과·교훈·후속 제언: [docs/lessons.md](docs/lessons.md). 현황과 열린 후보:
[docs/experiment-plan.md](docs/experiment-plan.md).

## 프로젝트 열기

Unity Hub → **Add → Add project from disk** → 이 폴더 선택. Unity
**6000.5.10f1** 정확히 (Mac 과 Windows 같은 버전).

플레이 씬: 메뉴 ▸ FluidStabilizer ▸ Rebuild Play Scene 후 `PlayScene.unity` 열기.
`W/S` 가감속, `A/D` 조향, `0~5` 스태빌라이저(5 = 후행지식 천장), `C` 3-way 비교,
`P` 주행 녹화, `L` 재생. 시연 흐름은 [docs/demo-guide.md](docs/demo-guide.md).
