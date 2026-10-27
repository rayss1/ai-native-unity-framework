#!/usr/bin/env python3
"""Native Windows/macOS local topology entry; Python 3.11+, no third-party modules."""
from __future__ import annotations

import argparse
import sys
import subprocess

from manual_runtime import DEFAULT_RUN, SERVICES, ManualError, Session, control, resolve_run


def bounded(low: int, high: int):
    def parse(value: str) -> int:
        number = int(value)
        if not low <= number <= high:
            raise argparse.ArgumentTypeError(f"must be between {low} and {high}")
        return number

    return parse


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description=__doc__)
    commands = result.add_subparsers(dest="command", required=True)
    for command in ("start", "stop", "status"):
        item = commands.add_parser(command)
        item.add_argument("--run-directory", "-RunDirectory", default=DEFAULT_RUN)
        item.add_argument(
            "--sdk-path",
            "-SdkPath",
            help="dotnet executable (SDK policy still comes from repository root)",
        )
        if command == "start":
            item.add_argument("--postgres-bin-directory", "-PostgresBinDirectory")
            item.add_argument(
                "--postgres-port", "-PostgresPort", type=bounded(1024, 65535), default=25439
            )
            item.add_argument(
                "--match-ticks", "-MatchTicks", type=bounded(120, 360000), default=36000
            )
            item.add_argument(
                "--players-per-room", "-PlayersPerRoom", type=bounded(2, 8), default=2
            )
            item.add_argument(
                "--debug-service",
                "-DebugService",
                choices=[s["Id"] for s in SERVICES if s["Role"] != "Client"],
            )
    return result


def main(argv=None) -> int:
    if sys.version_info < (3, 11):
        print("Python 3.11 or newer is required.", file=sys.stderr)
        return 2
    args = parser().parse_args(argv)
    try:
        run = resolve_run(args.run_directory)
        if args.command == "start":
            return Session(args, run).start()
        return control(args.command, run, args.sdk_path)
    except (ManualError, OSError, ValueError, subprocess.SubprocessError) as error:
        message = (
            "Command timed out; inspect the relevant log."
            if isinstance(error, subprocess.SubprocessError)
            else str(error)
        )
        print(f"Local topology: {message}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
