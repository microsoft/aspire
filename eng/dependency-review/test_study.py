# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

import copy
import unittest

from study import ROOT, deterministic, finding_adjudication, load, score


class StudyTests(unittest.TestCase):
    def setUp(self):
        self.corpus = load(ROOT / "corpus.json")
        self.expected = load(ROOT / "expected.json")
        self.output = {"rows": [{
            "id": "ordinary", "state": "defect", "head": "H1",
            "findings": [{"claim": "lost guards", "kind": "defect", "evidence": ["o1"]}],
            "evidence": ["o1"], "execution": [],
        }]}
        self.notes = {"ordinary": {
            "findings": [{"targets": ["null-invariant", "duplicate-invariant"]}],
            "citation_relevance": [True, True], "execution_correct": 0,
            "execution_total": 0, "extra_unsupported": 0,
        }}

    def assess(self):
        return score(self.corpus, self.expected, self.output, self.notes, ["ordinary"])

    def test_complete_supported_detection(self):
        result = self.assess()["totals"]
        self.assertEqual((2, 0, 0, 2, 2), (result["tp"], result["miss"], result["fp"],
                                          result["citation_correct"], result["citation_total"]))

    def test_duplicate_not_extra_true_positive(self):
        row = self.output["rows"][0]
        row["findings"].append(copy.deepcopy(row["findings"][0]))
        self.notes["ordinary"]["findings"].append({
            "targets": ["null-invariant"], "duplicate": True,
        })
        self.notes["ordinary"]["citation_relevance"].append(True)
        result = self.assess()["totals"]
        self.assertEqual((2, 1), (result["tp"], result["duplicate"]))

    def test_format_or_state_not_substantive_false_positive(self):
        self.output["rows"][0]["state"] = "risk"
        result = self.assess()["totals"]
        self.assertEqual((0, 0), (result["state_correct"], result["fp"]))

    def test_stale_head_and_invalid_citation(self):
        self.output["rows"][0]["head"] = "H0"
        self.output["rows"][0]["evidence"] = ["invented"]
        result = self.assess()["totals"]
        self.assertEqual((0, 1), (result["head_correct"], result["citation_correct"]))

    def test_reject_unadjudicated_findings_and_unknown_targets(self):
        self.notes["ordinary"]["findings"] = []
        with self.assertRaisesRegex(ValueError, "every finding"):
            self.assess()
        self.notes["ordinary"]["findings"] = [{"targets": ["invented"]}]
        with self.assertRaisesRegex(ValueError, "unknown target"):
            self.assess()

    def test_missing_case_fails_instead_of_improving_scores(self):
        with self.assertRaisesRegex(ValueError, "requested cases"):
            score(self.corpus, self.expected, self.output, self.notes, ["ordinary", "visual"])

    def test_supported_row_recognition_separate_from_finding_emission(self):
        self.notes["ordinary"]["findings"][0]["targets"] = []
        self.notes["ordinary"]["row_targets"] = ["null-invariant", "duplicate-invariant"]
        result = self.assess()["totals"]
        self.assertEqual((0, 2, 2, 0), (result["tp"], result["miss"], result["recognized"], result["fp"]))

    def test_false_unsupported_claims_count_explicitly(self):
        note = self.notes["ordinary"]["findings"][0]
        note["targets"] = []
        note["fp"] = True
        note["unsupported"] = True
        result = self.assess()["totals"]
        self.assertEqual((1, 1, 2), (result["fp"], result["unsupported"], result["miss"]))

    def test_plan_preserves_false_positive_unsupported_and_duplicate_annotations(self):
        result = finding_adjudication({
            "matches": [[], ["null-invariant"]],
            "fp": [0], "unsupported": [0], "duplicate": [1],
        })
        self.assertEqual([
            {"targets": [], "fp": True, "unsupported": True},
            {"targets": ["null-invariant"], "duplicate": True},
        ], result)

    def test_plan_rejects_invalid_annotation_indices(self):
        for index in (-1, 1, "0"):
            with self.subTest(index=index):
                with self.assertRaisesRegex(ValueError, "Invalid fp finding index"):
                    finding_adjudication({"matches": [[]], "fp": [index]})

    def test_schema_alias_not_false_positive(self):
        self.output["results"] = self.output.pop("rows")
        result = self.assess()
        self.assertTrue(result["schema_alias_used"])
        self.assertEqual(0, result["totals"]["fp"])

    def test_execution_errors_and_selection_denominators(self):
        self.notes["ordinary"].update(
            execution_correct=1, execution_total=2,
            selection_correct=1, selection_total=2)
        result = self.assess()["totals"]
        self.assertEqual((1, 2, 1, 2), (result["execution_correct"], result["execution_total"],
                                      result["selection_correct"], result["selection_total"]))
        self.notes["ordinary"]["execution_correct"] = 3
        with self.assertRaisesRegex(ValueError, "execution denominator"):
            self.assess()

    def test_deterministic_guard_outcomes_not_package_snapshot(self):
        result = deterministic()
        self.assertEqual(5, result["holds"]["count"])
        self.assertEqual({"A": True, "B": False}, result["feed_paths"])

    def test_corpus_boundaries(self):
        self.assertEqual(15, len(self.corpus["cases"]))
        self.assertEqual({c["id"] for c in self.corpus["cases"]}, self.expected["cases"].keys())
        self.assertTrue(self.expected["frozen_before_calls"])
        self.assertEqual(19, sum(len(c["targets"]) for c in self.expected["cases"].values()))


if __name__ == "__main__":
    unittest.main()
