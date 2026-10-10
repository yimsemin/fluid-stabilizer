# fluid-stabilizer — RL trainer

External Python trainer. **No Unity dependency** — talks to the C#
`TrainingServer` over a plain TCP socket.

- `fluidstab/client.py` — raw socket protocol (matches `Assets/FluidStabilizer/Rl/TrainingServer.cs`)
- `fluidstab/env.py` — `FluidStabEnv(gymnasium.Env)`; `info["privileged"]` holds the asymmetric-critic obs
- `train_ppo.py` — CleanRL-style PPO with an asymmetric critic
- `play.py` — run a checkpoint, print episode returns
- `bc_pretrain.py` — behaviour-cloning pretraining from `Logs/bc/dataset-*.csv`
- `serve_policy.py` — serve a checkpoint to Unity (`--release-bias/--release-ema` are experiment options, off by default)
- `dagger.py` — DAgger loop around the Unity plant (closed line, see `docs/findings.md` F12)

See [`../docs/rl.md`](../docs/rl.md) for the full workflow.

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
