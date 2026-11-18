#!/usr/bin/env python3
"""Agent Assist audio path on Kubernetes (agent-assist-works-end-to-end, design D3 and D9).

Usage:
  check-agentassist-k8s.py [--manifest FILE] [--values FILE] [--chart DIR] [--rendered FILE]

Asterisk connects to the acting Api replica's own pod address on the Agent Assist audio port (no Service in front).
The shipped files keep that path open when:
  - the audio port is the chart's `api.agentAssist.audioSocketPort` default (--values, default the chart's values.yaml),
    which must be set;
  - one `r55-platform` NetworkPolicy in --manifest (default infra/k8s/manifests/network-policies.yaml) selects
    `app.kubernetes.io/name: platform-api` and admits TCP on that port (by number, or by the container port's name)
    from a namespaceSelector on `kubernetes.io/metadata.name: r55-asterisk` (the default-deny drops it otherwise), and
    no rule of a platform-api policy there that names the port admits it from anything else: every `from` peer selects
    the r55-asterisk namespace (a podSelector beside it only narrows it), and a rule with no `from` admits every source;
  - `helm template <chart>` (or --rendered FILE) renders a platform-api Deployment whose `api` container declares that
    containerPort, takes POD_IP from `status.podIP`, sets AgentAssist__AudioSocket__AdvertisedHost (non-blank) and
    AgentAssist__AudioSocket__Port equal to the port;
  - every rendered container port name is an IANA service name: at most 15 characters, [a-z0-9-], starting and ending
    with a letter or digit, with at least one letter (the API server refuses the object otherwise);
  - the platform-api and platform-realtime Deployments render the same ConnectionStrings__Redis (one push backplane).
Each failure line names the file and the key.

CHECK_AGENTASSIST_K8S_HELM overrides the helm executable (tests).

Exit codes:
  0  pass
  1  a rule above fails
  2  usage error, no PyYAML, no helm, a render that fails, an input that cannot be read, or a port that is no number
"""
import argparse
import os
import re
import subprocess
import sys

try:
    import yaml
except ImportError:  # pragma: no cover - exercised only on a host without PyYAML
    print("check-agentassist-k8s: PyYAML is required (pip install pyyaml)", file=sys.stderr)
    sys.exit(2)

_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir))
_MANIFEST = os.path.join("infra", "k8s", "manifests", "network-policies.yaml")
_CHART = os.path.join("infra", "k8s", "helm", "platform")

_NAMESPACE = "r55-platform"
_FROM_NAMESPACE = "r55-asterisk"
_API_LABEL = ("app.kubernetes.io/name", "platform-api")
_API_DEFAULT_PORT = 9093  # AgentAssist:AudioSocket:Port when unset
_PORT_NAME_RE = re.compile(r"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")

_EXIT_FAIL = 1
_EXIT_INPUT = 2


class InputError(Exception):
    pass


