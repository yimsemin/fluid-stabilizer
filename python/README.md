# fluid-stabilizer — RL 학습기

외부 Python 학습기. **Unity 의존성 없음** — C# `TrainingServer` 와 평범한 TCP 소켓으로 대화한다.

- `fluidstab/client.py` — 소켓 프로토콜 (`Assets/FluidStabilizer/Rl/TrainingServer.cs` 와 일치)
- `fluidstab/env.py` — `FluidStabEnv(gymnasium.Env)`; `info["privileged"]` 에 비대칭 크리틱 obs
- `train_ppo.py` — 비대칭 크리틱을 가진 CleanRL 스타일 PPO
- `play.py` — 체크포인트를 돌려 에피소드 return 출력
- `bc_pretrain.py` — `Logs/bc/dataset-*.csv` 로 행동 복제 사전학습
- `serve_policy.py` — 체크포인트를 Unity 에 서빙 (`--release-bias/--release-ema` 는 실험 옵션, 기본 꺼짐)
- `dagger.py` — Unity 플랜트 주변 DAgger 루프 (줄기는 닫힘, `docs/findings.md` F12)

전체 워크플로는 [`../docs/rl.md`](../docs/rl.md).

## 설치와 실행

Python 3.12 는 `python/.python-version` 으로 고정되고, uv 가 직접 내려받아 쓴다
(시스템/brew/python.org Python 에 의존하지 않음). 의존성의 정본은
`pyproject.toml` + `uv.lock`.

```bash
cd python
uv sync                                   # .venv 생성 + 고정된 버전 설치
uv run python serve_policy.py --checkpoint runs/bc/actor.pt --port 5601
uv run python train_ppo.py --port 5555 --total-steps 30000 --run-name smoke
```

Unity 쪽은 `python/.venv` 위치를 가정하므로 venv 는 이 폴더에 둔다 (uv 기본값).

uv 를 쓰지 않는 경우 (Python >= 3.10):

```bash
cd python
python3 -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt           # uv.lock 에서 생성된 파일 — 직접 수정 금지
```

의존성을 바꿀 때: `pyproject.toml` 수정 → `uv lock` →
`uv export --no-hashes --no-dev --no-emit-project -o requirements.txt`.
