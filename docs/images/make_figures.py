"""docs/images 의 그림 2장을 만든다. 숫자는 docs/lessons.md 표(F13·F14·F15·F16)에서 옮긴 값이다.
실행: uv run --with matplotlib python docs/images/make_figures.py"""
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from pathlib import Path

out = Path(__file__).parent
plt.rcParams.update({"font.family": "AppleGothic", "axes.unicode_minus": False})

# 그림 1: 처음 보는 7 주행의 스필 합계 [L] (F13)
fig, ax = plt.subplots(figsize=(7, 3.8))
names = ["① Rigid (no control)", "② Rule-based (PID)", "③ Learned policy (best)", "③ Learned policy (worst)"]
pend = [114.4, 167.0, 120.2, 131.3]
shal = [583.4, 1049.2, None, None]
x = range(len(names))
ax.bar([i - 0.2 for i in x], pend, 0.4, label="Pendulum slosh model", color="#4C78A8")
ax.bar([i + 0.2 for i, v in zip(x, shal) if v], [v for v in shal if v], 0.4, label="Shallow-water slosh model", color="#F58518")
for i, v in zip(x, pend): ax.text(i - 0.2, v + 12, f"{v:g}", ha="center", fontsize=8)
for i, v in zip(x, shal):
    if v: ax.text(i + 0.2, v + 12, f"{v:g}", ha="center", fontsize=8)
ax.set_xticks(list(x)); ax.set_xticklabels(names, fontsize=8)
ax.set_ylabel("Total spill, 7 unseen drives [L] (lower = better)")
ax.set_title("Doing nothing (rigid) beat every active controller")
ax.legend(fontsize=8, frameon=False)
fig.text(0.01, 0.01, "③ was not evaluated on the shallow-water model. Source: docs/findings.md F13", fontsize=7)
fig.tight_layout(rect=(0, 0.03, 1, 1)); fig.savefig(out / "fig1-controllers.png", dpi=150); plt.close(fig)

# 그림 2: 능동 제어 vs 탱크 설계 (진자 162.8 L 기준 / 얕은물 874.7 L 기준, 8 주행) (F14·F15·F16)
fig, ax = plt.subplots(figsize=(7, 3.8))
labels = ["Oracle MPC\n(active control)", "Fill 0.62->0.56", "Rim 0.6->0.8 m", "Baffles\n2+1", "Baffles\n3+2"]
pct = [-6.2, -23, -29, -40, -62]   # MPC: 150.5~154.8 vs 162.8 의 중간값(-4.9~-7.6 %의 중앙 ≈ -6.2)
colors = ["#B279A2", "#54A24B", "#54A24B", "#54A24B", "#54A24B"]
ax.bar(labels, pct, color=colors)
for i, v in enumerate(pct): ax.text(i, v - 3.5, f"{v:g} %", ha="center", fontsize=8)
ax.set_ylim(-72, 5); ax.axhline(0, color="k", lw=0.6)
ax.set_ylabel("Change in spill vs baseline [%]")
ax.set_title("Changing the tank cuts spill far more than active control")
fig.text(0.01, 0.01, "Shallow-water model (conservative) except MPC (pendulum, range -4.9 to -7.6 %, midpoint shown).\nSource: docs/lessons.md; F14, F15, F16", fontsize=7)
fig.tight_layout(rect=(0, 0.06, 1, 1)); fig.savefig(out / "fig2-tank-design.png", dpi=150); plt.close(fig)
