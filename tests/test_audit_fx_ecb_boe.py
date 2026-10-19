import importlib.util
import sys
import unittest
from datetime import date
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "scripts" / "audit_fx_ecb_boe.py"
SPEC = importlib.util.spec_from_file_location("audit_fx_ecb_boe", SCRIPT)
audit = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = audit
SPEC.loader.exec_module(audit)

ECB_XML = b"""<?xml version="1.0" encoding="UTF-8"?>
<gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01"
  xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
  <Cube>
    <Cube time="2024-06-03">
      <Cube currency="GBP" rate="0.85175"/>
      <Cube currency="USD" rate="1.0842"/>
    </Cube>
    <Cube time="2024-06-04">
      <Cube currency="GBP" rate="0.85000"/>
      <Cube currency="USD" rate="1.0800"/>
    </Cube>
  </Cube>
</gesmes:Envelope>
"""


def boe_csv(usd_jun3: str = "1.2724") -> bytes:
    return (
        "DATE,XUDLUSS,XUDLERS\r\n"
        f"03 Jun 2024,{usd_jun3},1.1741\r\n"
        "04 Jun 2024,1.2706,1.1765\r\n"
        "05 Jun 2024,1.2700,1.1760\r\n"
    ).encode()


class FxIndependentAuditTests(unittest.TestCase):
    def test_derived_pairs_match_hand_arithmetic(self):
        result = audit.compare(audit.parse_ecb_xml(ECB_XML), audit.parse_boe_csv(boe_csv()),
                               date(2024, 6, 1), date(2024, 6, 30))
        self.assertEqual(2, result["pairs"]["usdPerGbp"]["matchedDates"])
        self.assertEqual(["2024-06-05"], result["boeOnlyDates"])
        self.assertEqual([], result["ecbOnlyDates"])
        # 3 Jun: ECB USD per GBP = 1.0842/0.85175 = 1.272908...; BoE 1.2724 is about -4.0 bps.
        # 4 Jun: 1.08/0.85 = 1.270588...; BoE 1.2706 is about +0.09 bps. Median of the two.
        median = result["pairs"]["usdPerGbp"]["medianBps"]
        self.assertAlmostEqual(-1.95, median, delta=0.35)
        self.assertEqual(0, result["pairs"]["usdPerGbp"]["over50Bps"])

    def test_direction_error_is_unmistakable(self):
        # 0.7856 is GBP per USD (the inverted quote); feeding it as USD per GBP must
        # produce thousands of bps, not a plausible fixing-time difference.
        result = audit.compare(audit.parse_ecb_xml(ECB_XML), audit.parse_boe_csv(boe_csv("0.7856")),
                               date(2024, 6, 1), date(2024, 6, 30))
        self.assertGreater(result["pairs"]["usdPerGbp"]["maxAbsBps"], 1000)
        self.assertEqual(1, result["pairs"]["usdPerGbp"]["over50Bps"])
        self.assertEqual("2024-06-03", result["over50BpsDates"]["usdPerGbp"][0]["date"])

    def test_malformed_inputs_fail_closed(self):
        with self.assertRaises(audit.AuditError):
            audit.parse_ecb_xml(b"<!DOCTYPE x>" + ECB_XML)
        with self.assertRaises(audit.AuditError):
            audit.parse_boe_csv(b"DATE,XUDLUSS\r\n03 Jun 2024,1.27\r\n")
        with self.assertRaises(audit.AuditError):
            audit.parse_boe_csv(boe_csv() + b"03 Jun 2024,1.27,1.17\r\n")
        with self.assertRaises(audit.AuditError):
            audit.parse_ecb_xml(ECB_XML.replace(b'rate="1.0842"', b'rate="-1"'))


if __name__ == "__main__":
    unittest.main()
