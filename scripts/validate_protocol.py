"""Validate the OpenAPI document and shared positive/negative wire fixtures."""
import json
from pathlib import Path
from jsonschema import Draft202012Validator, FormatChecker
from openapi_spec_validator import validate

ROOT = Path(__file__).resolve().parents[1]
api = json.loads((ROOT / "packages/protocol/openapi.json").read_text())
validate(api)
count = 0
for path in sorted((ROOT / "packages/test-fixtures/protocol").glob("*.json")):
    case = json.loads(path.read_text())
    schema = api["components"]["schemas"][case["schema"]]
    errors = list(Draft202012Validator(schema, format_checker=FormatChecker()).iter_errors(case["value"]))
    assert (not errors) == case["valid"], f"Unexpected validation result: {path.name}: {errors}"
    count += 1
assert count >= 6, "Protocol fixtures are missing"
print(f"OpenAPI valid; {count} wire fixtures passed")
