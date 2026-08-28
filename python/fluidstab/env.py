"""A Gymnasium environment backed by the C# ``TrainingServer``.

The C# process owns the deterministic simulation; this is a thin transport.
One env == one TCP connection == one server process. For parallel rollouts,
launch N servers on N ports and make N of these.

``info["privileged"]`` carries the asymmetric-critic observation each step
(never fed to the actor).
"""
from __future__ import annotations

import numpy as np
import gymnasium as gym
from gymnasium import spaces

from .client import FluidStabClient


class FluidStabEnv(gym.Env):
    metadata = {"render_modes": []}

    def __init__(self, host: str = "127.0.0.1", port: int = 5555):
        super().__init__()
        self._c = FluidStabClient(host, port)

        self.observation_space = spaces.Box(
            low=-np.inf, high=np.inf, shape=(self._c.obs_size,), dtype=np.float32
        )
        self.action_space = spaces.Box(
            low=-1.0, high=1.0, shape=(self._c.act_size,), dtype=np.float32
        )
        self.privileged_size = self._c.priv_size
        self._priv = np.zeros(self._c.priv_size, dtype=np.float32)

    def reset(self, *, seed: int | None = None, options=None):
        super().reset(seed=seed)
        s = seed if seed is not None else int(self.np_random.integers(0, 2**31 - 1))
        obs = self._c.reset(s)
        self._priv = np.zeros(self._c.priv_size, dtype=np.float32)
        return obs, {"privileged": self._priv.copy()}

    def step(self, action):
        action = np.asarray(action, dtype=np.float32).clip(-1.0, 1.0)
        obs, reward, terminated, truncated, priv = self._c.step(action)
        self._priv = priv
        return obs, reward, terminated, truncated, {"privileged": priv.copy()}

    def close(self):
        self._c.close()
