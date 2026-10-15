"""Locally trained regressors. Scaling is fitted on training observations only."""

from dataclasses import dataclass

import numpy as np


def training_arrays(x: np.ndarray, y: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    if x.ndim != 2 or y.shape != (len(x),) or len(x) < 2:
        raise ValueError("Invalid training shapes")
    if not np.isfinite(x).all() or not np.isfinite(y).all():
        raise ValueError("Training values must be finite")
    mean = x.mean(axis=0)
    scale = x.std(axis=0)
    scale[scale < 1e-12] = 1.0
    return (x - mean) / scale, mean, scale


@dataclass
class Ridge:
    alpha: float = 10.0

    def fit(self, x: np.ndarray, y: np.ndarray) -> None:
        scaled, self.mean, self.scale = training_arrays(x, y)
        self.target_mean = float(y.mean())
        self.weights = np.linalg.solve(scaled.T @ scaled + self.alpha * np.eye(x.shape[1]), scaled.T @ (y - self.target_mean))

    def predict(self, x: np.ndarray) -> np.ndarray:
        return ((x - self.mean) / self.scale) @ self.weights + self.target_mean

    def export(self) -> dict:
        return {"model": "ridge", "device": "cpu", "alpha": self.alpha,
                "mean": self.mean.tolist(), "scale": self.scale.tolist(),
                "weights": self.weights.tolist(), "target_mean": self.target_mean}


def torch_device(request: str) -> str:
    try:
        import torch
    except ImportError as exc:
        raise ValueError("Neural models require: uv sync --extra neural") from exc
    if request == "hpu":
        try:
            import habana_frameworks.torch  # noqa: F401
        except ImportError as exc:
            raise ValueError("HPU requires a Gaudi host and its vendor PyTorch bridge; unavailable here") from exc
        if not hasattr(torch, "hpu") or not torch.hpu.is_available():
            raise ValueError("No HPU available")
        return "hpu"
    if request == "auto":
        if torch.cuda.is_available():
            return "cuda"
        if hasattr(torch, "xpu") and torch.xpu.is_available():
            return "xpu"
        if torch.backends.mps.is_available():
            return "mps"
        return "cpu"
    if request == "cuda" and not torch.cuda.is_available():
        raise ValueError("CUDA requested but no compatible CUDA device is available")
    if request == "mps" and not torch.backends.mps.is_available():
        raise ValueError("MPS requested but unavailable on this host/PyTorch installation")
    if request == "xpu" and (not hasattr(torch, "xpu") or not torch.xpu.is_available()):
        raise ValueError("Intel XPU requested but unavailable; validate the GPU, drivers and Intel PyTorch wheel on the target host")
    if request not in ("cpu", "cuda", "mps", "xpu"):
        raise ValueError(f"Unsupported device: {request}")
    return request


@dataclass
class Neural:
    device: str = "cpu"
    epochs: int = 60
    seed: int = 212

    def fit(self, x: np.ndarray, y: np.ndarray) -> None:
        import torch
        from torch import nn

        self.resolved_device = torch_device(self.device)
        torch.manual_seed(self.seed)
        scaled, self.mean, self.scale = training_arrays(x, y)
        self.target_mean = float(y.mean())
        self.target_scale = max(float(y.std()), 1e-8)
        self.network = nn.Sequential(nn.Linear(x.shape[1], 32), nn.ReLU(), nn.Linear(32, 16), nn.ReLU(), nn.Linear(16, 1)).to(self.resolved_device)
        features = torch.tensor(scaled, dtype=torch.float32, device=self.resolved_device)
        targets = torch.tensor((y - self.target_mean) / self.target_scale, dtype=torch.float32, device=self.resolved_device)
        optimiser = torch.optim.AdamW(self.network.parameters(), lr=0.001, weight_decay=0.01)
        self.network.train()
        for _ in range(self.epochs):
            optimiser.zero_grad()
            loss = nn.functional.mse_loss(self.network(features).squeeze(-1), targets)
            loss.backward()
            optimiser.step()
        self.network.eval()
        if self.resolved_device != "cpu":
            getattr(torch, self.resolved_device).synchronize()

    def predict(self, x: np.ndarray) -> np.ndarray:
        import torch

        features = torch.tensor((x - self.mean) / self.scale, dtype=torch.float32, device=self.resolved_device)
        with torch.no_grad():
            return self.network(features).squeeze(-1).cpu().numpy().astype(float) * self.target_scale + self.target_mean

    def export(self) -> dict:
        import torch

        return {"model": "mlp", "device": self.resolved_device, "torch": torch.__version__, "precision": "float32", "epochs": self.epochs,
                "seed": self.seed, "mean": self.mean.tolist(), "scale": self.scale.tolist(),
                "target_mean": self.target_mean, "target_scale": self.target_scale,
                "architecture": [len(self.mean), 32, 16, 1],
                "state": {name: tensor.detach().cpu().tolist() for name, tensor in self.network.state_dict().items()}}
