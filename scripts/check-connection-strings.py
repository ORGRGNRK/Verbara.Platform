#!/usr/bin/env python3
"""Postgres connection-string guard: no shipped stack pastes the Postgres password into a connection string.

Usage:
  check-connection-strings.py [--root DIR]

The platform's .NET services read their Postgres connection strings from ConnectionStrings__* environment
variables, and Npgsql parses those strings the ADO.NET way: ';' ends a value, and a value holding a quote or
leading/trailing spaces is read differently than written. A stack that pastes POSTGRES_PASSWORD into the
string unquoted therefore starts fine with the default password and breaks the moment an operator sets a
real one with ';', a quote or a space in it — the Api, realtime service and workers cannot reach Postgres,
while Asterisk (whose entrypoint escapes the same password into res_pgsql.conf) works. Since #673 the
password travels as PGPASSWORD instead, which Npgsql reads from the process environment, where no parsing
stands between the operator's value and the server.

The script finds every compose file under docker/ (so a new stack is checked without being listed here) and,
for each stack — a compose file with a `postgres` service — works out that stack's Postgres password (the
postgres service's POSTGRES_PASSWORD after interpolation). It does so twice: with no variables set (every
`${VAR:-default}` takes its default) and with POSTGRES_DB, POSTGRES_USER and POSTGRES_PASSWORD set to probe
values (an operator's own .env; the probe password holds ';', a single quote, a double quote and a space).
In each environment it requires, for every ConnectionStrings__* value that names a Postgres host:

  - the value carries no Password= segment; and
  - the service carrying the string sets PGPASSWORD to exactly the stack's password.

A string that arrives wholesale from an operator variable (docker-compose.verified.yml's
PG_CONNECTION_STRING) interpolates to an empty or password-free value under both probe environments and is
judged on what the file itself builds. Redis connection strings name no host and are not Postgres strings;
they are ignored. A side whose value needs a variable compose would refuse to start without (`${VAR:?}`)
skips that environment.

The interpolation is compose's: ${VAR}, ${VAR:-d}, ${VAR-d}, ${VAR:?m}, ${VAR?m}, ${VAR:+a}, ${VAR+a}, $VAR
and $$.

Exit codes:
  0  pass
  1  a stack pastes its Postgres password into a connection string, or a service lacks its PGPASSWORD
  2  usage error, a compose file that cannot be read, or no compose file found to check
"""
import argparse
import glob
import os
import re
import sys

try:
    import yaml
except ImportError:  # pragma: no cover - CI installs PyYAML before this step
    print("check-connection-strings: PyYAML is required (pip install pyyaml)", file=sys.stderr)
    sys.exit(2)

_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir))

_EXIT_FAIL = 1
_EXIT_INPUT = 2

PROBE_PASSWORD = "probe;pass'word\" x"

ENVIRONMENTS = [
    ("no variables set", {}),
    ("POSTGRES_* set in .env", {
        "POSTGRES_DB": "probe_db",
        "POSTGRES_USER": "probe_user",
        "POSTGRES_PASSWORD": PROBE_PASSWORD,
        "PG_CONNECTION_STRING": "Host=postgres;Database=probe_db;Username=probe_user",
    }),
]

_VAR = re.compile(r"\$(?:\$|\{([A-Za-z_][A-Za-z0-9_]*)(?:(:?[-?+])([^}]*))?\}|([A-Za-z_][A-Za-z0-9_]*))")

_PASSWORD_SEGMENT = re.compile(r"(?:^|;)\s*Password\s*=", re.IGNORECASE)


class _ComposeLoader(yaml.SafeLoader):
    """SafeLoader that reads compose's merge tags (`!reset`, `!override`) as plain values."""


def _compose_tag(loader, _suffix, node):
    if isinstance(node, yaml.MappingNode):
        return loader.construct_mapping(node)
    if isinstance(node, yaml.SequenceNode):
        return loader.construct_sequence(node)
    return loader.construct_scalar(node)


_ComposeLoader.add_multi_constructor("!", _compose_tag)


class InputError(Exception):
    pass


class Refused(Exception):
    """compose would refuse to start: a `${VAR:?}` / `${VAR?}` variable is missing."""


