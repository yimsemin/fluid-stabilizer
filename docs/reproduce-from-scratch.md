# 처음부터 재현하는 법

**이 문서의 역할:** 공개 저장소를 새로 받은 사람이 *무엇을 직접 돌려볼 수 있고, 무엇은 재현되지 않는지*
한 곳에서 안내한다. 주행 파일·데이터 분할의 규칙은 [reproducibility.md](reproducibility.md), 조작(키·패널)은
[operating-guide.md](operating-guide.md), 결과 숫자는 [findings.md](findings.md) 가 담당한다.

## 먼저 알아둘 것 (한계)

| 항목 | 공개본의 상태 |
|---|---|
| 코드 · 문서 · 설정 | 전부 있음 |
| 녹화 주행 | **홀드아웃 `final` 7 개만** `Traces/` 에 있다. 현재 트리에 `train` 6 개와 `validation` 2 개는 없다(`datasets.tsv` 에는 목록과 해시만 있다). 과거 커밋 `4043c0d` 에는 `validation` 2 개가 포함돼 있다. |
| 학습된 모델(`*.pt`, `*.onnx`) · 학습 로그(`python/runs/`) | 없음 (gitignore). 학습 경로(BC·DAgger·PPO)는 코드로 있으나 학습에 쓴 주행이 없어 **같은 모델을 재생성할 수 없다.** |
| 재현되는 결과 | F13 (처음 보는 7 주행: ① 강체 · ② 규칙기반) — `final` 7 개만 쓰기 때문. 방법은 3절 |
| 그대로는 재현되지 않는 결과 | F14~F17 (MPC · 탱크 설계 스윕). 이 실험 도구는 `train` 주행을 읽는다. 자기 주행을 녹화해 `datasets.tsv` 에 등록하면 같은 *방법*으로 다시 돌릴 수 있지만 숫자는 달라진다. |
| 시뮬레이션 자체 | 같은 설정·시드는 같은 결과를 낸다 (결정론, 고정 시간 간격). |

따라서 이 저장소는 "숫자를 그대로 다시 얻는" 용도보다 **방법·구조·결론의 근거를 읽고 응용하는** 용도에 맞다.

## 1. 준비

1. **Unity 6000.5.10f1** 을 Unity Hub 로 설치한다 (정확히 이 버전. `ProjectSettings/ProjectVersion.txt`).
   Built-in 렌더 파이프라인이라 추가 패키지 설치는 없다.
2. (선택) 정책 서빙·학습을 쓸 때만: [uv](https://docs.astral.sh/uv/) 설치 후 `cd python && uv sync`.
3. 저장소를 받고 Git LFS 를 켠다 (`git lfs install`).

## 2. 테스트로 확인하기

터미널 (Unity 에디터가 같은 프로젝트를 열고 있으면 실행되지 않는다):

```bash
"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile -
```

Windows 는 Unity 실행 파일 경로만 바꾼다. 공개본에서는 일부 테스트가 `Ignore`(스킵)된다 — 학습 데이터가
없을 때 스킵되도록 만든 테스트다. **실패는 0 이어야 한다.** 공개본을 새로 clone 해 확인한 값(2026-10-25): 157 통과 / 0 실패 / 17 스킵. 전체 주행을 가진 환경의 값은 [experiment-plan.md](experiment-plan.md) 에 있다.

## 3. F13 숫자 다시 얻기

공개본의 `datasets.tsv` 는 비공개 주행(`train`·`validation`)도 나열하므로, 기본 설정의 **FluidStabilizer ▸
Evaluate Recorded Drives** 메뉴는 없는 파일을 찾다가 실패한다. `final` 7 개만 담은 `datasets-public.tsv`
로 헤드리스 실행한다 (Unity 에디터를 닫고 실행).

```bash
"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -quit -nographics -projectPath . -executeMethod FluidStabilizer.SimEditor.TraceEvalRunner.Run -traceManifest datasets-public.tsv -traceSplit final -logFile -
```

결과는 `Logs/trace-eval-*.txt` 의 끝에 있는 "summary" 표이고, 주행별 스필 합계가 진자 모델 기준으로
① 강체 114.4 L · ② SpecificForceAlignment 166.3 L · ② PID 167.0 L · PrivilegedFeedforward 174.6 L 이어야 한다
([F13](findings.md)). 얕은물 값(① 583.4 L 등)은 `TraceEvalRunner.RunWithShallowWater` 로 얻으며 느리다 —
이 경로는 재현을 확인하지 않았다.

## 4. 앱에서 눈으로 보기

1. Unity Hub 로 이 폴더를 연다 → 메뉴 **FluidStabilizer ▸ Rebuild Play Scene**.
2. `Assets/FluidStabilizer/Scenes/PlayScene.unity` 를 열고 Play ▶.
3. `L` 키로 녹화 주행을 재생하고 `0~2` 로 ① 강체 / ② 규칙기반을 바꿔 같은 주행을 비교한다.
   `final` 주행은 `Traces/` 에 있다. 키와 패널: [operating-guide.md](operating-guide.md).
4. (선택) README 의 비교 GIF 프레임 다시 만들기 — 에디터를 닫고, 그래픽을 켠 채(`-nographics` 없이) 실행하면
   `Logs/gif/` 에 ①②의 프레임 PNG 와 누적 스필 `spill.csv` 가 생긴다. GIF 조립: `python/.venv/bin/python docs/images/make_compare_gif.py` (Pillow 필요).

```bash
"/Applications/Unity/Hub/Editor/6000.5.10f1/Unity.app/Contents/MacOS/Unity" -batchmode -quit -projectPath . -executeMethod FluidStabilizer.SimEditor.SceneCapture.GrabCompareFrames -capTrace Traces/drive-20261010-232523-254-64830d7c.trace.csv -capSeconds 40 -capStep 0.4 -capFov 24 -logFile -
```

## 5. 탱크 설계 실험을 자기 데이터로 다시 하기

[experiment-plan.md](experiment-plan.md) "수동 해법 실험 도구" 의 `-executeMethod` 명령(`SloshSweepRunner`,
`ActiveVsBaffleRunner`)이 `train` 주행을 읽는다. 순서:

1. 앱에서 `P` 로 자기 주행 3~5 개를 녹화한다 (`experiment.txt` 운영점을 먼저 정한다).
2. `shasum -a 256` 으로 해시를 구해 `datasets.tsv` 에 `train` 행으로 등록한다 (절차: [reproducibility.md](reproducibility.md) 2절).
3. 위 명령을 실행한다. 결과는 `Logs/` 에 쓰인다.

## 6. 정책 학습·서빙 (선택)

학습 경로는 [rl.md](rl.md) 와 `python/README.md` 가 담당한다. 이 경로는 녹화 주행 → 교사 궤적 → BC → 서빙으로
이어지며, 위 한계 때문에 공개본만으로는 이 저장소가 보고한 모델을 다시 만들지 못한다.
