"""Unit tests for check-connection-strings.py (no stack pastes its Postgres password into a connection string).

Runs the script as a subprocess against small repository trees written to a temp directory (`--root`), and once
against this repository's own docker/ tree. Needs PyYAML, which the `coverage-scripts` CI job installs.
"""
import os
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-connection-strings.py")
_REPO = os.path.normpath(os.path.join(_HERE, os.pardir, os.pardir))

_POSTGRES = (
    "services:\n"
    "  postgres:\n"
    "    image: postgres:18-alpine\n"
    "    environment:\n"
    "      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-platform_secret}\n"
)


def _stack(*api_env_lines):
    lines = [
        "  platform-api:\n",
        "    image: verbara/platform-api:test\n",
        "    environment:\n",
    ]
    lines += ["      %s\n" % ln for ln in api_env_lines]
    return _POSTGRES + "".join(lines)


_GOOD = _stack(
    "ConnectionStrings__Postgres: Host=postgres;Database=verbara;Username=platform",
    "PGPASSWORD: ${POSTGRES_PASSWORD:-platform_secret}",
    'ConnectionStrings__Redis: "redis:6379${REDIS_PASSWORD:+,password=probe}"',
)

_PASTED = _stack(
    "ConnectionStrings__Postgres: Host=postgres;Database=verbara;Username=platform;"
    "Password=${POSTGRES_PASSWORD:-platform_secret}",
    "PGPASSWORD: ${POSTGRES_PASSWORD:-platform_secret}",
)

_NO_PGPASSWORD = _stack(
    "ConnectionStrings__Postgres: Host=postgres;Database=verbara;Username=platform",
)

_WRONG_PGPASSWORD = _stack(
    "ConnectionStrings__Postgres: Host=postgres;Database=verbara;Username=platform",
    "PGPASSWORD: something-else",
)


def _run(root):
    return subprocess.run(
        [sys.executable, _SCRIPT, "--root", root],
        capture_output=True, text=True, timeout=60,
    )


class CheckConnectionStringsTest(unittest.TestCase):
    def _tree(self, compose):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        docker = os.path.join(tmp.name, "docker")
        os.makedirs(docker)
        with open(os.path.join(docker, "docker-compose.test.yml"), "w", encoding="utf-8") as fh:
            fh.write(compose)
        return tmp.name

    def test_password_carried_as_pgpassword_passes(self):
        result = _run(self._tree(_GOOD))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_pasted_password_fails(self):
        result = _run(self._tree(_PASTED))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("platform-api", result.stdout + result.stderr)
        self.assertIn("Password=", result.stdout + result.stderr)

    def test_missing_pgpassword_fails(self):
        result = _run(self._tree(_NO_PGPASSWORD))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("no PGPASSWORD", result.stdout + result.stderr)

    def test_mismatched_pgpassword_fails(self):
        result = _run(self._tree(_WRONG_PGPASSWORD))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("does not match", result.stdout + result.stderr)

    def test_repository_tree_passes(self):
        result = _run(_REPO)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
