"""Fail-closed gate for the named real PostgreSQL persistence tests."""
import sys
import argparse
from pathlib import Path
import xml.etree.ElementTree as ET

REQUIRED = {
    "AiNative.Server.Backend.Tests.PostgresTests.Migrated_schema_initializes_with_only_runtime_DML_privileges",
    "AiNative.Server.Backend.Tests.PostgresTests.Settlement_transaction_survives_store_restart_and_updates_once",
    "AiNative.Server.Rooms.Tests.PostgresAllocationStoreTests.MigratedSchemaInitializesWithOnlyRuntimeDmlPrivileges",
    "AiNative.Server.Rooms.Tests.PostgresAllocationStoreTests.TerminatedOwnershipConnectionCannotContinueAllocating",
    "AiNative.Server.Rooms.Tests.PostgresAllocationStoreTests.ExclusiveOwnerDurableReloadAndConflictingIdentityAreEnforcedByPostgres",
}


def verify(directory):
    reports = sorted(directory.rglob("*.trx"))
    if not reports:
        raise ValueError("No TRX reports found")
    seen = set()
    for path in reports:
        root = ET.parse(path).getroot()
        summary = root.find("{*}ResultSummary")
        if summary is None or summary.get("outcome") not in ("Completed", "Passed"):
            raise ValueError(f"{path}: run did not complete successfully")
        definitions = {}
        for test in root.findall("{*}TestDefinitions/{*}UnitTest"):
            method = test.find("{*}TestMethod")
            identifier = test.get("id")
            if not identifier or identifier in definitions or method is None:
                raise ValueError(f"{path}: missing or duplicate test definition")
            definitions[identifier] = f"{method.get('className')}.{method.get('name')}"
        results = root.findall("{*}Results/{*}UnitTestResult")
        if not results:
            raise ValueError(f"{path}: no test results")
        for result in results:
            name = definitions.get(result.get("testId"))
            if name is None:
                raise ValueError(f"{path}: result has no test definition")
            if name in seen:
                raise ValueError(f"{path}: duplicate result for {name}")
            if result.get("outcome") != "Passed":
                raise ValueError(f"{path}: {name} outcome is {result.get('outcome')!r}, expected Passed")
            seen.add(name)
    missing = REQUIRED - seen
    if missing:
        raise ValueError("Missing required PostgreSQL results: " + ", ".join(sorted(missing)))
    return len(reports)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report_directory", type=Path)
    args = parser.parse_args()
    try:
        count = verify(args.report_directory)
    except (OSError, ValueError, ET.ParseError) as error:
        print(f"PostgreSQL gate failed: {error}", file=sys.stderr)
        return 1
    print(f"5 required PostgreSQL tests passed in {count} TRX reports")
    return 0


if __name__ == "__main__":
    sys.exit(main())
