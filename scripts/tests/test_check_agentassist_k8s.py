"""Unit tests for check-agentassist-k8s.py (the Agent Assist audio path on Kubernetes).

Runs the script as a subprocess against a NetworkPolicy manifest, a chart values file and a rendered chart written to
a temp directory (`--manifest`, `--values`, `--rendered`), or through a stand-in `helm`
(CHECK_AGENTASSIST_K8S_HELM) that fails, so every case goes through the one production code path. Needs PyYAML
(installed by the `coverage-scripts` CI job), like the script itself.
"""
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-agentassist-k8s.py")

_VALUES = """\
api:
  agentAssist:
    advertisedHost: "$(POD_IP)"
    audioSocketPort: 9093
"""

_POLICY = """\
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: default-deny-ingress
  namespace: r55-platform
spec:
  podSelector: {}
  policyTypes:
    - Ingress
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-agentassist-audio
  namespace: r55-platform
spec:
  podSelector:
    matchLabels:
      app.kubernetes.io/name: platform-api
  policyTypes:
    - Ingress
  ingress:
    - from:
        - namespaceSelector:
            matchLabels:
              kubernetes.io/metadata.name: r55-asterisk
      ports:
        - protocol: TCP
          port: 9093
"""

_RENDERED = """\
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: platform-api
spec:
  template:
    spec:
      initContainers:
        - name: wait-for-postgres
          image: busybox
      containers:
        - name: api
          ports:
            - name: http
              containerPort: 5000
            - name: aa-audio
              containerPort: 9093
              protocol: TCP
          env:
            - name: POD_IP
              valueFrom:
                fieldRef:
                  fieldPath: status.podIP
            - name: ConnectionStrings__Redis
              value: "redis-0:6379"
            - name: AgentAssist__AudioSocket__AdvertisedHost
              value: "$(POD_IP)"
            - name: AgentAssist__AudioSocket__Port
              value: "9093"
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: platform-realtime
spec:
  template:
    spec:
      initContainers:
        - name: wait-for-redis
          image: busybox
      containers:
        - name: realtime
          ports:
            - name: http
              containerPort: 8080
          env:
            - name: ConnectionStrings__Redis
              value: "redis-0:6379"
"""


