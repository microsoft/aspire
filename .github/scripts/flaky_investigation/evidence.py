"""Account only for original TRX records from the exact owned execution."""

import json
import xml.etree.ElementTree as ET

from contracts import require


def summarize(artifacts, owned, target):
    passed = failed = actual = iterations = 0
    errors = []
    seen = set()
    expected = {(os, index) for os in target["oses"] for index in range(1, owned["runners"] + 1)}
    require(set(artifacts) == expected, "Missing or unexpected runner evidence")
    for (os, index), files in artifacts.items():
        receipt = json.loads(files["receipt.json"])
        require(receipt == {
            "run_id": owned["id"], "run_attempt": 1, "sha": owned["source_sha"],
            "trusted_sha": owned["sha"],
            "os": os, "index": index, "iterations": owned["iterations"],
            "project": target["project"], "method": target["class_name"] + "." + target["method"],
            "mode": owned["mode"],
        }, "Evidence receipt does not match owned run/source/filter")
        for iteration in range(1, owned["iterations"] + 1):
            prefix = f"iteration-{iteration}/"
            metadata = json.loads(files[prefix + "result.json"])
            require(isinstance(metadata, dict) and type(metadata.get("exit_code")) is int,
                    "Malformed process exit receipt")
            reports = [value for path, value in files.items() if path.startswith(prefix) and path.endswith(".trx")]
            require(reports, "Missing original TRX: build/infra/zero tests are not passes")
            target_outcomes = []
            for report in reports:
                require(b"<!DOCTYPE" not in report.upper() and b"<!ENTITY" not in report.upper(),
                        "TRX entities are forbidden")
                try:
                    root = ET.fromstring(report)
                except ET.ParseError as error:
                    raise ValueError("Malformed original TRX report") from error
                ns = {"t": root.tag.split("}")[0][1:]} if "}" in root.tag else {}
                tag = lambda name: f"t:{name}" if ns else name
                definitions = {}
                for definition in root.findall(".//" + tag("UnitTest"), ns):
                    method = definition.find(tag("TestMethod"), ns)
                    if method is not None:
                        definitions[definition.get("id")] = (method.get("className"), method.get("name"))
                results = root.findall(".//" + tag("UnitTestResult"), ns)
                require(results, "Empty TRX is not a test execution")
                for result in results:
                    identity = (result.get("executionId"), result.get("testId"))
                    require(all(identity) and identity not in seen, "Duplicate/merged TRX execution")
                    seen.add(identity)
                    outcome = result.get("outcome")
                    require(outcome in {"Passed", "Failed", "NotExecuted"}, "Unknown TRX outcome")
                    if outcome != "NotExecuted":
                        actual += 1
                    if definitions.get(result.get("testId")) == (target["class_name"], target["method"]):
                        require(outcome != "NotExecuted", "Selected test was skipped")
                        target_outcomes.append(outcome)
                        if outcome == "Failed":
                            error = "\n".join(result.itertext())
                            if len(errors) < 3:
                                errors.append(error[:4000])
                            if owned["phase"] == "reproduce":
                                require(target["failure_signature"] in error,
                                        "Target failure does not match the issue signature")
                    elif outcome == "Failed":
                        raise ValueError("Unrelated project test failed; not reproduction evidence")
            require(target_outcomes, "Selected test did not execute")
            require(metadata.get("exit_code") == (2 if "Failed" in target_outcomes else 0),
                    "Test process exit code indicates an infrastructure failure")
            passed += target_outcomes.count("Passed")
            failed += target_outcomes.count("Failed")
            iterations += 1
    return {"passed": passed, "failed": failed, "actual_tests": actual,
            "runner_iterations": iterations, "errors": errors}
