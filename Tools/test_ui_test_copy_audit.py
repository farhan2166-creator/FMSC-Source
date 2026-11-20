"""Small deterministic tests for the advisory (never blocking) copy audit."""
import unittest

from audit_ui_test_copy import candidates, geometry_candidates


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


    def test_flags_exact_ui_dimensions_and_row_positions(self):
        source = (
            'Check(form.ClientSize.Height == 380, "layout");\\n'
            'Check(table.RowStyles[2].Height == 0, "layout");\\n'
            'Check(table.GetRow(field) == 1, "layout");\\n'
        )
        self.assertEqual(len(geometry_candidates(source)), 3)

    def test_does_not_flag_behavior_or_relative_visibility(self):
        source = (
            'Check(button.Width > 0 && field.Parent.ClientRectangle.Contains(field.Bounds), "usable");\\n'
            'Check(amount == 2500m && access.Enabled, "finance and permission");\\n'
            '// Check(form.Height == 420, "old geometry");\\n'
        )
        self.assertEqual(geometry_candidates(source), [])


if __name__ == "__main__":
    unittest.main()