def _read(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return fh.read()
    except OSError as e:
        raise InputError(f"cannot read {path}: {e}") from e


def _docs(text, where):
    try:
        return [d for d in yaml.safe_load_all(text) if isinstance(d, dict)]
    except yaml.YAMLError as e:
        raise InputError(f"{where}: not YAML: {e}") from e


def audio_port(values_path):
    try:
        values = yaml.safe_load(_read(values_path)) or {}
    except yaml.YAMLError as e:
        raise InputError(f"{values_path}: not YAML: {e}") from e
    agent_assist = ((values.get("api") or {}).get("agentAssist")) or {}
    if "audioSocketPort" not in agent_assist:
        return _API_DEFAULT_PORT, [f"{values_path}: api.agentAssist.audioSocketPort is not set (the Api listens on "
                                   f"{_API_DEFAULT_PORT} by default; checked against that)"]
    port = agent_assist["audioSocketPort"]
    if not isinstance(port, int) or isinstance(port, bool):
        raise InputError(f"{values_path}: api.agentAssist.audioSocketPort is not a port number ({port!r})")
    return port, []


def render(chart):
    helm = os.environ.get("CHECK_AGENTASSIST_K8S_HELM", "helm")
    try:
        r = subprocess.run([helm, "template", chart], capture_output=True, text=True, cwd=_ROOT, check=False)
    except OSError as e:
        raise InputError(f"cannot run '{helm} template {chart}': {e}") from e
    if r.returncode != 0:
        raise InputError(f"'helm template {chart}' failed (exit {r.returncode}): {r.stderr.strip()}")
    return r.stdout


def _from_asterisk(peer):
    """A `from` peer that selects the r55-asterisk namespace only (a podSelector in the same peer narrows it; the API
    server refuses an ipBlock beside a selector, so an ipBlock peer has no namespaceSelector and fails here)."""
    if not isinstance(peer, dict):
        return False
    ns = peer.get("namespaceSelector")
    return isinstance(ns, dict) and ((ns.get("matchLabels") or {}).get("kubernetes.io/metadata.name") == _FROM_NAMESPACE)


def _names_port(rule, port, port_names):
    for p in rule.get("ports") or []:
        if not isinstance(p, dict) or (p.get("protocol") or "TCP") != "TCP":
            continue
        if p.get("port") == port or (isinstance(p.get("port"), str) and p.get("port") in port_names):
            return True
    return False


def _admits(rule, port, port_names):
    sources = rule.get("from") or []
    return any(_from_asterisk(src) for src in sources) and _names_port(rule, port, port_names)


def _wider_sources(rule, port, port_names):
    """The peers of a rule naming the port that admit more than r55-asterisk; a rule with no `from` admits all."""
    if not _names_port(rule, port, port_names):
        return []
    sources = rule.get("from")
    if not sources:
        return ["no 'from' (every source)"]
    return [repr(src) for src in sources if not _from_asterisk(src)]


def check_policy(docs, where, port, port_names):
    failures = []
    admitted = False
    for d in docs:
        if d.get("kind") != "NetworkPolicy" or (d.get("metadata") or {}).get("namespace") != _NAMESPACE:
            continue
        spec = d.get("spec") or {}
        labels = ((spec.get("podSelector") or {}).get("matchLabels")) or {}
        if labels.get(_API_LABEL[0]) != _API_LABEL[1]:
            continue
        name = (d.get("metadata") or {}).get("name")
        for rule in spec.get("ingress") or []:
            if not isinstance(rule, dict):
                continue
            admitted = admitted or _admits(rule, port, port_names)
            for wider in _wider_sources(rule, port, port_names):
                failures.append(f"{where}: NetworkPolicy {name} admits TCP {port} to {_API_LABEL[0]}={_API_LABEL[1]} "
                                f"from a source beyond namespace {_FROM_NAMESPACE}: {wider}")
    if not admitted:
        failures.insert(0, f"{where}: no {_NAMESPACE} NetworkPolicy admits TCP {port} to {_API_LABEL[0]}={_API_LABEL[1]} "
                           f"from namespace {_FROM_NAMESPACE} (expected policy allow-agentassist-audio)")
    return failures


def _containers(doc):
    spec = ((((doc.get("spec") or {}).get("template")) or {}).get("spec")) or {}
    return list(spec.get("initContainers") or []) + list(spec.get("containers") or [])


def _deployment(docs, name):
    for d in docs:
        if d.get("kind") == "Deployment" and (d.get("metadata") or {}).get("name") == name:
            return d
    return None


def _env(container):
    return {e.get("name"): e for e in container.get("env") or [] if isinstance(e, dict)}


def check_rendered(docs, where, port):
    failures = []
    for d in docs:
        for c in _containers(d):
            for p in c.get("ports") or []:
                name = p.get("name")
                if name is None:
                    continue
                name = str(name)
                if len(name) > 15 or not _PORT_NAME_RE.match(name) or not re.search(r"[a-z]", name):
                    failures.append(f"{where}: {d.get('kind')}/{(d.get('metadata') or {}).get('name')} container "
                                    f"'{c.get('name')}' port name '{name}' is not an IANA service name (at most 15 "
                                    "characters of [a-z0-9-], a letter or digit at each end, at least one letter)")
    api = _deployment(docs, "platform-api")
    if api is None:
        raise InputError(f"{where}: no platform-api Deployment rendered")
    container = next((c for c in _containers(api) if c.get("name") == "api"), None)
    if container is None:
        raise InputError(f"{where}: the platform-api Deployment has no 'api' container")
    if not any(p.get("containerPort") == port and (p.get("protocol") or "TCP") == "TCP"
               for p in container.get("ports") or []):
        failures.append(f"{where}: platform-api container 'api' declares no TCP containerPort {port} "
                        "(api.agentAssist.audioSocketPort)")
    env = _env(container)
    pod_ip = (((env.get("POD_IP") or {}).get("valueFrom") or {}).get("fieldRef") or {}).get("fieldPath")
    if pod_ip != "status.podIP":
        failures.append(f"{where}: platform-api env POD_IP is not taken from status.podIP (found {pod_ip!r})")
    host = (env.get("AgentAssist__AudioSocket__AdvertisedHost") or {}).get("value")
    if host is None or not str(host).strip():
        failures.append(f"{where}: platform-api env AgentAssist__AudioSocket__AdvertisedHost is not set")
    env_port = (env.get("AgentAssist__AudioSocket__Port") or {}).get("value")
    if str(env_port) != str(port):
        failures.append(f"{where}: platform-api env AgentAssist__AudioSocket__Port is {env_port!r}, expected '{port}'")
    realtime = _deployment(docs, "platform-realtime")
    realtime_spec = ((((realtime or {}).get("spec") or {}).get("template")) or {}).get("spec") or {}
    realtime_container = next((c for c in realtime_spec.get("containers") or [] if c.get("name") == "realtime"), None)
    api_redis = (env.get("ConnectionStrings__Redis") or {}).get("value")
    rt_redis = (_env(realtime_container).get("ConnectionStrings__Redis") or {}).get("value") \
        if realtime_container else None
    if not api_redis or api_redis != rt_redis:
        failures.append(f"{where}: ConnectionStrings__Redis differs between platform-api ({api_redis!r}) and "
                        f"realtime ({rt_redis!r}); both must use the same Redis")
    return failures


def _port_names(docs):
    api = _deployment(docs, "platform-api")
    if api is None:
        return set()
    return {str(p.get("name")) for c in _containers(api) for p in c.get("ports") or [] if p.get("name")}


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--manifest", default=os.path.join(_ROOT, _MANIFEST))
    ap.add_argument("--chart", default=os.path.join(_ROOT, _CHART))
    ap.add_argument("--values", help="the chart's values file (default <chart>/values.yaml)")
    ap.add_argument("--rendered", help="an already rendered chart (helm template output), instead of helm")
    args = ap.parse_args(argv)
    values = args.values or os.path.join(args.chart, "values.yaml")
    try:
        port, failures = audio_port(values)
        rendered_where = args.rendered or f"helm template {os.path.relpath(args.chart, _ROOT)}"
        rendered = _docs(_read(args.rendered) if args.rendered else render(args.chart), rendered_where)
        policy = _docs(_read(args.manifest), args.manifest)
        failures += check_policy(policy, args.manifest, port, _port_names(rendered))
        failures += check_rendered(rendered, rendered_where, port)
    except InputError as e:
        print(f"check-agentassist-k8s: {e}", file=sys.stderr)
        return _EXIT_INPUT
    for line in failures:
        print(line)
    if failures:
        return _EXIT_FAIL
    print(f"check-agentassist-k8s: TCP {port} is declared, advertised and admitted from {_FROM_NAMESPACE}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