def interpolate(value, env):
    if value is None:
        return None
    text = str(value)

    def sub(m):
        if m.group(0) == "$$":
            return "$"
        name = m.group(1) or m.group(4)
        op, arg = m.group(2), m.group(3) or ""
        present = name in env
        val = env.get(name, "")
        if op == ":-":
            return val if val else arg
        if op == "-":
            return val if present else arg
        if op == ":?":
            if not val:
                raise Refused(name)
            return val
        if op == "?":
            if not present:
                raise Refused(name)
            return val
        if op == ":+":
            return arg if val else ""
        if op == "+":
            return arg if present else ""
        return val

    return _VAR.sub(sub, text)


def service_env(service):
    """A service's environment as {name: raw value}; a bare name (taken from the host) maps to None."""
    raw = (service or {}).get("environment") or {}
    if isinstance(raw, dict):
        return dict(raw)
    env = {}
    for item in raw:
        text = str(item)
        if "=" in text:
            name, val = text.split("=", 1)
            env[name] = val
        else:
            env[text] = None
    return env


def load_compose(path):
    try:
        with open(path, encoding="utf-8") as fh:
            doc = yaml.load(fh, Loader=_ComposeLoader)
    except (OSError, yaml.YAMLError) as exc:
        raise InputError(f"{path}: cannot read compose file ({exc})")
    if not isinstance(doc, dict):
        raise InputError(f"{path}: compose file is not a mapping")
    return doc.get("services") or {}


def check_file(path, root):
    failures = []
    services = load_compose(path)
    rel = os.path.relpath(path, root)
    envs = {name: service_env(svc) for name, svc in services.items()}
    postgres_env = envs.get("postgres")

    for env_name, env in ENVIRONMENTS:
        try:
            stack_password = None
            if postgres_env is not None and postgres_env.get("POSTGRES_PASSWORD") is not None:
                stack_password = interpolate(postgres_env["POSTGRES_PASSWORD"], env)
            for svc_name in sorted(envs):
                svc_env = envs[svc_name]
                for key in sorted(svc_env):
                    if not key.startswith("ConnectionStrings__"):
                        continue
                    raw = svc_env[key]
                    if raw is None:
                        continue
                    value = interpolate(raw, env)
                    if not value or "Host=" not in value:
                        continue  # not a Postgres connection string this file builds
                    if _PASSWORD_SEGMENT.search(value):
                        failures.append(
                            f"{rel} [{env_name}] service {svc_name}: {key} pastes a Password= segment "
                            "into the connection string; carry the password as PGPASSWORD instead"
                        )
                    if stack_password is not None:
                        pg = svc_env.get("PGPASSWORD")
                        if pg is None and "PGPASSWORD" in svc_env:
                            continue  # taken from the host environment; cannot be checked statically
                        if pg is None:
                            failures.append(
                                f"{rel} [{env_name}] service {svc_name}: {key} names a Postgres host "
                                "but the service sets no PGPASSWORD"
                            )
                        elif interpolate(pg, env) != stack_password:
                            failures.append(
                                f"{rel} [{env_name}] service {svc_name}: PGPASSWORD does not match the "
                                "stack's Postgres password"
                            )
        except Refused:
            continue  # compose would refuse to start in this environment; nothing to judge
    return failures


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=_ROOT, help="repository root (default: the script's repository)")
    args = parser.parse_args(argv)
    root = os.path.normpath(args.root)

    paths = sorted(
        glob.glob(os.path.join(root, "docker", "**", "*.yml"), recursive=True)
        + glob.glob(os.path.join(root, "docker", "**", "*.yaml"), recursive=True)
    )
    if not paths:
        print(f"check-connection-strings: no compose files found under {os.path.join(root, 'docker')}",
              file=sys.stderr)
        return _EXIT_INPUT

    failures = []
    try:
        for path in paths:
            failures.extend(check_file(path, root))
    except InputError as exc:
        print(f"check-connection-strings: {exc}", file=sys.stderr)
        return _EXIT_INPUT

    for failure in failures:
        print(failure)
    if failures:
        return _EXIT_FAIL
    print(f"check-connection-strings: {len(paths)} compose files checked, no connection string "
          "carries its stack's Postgres password")
    return 0


if __name__ == "__main__":
    sys.exit(main())