class CheckAgentAssistK8sTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)

    def _write(self, name, text):
        path = os.path.join(self._tmp.name, name)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(textwrap.dedent(text))
        return path

    def _run(self, policy=_POLICY, values=_VALUES, rendered=_RENDERED, env=None):
        args = [sys.executable, _SCRIPT,
                "--manifest", self._write("network-policies.yaml", policy),
                "--values", self._write("values.yaml", values)]
        if rendered is not None:
            args += ["--rendered", self._write("rendered.yaml", rendered)]
        full_env = dict(os.environ)
        full_env.update(env or {})
        return subprocess.run(args, capture_output=True, text=True, env=full_env, check=False)

    def test_passes_when_policy_port_and_address_agree(self):
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_fails_when_the_policy_is_deleted(self):
        r = self._run(policy=_POLICY.split("---\n")[0])
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("no r55-platform NetworkPolicy admits TCP 9093", r.stdout)

    def test_fails_when_the_policy_admits_another_port(self):
        r = self._run(policy=_POLICY.replace("port: 9093", "port: 9092"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("no r55-platform NetworkPolicy admits TCP 9093", r.stdout)

    def test_fails_when_the_policy_admits_another_namespace(self):
        r = self._run(policy=_POLICY.replace("kubernetes.io/metadata.name: r55-asterisk",
                                             "kubernetes.io/metadata.name: r55-data"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("r55-asterisk", r.stdout)

    def test_fails_when_the_rule_also_admits_every_pod_of_the_platform_namespace(self):
        r = self._run(policy=_POLICY.replace(
            "              kubernetes.io/metadata.name: r55-asterisk\n",
            "              kubernetes.io/metadata.name: r55-asterisk\n        - podSelector: {}\n"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("from a source beyond namespace r55-asterisk: {'podSelector': {}}", r.stdout)

    def test_fails_when_the_rule_also_admits_every_namespace(self):
        r = self._run(policy=_POLICY.replace(
            "              kubernetes.io/metadata.name: r55-asterisk\n",
            "              kubernetes.io/metadata.name: r55-asterisk\n        - namespaceSelector: {}\n"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("from a source beyond namespace r55-asterisk: {'namespaceSelector': {}}", r.stdout)

    def test_fails_when_the_rule_also_admits_an_ip_block(self):
        r = self._run(policy=_POLICY.replace(
            "              kubernetes.io/metadata.name: r55-asterisk\n",
            "              kubernetes.io/metadata.name: r55-asterisk\n        - ipBlock:\n            cidr: 0.0.0.0/0\n"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("from a source beyond namespace r55-asterisk: {'ipBlock'", r.stdout)

    def test_fails_when_another_rule_admits_the_port_from_every_source(self):
        r = self._run(policy=_POLICY + "    - ports:\n        - protocol: TCP\n          port: 9093\n")
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("from a source beyond namespace r55-asterisk: no 'from' (every source)", r.stdout)

    def test_passes_when_a_pod_selector_narrows_the_asterisk_peer(self):
        r = self._run(policy=_POLICY.replace(
            "              kubernetes.io/metadata.name: r55-asterisk\n",
            "              kubernetes.io/metadata.name: r55-asterisk\n          podSelector:\n"
            "            matchLabels:\n              app: asterisk\n"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_fails_when_the_policy_selects_another_workload(self):
        r = self._run(policy=_POLICY.replace("app.kubernetes.io/name: platform-api", "app.kubernetes.io/name: web"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_fails_when_the_policy_is_in_another_namespace(self):
        r = self._run(policy=_POLICY.replace("name: allow-agentassist-audio\n  namespace: r55-platform",
                                             "name: allow-agentassist-audio\n  namespace: r55-asterisk"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_fails_when_the_policy_admits_udp_only(self):
        r = self._run(policy=_POLICY.replace("protocol: TCP\n          port: 9093", "protocol: UDP\n          port: 9093"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_passes_when_the_rule_names_the_port_by_its_container_name(self):
        r = self._run(policy=_POLICY.replace("port: 9093", "port: aa-audio"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_fails_when_the_api_container_does_not_declare_the_port(self):
        rendered = _RENDERED.replace("            - name: aa-audio\n              containerPort: 9093\n"
                                     "              protocol: TCP\n", "")
        r = self._run(rendered=rendered)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("containerPort 9093", r.stdout)

    def test_fails_when_the_port_is_renamed_past_fifteen_characters(self):
        r = self._run(rendered=_RENDERED.replace("name: aa-audio", "name: agentassist-audio"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("agentassist-audio", r.stdout)
        self.assertIn("15", r.stdout)

    def test_fails_on_a_port_name_with_no_letter_or_bad_characters(self):
        r = self._run(rendered=_RENDERED.replace("name: http\n              containerPort: 5000",
                                                 "name: 5000\n              containerPort: 5000"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        r = self._run(rendered=_RENDERED.replace("name: aa-audio", "name: aa_audio"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_fails_when_the_port_differs_from_the_values_default(self):
        r = self._run(values=_VALUES.replace("audioSocketPort: 9093", "audioSocketPort: 9094"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("9094", r.stdout)

    def test_fails_when_the_advertised_address_is_not_set(self):
        rendered = _RENDERED.replace("            - name: AgentAssist__AudioSocket__AdvertisedHost\n"
                                     "              value: \"$(POD_IP)\"\n", "")
        r = self._run(rendered=rendered)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("AgentAssist__AudioSocket__AdvertisedHost", r.stdout)

    def test_fails_when_pod_ip_is_not_from_the_downward_api(self):
        r = self._run(rendered=_RENDERED.replace("fieldPath: status.podIP", "fieldPath: status.hostIP"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("POD_IP", r.stdout)

    def test_fails_when_the_redis_values_differ(self):
        r = self._run(rendered="redis-1:6379".join(_RENDERED.rsplit("redis-0:6379", 1)))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("ConnectionStrings__Redis", r.stdout)

    def test_fails_when_the_values_have_no_audio_port(self):
        r = self._run(values="api: {}\n")
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("api.agentAssist.audioSocketPort is not set", r.stdout)

    def test_exits_2_when_the_audio_port_is_not_a_number(self):
        r = self._run(values=_VALUES.replace("audioSocketPort: 9093", "audioSocketPort: audio"))
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)

    def test_exits_2_when_helm_fails_to_render(self):
        r = self._run(rendered=None, env={"CHECK_AGENTASSIST_K8S_HELM": "false"})
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)

    def test_exits_2_when_helm_is_missing(self):
        r = self._run(rendered=None, env={"CHECK_AGENTASSIST_K8S_HELM": os.path.join(self._tmp.name, "no-helm")})
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)


if __name__ == "__main__":
    unittest.main()
