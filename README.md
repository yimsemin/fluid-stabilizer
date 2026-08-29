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

- [docs/project-goals.md](docs/project-goals.md) — 무엇을 · 왜 · 헤드라인 결과 · 최종 아키텍처
- [docs/experiment-plan.md](docs/experiment-plan.md) — 살아있는 현황 + 다음 단계
- [docs/vision-check.md](docs/vision-check.md) — 초안 단계별 데모 + 소유자 체크리스트
- [docs/findings.md](docs/findings.md) — 실험으로 밝혀진 사실 F1~F5
- [docs/decisions.md](docs/decisions.md) — 설계 결정 로그 D1~D6
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

**초안(수직 슬라이스) 완성** (2026-08-29). 파이프라인 전체가 처음부터 끝까지
돈다: 녹화 → hindsight 천장 → BC 데이터셋 → BC 사전학습 → PPO 미세조정 →
`serve_policy.py` → Unity 앱 내 ①②③ 비교 → standalone 앱. EditMode 테스트
113 통과 / 8 스킵. GitHub 리모트 `origin` = `github.com/yimsemin/fluid-stabilizer`.

정직한 헤드라인: 현재 fill/도로 조건에서는 **① 강체 탱크가 이긴다** (finding F5).
다음은 소유자 중간 점검 → F5 전제 조정. 자세한 건
[docs/experiment-plan.md](docs/experiment-plan.md).

## 프로젝트 열기

Unity Hub → **Add → Add project from disk** → 이 폴더 선택. Unity
**6000.5.10f1** 정확히 (Mac 과 Windows 같은 버전).

플레이 씬: 메뉴 ▸ FluidStabilizer ▸ Rebuild Play Scene 후 `PlayScene.unity` 열기.
`W/S` 가감속, `A/D` 조향, `0~4` 스태빌라이저, `C` 3-way 비교, `P` 주행 녹화.
