"""Small deterministic tests for the advisory (never blocking) copy audit."""
import unittest

from audit_ui_test_copy import candidates


class UiCopyAuditTests(unittest.TestCase):
    def test_detects_literal_caption_comparisons(self):
        source = 'Check(button.Text == "Save Draft", "old wording");\n'
        self.assertEqual(len(candidates(source)), 1)

    def test_detects_sentence_fragment_coupling(self):
        source = 'Check(status.Text.Contains("the old instruction"), "copy");\n'
        self.assertEqual(len(candidates(source)), 1)

    def test_ignores_actual_capabilities_and_comments(self):
        source = (
            '// Check(button.Text == "Old caption", "historical");\n'
            'Check(button.Enabled && accountId == 41, "behavior");\n'
        )
        self.assertEqual(candidates(source), [])


if __name__ == "__main__":
    unittest.main()
