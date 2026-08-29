# 점검 — "이게 내가 원하는 방향으로 가고 있나?"

(vision-check) 소유자(owner)가 직접 관리하는 문서.

해결하려는 문제: 프로젝트가 한 층(슬로시 정밀도, feasibility 천장 등)만 계속
파고들다 보면 *전체*가 제대로 가고 있는지 감이 안 잡힌다. 그래서 **초안(수직
슬라이스)** 의 각 단계([experiment-plan.md](experiment-plan.md) → "초안" 절)를
끝낼 때마다 Claude가 짧은 데모를 올리고 아래 표에 한 줄을 추가한다. 소유자는
O/X 를 매기고 한 줄 코멘트를 남긴다.

데모를 읽는 법: **직접 실행할 수 있는 것**(메뉴 항목, 플레이 씬, 빌드) 또는
스크린샷/짧은 영상이어야 한다 — 로그 파일만으로는 부족하다.

| 단계 | 날짜 | 데모 (무엇을 실행하나) | 판정 | 코멘트 |
|---|---|---|---|---|
| A · 실험 프로파일 | 2026-08-29 | `experiment.txt` 를 편집하고 Play — 씬이 파일을 따라가야 함 | ⬜ | |
| B · 잔차 RL 배관 | 2026-08-29 | `-rlBaseline hold` 로 RL 서버 실행 — 부팅되고 로그에 "RL residual on Hold …" 가 찍힘 | ⬜ | |
| C · BC 웜스타트 | 2026-08-29 | `FluidStabilizer ▸ Export BC Dataset` → `python bc_pretrain.py` → `train_ppo.py --init-actor runs/bc/actor.pt --total-steps 20000` — 학습되고 크래시 없고 체크포인트 기록됨 | ⬜ | 초안 실행됨: 리턴 ≈ −2280 에서 안정 (from-scratch 의 −3200 절벽 아님). 실제 긴 학습은 나중 컴퓨팅 단계. |
| D · 정책을 별도 프로세스로 | 2026-08-29 | `python serve_policy.py --checkpoint runs/bc/actor.pt --port 5601` → `FluidStabilizer ▸ Evaluate Learned Policy` (또는 플레이 씬 키 `4`) — 세계가 브릿지로 뇌에 연결되고 주행별 스필이 출력됨 | ⬜ | 끝까지 돌아감. 정직한 결과: ③(BC 정책) 24–30 L vs ① 19–22 L → **여기선 강체가 이김** (F5). 아키텍처 수정: ONNX 를 유니티에 굽지 않음. |
| E · 앱 안의 3-way 비교 | 2026-08-29 | 플레이 씬 → `C` — ①②③ 트럭이 같은 도로 위에 나란히, 레인별 리터 패널, 최선 레인 초록. HUD 에 현재 `experiment.txt` 프로파일 표시. (③ 은 `serve_policy.py` 필요, 없으면 "offline → Hold") | ⬜ | |
| F · standalone 빌드 | 2026-08-29 | `FluidStabilizer ▸ Build Standalone App` → `Builds/fluid-stabilizer-StandaloneOSX/fluid-stabilizer.app` (더블클릭 실행) | ⬜ | 104 MB 유니버설 바이너리, 웜 빌드 ~6 초 |

## 소유자가 아무 때나 답하면 되는 열린 질문

- **헤드라인 결과.** 정직한 초안이 "① 강체 탱크가 이긴다" 로 나오면, 학습
  프로젝트의 헤드라인으로 그걸 받아들일 수 있나? ("③ 가 이길 조건은 여기다" 를
  후속으로 붙이면서.) 현재 기울기: 그렇다 — [experiment-plan.md](experiment-plan.md) "초안" 절.
- **정밀도 vs 폭.** 초안 이후, 노력을 먼저 어디에 — 더 강한/다시 하는 RL,
  더 높은 물리 정밀도, 아니면 제품 껍데기 다듬기?
- **앱.** 당신에게 "완성" 은 어떤 모습인가 — 누군가에게 시연하는 것, 계속
  만지작거리는 것, 아니면 포트폴리오 산출물?

## 손잡이(knob)들이 있는 곳

레포 최상위의 `experiment.txt` — fill, 도로, 운전자, 에피소드 길이, 슬로시 모델,
seed. 파일 하나를 플레이 씬 · RL 학습 · 평가 · 비교가 모두 읽는다. 그 외 나머지는
`Assets/FluidStabilizer/Core/Sim/SimulationConfig.cs` 의 코드 기본값.
