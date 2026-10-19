"""Exercise the actual embedded Python helper without starting the desktop UI."""
import io
from pathlib import Path
import re
import sys
from types import ModuleType

import pytest


@pytest.mark.parametrize("solver", ["nextastro", "astrometrynet"])
def test_online_solver_helper_preserves_hints_and_identifies_transitlab(tmp_path, monkeypatch, solver):
    source = (Path(__file__).resolve().parents[1] / "Services" / "PlateSolveService.cs").read_text(encoding="utf-8-sig")
    script = re.search(r'private const string PythonScript = """\r?\n(.*?)\r?\n""";', source, re.DOTALL).group(1)
    calls = []
    result = tmp_path / "solved.wcs.fits"
    result.write_bytes(b"test-result")

    class Solver:
        def __init__(self, **kwargs):
            calls.append(kwargs)

        def plate_solution(self):
            return result

    module = ModuleType("exotic.api.plate_solution")
    module.NextAstroPlateSolution = Solver
    module.PlateSolution = Solver
    for name in ("exotic", "exotic.api"):
        monkeypatch.setitem(sys.modules, name, ModuleType(name))
    monkeypatch.setitem(sys.modules, "exotic.api.plate_solution", module)
    monkeypatch.setattr(sys, "argv", ["helper.py", solver, "image.fits", str(tmp_path), "123.4", "-25.6", "1.25", "9.8.7"])
    stdout = io.StringIO()
    monkeypatch.setattr(sys, "stdout", stdout)
    monkeypatch.setattr(sys, "stderr", io.StringIO())
    exec(compile(script, "TransitLab online plate-solve helper", "exec"), {})

    assert stdout.getvalue().strip() == str(result)
    assert calls[0]["file"] == "image.fits"
    if solver == "nextastro":
        assert calls[0]["client_name"] == "transitlab"
        assert calls[0]["client_api_key"] == "nextastro-transitlab-astrometry-v1"
        assert calls[0]["software_name"] == "TransitLab/9.8.7"
        assert calls[0]["origin"] == "transitlab"
        assert calls[0]["ra"] == 123.4
        assert calls[0]["dec"] == -25.6
        assert calls[0]["pixel_scale"] == 1.25
    else:
        assert calls[0] == {"file": "image.fits", "directory": str(tmp_path)}
